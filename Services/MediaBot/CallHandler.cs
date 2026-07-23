using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Graph.Communications.Calls;
using Microsoft.Graph.Communications.Calls.Media;
using Microsoft.Skype.Bots.Media;
using Recorder.Api.Models;

namespace Recorder.Api.Services.MediaBot;

/// <summary>
/// Owns ONE active call: receives unmixed per-participant audio from the media
/// platform, maps each stream to a participant identity, batches it, and hands
/// WAV chunks to the existing recorder pipeline (TeamsMediaService) IN-PROCESS.
///
/// NOTE: the media-SDK API surface (event args, unmixed buffer shape, participant
/// resource) can vary slightly by SDK version. The spots most likely to need a
/// tweak on first build are marked with "// [SDK]".
/// </summary>
public sealed class CallHandler : IDisposable
{
    private readonly ICall _call;
    private readonly ILocalMediaSession _mediaSession;
    private readonly TeamsMediaService _recorder;
    private readonly MediaBotJoinRequest _join;
    private readonly ILogger _logger;

    // One audio buffer per active speaker (keyed by media source id / MSI).
    private readonly ConcurrentDictionary<uint, ParticipantAudioBuffer> _buffers = new();
    private readonly Timer _flushTimer;
    private const int FlushIntervalMs = 5000;

    private EnableRecordingRequest Recording => new()
    {
        CallId = _join.CallId,
        JoinWebUrl = _join.JoinWebUrl,
        OnlineMeetingId = _join.OnlineMeetingId,
        OrganizerId = _join.OrganizerId ?? string.Empty,
        MeetingSubject = _join.MeetingSubject,
        BotEndpoint = _join.BotEndpoint ?? string.Empty,
    };

    public CallHandler(
        ICall call,
        ILocalMediaSession mediaSession,
        TeamsMediaService recorder,
        MediaBotJoinRequest join,
        ILogger logger)
    {
        _call = call;
        _mediaSession = mediaSession;
        _recorder = recorder;
        _join = join;
        _logger = logger;

        // [SDK] Audio frames arrive here once media is established.
        _mediaSession.AudioSocket.AudioMediaReceived += OnAudioMediaReceived;

        // Register the call with the recorder so it starts the Azure Speech pipeline.
        // In teams mode this does NOT start local mic/loopback capture.
        _ = _recorder.EnableRecordingAsync(Recording, CancellationToken.None);

        _flushTimer = new Timer(_ => FlushAll(), null, FlushIntervalMs, FlushIntervalMs);
        _logger.LogInformation("CallHandler started for call {CallId}", _join.CallId);
    }

    private void OnAudioMediaReceived(object? sender, AudioMediaReceivedEventArgs e)
    {
        try
        {
            // [SDK] Unmixed audio: one buffer per dominant speaker, each tagged with MSI.
            var unmixed = e.Buffer.UnmixedAudioBuffers;
            if (unmixed != null && unmixed.Length > 0)
            {
                foreach (var buf in unmixed)
                {
                    var msi = buf.ActiveSpeakerId;
                    var bytes = CopyNative(buf.Data, buf.Length);
                    if (bytes.Length == 0) continue;

                    // Per-participant mute gate: ignore audio from a muted mic, transmit when
                    // unmuted. Teams already stops sending muted audio, so this is belt-and-
                    // suspenders that also makes the mute/unmute transition crisp and covers
                    // organizer/hard-mute.
                    if (IsParticipantMuted(msi)) continue;

                    var speaker = ResolveSpeaker(msi);
                    var buffer = _buffers.GetOrAdd(msi, _ => new ParticipantAudioBuffer(speaker));
                    buffer.Speaker = speaker; // keep name fresh as the roster resolves
                    buffer.Append(bytes, 0, bytes.Length);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error handling audio for call {CallId}", _join.CallId);
        }
        finally
        {
            // [SDK] Always dispose the received buffer to release native memory.
            e.Buffer.Dispose();
        }
    }

    private static byte[] CopyNative(IntPtr data, long length)
    {
        if (data == IntPtr.Zero || length <= 0) return Array.Empty<byte>();
        var managed = new byte[length];
        Marshal.Copy(data, managed, 0, (int)length);
        return managed;
    }

    /// <summary>Map a media source id to a participant display name via the call roster.</summary>
    private string ResolveSpeaker(uint msi)
    {
        try
        {
            foreach (var participant in _call.Participants)
            {
                // [SDK] Each participant exposes its audio media stream(s) with a SourceId
                // that matches the MSI on the unmixed buffer.
                var streams = participant.Resource?.MediaStreams;
                if (streams is null) continue;

                var match = streams.Any(s =>
                    string.Equals(s.SourceId, msi.ToString(), StringComparison.OrdinalIgnoreCase));
                if (!match) continue;

                var user = participant.Resource?.Info?.Identity?.User;
                if (user is not null)
                {
                    return user.DisplayName ?? user.Id ?? $"Participant {msi}";
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not resolve speaker for MSI {Msi}", msi);
        }

        return $"Participant {msi}";
    }

    /// <summary>True if the participant behind this MSI is muted (self-mute or server/hard-mute).</summary>
    private bool IsParticipantMuted(uint msi)
    {
        try
        {
            foreach (var participant in _call.Participants)
            {
                var streams = participant.Resource?.MediaStreams;
                if (streams is null) continue;

                var owns = streams.Any(s =>
                    string.Equals(s.SourceId, msi.ToString(), StringComparison.OrdinalIgnoreCase));
                if (!owns) continue;

                // [SDK] IsMuted = participant self-mute; ServerMuted = organizer/hard-mute.
                var selfMuted = participant.Resource?.IsMuted == true;
                var serverMuted = streams.Any(s => s.ServerMuted == true);
                return selfMuted || serverMuted;
            }
        }
        catch
        {
            // If we can't read mute state, don't suppress — fall back to Teams' implicit gating.
        }

        return false;
    }

    private void FlushAll()
    {
        foreach (var (msi, buffer) in _buffers)
        {
            try
            {
                var wav = buffer.FlushWav();
                if (wav is null) continue;

                var chunk = new AudioChunkRequest
                {
                    CallId = _join.CallId,
                    Speaker = buffer.Speaker,
                    ContentType = "audio/wav",
                    Language = "en-US",
                    Timestamp = DateTimeOffset.UtcNow.ToString("O"),
                };

                // In-process hand-off to the existing recorder/STT pipeline.
                _ = _recorder.ProcessAudioChunkAsync(chunk, wav, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Flush failed for MSI {Msi} on call {CallId}", msi, _join.CallId);
            }
        }
    }

    public void Dispose()
    {
        try { _mediaSession.AudioSocket.AudioMediaReceived -= OnAudioMediaReceived; } catch { }
        _flushTimer.Dispose();
        FlushAll(); // flush whatever remains
        foreach (var buffer in _buffers.Values) buffer.Dispose();
        _buffers.Clear();
        _ = _recorder.DisableRecordingAsync(_join.CallId);
        _logger.LogInformation("CallHandler disposed for call {CallId}", _join.CallId);
    }
}

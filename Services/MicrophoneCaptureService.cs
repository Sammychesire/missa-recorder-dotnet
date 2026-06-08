using System.Collections.Concurrent;
using System.IO;
using NAudio.Wave;
using Recorder.Api.Models;

namespace Recorder.Api.Services;

/// <summary>
/// Captures audio from the default microphone (or loopback/WASAPI capture device)
/// and feeds 3-second WAV chunks into the active call pipelines via TeamsMediaService.
/// One capture session is shared across all active calls to avoid opening multiple
/// device handles simultaneously.
/// </summary>
public sealed class MicrophoneCaptureService : IHostedService, IDisposable
{
    private sealed class CaptureSourceBuffer
    {
        public CaptureSourceBuffer(string speaker)
        {
            Speaker = speaker;
        }

        public string Speaker { get; }
        public object Sync { get; } = new();
        public MemoryStream CurrentBuffer { get; set; } = new();
        public WaveFileWriter? WaveWriter { get; set; }
    }

    private readonly TeamsMediaService _teamsMediaService;
    private readonly ILogger<MicrophoneCaptureService> _logger;
    private readonly IConfiguration _configuration;

    // Track which call IDs we should be feeding audio to.
    private readonly ConcurrentDictionary<string, EnableRecordingRequest> _activeCalls = new();

    // Two capture devices: loopback for others' voices, microphone for the user's voice.
    private IWaveIn? _loopbackDevice;
    private IWaveIn? _micDevice;
    private CaptureSourceBuffer? _loopbackBuffer;
    private CaptureSourceBuffer? _micBuffer;

    // Flush a chunk to Azure Speech every N milliseconds.
    private const int DefaultChunkIntervalMs = 10000;
    private const int MinChunkIntervalMs = 3000;
    private const int MaxChunkIntervalMs = 30000;
    private Timer? _flushTimer;

    // 16 kHz, 16-bit, mono — matches what Azure Speech REST expects.
    private static readonly WaveFormat TargetFormat = new WaveFormat(16000, 16, 1);

    public MicrophoneCaptureService(
        TeamsMediaService teamsMediaService,
        ILogger<MicrophoneCaptureService> logger,
        IConfiguration configuration)
    {
        _teamsMediaService = teamsMediaService;
        _logger = logger;
        _configuration = configuration;
    }

    // Called by TeamsMediaService when a new recording is enabled.
    public void RegisterCall(EnableRecordingRequest request)
    {
        _activeCalls[request.CallId] = request;
        _logger.LogInformation(
            "Microphone capture registered for call {CallId}. Active captures: {Count}",
            request.CallId,
            _activeCalls.Count);
        EnsureCaptureRunning();
    }

    // Called by TeamsMediaService when a call ends.
    public void UnregisterCall(string callId)
    {
        _activeCalls.TryRemove(callId, out _);
        _logger.LogInformation(
            "Microphone capture unregistered for call {CallId}. Active captures: {Count}",
            callId,
            _activeCalls.Count);

        if (_activeCalls.IsEmpty)
        {
            StopCapture();
        }
    }

    // IHostedService — nothing to do at startup; capture starts on demand.
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken)
    {
        StopCapture();
        return Task.CompletedTask;
    }

    private void EnsureCaptureRunning()
    {
        if (_loopbackDevice is not null || _micDevice is not null)
        {
            return; // Already capturing.
        }

        _loopbackBuffer ??= new CaptureSourceBuffer("Meeting participants");
        _micBuffer ??= new CaptureSourceBuffer("You (microphone)");

        if (ResolveCaptureEnabled("RECORDER_CAPTURE_LOOPBACK", defaultValue: true))
        {
            try
            {
                var loopback = new WasapiLoopbackCapture();
                ResetBuffer(_loopbackBuffer);
                loopback.DataAvailable += OnDataAvailable;
                loopback.RecordingStopped += OnRecordingStopped;
                loopback.StartRecording();
                _loopbackDevice = loopback;
                _logger.LogInformation("Microphone capture: WASAPI loopback started (captures other participants).");
            }
            catch (Exception ex)
            {
                _logger.LogWarning("WASAPI loopback unavailable: {Message}", ex.Message);
            }
        }
        else
        {
            _logger.LogInformation("Microphone capture: WASAPI loopback disabled by RECORDER_CAPTURE_LOOPBACK.");
        }

        if (ResolveCaptureEnabled("RECORDER_CAPTURE_MICROPHONE", defaultValue: false))
        {
            try
            {
                var mic = new WaveInEvent
                {
                    WaveFormat = TargetFormat,
                    BufferMilliseconds = 100,
                };
                ResetBuffer(_micBuffer);
                mic.DataAvailable += OnDataAvailable;
                mic.RecordingStopped += OnRecordingStopped;
                mic.StartRecording();
                _micDevice = mic;
                _logger.LogInformation("Microphone capture: default microphone started (captures user voice).");
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Microphone unavailable: {Message}", ex.Message);
            }
        }
        else
        {
            _logger.LogInformation("Microphone capture: default microphone disabled by RECORDER_CAPTURE_MICROPHONE.");
        }

        if (_loopbackDevice is null && _micDevice is null)
        {
            _logger.LogError("Failed to start any audio capture device.");
            return;
        }

        var chunkIntervalMs = ResolveChunkIntervalMs();
        _flushTimer = new Timer(FlushChunk, null, chunkIntervalMs, chunkIntervalMs);
        _logger.LogInformation(
            "Microphone capture started. loopbackEnabled={LoopbackEnabled}, microphoneEnabled={MicrophoneEnabled}, chunkIntervalMs={ChunkIntervalMs}",
            _loopbackDevice is not null,
            _micDevice is not null,
            chunkIntervalMs);
    }
    private void StopCapture()
    {
        _flushTimer?.Dispose();
        _flushTimer = null;

        foreach (var device in new IWaveIn?[] { _loopbackDevice, _micDevice })
        {
            if (device is null) continue;
            try { device.StopRecording(); } catch { }
            device.Dispose();
        }
        _loopbackDevice = null;
        _micDevice = null;

        DisposeBuffer(_loopbackBuffer);
        DisposeBuffer(_micBuffer);

        _logger.LogInformation("Microphone capture stopped.");
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0)
        {
            return;
        }

        var sourceBuffer = ResolveSourceBuffer(sender);
        if (sourceBuffer is null)
        {
            return;
        }

        lock (sourceBuffer.Sync)
        {
            if (sourceBuffer.WaveWriter is null)
            {
                return;
            }

            // NAudio WASAPI loopback may produce a different sample rate/channel layout;
            // resample to 16 kHz mono before writing.
            var sourceFormat = (sender as IWaveIn)?.WaveFormat ?? TargetFormat;

            if (sourceFormat.Equals(TargetFormat))
            {
                sourceBuffer.WaveWriter.Write(e.Buffer, 0, e.BytesRecorded);
            }
            else
            {
                // Resample on the fly.
                using var rawStream = new RawSourceWaveStream(
                    new MemoryStream(e.Buffer, 0, e.BytesRecorded, writable: false),
                    sourceFormat);
                using var resampler = new MediaFoundationResampler(rawStream, TargetFormat)
                {
                    ResamplerQuality = 60,
                };

                var resampledBuf = new byte[4096];
                int read;
                while ((read = resampler.Read(resampledBuf, 0, resampledBuf.Length)) > 0)
                {
                    sourceBuffer.WaveWriter.Write(resampledBuf, 0, read);
                }
            }
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null)
        {
            _logger.LogError(e.Exception, "Microphone capture stopped with error.");
        }
    }

    private void FlushChunk(object? _)
    {
        if (_activeCalls.IsEmpty)
        {
            return;
        }

        FlushSourceChunk(_loopbackBuffer);
        FlushSourceChunk(_micBuffer);
    }

    private void FlushSourceChunk(CaptureSourceBuffer? sourceBuffer)
    {
        if (sourceBuffer is null)
        {
            return;
        }

        byte[] wav;
        lock (sourceBuffer.Sync)
        {
            if (sourceBuffer.WaveWriter is null)
            {
                return;
            }

            try
            {
                sourceBuffer.WaveWriter.Flush();
                wav = sourceBuffer.CurrentBuffer.ToArray();
            }
            catch
            {
                wav = Array.Empty<byte>();
            }
            ResetBuffer(sourceBuffer);
        }

        if (wav.Length < 200)
        {
            return;
        }

        foreach (var (callId, _) in _activeCalls)
        {
            var request = new AudioChunkRequest
            {
                CallId = callId,
                AudioBase64 = Convert.ToBase64String(wav),
                ContentType = "audio/wav",
                Language = "en-US",
                Speaker = sourceBuffer.Speaker,
                Timestamp = DateTimeOffset.UtcNow.ToString("O"),
            };

            _ = Task.Run(async () =>
            {
                try
                {
                    await _teamsMediaService.ProcessAudioChunkAsync(request, wav, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to process {Speaker} chunk for call {CallId}.", sourceBuffer.Speaker, callId);
                }
            });
        }
    }

    private CaptureSourceBuffer? ResolveSourceBuffer(object? sender)
    {
        if (ReferenceEquals(sender, _loopbackDevice))
        {
            return _loopbackBuffer;
        }

        if (ReferenceEquals(sender, _micDevice))
        {
            return _micBuffer;
        }

        return null;
    }

    private static void ResetBuffer(CaptureSourceBuffer sourceBuffer)
    {
        sourceBuffer.WaveWriter?.Dispose();
        sourceBuffer.WaveWriter = null;
        sourceBuffer.CurrentBuffer = new MemoryStream();
        sourceBuffer.WaveWriter = new WaveFileWriter(sourceBuffer.CurrentBuffer, TargetFormat);
    }

    private static void DisposeBuffer(CaptureSourceBuffer? sourceBuffer)
    {
        if (sourceBuffer is null)
        {
            return;
        }

        lock (sourceBuffer.Sync)
        {
            sourceBuffer.WaveWriter?.Dispose();
            sourceBuffer.WaveWriter = null;
            sourceBuffer.CurrentBuffer.Dispose();
            sourceBuffer.CurrentBuffer = new MemoryStream();
        }
    }

    private int ResolveChunkIntervalMs()
    {
        var configured = _configuration["RECORDER_AUDIO_CHUNK_INTERVAL_MS"];
        if (!int.TryParse(configured, out var intervalMs))
        {
            return DefaultChunkIntervalMs;
        }

        return Math.Clamp(intervalMs, MinChunkIntervalMs, MaxChunkIntervalMs);
    }

    private bool ResolveCaptureEnabled(string key, bool defaultValue)
    {
        var configured = _configuration[key];
        if (string.IsNullOrWhiteSpace(configured))
        {
            return defaultValue;
        }

        var normalized = configured.Trim();
        return normalized.Equals("true", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("1", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        StopCapture();
    }
}

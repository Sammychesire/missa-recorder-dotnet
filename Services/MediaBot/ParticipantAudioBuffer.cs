using NAudio.Wave;

namespace Recorder.Api.Services.MediaBot;

/// <summary>
/// Accumulates raw 16 kHz / 16-bit / mono PCM for ONE participant and flushes it
/// as a complete WAV chunk on a timer. The media platform delivers ~20 ms frames,
/// which are far too small to transcribe individually, so we batch them — exactly
/// like MicrophoneCaptureService does for the loopback path.
/// </summary>
public sealed class ParticipantAudioBuffer
{
    // Must match what we request from the media platform (AudioFormat.Pcm16K).
    private static readonly WaveFormat TargetFormat = new(16000, 16, 1);

    private readonly object _sync = new();
    private MemoryStream _stream = new();
    private WaveFileWriter _writer;

    public ParticipantAudioBuffer(string speaker)
    {
        Speaker = speaker;
        _writer = new WaveFileWriter(_stream, TargetFormat);
    }

    /// <summary>Display name (or AAD id) of the speaker this buffer belongs to.</summary>
    public string Speaker { get; set; }

    public void Append(byte[] pcm, int offset, int count)
    {
        if (count <= 0) return;
        lock (_sync)
        {
            _writer.Write(pcm, offset, count);
        }
    }

    /// <summary>
    /// Returns the buffered audio as a WAV byte[] and resets the buffer.
    /// Returns null if there is not enough audio to bother transcribing.
    /// </summary>
    public byte[]? FlushWav(int minBytes = 4000)
    {
        lock (_sync)
        {
            _writer.Flush();
            var wav = _stream.ToArray();

            // start fresh
            _writer.Dispose();
            _stream = new MemoryStream();
            _writer = new WaveFileWriter(_stream, TargetFormat);

            // 44-byte header + a little audio; skip near-empty windows.
            return wav.Length >= 44 + minBytes ? wav : null;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _writer.Dispose();
            _stream.Dispose();
        }
    }
}

namespace Recorder.Api.Models;

public sealed class AudioChunkRequest
{
    public string CallId { get; set; } = string.Empty;
    public string AudioBase64 { get; set; } = string.Empty;
    public string ContentType { get; set; } = "audio/wav; codecs=audio/pcm; samplerate=16000";
    public string Language { get; set; } = "en-US";
    public string Speaker { get; set; } = "Meeting audio";
    public string? Timestamp { get; set; }
}

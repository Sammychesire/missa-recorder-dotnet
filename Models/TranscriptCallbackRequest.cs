namespace Recorder.Api.Models;

public sealed class TranscriptCallbackRequest
{
    public string CallId { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public string Speaker { get; set; } = "Meeting audio";
    public string Language { get; set; } = "en-US";
    public string Timestamp { get; set; } = DateTimeOffset.UtcNow.ToString("O");
    public bool IsFinal { get; set; } = true;
}

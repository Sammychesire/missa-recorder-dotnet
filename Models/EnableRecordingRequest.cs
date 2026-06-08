namespace Recorder.Api.Models;

public sealed class EnableRecordingRequest
{
    public string CallId { get; set; } = string.Empty;
    public string OrganizerId { get; set; } = string.Empty;
    public string JoinWebUrl { get; set; } = string.Empty;
    public string OnlineMeetingId { get; set; } = string.Empty;
    public string? MeetingSubject { get; set; }
    public string BotId { get; set; } = string.Empty;
    public string BotEndpoint { get; set; } = string.Empty;
}

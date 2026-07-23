namespace Recorder.Api.Models;

/// <summary>
/// Payload Node posts to POST /api/media-bot/join (via joinMediaBot()).
/// Mirrors EnableRecordingRequest plus the meeting context the media SDK needs
/// to join the call with application-hosted media.
/// </summary>
public sealed class MediaBotJoinRequest
{
    public string CallId { get; set; } = string.Empty;
    public string JoinWebUrl { get; set; } = string.Empty;
    public string OnlineMeetingId { get; set; } = string.Empty;
    public string? OrganizerId { get; set; }
    public string? TenantId { get; set; }
    public string? MeetingSubject { get; set; }

    /// <summary>Node's current public endpoint, used by the recorder callback (overrides BOT_ENDPOINT).</summary>
    public string? BotEndpoint { get; set; }
}

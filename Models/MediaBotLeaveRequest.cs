namespace Recorder.Api.Models;

/// <summary>Payload Node posts to POST /api/media-bot/leave (via leaveMediaBot()).</summary>
public sealed class MediaBotLeaveRequest
{
    public string CallId { get; set; } = string.Empty;
}

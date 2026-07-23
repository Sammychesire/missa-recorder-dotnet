using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Recorder.Api.Models;
using Recorder.Api.Services.MediaBot;

namespace Recorder.Api.Controllers;

/// <summary>
/// Endpoints for the application-hosted media bot:
///   POST /api/media-bot/join  — Node triggers a join (signed, same HMAC as the recorder)
///   POST /api/calls           — Microsoft Teams calling notifications (Graph-signed)
/// </summary>
[ApiController]
public sealed class MediaBotController : ControllerBase
{
    private readonly MediaBotService _mediaBot;
    private readonly IConfiguration _configuration;
    private readonly ILogger<MediaBotController> _logger;

    public MediaBotController(
        MediaBotService mediaBot,
        IConfiguration configuration,
        ILogger<MediaBotController> logger)
    {
        _mediaBot = mediaBot;
        _configuration = configuration;
        _logger = logger;
    }

    [HttpPost("api/media-bot/join")]
    public async Task<IActionResult> Join([FromBody] MediaBotJoinRequest request, CancellationToken cancellationToken)
    {
        var rawBody = await ReadRawBodyAsync(cancellationToken);
        if (!IsValidSignature(rawBody))
        {
            _logger.LogWarning("Rejected media-bot join for call {CallId}: invalid signature", request.CallId);
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "invalid_signature" });
        }

        if (string.IsNullOrWhiteSpace(request.CallId) ||
            string.IsNullOrWhiteSpace(request.JoinWebUrl) ||
            string.IsNullOrWhiteSpace(request.OnlineMeetingId))
        {
            return BadRequest(new { error = "missing_meeting_metadata" });
        }

        try
        {
            await _mediaBot.JoinAsync(request);
            return Ok(new { status = "ok", message = "Media bot joining" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Media bot join failed for call {CallId}", request.CallId);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "join_failed", message = ex.Message });
        }
    }

    [HttpPost("api/media-bot/leave")]
    public async Task<IActionResult> Leave([FromBody] MediaBotLeaveRequest request, CancellationToken cancellationToken)
    {
        var rawBody = await ReadRawBodyAsync(cancellationToken);
        if (!IsValidSignature(rawBody))
        {
            _logger.LogWarning("Rejected media-bot leave for call {CallId}: invalid signature", request.CallId);
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "invalid_signature" });
        }

        if (string.IsNullOrWhiteSpace(request.CallId))
        {
            return BadRequest(new { error = "missing_call_id" });
        }

        try
        {
            await _mediaBot.LeaveAsync(request.CallId);
            return Ok(new { status = "ok", message = "Media bot leaving" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Media bot leave failed for call {CallId}", request.CallId);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "leave_failed", message = ex.Message });
        }
    }

    /// <summary>Teams calling webhook. Validated by the media SDK's auth provider, not HMAC.</summary>
    [HttpPost("api/calls")]
    public async Task<IActionResult> Calls()
    {
        try
        {
            var status = await _mediaBot.ProcessNotificationAsync(Request);
            return StatusCode(status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process calling notification");
            return StatusCode(StatusCodes.Status500InternalServerError);
        }
    }

    private async Task<string> ReadRawBodyAsync(CancellationToken cancellationToken)
    {
        Request.Body.Position = 0;
        using var reader = new StreamReader(Request.Body, Encoding.UTF8, leaveOpen: true);
        var body = await reader.ReadToEndAsync(cancellationToken);
        Request.Body.Position = 0;
        return body;
    }

    // Same HMAC scheme as RecordingController: sha256 = HMAC(secret, "{timestamp}.{rawBody}")
    private bool IsValidSignature(string rawBody)
    {
        var sharedSecret = _configuration["RECORDER_SHARED_SECRET"] ?? _configuration["SECRET_RECORDER_SHARED_SECRET"];
        var timestamp = Request.Headers["X-Missa-Timestamp"].ToString();
        var signature = Request.Headers["X-Missa-Signature"].ToString();

        if (string.IsNullOrWhiteSpace(sharedSecret) ||
            string.IsNullOrWhiteSpace(timestamp) ||
            string.IsNullOrWhiteSpace(signature) ||
            !long.TryParse(timestamp, out var unixSeconds))
        {
            return false;
        }

        if (Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - unixSeconds) > 300)
        {
            return false;
        }

        var expected = "sha256=" + Convert.ToHexString(
            HMACSHA256.HashData(
                Encoding.UTF8.GetBytes(sharedSecret),
                Encoding.UTF8.GetBytes($"{timestamp}.{rawBody}")))
            .ToLowerInvariant();

        var incoming = signature.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase)
            ? signature
            : $"sha256={signature}";

        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var incomingBytes = Encoding.UTF8.GetBytes(incoming.ToLowerInvariant());
        return expectedBytes.Length == incomingBytes.Length &&
            CryptographicOperations.FixedTimeEquals(expectedBytes, incomingBytes);
    }
}

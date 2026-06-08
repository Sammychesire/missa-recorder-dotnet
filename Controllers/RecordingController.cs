using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Recorder.Api.Models;
using Recorder.Api.Services;

namespace Recorder.Api.Controllers;

[ApiController]
[Route("api/recordings")]
public sealed class RecordingController : ControllerBase
{
    private readonly IConfiguration _configuration;
    private readonly TeamsMediaService _teamsMediaService;
    private readonly IAppHostedMediaBridge _mediaBridge;
    private readonly AppHostedMediaReadinessService _mediaReadinessService;
    private readonly DebugPersistenceService _debugPersistenceService;
    private readonly ILogger<RecordingController> _logger;

    public RecordingController(
        IConfiguration configuration,
        TeamsMediaService teamsMediaService,
        IAppHostedMediaBridge mediaBridge,
        AppHostedMediaReadinessService mediaReadinessService,
        DebugPersistenceService debugPersistenceService,
        ILogger<RecordingController> logger)
    {
        _configuration = configuration;
        _teamsMediaService = teamsMediaService;
        _mediaBridge = mediaBridge;
        _mediaReadinessService = mediaReadinessService;
        _debugPersistenceService = debugPersistenceService;
        _logger = logger;
    }

    [HttpGet("media-readiness")]
    public IActionResult MediaReadiness()
    {
        return Ok(_mediaReadinessService.GetReadiness());
    }

    [HttpPost("enable")]
    public async Task<IActionResult> Enable([FromBody] EnableRecordingRequest request, CancellationToken cancellationToken)
    {
        var rawBody = await ReadRawBodyAsync(cancellationToken);
        if (!IsValidSignature(rawBody))
        {
            _logger.LogWarning("Rejected recorder enable request for call {CallId}: invalid signature", request.CallId);
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "invalid_signature" });
        }

        if (string.IsNullOrWhiteSpace(request.CallId) ||
            string.IsNullOrWhiteSpace(request.JoinWebUrl) ||
            string.IsNullOrWhiteSpace(request.OnlineMeetingId))
        {
            return BadRequest(new { error = "missing_meeting_metadata" });
        }

        await _debugPersistenceService.SaveEnableRequestAsync(request, cancellationToken);
        await _teamsMediaService.EnableRecordingAsync(request, cancellationToken);
        return Ok(new { status = "ok", message = "Recording enabled" });
    }

    [HttpPost("audio-chunk")]
    public async Task<IActionResult> AudioChunk([FromBody] AudioChunkRequest request, CancellationToken cancellationToken)
    {
        var rawBody = await ReadRawBodyAsync(cancellationToken);
        if (!IsValidSignature(rawBody))
        {
            _logger.LogWarning("Rejected recorder audio chunk for call {CallId}: invalid signature", request.CallId);
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "invalid_signature" });
        }

        if (string.IsNullOrWhiteSpace(request.CallId) || string.IsNullOrWhiteSpace(request.AudioBase64))
        {
            return BadRequest(new { error = "missing_audio_chunk" });
        }

        byte[] audioBytes;
        try
        {
            audioBytes = Convert.FromBase64String(request.AudioBase64);
        }
        catch (FormatException)
        {
            return BadRequest(new { error = "invalid_audio_base64" });
        }

        if (audioBytes.Length == 0)
        {
            return BadRequest(new { error = "empty_audio_chunk" });
        }

        var filePath = await _debugPersistenceService.SaveAudioChunkAsync(
            request.CallId,
            audioBytes,
            request.ContentType,
            request.Language,
            request.Speaker,
            cancellationToken);

        await _teamsMediaService.ProcessAudioChunkAsync(request, audioBytes, cancellationToken);

        return Ok(new { status = "ok", bytes = audioBytes.Length, filePath });
    }

    [HttpPost("media-frame")]
    public async Task<IActionResult> MediaFrame([FromBody] MediaFrameRequest request, CancellationToken cancellationToken)
    {
        var rawBody = await ReadRawBodyAsync(cancellationToken);
        if (!IsValidSignature(rawBody))
        {
            _logger.LogWarning("Rejected media frame for call {CallId}: invalid signature", request.CallId);
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "invalid_signature" });
        }

        if (string.IsNullOrWhiteSpace(request.CallId) || string.IsNullOrWhiteSpace(request.AudioBase64))
        {
            return BadRequest(new { error = "missing_media_frame" });
        }

        if (!_mediaBridge.IsCallRegistered(request.CallId))
        {
            return NotFound(new { error = "unknown_call", message = "Call must be enabled before media frames can be ingested." });
        }

        var accepted = await _mediaBridge.EnqueueFrameAsync(request, cancellationToken);
        if (!accepted)
        {
            return StatusCode(StatusCodes.Status429TooManyRequests, new { error = "media_queue_full" });
        }

        return Accepted(new { status = "queued" });
    }

    private async Task<string> ReadRawBodyAsync(CancellationToken cancellationToken)
    {
        Request.Body.Position = 0;
        using var reader = new StreamReader(Request.Body, Encoding.UTF8, leaveOpen: true);
        var body = await reader.ReadToEndAsync(cancellationToken);
        Request.Body.Position = 0;
        return body;
    }

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

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (Math.Abs(now - unixSeconds) > 300)
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

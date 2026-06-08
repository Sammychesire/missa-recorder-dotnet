using System.Collections.Concurrent;
using Recorder.Api.Models;

namespace Recorder.Api.Services;

public sealed class TeamsMediaService
{
    private enum RecorderMediaSource
    {
        Local,
        Teams,
        Hybrid
    }

    private readonly AzureSpeechTranscriptionService _speechTranscriptionService;
    private readonly IAppHostedMediaBridge _mediaBridge;
    private readonly AppHostedMediaReadinessService _mediaReadinessService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<TeamsMediaService> _logger;
    private MicrophoneCaptureService? _micCapture;
    private readonly ConcurrentDictionary<string, EnableRecordingRequest> _activeRecordings = new();
    private readonly ConcurrentDictionary<string, int> _chunkCounts = new();

    private static readonly TimeSpan[] NoMediaCheckpoints =
    [
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(45),
        TimeSpan.FromSeconds(90)
    ];

    public TeamsMediaService(
        AzureSpeechTranscriptionService speechTranscriptionService,
        IAppHostedMediaBridge mediaBridge,
        AppHostedMediaReadinessService mediaReadinessService,
        IConfiguration configuration,
        ILogger<TeamsMediaService> logger)
    {
        _speechTranscriptionService = speechTranscriptionService;
        _mediaBridge = mediaBridge;
        _mediaReadinessService = mediaReadinessService;
        _configuration = configuration;
        _logger = logger;
    }

    // Injected after construction to break the circular dependency.
    public void SetMicrophoneCaptureService(MicrophoneCaptureService micCapture)
    {
        _micCapture = micCapture;
    }

    public async Task EnableRecordingAsync(EnableRecordingRequest request, CancellationToken cancellationToken)
    {
        var mediaSource = ResolveMediaSource();
        _activeRecordings[request.CallId] = request;
        _chunkCounts[request.CallId] = 0;
        _mediaBridge.RegisterCall(request);

        _logger.LogInformation(
            "Recorder enabled for call {CallId}, online meeting {OnlineMeetingId}",
            request.CallId,
            request.OnlineMeetingId);
        _logger.LogInformation(
            "Recorder is awaiting media for call {CallId}. Active recordings: {ActiveRecordingCount}. mediaSource={MediaSource}. App-hosted Teams frames can be pushed to /api/recordings/media-frame and will be forwarded to /api/recordings/audio-chunk.",
            request.CallId,
            _activeRecordings.Count,
            mediaSource);

        _ = WarnIfNoMediaAsync(request.CallId, mediaSource);

        if (mediaSource is RecorderMediaSource.Local or RecorderMediaSource.Hybrid)
        {
            _micCapture?.RegisterCall(request);
        }
        else
        {
            _logger.LogInformation(
                "Local Windows audio capture is disabled for call {CallId} because RECORDER_MEDIA_SOURCE=teams. Waiting only for Teams/app-hosted media frames.",
                request.CallId);
            _logger.LogInformation(
                "Teams media readiness for call {CallId}: {@Readiness}",
                request.CallId,
                _mediaReadinessService.GetReadiness());
        }

        await _speechTranscriptionService.StartAsync(request, cancellationToken);
    }

    public async Task ProcessAudioChunkAsync(
        AudioChunkRequest request,
        byte[] audioBytes,
        CancellationToken cancellationToken)
    {
        if (!_activeRecordings.TryGetValue(request.CallId, out var recording))
        {
            _logger.LogWarning(
                "Received audio chunk for unknown call {CallId}. Enable recording first.",
                request.CallId);
            return;
        }

        var chunkNumber = _chunkCounts.AddOrUpdate(request.CallId, 1, (_, current) => current + 1);
        if (chunkNumber == 1)
        {
            _logger.LogInformation(
                "First media chunk received for call {CallId}. speaker={Speaker}, contentType={ContentType}",
                request.CallId,
                string.IsNullOrWhiteSpace(request.Speaker) ? "unknown" : request.Speaker,
                string.IsNullOrWhiteSpace(request.ContentType) ? "unknown" : request.ContentType);
        }

        await _speechTranscriptionService.ProcessAudioChunkAsync(
            recording,
            audioBytes,
            request.ContentType,
            request.Language,
            request.Speaker,
            cancellationToken);
    }

    private async Task WarnIfNoMediaAsync(string callId, RecorderMediaSource mediaSource)
    {
        foreach (var delay in NoMediaCheckpoints)
        {
            await Task.Delay(delay);

            if (!_activeRecordings.ContainsKey(callId))
            {
                return;
            }

            var chunksReceived = _chunkCounts.GetValueOrDefault(callId, 0);
            if (chunksReceived > 0)
            {
                return;
            }

            if (mediaSource is RecorderMediaSource.Teams)
            {
                _logger.LogWarning(
                    "No Teams/app-hosted media frames received for call {CallId} after {ElapsedSeconds}s. RECORDER_MEDIA_SOURCE=teams requires a real Teams media producer; local Windows capture is intentionally disabled.",
                    callId,
                    (int)delay.TotalSeconds);
            }
            else
            {
                _logger.LogWarning(
                    "No media chunks received for call {CallId} after {ElapsedSeconds}s. Recorder enable succeeded, but no local capture or app-hosted media producer has delivered audio.",
                    callId,
                    (int)delay.TotalSeconds);
            }
        }
    }

    public Task DisableRecordingAsync(string callId)
    {
        _activeRecordings.TryRemove(callId, out _);
        _chunkCounts.TryRemove(callId, out _);
        _micCapture?.UnregisterCall(callId);
        return Task.CompletedTask;
    }

    private RecorderMediaSource ResolveMediaSource()
    {
        var configured = _configuration["RECORDER_MEDIA_SOURCE"];
        if (string.IsNullOrWhiteSpace(configured))
        {
            return RecorderMediaSource.Local;
        }

        return configured.Trim().ToLowerInvariant() switch
        {
            "teams" or "app-hosted" or "apphosted" => RecorderMediaSource.Teams,
            "hybrid" or "both" => RecorderMediaSource.Hybrid,
            _ => RecorderMediaSource.Local
        };
    }
}

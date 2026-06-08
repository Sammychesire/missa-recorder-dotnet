using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Recorder.Api.Models;

namespace Recorder.Api.Services;

public interface IAppHostedMediaBridge
{
    void RegisterCall(EnableRecordingRequest request);
    bool IsCallRegistered(string callId);
    ValueTask<bool> EnqueueFrameAsync(MediaFrameRequest request, CancellationToken cancellationToken);
}

public sealed class AppHostedMediaBridgeService : BackgroundService, IAppHostedMediaBridge
{
    private readonly ConcurrentDictionary<string, EnableRecordingRequest> _activeCalls = new();
    private readonly Channel<MediaFrameRequest> _queue = Channel.CreateBounded<MediaFrameRequest>(
        new BoundedChannelOptions(1024)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest,
        });
    private readonly IConfiguration _configuration;
    private readonly ILogger<AppHostedMediaBridgeService> _logger;
    private readonly HttpClient _httpClient;

    public AppHostedMediaBridgeService(
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        ILogger<AppHostedMediaBridgeService> logger)
    {
        _configuration = configuration;
        _logger = logger;
        _httpClient = httpClientFactory.CreateClient(nameof(AppHostedMediaBridgeService));
    }

    public void RegisterCall(EnableRecordingRequest request)
    {
        _activeCalls[request.CallId] = request;
        _logger.LogInformation("App-hosted media bridge registered call {CallId}", request.CallId);
    }

    public bool IsCallRegistered(string callId) => _activeCalls.ContainsKey(callId);

    public async ValueTask<bool> EnqueueFrameAsync(MediaFrameRequest request, CancellationToken cancellationToken)
    {
        if (!await _queue.Writer.WaitToWriteAsync(cancellationToken))
        {
            return false;
        }

        return _queue.Writer.TryWrite(request);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("App-hosted media bridge worker started");
        while (!stoppingToken.IsCancellationRequested)
        {
            MediaFrameRequest frame;
            try
            {
                frame = await _queue.Reader.ReadAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (!_activeCalls.ContainsKey(frame.CallId))
            {
                _logger.LogDebug("Dropped media frame for unknown call {CallId}", frame.CallId);
                continue;
            }

            await ForwardFrameAsChunkAsync(frame, stoppingToken);
        }
    }

    private async Task ForwardFrameAsChunkAsync(MediaFrameRequest frame, CancellationToken cancellationToken)
    {
        var chunkRequest = new AudioChunkRequest
        {
            CallId = frame.CallId,
            AudioBase64 = frame.AudioBase64,
            ContentType = string.IsNullOrWhiteSpace(frame.ContentType)
                ? "audio/wav; codecs=audio/pcm; samplerate=16000"
                : frame.ContentType,
            Language = string.IsNullOrWhiteSpace(frame.Language) ? "en-US" : frame.Language,
            Speaker = string.IsNullOrWhiteSpace(frame.Speaker) ? "Meeting audio" : frame.Speaker,
            Timestamp = frame.Timestamp ?? DateTimeOffset.UtcNow.ToString("O"),
        };

        var body = JsonSerializer.Serialize(chunkRequest);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var signature = CreateSignature(timestamp, body);
        if (string.IsNullOrWhiteSpace(signature))
        {
            _logger.LogWarning("Dropped media frame for call {CallId}: RECORDER_SHARED_SECRET missing", frame.CallId);
            return;
        }

        var target = GetChunkEndpoint();
        using var request = new HttpRequestMessage(HttpMethod.Post, target)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-Missa-Timestamp", timestamp);
        request.Headers.Add("X-Missa-Signature", signature);

        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var failure = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning(
                    "Media frame forward failed for call {CallId}. status={StatusCode} body={Body}",
                    frame.CallId,
                    (int)response.StatusCode,
                    failure);
                return;
            }

            _logger.LogDebug("Forwarded media frame to chunk endpoint for call {CallId}", frame.CallId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error forwarding media frame for call {CallId}", frame.CallId);
        }
    }

    private string GetChunkEndpoint()
    {
        var configured = _configuration["RECORDER_INTERNAL_CHUNK_URL"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        var urls = _configuration["ASPNETCORE_URLS"];
        if (!string.IsNullOrWhiteSpace(urls))
        {
            var first = urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(first))
            {
                return $"{first.TrimEnd('/')}/api/recordings/audio-chunk";
            }
        }

        return "http://127.0.0.1:5000/api/recordings/audio-chunk";
    }

    private string CreateSignature(string timestamp, string body)
    {
        var sharedSecret = _configuration["RECORDER_SHARED_SECRET"] ?? _configuration["SECRET_RECORDER_SHARED_SECRET"];
        if (string.IsNullOrWhiteSpace(sharedSecret))
        {
            return string.Empty;
        }

        var payload = $"{timestamp}.{body}";
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(sharedSecret), Encoding.UTF8.GetBytes(payload));
        return $"sha256={Convert.ToHexString(hash).ToLowerInvariant()}";
    }
}
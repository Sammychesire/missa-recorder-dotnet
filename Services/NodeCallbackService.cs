using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Recorder.Api.Models;

namespace Recorder.Api.Services;

public sealed class NodeCallbackService
{
    private readonly IConfiguration _configuration;
    private readonly HttpClient _httpClient;
    private readonly ILogger<NodeCallbackService> _logger;

    public NodeCallbackService(
        IConfiguration configuration,
        HttpClient httpClient,
        ILogger<NodeCallbackService> logger)
    {
        _configuration = configuration;
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task PostTranscriptAsync(
        TranscriptCallbackRequest request,
        string? botEndpointOverride,
        CancellationToken cancellationToken)
    {
        var botEndpoint = (botEndpointOverride ?? _configuration["BOT_ENDPOINT"] ?? string.Empty).TrimEnd('/');
        if (string.IsNullOrWhiteSpace(botEndpoint))
        {
            throw new InvalidOperationException("BOT_ENDPOINT is not configured.");
        }

        var sharedSecret = _configuration["RECORDER_SHARED_SECRET"] ?? _configuration["SECRET_RECORDER_SHARED_SECRET"] ?? string.Empty;
        if (string.IsNullOrWhiteSpace(sharedSecret))
        {
            throw new InvalidOperationException("RECORDER_SHARED_SECRET is not configured.");
        }
        var token = Convert.ToHexString(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(sharedSecret), Array.Empty<byte>()))
            .ToLowerInvariant();

        var url = $"{botEndpoint}/api/botAudioTranscription";
        using var message = new HttpRequestMessage(HttpMethod.Post, url);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        message.Content = new StringContent(
            JsonSerializer.Serialize(request, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            Encoding.UTF8,
            "application/json");

        using var response = await _httpClient.SendAsync(message, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogWarning(
                "Transcript callback failed with status {StatusCode}: {ResponseBody}",
                (int)response.StatusCode,
                responseBody);
            response.EnsureSuccessStatusCode();
        }
    }
}

using Recorder.Api.Models;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Recorder.Api.Services;

public sealed class AzureSpeechTranscriptionService
{
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly NodeCallbackService _nodeCallbackService;
    private readonly DebugPersistenceService _debugPersistenceService;
    private readonly ILogger<AzureSpeechTranscriptionService> _logger;

    public AzureSpeechTranscriptionService(
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        NodeCallbackService nodeCallbackService,
        DebugPersistenceService debugPersistenceService,
        ILogger<AzureSpeechTranscriptionService> logger)
    {
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
        _nodeCallbackService = nodeCallbackService;
        _debugPersistenceService = debugPersistenceService;
        _logger = logger;
    }

    public async Task StartAsync(EnableRecordingRequest request, CancellationToken cancellationToken)
    {
        var speechKey = ResolveSpeechKey();
        var speechRegion = ResolveSpeechRegion();
        var speechKeyConfigured = !string.IsNullOrWhiteSpace(speechKey);
        var speechRegionConfigured = !string.IsNullOrWhiteSpace(speechRegion);

        _logger.LogInformation(
            "Azure Speech transcript pipeline ready for call {CallId}. keyConfigured={SpeechKeyConfigured}, regionConfigured={SpeechRegionConfigured}",
            request.CallId,
            speechKeyConfigured,
            speechRegionConfigured);

        await Task.CompletedTask;
    }

    public async Task PublishRecognizedTextAsync(
        EnableRecordingRequest recording,
        string text,
        string speaker = "Meeting audio",
        string language = "en-US",
        CancellationToken cancellationToken = default)
    {
        var callback = new TranscriptCallbackRequest
        {
            CallId = recording.CallId,
            Text = text,
            Speaker = speaker,
            Language = language,
            Timestamp = DateTimeOffset.UtcNow.ToString("O"),
            IsFinal = true
        };

        await _debugPersistenceService.SaveTranscriptAsync(callback, "azure_speech_recognized", cancellationToken);
        await _nodeCallbackService.PostTranscriptAsync(callback, recording.BotEndpoint, cancellationToken);
    }

    public async Task ProcessAudioChunkAsync(
        EnableRecordingRequest recording,
        byte[] audioBytes,
        string? contentType,
        string? language,
        string? speaker,
        CancellationToken cancellationToken)
    {
        var speechKey = ResolveSpeechKey();
        var recognitionUrl = BuildRecognitionUrl(language);
        if (string.IsNullOrWhiteSpace(speechKey) || string.IsNullOrWhiteSpace(recognitionUrl))
        {
            _logger.LogWarning(
                "Skipping speech recognition for call {CallId}. speechKeyConfigured={SpeechKeyConfigured}, speechUrlConfigured={SpeechUrlConfigured}",
                recording.CallId,
                !string.IsNullOrWhiteSpace(speechKey),
                !string.IsNullOrWhiteSpace(recognitionUrl));
            return;
        }

        var client = _httpClientFactory.CreateClient(nameof(AzureSpeechTranscriptionService));
        using var request = new HttpRequestMessage(HttpMethod.Post, recognitionUrl);
        request.Headers.Add("Ocp-Apim-Subscription-Key", speechKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = new ByteArrayContent(audioBytes);
        var normalizedContentType = NormalizeContentType(contentType);
        if (!MediaTypeHeaderValue.TryParse(normalizedContentType, out var mediaTypeHeaderValue))
        {
            mediaTypeHeaderValue = new MediaTypeHeaderValue("audio/wav");
        }
        request.Content.Headers.ContentType = mediaTypeHeaderValue;

        using var response = await client.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "Azure Speech recognition request failed for call {CallId}. status={StatusCode}, body={ResponseBody}",
                recording.CallId,
                (int)response.StatusCode,
                Truncate(responseBody, 400));
            return;
        }

        var recognizedText = ExtractRecognizedText(responseBody);
        if (string.IsNullOrWhiteSpace(recognizedText))
        {
            _logger.LogInformation(
                "Azure Speech returned no recognized text for call {CallId}.",
                recording.CallId);
            return;
        }

        _logger.LogInformation(
            "Azure Speech recognized text for call {CallId}. chars={RecognizedLength}",
            recording.CallId,
            recognizedText.Length);

        await PublishRecognizedTextAsync(
            recording,
            recognizedText,
            speaker: string.IsNullOrWhiteSpace(speaker) ? "Meeting audio" : speaker,
            language: string.IsNullOrWhiteSpace(language) ? "en-US" : language,
            cancellationToken: cancellationToken);
    }

    private string? ResolveSpeechKey()
    {
        return FirstNonEmpty(
            _configuration["AZURE_SPEECH_KEY"],
            _configuration["COGNITIVE_SERVICES_KEY"],
            _configuration["SECRET_COGNITIVE_SERVICES_KEY"]);
    }

    private string? ResolveSpeechRegion()
    {
        var explicitRegion = FirstNonEmpty(
            _configuration["AZURE_SPEECH_REGION"],
            _configuration["COGNITIVE_SERVICES_REGION"]);

        if (!string.IsNullOrWhiteSpace(explicitRegion))
        {
            return explicitRegion;
        }

        var endpoint = FirstNonEmpty(
            _configuration["COGNITIVE_SERVICES_ENDPOINT"],
            _configuration["AZURE_SPEECH_ENDPOINT"]);

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri))
        {
            return null;
        }

        // Cognitive Services endpoints are typically "<region>.api.cognitive.microsoft.com".
        var hostParts = endpointUri.Host.Split('.', StringSplitOptions.RemoveEmptyEntries);
        return hostParts.Length > 0 ? hostParts[0] : null;
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private string? BuildRecognitionUrl(string? language)
    {
        var endpoint = FirstNonEmpty(
            _configuration["AZURE_SPEECH_ENDPOINT"],
            _configuration["COGNITIVE_SERVICES_ENDPOINT"]);

        if (string.IsNullOrWhiteSpace(endpoint))
        {
            var region = ResolveSpeechRegion();
            if (string.IsNullOrWhiteSpace(region))
            {
                return null;
            }

            endpoint = $"https://{region}.stt.speech.microsoft.com";
        }

        var normalizedLanguage = string.IsNullOrWhiteSpace(language) ? "en-US" : language;
        var trimmed = endpoint.Trim().TrimEnd('/');

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var parsed) &&
            parsed.Host.EndsWith(".api.cognitive.microsoft.com", StringComparison.OrdinalIgnoreCase))
        {
            var region = ResolveSpeechRegion();
            if (!string.IsNullOrWhiteSpace(region))
            {
                trimmed = $"https://{region}.stt.speech.microsoft.com";
            }
        }

        if (trimmed.Contains("/speech/recognition/conversation/cognitiveservices/v1", StringComparison.OrdinalIgnoreCase))
        {
            return $"{trimmed}?language={Uri.EscapeDataString(normalizedLanguage)}";
        }

        return $"{trimmed}/speech/recognition/conversation/cognitiveservices/v1?language={Uri.EscapeDataString(normalizedLanguage)}";
    }

    private static string NormalizeContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return "audio/wav";
        }

        var lowered = contentType.ToLowerInvariant();
        if (lowered.Contains("audio/wav") || lowered.Contains("audio/x-wav"))
        {
            return "audio/wav";
        }

        return contentType;
    }

    private static string ExtractRecognizedText(string responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return string.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(responseBody);
            var root = document.RootElement;

            if (root.TryGetProperty("DisplayText", out var displayText) && displayText.ValueKind == JsonValueKind.String)
            {
                return displayText.GetString() ?? string.Empty;
            }

            if (root.TryGetProperty("Text", out var text) && text.ValueKind == JsonValueKind.String)
            {
                return text.GetString() ?? string.Empty;
            }
        }
        catch
        {
            // Best-effort parse.
        }

        return string.Empty;
    }

    private static string Truncate(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        return value[..maxLength] + "...";
    }
}

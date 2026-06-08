using System.Text.Json;
using Recorder.Api.Models;

namespace Recorder.Api.Services;

public sealed class DebugPersistenceService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<DebugPersistenceService> _logger;

    public DebugPersistenceService(
        IConfiguration configuration,
        ILogger<DebugPersistenceService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<string> SaveEnableRequestAsync(EnableRecordingRequest request, CancellationToken cancellationToken)
    {
        var callDir = await EnsureCallDirectoryAsync(request.CallId, cancellationToken);
        var filePath = Path.Combine(callDir, $"{Stamp()}_enable.json");
        await File.WriteAllTextAsync(
            filePath,
            JsonSerializer.Serialize(request, new JsonSerializerOptions { WriteIndented = true }),
            cancellationToken);
        await AppendManifestAsync(request.CallId, new
        {
            eventName = "enable_received",
            request.OnlineMeetingId,
            request.MeetingSubject,
            filePath
        }, cancellationToken);
        _logger.LogInformation("Saved recorder enable request for call {CallId} to {FilePath}", request.CallId, filePath);
        return filePath;
    }

    public async Task<string> SaveAudioChunkAsync(
        string callId,
        byte[] audioBytes,
        string? contentType,
        string? language,
        string? speaker,
        CancellationToken cancellationToken)
    {
        var callDir = await EnsureCallDirectoryAsync(callId, cancellationToken);
        var chunkNumber = Directory.EnumerateFiles(callDir, "*_chunk_*.*").Count() + 1;
        var extension = (contentType ?? string.Empty).Contains("wav", StringComparison.OrdinalIgnoreCase)
            ? "wav"
            : "bin";
        var filePath = Path.Combine(callDir, $"{Stamp()}_chunk_{chunkNumber:00000}.{extension}");
        await File.WriteAllBytesAsync(filePath, audioBytes, cancellationToken);
        await AppendManifestAsync(callId, new
        {
            eventName = "audio_chunk_received",
            chunkNumber,
            bytes = audioBytes.Length,
            contentType,
            language,
            speaker,
            filePath
        }, cancellationToken);
        _logger.LogInformation("Saved recorder audio chunk {ChunkNumber} for call {CallId}: {Bytes} bytes at {FilePath}", chunkNumber, callId, audioBytes.Length, filePath);
        return filePath;
    }

    public async Task<string> SaveTranscriptAsync(TranscriptCallbackRequest request, string source, CancellationToken cancellationToken)
    {
        var callDir = await EnsureCallDirectoryAsync(request.CallId, cancellationToken);
        var filePath = Path.Combine(callDir, $"{Stamp()}_{SafeSegment(source, "transcript")}.txt");
        var body = string.Join(Environment.NewLine, new[]
        {
            $"callId: {request.CallId}",
            $"source: {source}",
            $"speaker: {request.Speaker}",
            $"language: {request.Language}",
            $"isFinal: {request.IsFinal}",
            $"timestamp: {request.Timestamp}",
            string.Empty,
            request.Text,
            string.Empty
        });
        await File.WriteAllTextAsync(filePath, body, cancellationToken);
        await AppendManifestAsync(request.CallId, new
        {
            eventName = "transcript_text_received",
            source,
            request.Speaker,
            request.Language,
            request.IsFinal,
            textLength = request.Text.Length,
            filePath
        }, cancellationToken);
        _logger.LogInformation("Saved recorder transcript text for call {CallId}: {TextLength} chars at {FilePath}", request.CallId, request.Text.Length, filePath);
        return filePath;
    }

    private async Task<string> EnsureCallDirectoryAsync(string callId, CancellationToken cancellationToken)
    {
        var root = _configuration["RECORDER_DEBUG_DIR"];
        if (string.IsNullOrWhiteSpace(root))
        {
            root = Path.Combine(AppContext.BaseDirectory, "recorder_debug");
        }

        var callDir = Path.Combine(root, SafeSegment(callId, "unknown_call"));
        Directory.CreateDirectory(callDir);
        await Task.CompletedTask.WaitAsync(cancellationToken);
        return callDir;
    }

    private async Task AppendManifestAsync(string callId, object eventData, CancellationToken cancellationToken)
    {
        var callDir = await EnsureCallDirectoryAsync(callId, cancellationToken);
        var manifestPath = Path.Combine(callDir, "manifest.jsonl");
        var line = JsonSerializer.Serialize(new
        {
            capturedAt = DateTimeOffset.UtcNow.ToString("O"),
            callId,
            eventData
        });
        await File.AppendAllTextAsync(manifestPath, line + Environment.NewLine, cancellationToken);
    }

    private static string Stamp() => DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ");

    private static string SafeSegment(string value, string fallback)
    {
        var chars = value
            .Where(c => char.IsAsciiLetterOrDigit(c) || c == '_' || c == '-')
            .Take(80)
            .ToArray();
        return chars.Length == 0 ? fallback : new string(chars);
    }
}

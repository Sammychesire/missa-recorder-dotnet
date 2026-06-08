using System.Runtime.InteropServices;

namespace Recorder.Api.Services;

public sealed class AppHostedMediaReadinessService
{
    private readonly IConfiguration _configuration;

    private static readonly string[] RequiredTeamsModeSettings =
    [
        "RECORDER_SHARED_SECRET",
        "BOT_ENDPOINT",
        "AZURE_SPEECH_KEY",
        "AZURE_SPEECH_REGION",
        "MICROSOFT_APP_ID",
        "MICROSOFT_APP_PASSWORD",
        "MICROSOFT_APP_TENANT_ID"
    ];

    public AppHostedMediaReadinessService(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public object GetReadiness()
    {
        var mediaSource = _configuration["RECORDER_MEDIA_SOURCE"] ?? "local";
        var missingSettings = RequiredTeamsModeSettings
            .Where(key => string.IsNullOrWhiteSpace(_configuration[key]) &&
                !(key == "RECORDER_SHARED_SECRET" && !string.IsNullOrWhiteSpace(_configuration["SECRET_RECORDER_SHARED_SECRET"])))
            .ToArray();

        var warnings = new List<string>();
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            warnings.Add("Application-hosted Teams media requires Windows Server/Windows hosting.");
        }

        if (!IsTruthy(_configuration["RECORDER_APP_HOSTED_MEDIA_PRODUCER_ENABLED"]))
        {
            warnings.Add("No Graph Communications media producer is enabled in this recorder. A separate media bot must post real Teams frames to /api/recordings/media-frame.");
        }

        if (string.Equals(mediaSource, "teams", StringComparison.OrdinalIgnoreCase) && missingSettings.Length > 0)
        {
            warnings.Add("RECORDER_MEDIA_SOURCE=teams is configured, but required recorder settings are missing.");
        }

        return new
        {
            mediaSource,
            windowsHost = RuntimeInformation.IsOSPlatform(OSPlatform.Windows),
            osDescription = RuntimeInformation.OSDescription,
            processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            appHostedMediaProducerEnabled = IsTruthy(_configuration["RECORDER_APP_HOSTED_MEDIA_PRODUCER_ENABLED"]),
            mediaFrameReceiverEndpoint = "/api/recordings/media-frame",
            requiredGraphApplicationPermissions = new[]
            {
                "Calls.AccessMedia.All",
                "Calls.JoinGroupCall.All"
            },
            requiredAzureHosting = new[]
            {
                "Windows Server guest OS",
                "Azure VM, VMSS, Service Fabric, or AKS with Windows nodes",
                "Public HTTPS signaling endpoint",
                "Instance-level public IP / media-reachable VM networking for application-hosted media"
            },
            missingSettings,
            warnings
        };
    }

    private static bool IsTruthy(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim();
        return normalized.Equals("true", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("1", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("on", StringComparison.OrdinalIgnoreCase);
    }
}

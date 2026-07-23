namespace Recorder.Api.Services.MediaBot;

/// <summary>
/// Configuration for the application-hosted media bot. Read from environment /
/// appsettings. The media-platform settings (cert, ports, FQDN) are the part that
/// MUST be correct for media to connect — see MEDIABOT_SETUP.md.
/// </summary>
public sealed class MediaBotOptions
{
    /// <summary>Whether the media bot is enabled at all (MEDIA_BOT_ENABLED).</summary>
    public bool Enabled { get; set; }

    // ----- Bot identity (same Entra app as the Node bot) -----
    public string AppId { get; set; } = string.Empty;          // MICROSOFT_APP_ID
    public string AppSecret { get; set; } = string.Empty;      // MICROSOFT_APP_PASSWORD
    public string TenantId { get; set; } = string.Empty;       // MICROSOFT_APP_TENANT_ID

    // ----- Public media-platform settings (the hard part) -----
    /// <summary>Public FQDN the certificate is issued for, e.g. missa-media-bot-aueast.australiaeast.cloudapp.azure.com</summary>
    public string ServiceFqdn { get; set; } = string.Empty;    // MEDIA_BOT_SERVICE_FQDN
    /// <summary>Thumbprint of the TLS cert installed in LocalMachine\My for ServiceFqdn.</summary>
    public string CertificateThumbprint { get; set; } = string.Empty; // MEDIA_BOT_CERT_THUMBPRINT
    /// <summary>Public TCP media port (must be open on NSG + firewall), e.g. 8445.</summary>
    public int MediaInstancePublicPort { get; set; } = 8445;   // MEDIA_BOT_MEDIA_PORT
    /// <summary>Local port the media platform binds (usually same as public for a single instance).</summary>
    public int MediaInstanceInternalPort { get; set; } = 8445; // MEDIA_BOT_MEDIA_INTERNAL_PORT

    /// <summary>Public HTTPS notification (calling webhook) URL: https://&lt;fqdn&gt;:&lt;port&gt;/api/calls</summary>
    public string CallingNotificationUrl { get; set; } = string.Empty; // MEDIA_BOT_NOTIFICATION_URL

    /// <summary>IP the media platform binds to (the VM's local NIC IPv4). Auto-detected if empty.</summary>
    public string PublicIpAddress { get; set; } = string.Empty;        // MEDIA_BOT_PUBLIC_IP

    public static MediaBotOptions FromConfiguration(IConfiguration cfg)
    {
        bool ParseBool(string? v) =>
            !string.IsNullOrWhiteSpace(v) &&
            (v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1" ||
             v.Equals("yes", StringComparison.OrdinalIgnoreCase));

        int ParseInt(string? v, int fallback) => int.TryParse(v, out var i) ? i : fallback;

        return new MediaBotOptions
        {
            Enabled = ParseBool(cfg["MEDIA_BOT_ENABLED"]),
            AppId = cfg["MICROSOFT_APP_ID"] ?? cfg["CLIENT_ID"] ?? string.Empty,
            AppSecret = cfg["MICROSOFT_APP_PASSWORD"] ?? cfg["CLIENT_SECRET"] ?? string.Empty,
            TenantId = cfg["MICROSOFT_APP_TENANT_ID"] ?? cfg["TENANT_ID"] ?? string.Empty,
            ServiceFqdn = cfg["MEDIA_BOT_SERVICE_FQDN"] ?? string.Empty,
            CertificateThumbprint = cfg["MEDIA_BOT_CERT_THUMBPRINT"] ?? string.Empty,
            MediaInstancePublicPort = ParseInt(cfg["MEDIA_BOT_MEDIA_PORT"], 8445),
            MediaInstanceInternalPort = ParseInt(cfg["MEDIA_BOT_MEDIA_INTERNAL_PORT"], ParseInt(cfg["MEDIA_BOT_MEDIA_PORT"], 8445)),
            CallingNotificationUrl = cfg["MEDIA_BOT_NOTIFICATION_URL"] ?? string.Empty,
            PublicIpAddress = cfg["MEDIA_BOT_PUBLIC_IP"] ?? string.Empty,
        };
    }

    public IEnumerable<string> Validate()
    {
        if (string.IsNullOrWhiteSpace(AppId)) yield return "MICROSOFT_APP_ID is required.";
        if (string.IsNullOrWhiteSpace(AppSecret)) yield return "MICROSOFT_APP_PASSWORD is required.";
        if (string.IsNullOrWhiteSpace(TenantId)) yield return "MICROSOFT_APP_TENANT_ID is required.";
        if (string.IsNullOrWhiteSpace(ServiceFqdn)) yield return "MEDIA_BOT_SERVICE_FQDN is required.";
        if (string.IsNullOrWhiteSpace(CertificateThumbprint)) yield return "MEDIA_BOT_CERT_THUMBPRINT is required.";
        if (string.IsNullOrWhiteSpace(CallingNotificationUrl)) yield return "MEDIA_BOT_NOTIFICATION_URL is required.";
    }
}

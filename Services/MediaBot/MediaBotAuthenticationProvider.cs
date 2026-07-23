using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Graph.Communications.Client.Authentication;

namespace Recorder.Api.Services.MediaBot;

/// <summary>
/// Auth provider the Graph Communications client uses to (a) sign outbound calls to
/// Graph with an app token, and (b) validate inbound notifications on /api/calls.
///
/// NOTE: inbound validation here is intentionally minimal to get the pipeline running.
/// For production you should validate the Graph-signed JWT (issuer, audience=AppId,
/// signing keys from the Graph OpenID config). Marked with TODO below.
/// </summary>
public sealed class MediaBotAuthenticationProvider : IRequestAuthenticationProvider
{
    private readonly MediaBotOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<MediaBotAuthenticationProvider> _logger;

    private sealed record CachedToken(string Token, DateTimeOffset ExpiresAt);
    private readonly ConcurrentDictionary<string, CachedToken> _tokenCache = new();

    public MediaBotAuthenticationProvider(
        MediaBotOptions options,
        IHttpClientFactory httpClientFactory,
        ILogger<MediaBotAuthenticationProvider> logger)
    {
        _options = options;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task AuthenticateOutboundRequestAsync(HttpRequestMessage request, string tenant)
    {
        var tenantId = string.IsNullOrWhiteSpace(tenant) ? _options.TenantId : tenant;
        var token = await GetAppTokenAsync(tenantId);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    public Task<RequestValidationResult> ValidateInboundRequestAsync(HttpRequestMessage request)
    {
        // TODO (production): validate the inbound Graph token:
        //   - bearer present in Authorization header
        //   - signature verified against Graph's OpenID signing keys
        //   - audience == _options.AppId, issuer is the Graph calling service
        // For now accept and surface the configured tenant so the SDK can route.
        return Task.FromResult(new RequestValidationResult { IsValid = true, TenantId = _options.TenantId });
    }

    private async Task<string> GetAppTokenAsync(string tenantId)
    {
        if (_tokenCache.TryGetValue(tenantId, out var cached) &&
            cached.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(2))
        {
            return cached.Token;
        }

        var client = _httpClientFactory.CreateClient(nameof(MediaBotAuthenticationProvider));
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = _options.AppId,
            ["client_secret"] = _options.AppSecret,
            ["scope"] = "https://graph.microsoft.com/.default",
            ["grant_type"] = "client_credentials",
        });

        var url = $"https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/token";
        using var resp = await client.PostAsync(url, form);
        var body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogError("Failed to acquire app token for tenant {Tenant}: {Body}", tenantId, body);
            resp.EnsureSuccessStatusCode();
        }

        using var doc = JsonDocument.Parse(body);
        var token = doc.RootElement.GetProperty("access_token").GetString()!;
        var expiresIn = doc.RootElement.TryGetProperty("expires_in", out var exp) ? exp.GetInt32() : 3600;
        _tokenCache[tenantId] = new CachedToken(token, DateTimeOffset.UtcNow.AddSeconds(expiresIn));
        return token;
    }
}

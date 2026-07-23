using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Graph.Models;
using Microsoft.Graph.Communications.Calls;
using Microsoft.Graph.Communications.Calls.Media;
using Microsoft.Graph.Communications.Client;
using Microsoft.Skype.Bots.Media;
using Recorder.Api.Models;

namespace Recorder.Api.Services.MediaBot;

/// <summary>
/// Owns the Graph Communications client (the real-time media platform) and joins
/// meetings on demand. One instance per process; one CallHandler per active call.
///
/// "// [SDK]" marks calls whose exact shape may differ by SDK version — verify on
/// first build against the restored packages.
/// </summary>
public sealed class MediaBotService
{
    private readonly MediaBotOptions _options;
    private readonly MediaBotAuthenticationProvider _authProvider;
    private readonly TeamsMediaService _recorder;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<MediaBotService> _logger;

    private readonly ConcurrentDictionary<string, CallHandler> _handlers = new();
    private readonly ConcurrentDictionary<string, ICall> _calls = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _joinedAt = new();
    private readonly ConcurrentDictionary<string, bool> _sawHuman = new();
    private static readonly TimeSpan AutoLeaveGrace = TimeSpan.FromSeconds(30);
    private ICommunicationsClient? _client;
    private readonly object _clientLock = new();

    public MediaBotService(
        MediaBotOptions options,
        MediaBotAuthenticationProvider authProvider,
        TeamsMediaService recorder,
        ILoggerFactory loggerFactory)
    {
        _options = options;
        _authProvider = authProvider;
        _recorder = recorder;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<MediaBotService>();
    }

    /// <summary>Build the communications client once, lazily. Throws if misconfigured.</summary>
    private ICommunicationsClient Client
    {
        get
        {
            if (_client is not null) return _client;
            lock (_clientLock)
            {
                if (_client is not null) return _client;

                var errors = _options.Validate().ToList();
                if (errors.Count > 0)
                {
                    throw new InvalidOperationException(
                        "Media bot is not configured: " + string.Join(" ", errors));
                }

                // [SDK] Build the client with the public media-platform settings.
                var builder = new CommunicationsClientBuilder("MissaMediaBot", _options.AppId)
                    .SetAuthenticationProvider(_authProvider)
                    .SetNotificationUrl(new Uri(_options.CallingNotificationUrl))
                    .SetServiceBaseUrl(new Uri("https://graph.microsoft.com/v1.0"))
                    .SetMediaPlatformSettings(new MediaPlatformSettings
                    {
                        ApplicationId = _options.AppId,
                        MediaPlatformInstanceSettings = new MediaPlatformInstanceSettings
                        {
                            CertificateThumbprint = _options.CertificateThumbprint,
                            InstanceInternalPort = _options.MediaInstanceInternalPort,
                            InstancePublicPort = _options.MediaInstancePublicPort,
                            InstancePublicIPAddress = ResolvePublicIp(),
                            ServiceFqdn = _options.ServiceFqdn,
                        },
                    });

                _client = builder.Build();
                _logger.LogInformation("Graph Communications client built (FQDN={Fqdn}, mediaPort={Port}).",
                    _options.ServiceFqdn, _options.MediaInstancePublicPort);
                return _client;
            }
        }
    }

    /// <summary>The IP the media platform binds to (local NIC IPv4), or the configured override.</summary>
    private IPAddress ResolvePublicIp()
    {
        if (!string.IsNullOrWhiteSpace(_options.PublicIpAddress) &&
            IPAddress.TryParse(_options.PublicIpAddress, out var configured))
        {
            return configured;
        }

        var host = Dns.GetHostEntry(Dns.GetHostName());
        var ipv4 = host.AddressList.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
        return ipv4 ?? IPAddress.Loopback;
    }

    public async Task JoinAsync(MediaBotJoinRequest request)
    {
        if (_handlers.ContainsKey(request.CallId))
        {
            _logger.LogInformation("Call {CallId} already joined; ignoring duplicate join.", request.CallId);
            return;
        }

        var parsed = JoinUrlParser.Parse(request.JoinWebUrl);
        var tenantId = !string.IsNullOrWhiteSpace(request.TenantId) ? request.TenantId : parsed.TenantId;
        var organizerId = !string.IsNullOrWhiteSpace(request.OrganizerId) ? request.OrganizerId : parsed.OrganizerId;

        // [SDK] Receive-only audio, 16 kHz PCM, UNMIXED so we get per-participant streams.
        var mediaSession = Client.CreateMediaSession(
            new AudioSocketSettings
            {
                StreamDirections = StreamDirection.Recvonly,
                SupportedAudioFormat = AudioFormat.Pcm16K,
                ReceiveUnmixedMeetingAudio = true,
            },
            new VideoSocketSettings { StreamDirections = StreamDirection.Inactive });

        // [SDK] Build join parameters from the parsed meeting coordinates.
        var chatInfo = new ChatInfo { ThreadId = parsed.ThreadId, MessageId = "0" };

        // The organizer identity MUST carry the tenant id (in AdditionalData) so the calling
        // API resolves the meeting's tenant; otherwise the join fails with 7505 tenant mismatch.
        var organizerIdentity = new Identity
        {
            Id = organizerId,
            AdditionalData = new Dictionary<string, object> { ["tenantId"] = tenantId },
        };
        var meetingInfo = new OrganizerMeetingInfo
        {
            Organizer = new IdentitySet { User = organizerIdentity },
        };

        var joinParams = new JoinMeetingParameters(chatInfo, meetingInfo, mediaSession)
        {
            TenantId = tenantId,
        };

        _logger.LogInformation("Joining meeting for call {CallId} (thread={Thread}, organizer={Org}).",
            request.CallId, parsed.ThreadId, organizerId);

        var call = await Client.Calls().AddAsync(joinParams);

        var handler = new CallHandler(call, mediaSession, _recorder, request,
            _loggerFactory.CreateLogger($"CallHandler.{request.CallId}"));
        _handlers[request.CallId] = handler;
        _calls[request.CallId] = call;
        _joinedAt[request.CallId] = DateTimeOffset.UtcNow;

        // [SDK] Leave the meeting on our own once every human participant has dropped.
        // Roster changes raise Participants.OnUpdated; re-evaluate on each change.
        call.Participants.OnUpdated += (sender, args) =>
        {
            _ = EvaluateAutoLeaveAsync(request.CallId, call);
        };

        // [SDK] Dispose the handler and forget the call when it ends.
        call.OnUpdated += (sender, args) =>
        {
            // [SDK] State enum lives on call.Resource.State; terminate cleans up.
            if (call.Resource?.State == CallState.Terminated)
            {
                _calls.TryRemove(request.CallId, out _);
                _joinedAt.TryRemove(request.CallId, out _);
                _sawHuman.TryRemove(request.CallId, out _);
                if (_handlers.TryRemove(request.CallId, out var h)) h.Dispose();
            }
        };
    }

    /// <summary>
    /// Leave the meeting on our own once no human participants remain. Uses a grace
    /// window (people may still be joining) and only acts after at least one human has
    /// been present, so the bot never drops out before the meeting really gets going.
    /// </summary>
    private async Task EvaluateAutoLeaveAsync(string callId, ICall call)
    {
        try
        {
            var humans = CountHumanParticipants(call);
            if (humans > 0)
            {
                _sawHuman[callId] = true;
                return;
            }

            // Never leave before a human was ever present, or inside the grace window.
            if (!_sawHuman.TryGetValue(callId, out var seen) || !seen) return;
            if (_joinedAt.TryGetValue(callId, out var joined) &&
                DateTimeOffset.UtcNow - joined < AutoLeaveGrace) return;

            _logger.LogInformation("No human participants remain in call {CallId}; leaving the meeting.", callId);
            await LeaveAsync(callId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Auto-leave evaluation failed for call {CallId}", callId);
        }
    }

    private static int CountHumanParticipants(ICall call)
    {
        var count = 0;
        foreach (var participant in call.Participants)
        {
            var identity = participant.Resource?.Info?.Identity;
            if (identity?.User != null && identity.Application == null) count++;
        }
        return count;
    }

    public async Task LeaveAsync(string callId)
    {
        _joinedAt.TryRemove(callId, out _);
        _sawHuman.TryRemove(callId, out _);

        if (_calls.TryRemove(callId, out var call))
        {
            try
            {
                await call.DeleteAsync(); // [SDK] actually drop the bot out of the meeting
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "call.DeleteAsync failed for call {CallId}", callId);
            }
        }

        if (_handlers.TryRemove(callId, out var handler))
        {
            handler.Dispose();
        }
    }

    /// <summary>Hand an inbound calling notification (POST /api/calls) to the SDK.</summary>
    /// <returns>HTTP status code to return to Teams.</returns>
    public async Task<int> ProcessNotificationAsync(HttpRequest request)
    {
        // The SDK processes an HttpRequestMessage and returns an HttpResponseMessage.
        // ASP.NET Core hands us an HttpRequest, so adapt it.
        var httpRequest = new HttpRequestMessage(new HttpMethod(request.Method), _options.CallingNotificationUrl);

        using var ms = new MemoryStream();
        await request.Body.CopyToAsync(ms);
        ms.Position = 0;
        httpRequest.Content = new ByteArrayContent(ms.ToArray());

        foreach (var header in request.Headers)
        {
            if (!httpRequest.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()))
            {
                httpRequest.Content.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            }
        }

        var response = await Client.ProcessNotificationAsync(httpRequest);
        return (int)response.StatusCode;
    }
}

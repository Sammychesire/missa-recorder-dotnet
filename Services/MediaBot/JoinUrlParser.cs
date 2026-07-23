using System.Text.Json;
using System.Web;

namespace Recorder.Api.Services.MediaBot;

/// <summary>
/// Parses a Teams joinWebUrl into the thread id + organizer id + tenant id the
/// media SDK needs to build JoinMeetingParameters.
///
/// A joinWebUrl looks like:
///   https://teams.microsoft.com/l/meetup-join/19%3ameeting_XXX%40thread.v2/0
///       ?context=%7b%22Tid%22%3a%22&lt;tenant&gt;%22%2c%22Oid%22%3a%22&lt;organizer&gt;%22%7d
/// </summary>
public static class JoinUrlParser
{
    public sealed record ParsedJoin(string ThreadId, string OrganizerId, string TenantId);

    public static ParsedJoin Parse(string joinWebUrl)
    {
        if (string.IsNullOrWhiteSpace(joinWebUrl))
        {
            throw new ArgumentException("joinWebUrl is empty.", nameof(joinWebUrl));
        }

        var uri = new Uri(joinWebUrl);

        // Thread id is the path segment after "meetup-join", URL-decoded:
        //   19:meeting_XXXXXXXX@thread.v2
        var threadId = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.UnescapeDataString)
            .FirstOrDefault(s => s.Contains("@thread.", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Could not find thread id in joinWebUrl.");

        // context query carries {"Tid":"<tenant>","Oid":"<organizer>"}
        var query = HttpUtility.ParseQueryString(uri.Query);
        var contextJson = query["context"];
        string tenantId = string.Empty;
        string organizerId = string.Empty;

        if (!string.IsNullOrWhiteSpace(contextJson))
        {
            using var doc = JsonDocument.Parse(contextJson);
            if (doc.RootElement.TryGetProperty("Tid", out var tid)) tenantId = tid.GetString() ?? string.Empty;
            if (doc.RootElement.TryGetProperty("Oid", out var oid)) organizerId = oid.GetString() ?? string.Empty;
        }

        return new ParsedJoin(threadId, organizerId, tenantId);
    }
}

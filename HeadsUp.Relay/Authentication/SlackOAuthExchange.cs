using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace HeadsUp.Relay.Authentication;

/// <summary>The identity Slack confirms after a successful code exchange.</summary>
/// <param name="SlackUserId">From <c>authed_user.id</c> — Slack's own statement of who consented.</param>
/// <param name="TeamId">The workspace they consented in.</param>
public sealed record SlackIdentity(string SlackUserId, string? TeamId);

/// <summary>
/// Exchanges an OAuth authorization code for the consenting user's identity.
///
/// The relay does this rather than the desktop because it lets users face a single consent
/// screen: Slack refuses to combine Sign in with Slack scopes with ordinary user scopes, so
/// asking the desktop to prove identity separately would have meant authorizing twice.
///
/// Only the identity is kept. The access token Slack returns is deliberately discarded — the
/// relay reads events over Socket Mode with the app-level token and never needs to act as a
/// user, so holding one would be avoidable risk.
/// </summary>
public sealed class SlackOAuthExchange
{
    private const string AccessUrl = "https://slack.com/api/oauth.v2.access";

    private readonly HttpClient _http;
    private readonly ILogger<SlackOAuthExchange> _logger;

    public SlackOAuthExchange(HttpClient http, ILogger<SlackOAuthExchange> logger)
    {
        _http = http;
        _logger = logger;
    }

    /// <summary>Builds the consent URL. Requests user scopes only — no bot scopes.</summary>
    public static string BuildAuthorizeUrl(string clientId, string redirectUri, string state, string[] userScopes)
    {
        var query = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["user_scope"] = string.Join(',', userScopes),
            ["redirect_uri"] = redirectUri,
            ["state"] = state,
        };

        return "https://slack.com/oauth/v2/authorize?" + string.Join('&',
            query.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
    }

    /// <summary>
    /// Exchanges the code, returning who consented, or null if Slack rejected it.
    /// </summary>
    public async Task<SlackIdentity?> ExchangeAsync(
        string clientId, string clientSecret, string code, string redirectUri, CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
        };

        using var response = await _http.PostAsync(AccessUrl, new FormUrlEncodedContent(form), ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);

        // Slack answers 200 with ok:false for logical failures.
        if (!json.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
        {
            var error = json.TryGetProperty("error", out var e) ? e.GetString() : "unknown";
            _logger.LogWarning("Slack rejected the OAuth exchange: {Error}.", error);
            return null;
        }

        // authed_user is the person who consented. The top level describes the bot install,
        // which is not who we're identifying.
        if (!json.TryGetProperty("authed_user", out var authed) ||
            !authed.TryGetProperty("id", out var id) ||
            id.GetString() is not { Length: > 0 } userId)
        {
            _logger.LogWarning("Slack's OAuth response named no authed_user.");
            return null;
        }

        var teamId = json.TryGetProperty("team", out var team) && team.TryGetProperty("id", out var tid)
            ? tid.GetString()
            : null;

        return new SlackIdentity(userId, teamId);
    }
}

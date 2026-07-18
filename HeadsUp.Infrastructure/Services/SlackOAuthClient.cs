using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace HeadsUp.Services;

/// <summary>
/// The PKCE proof pair for one authorization attempt. The verifier is the secret half: it
/// never leaves the machine until the code exchange, which is what makes a client secret
/// unnecessary and lets Heads-Up ship as a public OAuth client.
/// </summary>
public sealed record PkcePair(string Verifier, string Challenge)
{
    /// <summary>Creates a fresh, cryptographically random verifier and its S256 challenge.</summary>
    public static PkcePair Create()
    {
        // 32 bytes -> 43-char base64url, comfortably inside RFC 7636's 43..128 range.
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return new PkcePair(verifier, challenge);
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>The token half of an <c>oauth.v2.access</c> response.</summary>
public sealed record SlackOAuthTokens(string AccessToken, string? RefreshToken, DateTimeOffset? ExpiresAt);

/// <summary>
/// Speaks Slack's OAuth v2 with PKCE. No client secret is involved anywhere: the app is
/// registered as a public client, so the code exchange authenticates with the PKCE verifier
/// instead. See https://docs.slack.dev/authentication/using-pkce/.
/// </summary>
public sealed class SlackOAuthClient
{
    private const string AuthorizeUrl = "https://slack.com/oauth/v2/authorize";
    private const string AccessUrl = "https://slack.com/api/oauth.v2.access";

    /// <summary>
    /// User scopes Heads-Up needs: read message events across the conversation types the user
    /// belongs to, plus the identity/metadata lookups used to name people and channels.
    /// </summary>
    public static readonly string[] UserScopes =
    [
        "channels:history", "groups:history", "im:history", "mpim:history",
        "channels:read", "groups:read", "im:read", "mpim:read",
        "users:read"
    ];

    private readonly HttpClient _http;
    private readonly ILogger<SlackOAuthClient> _logger;

    public SlackOAuthClient(HttpClient http, ILogger<SlackOAuthClient> logger)
    {
        _http = http;
        _logger = logger;
    }

    /// <summary>Builds the URL to open in the user's browser to start authorization.</summary>
    public static string BuildAuthorizeUrl(string clientId, string redirectUri, PkcePair pkce, string state)
    {
        var query = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["user_scope"] = string.Join(',', UserScopes),
            ["redirect_uri"] = redirectUri,
            ["state"] = state,
            ["code_challenge"] = pkce.Challenge,
            ["code_challenge_method"] = "S256",
        };

        return AuthorizeUrl + "?" + string.Join('&',
            query.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
    }

    /// <summary>Exchanges an authorization code for tokens, proving possession via the verifier.</summary>
    public Task<SlackOAuthTokens> ExchangeCodeAsync(
        string clientId, string code, string redirectUri, PkcePair pkce, CancellationToken ct = default) =>
        PostAsync(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = pkce.Verifier,
            ["grant_type"] = "authorization_code",
        }, ct);

    /// <summary>
    /// Renews an access token. Custom-scheme PKCE always issues rotating tokens, so this runs
    /// on a schedule, not just after a failure — and the returned refresh token replaces the
    /// old one, which Slack invalidates.
    /// </summary>
    public Task<SlackOAuthTokens> RefreshAsync(
        string clientId, string refreshToken, CancellationToken ct = default) =>
        PostAsync(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["refresh_token"] = refreshToken,
            ["grant_type"] = "refresh_token",
        }, ct);

    private async Task<SlackOAuthTokens> PostAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        using var response = await _http.PostAsync(AccessUrl, new FormUrlEncodedContent(form), ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);

        // Slack answers 200 with ok:false for logical failures, so the status code isn't enough.
        if (!json.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
        {
            var error = json.TryGetProperty("error", out var e) ? e.GetString() : "unknown";
            _logger.LogWarning("Slack OAuth call failed: {Error}.", error);
            throw new InvalidOperationException($"Slack OAuth failed: {error}");
        }

        // User tokens arrive nested under authed_user; the top level carries the *bot* token.
        // Never fall back to the top level — mistaking a bot token for the user token would
        // silently connect as the wrong identity and see the wrong conversations.
        if (!json.TryGetProperty("authed_user", out var authed))
            throw new InvalidOperationException("Slack OAuth response contained no authed_user.");

        var accessToken = authed.TryGetProperty("access_token", out var at) ? at.GetString() : null;
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new InvalidOperationException("Slack OAuth response contained no user access token.");

        var refresh = authed.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;
        DateTimeOffset? expiresAt = authed.TryGetProperty("expires_in", out var ei) && ei.TryGetInt32(out var secs)
            ? DateTimeOffset.UtcNow.AddSeconds(secs)
            : null;

        return new SlackOAuthTokens(accessToken, refresh, expiresAt);
    }
}

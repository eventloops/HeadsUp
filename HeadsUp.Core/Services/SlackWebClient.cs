using System.Text.Json;

namespace HeadsUp.Services;

/// <summary>
/// Thin client for Slack's <b>unofficial</b> web API - the endpoints the browser
/// and desktop clients call, authenticated with an <c>xoxc-...</c> client token
/// plus the <c>d</c> session cookie. Everything is POSTed as form data to
/// <c>{base}/api/{method}</c> with the token in the body and the cookie in the
/// header, mirroring the web client. Responses are returned as raw
/// <see cref="JsonElement"/> so callers pick out only what they need.
/// </summary>
public sealed class SlackWebClient : IDisposable
{
    private const string DefaultBaseUrl = "https://slack.com";

    // Slack's real-time gateway (and some API edges) reject non-browser User-Agents,
    // so present the same shape the desktop client does. The WebSocket handshake in
    // SlackWebHuddleSource reuses CookieHeader + UserAgent so the socket authenticates
    // with the same identity as the HTTP calls.
    public const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Slack/4.38 Chrome/124 Electron/30 Safari/537.36";

    private readonly HttpClient _http;
    private readonly string _token;
    private readonly string _baseUrl;

    /// <summary>The exact <c>Cookie</c> header sent on every request (e.g. <c>d=xoxd-…; d-s=…</c>).</summary>
    public string CookieHeader { get; }

    /// <summary>The <c>User-Agent</c> sent on every request.</summary>
    public string UserAgent => BrowserUserAgent;

    public SlackWebClient(string clientToken, string cookie, string? workspaceUrl = null)
    {
        _token = clientToken;
        _baseUrl = string.IsNullOrWhiteSpace(workspaceUrl)
            ? DefaultBaseUrl
            : workspaceUrl!.TrimEnd('/');

        // Manage the cookie header ourselves; the "d" cookie must go out verbatim.
        CookieHeader = NormalizeCookie(cookie);
        var handler = new HttpClientHandler { UseCookies = false };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.Add("Cookie", CookieHeader);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(BrowserUserAgent);
    }

    /// <summary>POSTs to an API method with the client token and optional args.</summary>
    public async Task<JsonElement> CallAsync(
        string method,
        IEnumerable<KeyValuePair<string, string>>? args = null,
        CancellationToken ct = default)
    {
        var form = new List<KeyValuePair<string, string>> { new("token", _token) };
        if (args is not null)
            form.AddRange(args);

        using var content = new FormUrlEncodedContent(form);
        using var response = await _http.PostAsync($"{_baseUrl}/api/{method}", content, ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        return doc.RootElement.Clone();
    }

    /// <summary>Confirms the session and returns the self/team identity.</summary>
    public Task<JsonElement> AuthTestAsync(CancellationToken ct = default) =>
        CallAsync("auth.test", null, ct);

    /// <summary>Opens a real-time WebSocket; the reply carries the <c>url</c> to connect to.</summary>
    public Task<JsonElement> RtmConnectAsync(CancellationToken ct = default) =>
        CallAsync("rtm.connect", new[] { new KeyValuePair<string, string>("batch_presence_aware", "1") }, ct);

    public Task<JsonElement> UserInfoAsync(string userId, CancellationToken ct = default) =>
        CallAsync("users.info", new[] { new KeyValuePair<string, string>("user", userId) }, ct);

    public Task<JsonElement> ConversationInfoAsync(string channelId, CancellationToken ct = default) =>
        CallAsync("conversations.info", new[] { new KeyValuePair<string, string>("channel", channelId) }, ct);

    /// <summary>Lists workspace members, one page at a time (cursor via <c>response_metadata.next_cursor</c>).</summary>
    public Task<JsonElement> UsersListAsync(string? cursor, int limit, CancellationToken ct = default)
    {
        var args = new List<KeyValuePair<string, string>> { new("limit", limit.ToString()) };
        if (!string.IsNullOrEmpty(cursor))
            args.Add(new("cursor", cursor));
        return CallAsync("users.list", args, ct);
    }

    private static string NormalizeCookie(string cookie)
    {
        cookie = (cookie ?? string.Empty).Trim();
        if (cookie.Length == 0)
            return "d=";
        return cookie.StartsWith("d=", StringComparison.OrdinalIgnoreCase) ? cookie : "d=" + cookie;
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>Null-safe accessors over Slack's loosely-typed JSON responses.</summary>
internal static class JsonElementExtensions
{
    public static string? Str(this JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object &&
        el.TryGetProperty(name, out var p) &&
        p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;

    public static bool Bool(this JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object &&
        el.TryGetProperty(name, out var p) &&
        p.ValueKind == JsonValueKind.True;

    public static JsonElement? Obj(this JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object &&
        el.TryGetProperty(name, out var p) &&
        p.ValueKind == JsonValueKind.Object
            ? p
            : null;
}

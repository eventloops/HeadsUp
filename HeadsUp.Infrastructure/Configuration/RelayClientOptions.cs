namespace HeadsUp.Configuration;

/// <summary>
/// How the desktop reaches the Heads-Up relay.
/// </summary>
public sealed class RelayClientOptions
{
    public const string SectionName = "Relay";

    /// <summary>
    /// The relay's base URL, e.g. <c>https://headsup.internal</c>. Deployed alongside the app
    /// rather than discovered, since there's no sensible default for an internal service.
    /// </summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>The event stream, derived from <see cref="BaseUrl"/> unless overridden.</summary>
    public string EventsUrl
    {
        get => string.IsNullOrWhiteSpace(_eventsUrl)
            ? DeriveWebSocketUrl(BaseUrl)
            : _eventsUrl;
        set => _eventsUrl = value;
    }

    private string _eventsUrl = string.Empty;

    /// <summary>Where the browser is sent to begin sign-in.</summary>
    public string AuthStartUrl => string.IsNullOrWhiteSpace(BaseUrl)
        ? string.Empty
        : BaseUrl.TrimEnd('/') + "/auth/start";

    /// <summary>A pre-issued session, for headless runs. Normally obtained by signing in.</summary>
    public string Session { get; set; } = string.Empty;

    /// <summary>The Slack user id that session belongs to.</summary>
    public string SlackUserId { get; set; } = string.Empty;

    /// <summary>http(s) maps to ws(s) — the socket lives on the same host as the API.</summary>
    private static string DeriveWebSocketUrl(string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            return string.Empty;

        var trimmed = baseUrl.TrimEnd('/');
        var scheme = trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? "wss://" : "ws://";
        var withoutScheme = trimmed
            .Replace("https://", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("http://", string.Empty, StringComparison.OrdinalIgnoreCase);

        return $"{scheme}{withoutScheme}/events";
    }
}

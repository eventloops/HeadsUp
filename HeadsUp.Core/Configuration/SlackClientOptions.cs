namespace HeadsUp.Configuration;

/// <summary>
/// Credentials for the unofficial "client API" detection mode: the same session
/// your browser or the Slack desktop app already uses. No Slack app, no admin
/// approval. Extract both from a logged-in session (see the README):
///   <see cref="ClientToken"/> - the xoxc-... token from localStorage.
///   <see cref="Cookie"/>      - the value of the HttpOnly "d" cookie (xoxd-...).
/// The token is useless without the matching cookie; both are required.
/// Priority users are shared with <see cref="SlackOptions.PriorityUsers"/>.
/// </summary>
public sealed class SlackClientOptions
{
    public const string SectionName = "SlackClient";

    /// <summary>Browser client token (xoxc-...).</summary>
    public string ClientToken { get; set; } = string.Empty;

    /// <summary>Value of the "d" session cookie (xoxd-...). Sent as <c>Cookie: d=...</c>.</summary>
    public string Cookie { get; set; } = string.Empty;

    /// <summary>
    /// Workspace base URL, e.g. <c>https://myworkspace.slack.com</c>. Optional;
    /// falls back to <c>https://slack.com</c> when empty. Set it if API calls
    /// come back with auth errors on the shared host.
    /// </summary>
    public string WorkspaceUrl { get; set; } = string.Empty;

    /// <summary>
    /// Where credentials come from:
    ///   "Config"   - the pasted <see cref="ClientToken"/>/<see cref="Cookie"/> above (default).
    ///   "WebView2" - the Windows head hosts a Slack sign-in and reads the token/cookie
    ///                live from that session, surviving rotation (no manual paste).
    /// </summary>
    public string CredentialSource { get; set; } = "Config";

    public bool UseWebViewCredentials =>
        string.Equals(CredentialSource, "WebView2", StringComparison.OrdinalIgnoreCase);
}

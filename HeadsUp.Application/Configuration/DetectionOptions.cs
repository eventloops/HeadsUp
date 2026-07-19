namespace HeadsUp.Configuration;

public sealed class DetectionOptions
{
    public const string SectionName = "Detection";

    public const string SlackApi = "SlackApi";
    public const string SlackClientApi = "SlackClientApi";
    public const string SlackRelay = "SlackRelay";

    /// <summary>
    /// The connectors to run, side by side. Each entry's <see cref="ConnectorOptions.Type"/> is
    /// a route name:
    /// "SlackClientApi" - the current prototype: a browser/desktop session (xoxc token + "d"
    ///   cookie, obtained via an embedded Slack sign-in); no Slack app and no admin approval,
    ///   but unofficial and undocumented. Cross-platform.
    /// "SlackApi" - the sanctioned path: your own Slack app, Socket Mode, and a user OAuth
    ///   token obtained with PKCE (no client secret, nothing hosted). Documented and stable,
    ///   at the cost of a one-time app setup per user - Slack only mints the app-level token
    ///   Socket Mode needs from its own config UI. See docs/slack-app-setup.md.
    /// When left empty, <see cref="ResolveConnectors"/> defaults to a single SlackClientApi
    /// connector so the app works out of the box.
    /// </summary>
    public List<ConnectorOptions> Connectors { get; set; } = [];

    /// <summary>
    /// Debug/testing aid: when true, huddles you start yourself also raise a notification
    /// (normally suppressed). Lets you verify the whole pipeline end to end without a second
    /// person. Hot-reloadable - flip it in appsettings while running.
    /// </summary>
    public bool NotifyOwnHuddles { get; set; }

    /// <summary>
    /// The effective connectors to run: the configured <see cref="Connectors"/>, or a single
    /// default SlackClientApi connector when none are configured.
    /// </summary>
    public IReadOnlyList<ConnectorOptions> ResolveConnectors() =>
        Connectors.Count > 0
            ? Connectors
            : [new ConnectorOptions { Type = SlackClientApi }];
}

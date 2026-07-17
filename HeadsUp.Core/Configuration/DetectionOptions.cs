namespace HeadsUp.Configuration;

public sealed class DetectionOptions
{
    public const string SectionName = "Detection";

    public const string SlackApi = "SlackApi";
    public const string SlackClientApi = "SlackClientApi";

    /// <summary>
    /// How activity is detected:
    /// "SlackClientApi" - the current prototype: a browser/desktop session (xoxc token + "d"
    ///   cookie, obtained via an embedded Slack sign-in); no Slack app and no admin approval,
    ///   but unofficial and undocumented. Cross-platform.
    /// "SlackApi" (default) - the goal: a proper admin-approved Slack app using your own OAuth
    ///   tokens. Robust and sanctioned; the target once app permission is granted.
    /// </summary>
    public string Mode { get; set; } = SlackApi;

    /// <summary>
    /// Debug/testing aid: when true, huddles you start yourself also raise a notification
    /// (normally suppressed). Lets you verify the whole pipeline end to end without a second
    /// person. Hot-reloadable - flip it in appsettings while running.
    /// </summary>
    public bool NotifyOwnHuddles { get; set; }

    public bool UseSlackClientApi =>
        string.Equals(Mode, SlackClientApi, StringComparison.OrdinalIgnoreCase);
}

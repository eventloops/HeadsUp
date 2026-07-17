namespace SlackHuddleNotifier.Configuration;

public sealed class DetectionOptions
{
    public const string SectionName = "Detection";

    public const string SlackApi = "SlackApi";
    public const string SlackClientApi = "SlackClientApi";
    public const string WindowsNotifications = "WindowsNotifications";

    /// <summary>
    /// How huddles are detected:
    /// "SlackApi" (default) - connect to Slack with your own app's tokens; robust, cross-platform.
    /// "SlackClientApi" - use a browser/desktop session (xoxc token + "d" cookie); no Slack app
    ///   and no admin approval, but unofficial and undocumented (can change without notice).
    ///   Cross-platform. Prototype.
    /// "WindowsNotifications" - watch Slack's own Windows toasts; zero Slack setup,
    ///   but only fires when Slack actually posts a notification. Windows head only.
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

    public bool UseWindowsNotifications =>
        string.Equals(Mode, WindowsNotifications, StringComparison.OrdinalIgnoreCase);
}

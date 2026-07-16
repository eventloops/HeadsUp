namespace SlackHuddleNotifier.Configuration;

public sealed class DetectionOptions
{
    public const string SectionName = "Detection";

    public const string SlackApi = "SlackApi";
    public const string WindowsNotifications = "WindowsNotifications";

    /// <summary>
    /// How huddles are detected:
    /// "SlackApi" (default) - connect to Slack with your tokens; robust, cross-platform.
    /// "WindowsNotifications" - watch Slack's own Windows toasts; zero Slack setup,
    /// but only fires when Slack actually posts a notification. Windows head only.
    /// </summary>
    public string Mode { get; set; } = SlackApi;

    public bool UseWindowsNotifications =>
        string.Equals(Mode, WindowsNotifications, StringComparison.OrdinalIgnoreCase);
}

namespace SlackHuddleNotifier.Configuration;

public sealed class SlackOptions
{
    public const string SectionName = "Slack";

    /// <summary>Socket Mode app-level token (xapp-...).</summary>
    public string AppToken { get; set; } = string.Empty;

    /// <summary>Bot OAuth token (xoxb-...).</summary>
    public string BotToken { get; set; } = string.Empty;

    /// <summary>User IDs that should trigger extra-prominent notifications.</summary>
    public List<string> PriorityUsers { get; set; } = [];
}

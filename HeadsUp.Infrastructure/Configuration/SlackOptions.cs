namespace HeadsUp.Configuration;

public sealed class SlackOptions
{
    public const string SectionName = "Slack";

    /// <summary>
    /// The Slack app's client id. Not a secret — it identifies the app when starting the
    /// PKCE OAuth flow. Each user creates their own app (one app-level token cannot be
    /// shared across machines), so this is per-install rather than shipped with Heads-Up.
    /// </summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>Socket Mode app-level token (xapp-...).</summary>
    public string AppToken { get; set; } = string.Empty;

    /// <summary>
    /// User OAuth token (xoxp-...). The app sees Slack as you do: huddles are
    /// detected in exactly the channels, group DMs and DMs you are a member of.
    /// </summary>
    public string UserToken { get; set; } = string.Empty;

    // Priority users live in the namespaced PriorityOptions ("Priority:Users"), not here.
}

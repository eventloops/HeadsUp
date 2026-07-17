namespace HeadsUp.Services;

/// <summary>
/// The stable identity of the Slack connector, shared by both Slack detection
/// sources (Socket Mode and the client-session prototype), the Slack deep-link
/// resolver, DND reporting, and settings namespacing — so the id/name live in one
/// place rather than as scattered "slack" literals.
/// </summary>
public static class SlackSource
{
    public const string Id = "slack";
    public const string Name = "Slack";
}

using HeadsUp.Services;

namespace HeadsUp.Configuration;

/// <summary>
/// A user whose alerts get the louder, incoming-call treatment — namespaced by the
/// connector they belong to, so a Slack user id and a future Teams object id can't
/// collide. Defaults to source "slack" so ids read from older flat configs are treated
/// as Slack users.
/// </summary>
public sealed class PriorityUser
{
    public string Source { get; set; } = SlackSource.Id;
    public string Id { get; set; } = string.Empty;
}

/// <summary>
/// The priority-user list, in its own config section so it is independent of the
/// <c>Alerts</c> section the alert store rewrites. Hot-reloadable.
/// </summary>
public sealed class PriorityOptions
{
    public const string SectionName = "Priority";

    /// <summary>Priority users across all connectors.</summary>
    public List<PriorityUser> Users { get; set; } = [];

    /// <summary>True when the given user, on the given source, is a priority user.</summary>
    public bool IsPriority(string sourceId, string userId) =>
        Users.Any(p =>
            string.Equals(p.Source, sourceId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(p.Id, userId, StringComparison.OrdinalIgnoreCase));
}

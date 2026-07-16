namespace SlackHuddleNotifier.Models;

/// <summary>Represents a detected Slack Huddle invitation event.</summary>
public sealed record HuddleEvent(
    string InitiatorUserId,
    string InitiatorDisplayName,
    string? InitiatorAvatarUrl,
    string ChannelId,
    string ChannelName,
    bool IsDirectMessage,
    string HuddleLink,
    bool IsPriorityUser,
    DateTimeOffset OccurredAt);

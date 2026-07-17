namespace HeadsUp.Models;

/// <summary>The kind of Slack activity an <see cref="Alert"/> represents.</summary>
public enum AlertKind
{
    Huddle,
    Mention,
    DirectMessage,
    Keyword
}

/// <summary>A detected piece of activity worth surfacing as a prominent alert.</summary>
public sealed record Alert(
    string InitiatorUserId,
    string InitiatorDisplayName,
    string? InitiatorAvatarUrl,
    string ChannelId,
    string ChannelName,
    bool IsDirectMessage,
    string Link,
    bool IsPriorityUser,
    DateTimeOffset OccurredAt,
    AlertKind Kind = AlertKind.Huddle,
    string Preview = "",
    // Which connector produced this alert. Stamped by the coordinator on relay from
    // the raising IAlertSource, so sources don't each set it; empty for a bare alert
    // built outside the pipeline (e.g. a --test-popup preview).
    string SourceId = "",
    string SourceName = "");

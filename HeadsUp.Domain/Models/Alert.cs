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
    UserId InitiatorUserId,
    string InitiatorDisplayName,
    string? InitiatorAvatarUrl,
    ChannelId ChannelId,
    string ChannelName,
    bool IsDirectMessage,
    string Link,
    bool IsPriorityUser,
    DateTimeOffset OccurredAt,
    // Which connector produced this alert — stamped by the raising IAlertSource.
    SourceId SourceId,
    string SourceName,
    AlertKind Kind = AlertKind.Huddle,
    string Preview = "");

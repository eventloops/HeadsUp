using System.Text.RegularExpressions;

namespace HeadsUp.Services;

/// <summary>
/// Slack-specific parsing of a raw message into the normalized facts the
/// source-neutral <see cref="AlertClassifier"/> needs — mention detection, DM
/// detection, and a human-readable preview. Shared by both Slack sources (Socket
/// Mode and the client-session prototype) so they interpret Slack's wire format
/// identically. A future connector (Teams, Discord) would supply its own parser.
/// </summary>
public static class SlackMessageParser
{
    /// <summary>True when the text directly @-mentions the given user (Slack's <c>&lt;@id&gt;</c> token).</summary>
    public static bool IsMention(string text, string? selfUserId) =>
        selfUserId is not null &&
        text.Contains($"<@{selfUserId}>", StringComparison.OrdinalIgnoreCase);

    /// <summary>True for a 1:1 DM: Slack conversation ids for IMs start with 'D'.</summary>
    public static bool IsDirectMessage(string channelId) => channelId.StartsWith('D');

    /// <summary>
    /// Strip Slack's angle-bracket tokens (mentions/links/channels), flatten newlines,
    /// and clamp length — the message preview shown in popups and history.
    /// </summary>
    public static string Preview(string text)
    {
        text = Regex.Replace(text, "<[^>]*>", string.Empty);
        return AlertClassifier.Truncate(text);
    }
}

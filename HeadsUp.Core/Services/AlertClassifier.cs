using HeadsUp.Configuration;
using HeadsUp.Models;
using System.Text.RegularExpressions;

namespace HeadsUp.Services;

/// <summary>
/// Shared alert-classification rules, used by both detection sources so SlackApi
/// (Socket Mode) and SlackClientApi (client session) modes alert on exactly the
/// same activity and honor the same <see cref="AlertOptions"/> toggles.
/// </summary>
public static class AlertClassifier
{
    /// <summary>
    /// True for message subtypes that signal a huddle start: "huddle_thread" for
    /// huddles, "sh_room_created" for legacy Slack Calls.
    /// </summary>
    public static bool IsHuddleSubtype(string? subtype) =>
        subtype is "huddle_thread" or "sh_room_created";

    /// <summary>
    /// Classifies a plain user message (no subtype) against the enabled alert kinds,
    /// or <c>null</c> when it shouldn't alert. Priority: a direct @mention beats
    /// "it's a DM" beats a keyword match.
    /// </summary>
    public static AlertKind? ClassifyMessage(string text, string channelId, string? selfUserId, AlertOptions alerts)
    {
        var isMention = selfUserId is not null &&
                        text.Contains($"<@{selfUserId}>", StringComparison.OrdinalIgnoreCase);
        var isDm = channelId.StartsWith('D');

        if (alerts.Mentions && isMention) return AlertKind.Mention;
        if (alerts.DirectMessages && isDm) return AlertKind.DirectMessage;
        if (alerts.Keywords && MatchesKeyword(text, alerts.KeywordList)) return AlertKind.Keyword;
        return null;
    }

    /// <summary>Case-insensitive substring match against the configured keyword list.</summary>
    public static bool MatchesKeyword(string text, IReadOnlyList<string> keywords)
    {
        foreach (var k in keywords)
            if (!string.IsNullOrWhiteSpace(k) && text.Contains(k, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>Strip Slack's angle-bracket tokens (mentions/links/channels) and clamp length.</summary>
    public static string Preview(string text)
    {
        text = Regex.Replace(text, "<[^>]*>", string.Empty).Replace('\n', ' ').Trim();
        return text.Length > 140 ? text[..140] + "…" : text;
    }
}

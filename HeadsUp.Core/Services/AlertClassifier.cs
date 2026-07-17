using HeadsUp.Configuration;
using HeadsUp.Models;

namespace HeadsUp.Services;

/// <summary>
/// Source-neutral alert-classification policy: which enabled kind (if any) a message
/// should alert as, and the shared muted/keyword rules. Every connector feeds it the
/// same normalized facts (is this a mention of me, is this a DM, the plain text), so
/// all sources honor identical <see cref="AlertOptions"/> toggles. Connector-specific
/// parsing of those facts lives in each source (e.g. <see cref="SlackMessageParser"/>).
/// </summary>
public static class AlertClassifier
{
    /// <summary>Default maximum length of a message preview before it is ellipsized.</summary>
    public const int PreviewMaxLength = 140;

    /// <summary>
    /// True for message subtypes that signal a huddle start: "huddle_thread" for
    /// huddles, "sh_room_created" for legacy Slack Calls.
    /// </summary>
    public static bool IsHuddleSubtype(string? subtype) =>
        subtype is "huddle_thread" or "sh_room_created";

    /// <summary>
    /// Classifies a message from its normalized facts against the enabled alert kinds,
    /// or <c>null</c> when it shouldn't alert. Priority: a direct @mention beats
    /// "it's a DM" beats a keyword match. A muted channel never alerts.
    /// </summary>
    public static AlertKind? Classify(bool isMention, bool isDm, string text, string channelId, string sourceId, AlertOptions alerts)
    {
        if (IsChannelMuted(channelId, sourceId, alerts))
            return null;

        if (alerts.Mentions && isMention) return AlertKind.Mention;
        if (alerts.DirectMessages && isDm) return AlertKind.DirectMessage;
        if (alerts.Keywords && MatchesKeyword(text, alerts.KeywordList)) return AlertKind.Keyword;
        return null;
    }

    /// <summary>
    /// True when the channel is muted for its connector (case-insensitive on id and source).
    /// A muted entry only silences the source it belongs to, so ids can't collide across connectors.
    /// </summary>
    public static bool IsChannelMuted(string channelId, string sourceId, AlertOptions alerts)
    {
        foreach (var muted in alerts.MutedChannels)
            if (string.Equals(muted.Id, channelId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(muted.Source, sourceId, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>Case-insensitive substring match against the configured keyword list.</summary>
    public static bool MatchesKeyword(string text, IReadOnlyList<string> keywords)
    {
        foreach (var k in keywords)
            if (!string.IsNullOrWhiteSpace(k) && text.Contains(k, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>Collapse newlines, trim, and clamp to <see cref="PreviewMaxLength"/> with an ellipsis.</summary>
    public static string Truncate(string text, int maxLength = PreviewMaxLength)
    {
        text = text.Replace('\n', ' ').Trim();
        return text.Length > maxLength ? text[..maxLength] + "…" : text;
    }
}

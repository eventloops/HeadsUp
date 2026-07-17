using HeadsUp.Models;

namespace HeadsUp.Services;

/// <summary>
/// Source-neutral alert-classification policy: which enabled kind (if any) a message should
/// alert as, and the shared muted/keyword rules. Pure domain logic — every connector feeds
/// it the same normalized facts (is this a mention of me, is this a DM, the plain text) and
/// the same <see cref="AlertPreferences"/>, so all sources behave identically. Connector-
/// specific parsing of those facts lives in each connector's adapter.
/// </summary>
public static class AlertPolicy
{
    /// <summary>Default maximum length of a message preview before it is ellipsized.</summary>
    public const int PreviewMaxLength = 140;

    /// <summary>
    /// Classifies a message from its normalized facts against the enabled kinds, or
    /// <c>null</c> when it shouldn't alert. Priority: a direct @mention beats "it's a DM"
    /// beats a keyword match. A muted channel never alerts.
    /// </summary>
    public static AlertKind? Classify(
        bool isMention, bool isDm, string text, ChannelId channel, SourceId source, AlertPreferences prefs)
    {
        if (IsChannelMuted(channel, source, prefs.Muted))
            return null;

        if (prefs.Toggles.Mentions && isMention) return AlertKind.Mention;
        if (prefs.Toggles.DirectMessages && isDm) return AlertKind.DirectMessage;
        if (prefs.Toggles.Keywords && MatchesKeyword(text, prefs.Keywords)) return AlertKind.Keyword;
        return null;
    }

    /// <summary>True when the channel is muted for its connector (matched by channel and source).</summary>
    public static bool IsChannelMuted(ChannelId channel, SourceId source, IReadOnlyList<MutedChannelRef> muted)
    {
        foreach (var m in muted)
            if (m.Channel == channel && m.Source == source)
                return true;
        return false;
    }

    /// <summary>Case-insensitive substring match against the configured keywords.</summary>
    public static bool MatchesKeyword(string text, IReadOnlyList<Keyword> keywords)
    {
        foreach (var k in keywords)
            if (!string.IsNullOrWhiteSpace(k.Value) && text.Contains(k.Value, StringComparison.OrdinalIgnoreCase))
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

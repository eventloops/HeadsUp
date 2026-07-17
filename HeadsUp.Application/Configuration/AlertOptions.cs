using HeadsUp.Models;

namespace HeadsUp.Configuration;

/// <summary>
/// Which kinds of Slack activity Heads-Up alerts on. Hot-reloadable, and surfaced in the
/// dashboard's "Notify me about" section. This is the string-typed configuration DTO bound
/// from JSON; <see cref="ToPreferences"/> maps it to the domain <see cref="AlertPreferences"/>.
/// </summary>
public sealed class AlertOptions
{
    public const string SectionName = "Alerts";

    /// <summary>Someone starts a huddle in one of your conversations.</summary>
    public bool Huddles { get; set; } = true;

    /// <summary>A message that @-mentions you directly.</summary>
    public bool Mentions { get; set; } = true;

    /// <summary>Any direct message (1:1 IM). Off by default - DMs can be frequent.</summary>
    public bool DirectMessages { get; set; }

    /// <summary>A message containing one of <see cref="KeywordList"/>.</summary>
    public bool Keywords { get; set; }

    /// <summary>Words/phrases that trigger a keyword alert (matched case-insensitively).</summary>
    public List<string> KeywordList { get; set; } = [];

    /// <summary>Channels to never alert on, whatever the kind. Managed from the dashboard.</summary>
    public List<MutedChannel> MutedChannels { get; set; } = [];

    /// <summary>Maps this configuration DTO to the domain policy inputs (value objects).</summary>
    public AlertPreferences ToPreferences() => new(
        new AlertToggles(Huddles, Mentions, DirectMessages, Keywords),
        KeywordList.Where(k => !string.IsNullOrWhiteSpace(k)).Select(k => new Keyword(k)).ToList(),
        MutedChannels
            .Where(m => !string.IsNullOrWhiteSpace(m.Id))
            .Select(m => new MutedChannelRef(new SourceId(m.Source), new ChannelId(m.Id)))
            .ToList());
}

/// <summary>A channel the user has silenced, kept with its name so the dashboard can list it.</summary>
public sealed class MutedChannel
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    /// <summary>Which connector this channel belongs to. Defaults to the Slack connector id
    /// so entries that omit a source are treated as Slack channels.</summary>
    public string Source { get; set; } = "slack";
}

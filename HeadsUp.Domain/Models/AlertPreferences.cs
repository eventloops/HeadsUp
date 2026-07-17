namespace HeadsUp.Models;

/// <summary>Which alert kinds are enabled. Domain view of the "Notify me about" toggles.</summary>
public sealed record AlertToggles(bool Huddles, bool Mentions, bool DirectMessages, bool Keywords);

/// <summary>Identity of a muted channel (connector + channel), no display name — the
/// domain only needs to know what to silence.</summary>
public sealed record MutedChannelRef(SourceId Source, ChannelId Channel);

/// <summary>
/// The domain inputs the <see cref="Services.AlertPolicy"/> classifies against: the enabled
/// kinds, the watch-words, and the muted channels. Mapped from the string-typed
/// configuration at the boundary, so the policy stays pure and connector-agnostic.
/// </summary>
public sealed record AlertPreferences(
    AlertToggles Toggles,
    IReadOnlyList<Keyword> Keywords,
    IReadOnlyList<MutedChannelRef> Muted);

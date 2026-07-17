using HeadsUp.Models;

namespace HeadsUp.ViewModels;

/// <summary>
/// Presentation mapping for an <see cref="Alert"/> — its headline, glyph, and action
/// label per kind, plus initials for an avatar fallback. Shared by the huddle popup
/// and the dashboard's alert-history rows so both read identically.
/// </summary>
public static class AlertDisplay
{
    /// <summary>Headline, leading glyph, and primary-action label for an alert kind.</summary>
    public static (string Headline, string Icon, string Action) Describe(AlertKind kind) => kind switch
    {
        AlertKind.Mention => ("Mentioned you", "\U0001F4AC", "Open"),
        AlertKind.DirectMessage => ("Direct message", "✉", "Open"),
        AlertKind.Keyword => ("Keyword match", "\U0001F514", "Open"),
        _ => ("Huddle started", "\U0001F3A7", "Join huddle"),
    };

    /// <summary>Up to two initials from a display name, for the avatar fallback circle.</summary>
    public static string Initials(string displayName)
    {
        var parts = displayName.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length switch
        {
            0 => "?",
            1 => char.ToUpperInvariant(parts[0][0]).ToString(),
            _ => $"{char.ToUpperInvariant(parts[0][0])}{char.ToUpperInvariant(parts[^1][0])}"
        };
    }
}

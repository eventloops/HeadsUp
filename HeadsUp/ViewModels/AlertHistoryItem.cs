using HeadsUp.Models;

namespace HeadsUp.ViewModels;

/// <summary>
/// A single row in the dashboard's "Recent alerts" list — a read-only, display-ready
/// projection of an <see cref="Alert"/>.
/// </summary>
public sealed class AlertHistoryItem
{
    public AlertHistoryItem(Alert alert)
    {
        Alert = alert;
        (Headline, Icon, _) = AlertDisplay.Describe(alert.Kind);
        InitiatorName = alert.InitiatorDisplayName;
        AvatarUrl = alert.InitiatorAvatarUrl;
        Initials = AlertDisplay.Initials(alert.InitiatorDisplayName);
        Where = alert.IsDirectMessage ? "DM" : $"#{alert.ChannelName}";
        TimeDisplay = FormatTime(alert.OccurredAt.ToLocalTime());
        // Message preview for mentions/DMs/keywords; huddles just show where.
        Detail = string.IsNullOrWhiteSpace(alert.Preview) ? Where : $"{Where} · {alert.Preview}";
        IsPriorityUser = alert.IsPriorityUser;
    }

    public Alert Alert { get; }
    public string Headline { get; }
    public string Icon { get; }
    public string InitiatorName { get; }
    public string? AvatarUrl { get; }
    public string Initials { get; }
    public string Where { get; }
    public string Detail { get; }
    public string TimeDisplay { get; }
    public bool IsPriorityUser { get; }

    /// <summary>Relative time for recent items ("2m ago"), a clock time for older ones.</summary>
    private static string FormatTime(DateTimeOffset when)
    {
        var age = DateTimeOffset.Now - when;
        if (age < TimeSpan.FromMinutes(1)) return "just now";
        if (age < TimeSpan.FromHours(1)) return $"{(int)age.TotalMinutes}m ago";
        if (when.Date == DateTimeOffset.Now.Date) return when.ToString("h:mm tt").ToLowerInvariant();
        return when.ToString("ddd h:mm tt").ToLowerInvariant();
    }
}

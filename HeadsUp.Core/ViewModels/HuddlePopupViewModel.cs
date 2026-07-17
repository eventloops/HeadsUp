using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HeadsUp.Models;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace HeadsUp.ViewModels;

public sealed partial class HuddlePopupViewModel : ObservableObject
{
    [ObservableProperty]
    private string _initiatorName = string.Empty;

    [ObservableProperty]
    private string? _avatarUrl;

    [ObservableProperty]
    private string _initials = string.Empty;

    [ObservableProperty]
    private string _channelDisplay = string.Empty;

    [ObservableProperty]
    private string _timeDisplay = string.Empty;

    [ObservableProperty]
    private string _subtitle = string.Empty;

    [ObservableProperty]
    private bool _isPriorityUser;

    [ObservableProperty]
    private string _headline = string.Empty;

    [ObservableProperty]
    private string _icon = string.Empty;

    [ObservableProperty]
    private string _actionLabel = string.Empty;

    private string _link = string.Empty;

    public event EventHandler? CloseRequested;

    public void Populate(Alert alert)
    {
        InitiatorName = alert.InitiatorDisplayName;
        AvatarUrl = alert.InitiatorAvatarUrl;
        Initials = GetInitials(alert.InitiatorDisplayName);
        ChannelDisplay = alert.IsDirectMessage
            ? $"DM with {alert.InitiatorDisplayName}"
            : $"#{alert.ChannelName}";
        TimeDisplay = alert.OccurredAt.ToLocalTime().ToString("h:mm tt").ToLowerInvariant();
        // Show the message preview when there is one (mentions/DMs/keywords); else channel · time.
        Subtitle = string.IsNullOrWhiteSpace(alert.Preview)
            ? $"{ChannelDisplay} · {TimeDisplay}"
            : alert.Preview;
        IsPriorityUser = alert.IsPriorityUser;
        _link = alert.Link;

        (Headline, Icon, ActionLabel) = alert.Kind switch
        {
            AlertKind.Mention => ("Mentioned you", "\U0001F4AC", "Open"),
            AlertKind.DirectMessage => ("Direct message", "✉", "Open"),
            AlertKind.Keyword => ("Keyword match", "\U0001F514", "Open"),
            _ => ("Huddle started", "\U0001F3A7", "Join huddle"),
        };
    }

    private static string GetInitials(string displayName)
    {
        var parts = displayName.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length switch
        {
            0 => "?",
            1 => char.ToUpperInvariant(parts[0][0]).ToString(),
            _ => $"{char.ToUpperInvariant(parts[0][0])}{char.ToUpperInvariant(parts[^1][0])}"
        };
    }

    [RelayCommand]
    private void JoinHuddle()
    {
        // Prefer the desktop Slack app (slack:// scheme) over bouncing through the browser;
        // fall back to the next target if a launch fails (e.g. the app isn't installed).
        foreach (var target in JoinTargets(_link))
        {
            try
            {
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
                break;
            }
            catch { /* try the next target */ }
        }
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Desktop-app deep link first, then the original (web) link as a fallback.</summary>
    private static IEnumerable<string> JoinTargets(string link)
    {
        var m = Regex.Match(link, @"app\.slack\.com/huddle/(?<team>[^/]+)/(?<channel>[^/?#]+)");
        if (m.Success)
            yield return $"slack://huddle?team={m.Groups["team"].Value}&id={m.Groups["channel"].Value}";
        yield return link;
    }

    [RelayCommand]
    private void Dismiss()
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}

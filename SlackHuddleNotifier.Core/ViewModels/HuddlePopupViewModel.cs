using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SlackHuddleNotifier.Models;
using System.Diagnostics;

namespace SlackHuddleNotifier.ViewModels;

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

    private string _huddleLink = string.Empty;

    public event EventHandler? CloseRequested;

    public void Populate(HuddleEvent huddle)
    {
        InitiatorName = huddle.InitiatorDisplayName;
        AvatarUrl = huddle.InitiatorAvatarUrl;
        Initials = GetInitials(huddle.InitiatorDisplayName);
        ChannelDisplay = huddle.IsDirectMessage
            ? $"DM with {huddle.InitiatorDisplayName}"
            : $"#{huddle.ChannelName}";
        TimeDisplay = huddle.OccurredAt.ToLocalTime().ToString("h:mm tt").ToLowerInvariant();
        Subtitle = $"{ChannelDisplay} · {TimeDisplay}";
        IsPriorityUser = huddle.IsPriorityUser;
        _huddleLink = huddle.HuddleLink;
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
        try
        {
            Process.Start(new ProcessStartInfo(_huddleLink) { UseShellExecute = true });
        }
        catch { /* ignore */ }
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Dismiss()
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}

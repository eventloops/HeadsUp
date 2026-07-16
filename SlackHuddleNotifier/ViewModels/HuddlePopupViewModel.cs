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
    private string _channelDisplay = string.Empty;

    [ObservableProperty]
    private string _timeDisplay = string.Empty;

    [ObservableProperty]
    private bool _isPriorityUser;

    private string _huddleLink = string.Empty;

    public event EventHandler? CloseRequested;

    public void Populate(HuddleEvent huddle)
    {
        InitiatorName = huddle.InitiatorDisplayName;
        ChannelDisplay = huddle.IsDirectMessage
            ? $"DM with {huddle.InitiatorDisplayName}"
            : $"#{huddle.ChannelName}";
        TimeDisplay = huddle.OccurredAt.ToLocalTime().ToString("h:mm tt");
        IsPriorityUser = huddle.IsPriorityUser;
        _huddleLink = huddle.HuddleLink;
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

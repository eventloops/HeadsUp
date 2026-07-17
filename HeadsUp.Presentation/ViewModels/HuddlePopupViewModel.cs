using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HeadsUp.Models;
using HeadsUp.Services;

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

    /// <summary>
    /// Ordered launch targets for the Join/Open button (connector deep link first, raw
    /// link last), resolved by <see cref="AlertLinks"/> when the popup is built. Falls
    /// back to the plain link if left unset (e.g. a bare --test-popup preview).
    /// </summary>
    public IReadOnlyList<string> JoinTargets { get; set; } = Array.Empty<string>();

    public event EventHandler? CloseRequested;

    public void Populate(Alert alert)
    {
        InitiatorName = alert.InitiatorDisplayName;
        AvatarUrl = alert.InitiatorAvatarUrl;
        Initials = AlertDisplay.Initials(alert.InitiatorDisplayName);
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

        (Headline, Icon, ActionLabel) = AlertDisplay.Describe(alert.Kind);
    }

    /// <summary>
    /// Launches one join target, returning true when it was handed off. Replaceable in
    /// tests, where opening a real app or browser is not an option.
    /// </summary>
    internal Func<string, bool> Launch { get; set; } = AlertLauncher.DefaultLaunch;

    [RelayCommand]
    private void JoinHuddle()
    {
        var targets = JoinTargets.Count > 0 ? JoinTargets : new[] { _link };
        AlertLauncher.Open(targets, Launch);
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Dismiss()
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}

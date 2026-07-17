using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HeadsUp.Configuration;
using HeadsUp.Services;

namespace HeadsUp.ViewModels;

/// <summary>
/// Backs the first-run welcome screen: introduces Heads-Up and lets the user pick which
/// activity to be alerted on before connecting. Choices persist immediately, so whichever
/// button they press the settings are already saved.
/// </summary>
public sealed partial class WelcomeViewModel : ObservableObject
{
    private readonly IAlertSettingsStore _alertStore;
    private bool _loading;

    [ObservableProperty] private bool _notifyHuddles;
    [ObservableProperty] private bool _notifyMentions;
    [ObservableProperty] private bool _notifyDirectMessages;
    [ObservableProperty] private bool _notifyKeywords;

    /// <summary>Raised when the user chooses to connect to Slack.</summary>
    public event EventHandler? ConnectRequested;

    /// <summary>Raised when the user dismisses the welcome ("set up later").</summary>
    public event EventHandler? Dismissed;

    public WelcomeViewModel(IAlertSettingsStore alertStore)
    {
        _alertStore = alertStore;
        var a = alertStore.Current;
        _loading = true;
        NotifyHuddles = a.Huddles;
        NotifyMentions = a.Mentions;
        NotifyDirectMessages = a.DirectMessages;
        NotifyKeywords = a.Keywords;
        _loading = false;
    }

    partial void OnNotifyHuddlesChanged(bool value) => _ = SaveAsync();
    partial void OnNotifyMentionsChanged(bool value) => _ = SaveAsync();
    partial void OnNotifyDirectMessagesChanged(bool value) => _ = SaveAsync();
    partial void OnNotifyKeywordsChanged(bool value) => _ = SaveAsync();

    private async Task SaveAsync()
    {
        if (_loading)
            return;

        var current = _alertStore.Current;
        try
        {
            await _alertStore.SaveAsync(new AlertOptions
            {
                Huddles = NotifyHuddles,
                Mentions = NotifyMentions,
                DirectMessages = NotifyDirectMessages,
                Keywords = NotifyKeywords,
                KeywordList = current.KeywordList,       // preserve anything already configured
                MutedChannels = current.MutedChannels,
            });
        }
        catch
        {
            // First-run best effort; the dashboard can set these later.
        }
    }

    [RelayCommand]
    private void Connect() => ConnectRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void Later() => Dismissed?.Invoke(this, EventArgs.Empty);
}

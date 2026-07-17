using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HeadsUp.Configuration;
using HeadsUp.Services;

namespace HeadsUp.ViewModels;

/// <summary>
/// Backs the first-run welcome screen: introduces Heads-Up and lets the user pick which
/// activity to be alerted on before connecting. Choices persist immediately. Clicking Connect
/// doesn't dismiss the window — it switches to a "Connecting…" state and stays up until the
/// connection is established or the Slack sign-in window takes over.
/// </summary>
public sealed partial class WelcomeViewModel : ObservableObject, IDisposable
{
    private readonly IAlertSettingsStore _alertStore;
    private readonly AppStatus _status;
    private bool _loading;

    [ObservableProperty] private bool _notifyHuddles;
    [ObservableProperty] private bool _notifyMentions;
    [ObservableProperty] private bool _notifyDirectMessages;
    [ObservableProperty] private bool _notifyKeywords;

    /// <summary>True once the user has clicked Connect — the window shows the connecting state.</summary>
    [ObservableProperty] private bool _isConnecting;

    /// <summary>Raised when the user chooses to connect (the app opens the connection gate).</summary>
    public event EventHandler? ConnectRequested;

    /// <summary>Raised when the user dismisses the welcome ("set up later").</summary>
    public event EventHandler? Dismissed;

    /// <summary>Raised when the welcome should close itself — connected, or the sign-in window is up.</summary>
    public event EventHandler? CloseRequested;

    public WelcomeViewModel(IAlertSettingsStore alertStore, AppStatus status)
    {
        _alertStore = alertStore;
        _status = status;
        var a = alertStore.Current;
        _loading = true;
        NotifyHuddles = a.Huddles;
        NotifyMentions = a.Mentions;
        NotifyDirectMessages = a.DirectMessages;
        NotifyKeywords = a.Keywords;
        _loading = false;

        _status.PropertyChanged += OnStatusChanged;
    }

    private void OnStatusChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (!IsConnecting)
            return;

        // Hand off: close once we're connected, or once the Slack sign-in window has come up.
        if ((e.PropertyName == nameof(AppStatus.IsConnected) && _status.IsConnected) ||
            (e.PropertyName == nameof(AppStatus.NeedsSignIn) && _status.NeedsSignIn))
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
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
    private void Connect()
    {
        IsConnecting = true;
        ConnectRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Later() => Dismissed?.Invoke(this, EventArgs.Empty);

    public void Dispose() => _status.PropertyChanged -= OnStatusChanged;
}

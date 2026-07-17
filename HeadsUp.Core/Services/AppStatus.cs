using CommunityToolkit.Mvvm.ComponentModel;

namespace HeadsUp.Services;

/// <summary>
/// Live, observable snapshot of what the app is doing right now - detection mode,
/// connection state, and the signed-in account. A single instance is shared (as a
/// singleton) between the detection source, which updates it, and the tray/dashboard
/// UI, which binds to it. WPF marshals scalar property-change notifications to the UI
/// thread, so background updates are safe.
/// </summary>
public sealed partial class AppStatus : ObservableObject
{
    /// <summary>True once a live connection to Slack is established and watching.</summary>
    [ObservableProperty]
    private bool _isConnected;

    /// <summary>Short human-readable state, e.g. "Watching for huddles" or "Signing in…".</summary>
    [ObservableProperty]
    private string _statusMessage = "Starting…";

    /// <summary>Signed-in user handle (e.g. "cameron.lambert"), or null when unknown.</summary>
    [ObservableProperty]
    private string? _account;

    /// <summary>Workspace/team name, or null when unknown.</summary>
    [ObservableProperty]
    private string? _team;

    /// <summary>Detection mode in effect (SlackApi / SlackClientApi).</summary>
    [ObservableProperty]
    private string _mode = string.Empty;

    /// <summary>Where credentials come from (e.g. "appsettings" or "WebView2 Slack session").</summary>
    [ObservableProperty]
    private string _credentialSource = string.Empty;

    /// <summary>Convenience setter for the detection source to report a connection change.</summary>
    public void SetConnection(bool connected, string message)
    {
        IsConnected = connected;
        StatusMessage = message;
    }

    /// <summary>Records the authenticated identity after a successful auth check.</summary>
    public void SetAccount(string? account, string? team)
    {
        Account = account;
        Team = team;
    }
}

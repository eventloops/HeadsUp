using CommunityToolkit.Mvvm.ComponentModel;

namespace HeadsUp.Services;

/// <summary>
/// Live, observable status of a single connector — its connection state, the signed-in
/// account, and how it's authenticating. One instance per source, held by
/// <see cref="AppStatus"/>. The detection source updates it; the tray/dashboard bind to
/// it. WPF marshals scalar property-change notifications to the UI thread, so background
/// updates are safe.
/// </summary>
public sealed partial class SourceStatus : ObservableObject
{
    /// <summary>Stable connector id (e.g. "slack"); matches <see cref="IAlertSource.SourceId"/>.</summary>
    public string SourceId { get; }

    /// <summary>Human-readable connector name (e.g. "Slack").</summary>
    [ObservableProperty]
    private string _displayName;

    /// <summary>True once a live connection to this source is established and watching.</summary>
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

    /// <summary>Detection route in effect for this source (e.g. SlackApi / SlackClientApi).</summary>
    [ObservableProperty]
    private string _mode = string.Empty;

    /// <summary>Where credentials come from (e.g. "appsettings" or "WebView2 Slack session").</summary>
    [ObservableProperty]
    private string _credentialSource = string.Empty;

    /// <summary>True while this source's embedded sign-in window is up waiting for the user.</summary>
    [ObservableProperty]
    private bool _needsSignIn;

    public SourceStatus(string sourceId, string displayName)
    {
        SourceId = sourceId;
        _displayName = displayName;
    }

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

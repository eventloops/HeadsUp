using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HeadsUp.Services;

namespace HeadsUp.ViewModels;

public sealed partial class TrayViewModel : ObservableObject, IDisposable
{
    private readonly SnoozeService _snooze;

    /// <summary>Live status, bound by the tray menu and shared with the dashboard.</summary>
    public AppStatus Status { get; }

    /// <summary>True while alerts are silenced (manual snooze or Slack DND).</summary>
    [ObservableProperty]
    private bool _isMuted;

    /// <summary>True while a manual snooze is active (i.e. it can be resumed from here).</summary>
    [ObservableProperty]
    private bool _isManuallySnoozed;

    /// <summary>Why alerts are silenced, e.g. "Snoozed until 3:30 pm"; empty when not muted.</summary>
    [ObservableProperty]
    private string _muteStatus = string.Empty;

    public event EventHandler? ExitRequested;
    public event EventHandler? OpenDashboardRequested;
    public event EventHandler? AboutRequested;

    public TrayViewModel(AppStatus status, SnoozeService snooze)
    {
        Status = status;
        _snooze = snooze;
        _snooze.Changed += OnSnoozeChanged;
        RefreshMute();
    }

    private void OnSnoozeChanged(object? sender, EventArgs e) => RefreshMute();

    private void RefreshMute()
    {
        // WPF marshals scalar property-change notifications to the UI thread, so it's safe
        // to update these from the snooze expiry timer / DND poller thread.
        IsMuted = _snooze.IsMuted(out var reason);
        MuteStatus = reason;
        IsManuallySnoozed = _snooze.SnoozedUntil is not null;
    }

    [RelayCommand]
    private void Snooze30() => _snooze.SnoozeFor(TimeSpan.FromMinutes(30));

    [RelayCommand]
    private void Snooze1Hour() => _snooze.SnoozeFor(TimeSpan.FromHours(1));

    [RelayCommand]
    private void SnoozeUntilTomorrow() => _snooze.SnoozeUntil(NextMorning());

    [RelayCommand]
    private void Resume() => _snooze.Resume();

    /// <summary>8am tomorrow, local time.</summary>
    private static DateTimeOffset NextMorning()
    {
        var tomorrow = DateTime.Today.AddDays(1).AddHours(8);
        return new DateTimeOffset(tomorrow, TimeZoneInfo.Local.GetUtcOffset(tomorrow));
    }

    [RelayCommand]
    private void OpenDashboard() => OpenDashboardRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void ShowAbout() => AboutRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void Exit() => ExitRequested?.Invoke(this, EventArgs.Empty);

    public void Dispose() => _snooze.Changed -= OnSnoozeChanged;
}

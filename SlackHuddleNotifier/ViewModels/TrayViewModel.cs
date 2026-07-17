using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SlackHuddleNotifier.Services;

namespace SlackHuddleNotifier.ViewModels;

public sealed partial class TrayViewModel : ObservableObject
{
    /// <summary>Live status, bound by the tray menu and shared with the dashboard.</summary>
    public AppStatus Status { get; }

    public event EventHandler? ExitRequested;
    public event EventHandler? OpenDashboardRequested;
    public event EventHandler? AboutRequested;

    public TrayViewModel(AppStatus status) => Status = status;

    [RelayCommand]
    private void OpenDashboard() => OpenDashboardRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void ShowAbout() => AboutRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void Exit() => ExitRequested?.Invoke(this, EventArgs.Empty);
}

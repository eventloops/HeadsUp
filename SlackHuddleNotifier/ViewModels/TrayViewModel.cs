using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Windows;

namespace SlackHuddleNotifier.ViewModels;

public sealed partial class TrayViewModel : ObservableObject
{
    [ObservableProperty]
    private string _statusText = "Connecting...";

    [ObservableProperty]
    private bool _isConnected;

    public event EventHandler? ExitRequested;

    public void SetConnected(bool connected)
    {
        IsConnected = connected;
        StatusText = connected ? "Connected to Slack" : "Disconnected";
    }

    [RelayCommand]
    private void ShowAbout()
    {
        MessageBox.Show(
            "Slack Huddle Notifier\n\nMonitors your Slack workspace and shows prominent notifications when a Huddle starts.",
            "About",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    [RelayCommand]
    private void Exit()
    {
        ExitRequested?.Invoke(this, EventArgs.Empty);
    }
}

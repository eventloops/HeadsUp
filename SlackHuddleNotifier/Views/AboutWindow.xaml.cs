using SlackHuddleNotifier.Services;
using System.Windows;

namespace SlackHuddleNotifier.Views;

public partial class AboutWindow : Window
{
    public AppStatus Status { get; }
    public string Version { get; }

    public AboutWindow(AppStatus status)
    {
        InitializeComponent();
        Status = status;
        Version = GetType().Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        DataContext = this;
    }
}

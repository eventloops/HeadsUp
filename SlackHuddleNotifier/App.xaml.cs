using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SlackHuddleNotifier.Models;
using SlackHuddleNotifier.Services;
using SlackHuddleNotifier.ViewModels;
using SlackHuddleNotifier.Views;
using System.Windows;

namespace SlackHuddleNotifier;

public partial class App : Application
{
    private IHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _host = Program.CreateHostBuilder(e.Args).Build();

        // Wire the tray view model to the running host status.
        var trayVm = _host.Services.GetRequiredService<TrayViewModel>();

        // Show the hidden main window (hosts the tray icon).
        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        mainWindow.Show();

        try
        {
            await _host.StartAsync();
            trayVm.SetConnected(true);

            // Preview the popup without a real huddle: --test-popup [--priority]
            if (e.Args.Contains("--test-popup"))
            {
                var notifier = _host.Services.GetRequiredService<INotificationService>();
                _ = notifier.ShowHuddleNotificationAsync(new HuddleEvent(
                    InitiatorUserId: "U000TEST",
                    InitiatorDisplayName: "Sarah Whitfield",
                    InitiatorAvatarUrl: null,
                    ChannelId: "C000TEST",
                    ChannelName: "project-atlas",
                    IsDirectMessage: false,
                    HuddleLink: "https://slack.com",
                    IsPriorityUser: e.Args.Contains("--priority"),
                    OccurredAt: DateTimeOffset.Now));
            }
        }
        catch (Exception ex)
        {
            var logger = _host.Services.GetRequiredService<ILogger<App>>();
            logger.LogCritical(ex, "Host failed to start.");
            trayVm.SetConnected(false);
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            await _host.StopAsync(TimeSpan.FromSeconds(5));
            _host.Dispose();
        }

        base.OnExit(e);
    }
}

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using HeadsUp.Models;
using HeadsUp.Services;
using HeadsUp.ViewModels;
using HeadsUp.Views;
using System.Windows;

namespace HeadsUp;

public partial class App : Application
{
    private IHost? _host;
    private DashboardWindow? _dashboard;
    private AboutWindow? _about;
    private bool _dashboardShownOnce;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _host = Program.CreateHostBuilder(e.Args).Build();

        // Wire the tray view model to the running host status.
        var trayVm = _host.Services.GetRequiredService<TrayViewModel>();
        trayVm.OpenDashboardRequested += (_, _) => ShowDashboard();
        trayVm.AboutRequested += (_, _) => ShowAbout();

        // Show the hidden main window (hosts the tray icon).
        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        mainWindow.Show();

        // Bring the overview up automatically the first time we connect (after sign-in on a
        // cold start, or right away on a warm session). Subscribed before StartAsync so a
        // fast connect can't slip in ahead of the handler.
        var status = _host.Services.GetRequiredService<AppStatus>();
        status.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(AppStatus.IsConnected) && status.IsConnected && !_dashboardShownOnce)
            {
                _dashboardShownOnce = true;
                Dispatcher.BeginInvoke(new Action(ShowDashboard));
            }
        };

        try
        {
            await _host.StartAsync();

            // Preview the popup without a real huddle: --test-popup [--priority]
            if (e.Args.Contains("--test-popup"))
            {
                var notifier = _host.Services.GetRequiredService<INotificationService>();
                _ = notifier.ShowAlertAsync(new Alert(
                    InitiatorUserId: "U000TEST",
                    InitiatorDisplayName: "Sarah Whitfield",
                    InitiatorAvatarUrl: null,
                    ChannelId: "C000TEST",
                    ChannelName: "project-atlas",
                    IsDirectMessage: false,
                    Link: "https://app.slack.com/huddle/T000TEST/C000TEST",
                    IsPriorityUser: e.Args.Contains("--priority"),
                    OccurredAt: DateTimeOffset.Now));
            }
        }
        catch (Exception ex)
        {
            var logger = _host.Services.GetRequiredService<ILogger<App>>();
            logger.LogCritical(ex, "Host failed to start.");
        }
    }

    private void ShowDashboard()
    {
        if (_dashboard is null)
        {
            _dashboard = _host!.Services.GetRequiredService<DashboardWindow>();
            _dashboard.Closed += (_, _) => _dashboard = null;
            _dashboard.Show();
        }
        else
        {
            _dashboard.Activate();
        }
    }

    private void ShowAbout()
    {
        if (_about is null)
        {
            _about = _host!.Services.GetRequiredService<AboutWindow>();
            _about.Closed += (_, _) => _about = null;
            _about.Show();
        }
        else
        {
            _about.Activate();
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

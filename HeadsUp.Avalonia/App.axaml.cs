using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Events;
using HeadsUp.AvaloniaApp.Services;
using HeadsUp.Configuration;
using HeadsUp.Models;
using HeadsUp.Services;
using System.IO;

namespace HeadsUp.AvaloniaApp;

public partial class App : Application
{
    private IHost? _host;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override async void OnFrameworkInitializationCompleted()
    {
        base.OnFrameworkInitializationCompleted();

        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return;

        // Tray-only app: keep running with no windows open.
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        desktop.ShutdownRequested += (_, _) => StopHost();

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.Console(outputTemplate:
                "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(
                path: Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "HeadsUp", "logs", "log-.txt"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7)
            .CreateLogger();

        _host = Host.CreateDefaultBuilder(desktop.Args ?? [])
            .UseSerilog()
            .ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.SetBasePath(AppContext.BaseDirectory)
                   .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                   // Git-ignored local override for secrets (Slack token/cookie). Wins over appsettings.json.
                   .AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true)
                   .AddEnvironmentVariables("SHN_")
                   .AddCommandLine(desktop.Args ?? []);
            })
            .ConfigureServices((ctx, services) =>
            {
                services.AddHeadsUpCore(ctx.Configuration);

                // Detection sources: each configured connector runs side by side (legacy
                // single-Mode configs resolve to one connector).
                var detection = ctx.Configuration.GetSection(DetectionOptions.SectionName)
                    .Get<DetectionOptions>() ?? new();
                foreach (var connector in detection.ResolveConnectors())
                    services.AddConnector(ctx.Configuration, connector);

                services.AddSingleton<INotificationService, AvaloniaNotificationService>();
            })
            .Build();

        try
        {
            await _host.StartAsync();
            SetStatus("Connected to Slack");
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Host failed to start.");
            SetStatus("Disconnected");
        }

        // Preview the popup without a real huddle: --test-popup [--priority]
        var args = desktop.Args ?? [];
        if (args.Contains("--test-popup"))
            ShowTestPopup(args.Contains("--priority"));
    }

    private void OnTestNotificationClicked(object? sender, EventArgs e) => ShowTestPopup(false);

    private void ShowTestPopup(bool priority)
    {
        var notifier = _host?.Services.GetRequiredService<INotificationService>();
        _ = notifier?.ShowAlertAsync(new Alert(
            InitiatorUserId: "U000TEST",
            InitiatorDisplayName: "Sarah Whitfield",
            InitiatorAvatarUrl: null,
            ChannelId: "C000TEST",
            ChannelName: "project-atlas",
            IsDirectMessage: false,
            Link: "https://slack.com",
            IsPriorityUser: priority,
            OccurredAt: DateTimeOffset.Now));
    }

    private void SetStatus(string text)
    {
        var trayIcons = TrayIcon.GetIcons(this);
        var menu = trayIcons?.FirstOrDefault()?.Menu;
        if (menu?.Items.FirstOrDefault() is NativeMenuItem status)
            status.Header = text;
    }

    private void OnExitClicked(object? sender, EventArgs e)
    {
        StopHost();
        (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }

    private void StopHost()
    {
        try
        {
            _host?.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            _host?.Dispose();
            _host = null;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Error while stopping host.");
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }
}

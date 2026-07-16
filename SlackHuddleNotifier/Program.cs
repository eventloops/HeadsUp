using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Events;
using SlackHuddleNotifier.Configuration;
using SlackHuddleNotifier.Services;
using SlackHuddleNotifier.ViewModels;
using SlackHuddleNotifier.Views;
using System.IO;

namespace SlackHuddleNotifier;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Configure Serilog early so console + file logs work before the host starts.
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.Console(outputTemplate:
                "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(
                path: Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SlackHuddleNotifier", "logs", "log-.txt"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7)
            .CreateLogger();

        try
        {
            Log.Information("Starting Slack Huddle Notifier...");

            // WPF requires STA and its own startup path.
            var app = new App();
            app.InitializeComponent();
            app.Run();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Application terminated unexpectedly.");
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    /// <summary>
    /// Builds the <see cref="IHost"/> shared between <see cref="Program"/> and <see cref="App"/>.
    /// </summary>
    public static IHostBuilder CreateHostBuilder(string[] args) =>
        Host.CreateDefaultBuilder(args)
            .UseSerilog()
            .ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.SetBasePath(AppContext.BaseDirectory)
                   .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                   // Git-ignored local override for secrets (Slack token/cookie). Wins over appsettings.json.
                   .AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true)
                   .AddEnvironmentVariables("SHN_")
                   .AddCommandLine(args);
            })
            .ConfigureServices((ctx, services) =>
            {
                // Platform-neutral pipeline (options, coordinator).
                services.AddSlackHuddleNotifierCore(ctx.Configuration);

                // Detection source: Slack API (default) or Slack's own Windows toasts.
                var detection = ctx.Configuration.GetSection(DetectionOptions.SectionName)
                    .Get<DetectionOptions>() ?? new();
                if (detection.UseWindowsNotifications)
                {
                    services.AddSingleton<WindowsNotificationHuddleSource>();
                    services.AddSingleton<IHuddleEventSource>(sp => sp.GetRequiredService<WindowsNotificationHuddleSource>());
                    services.AddHostedService(sp => sp.GetRequiredService<WindowsNotificationHuddleSource>());
                }
                else if (detection.UseSlackClientApi)
                {
                    // WebView2 credentials survive rotation but are Windows-only, so they're
                    // registered here in the WPF head (before the core call, whose TryAdd then
                    // leaves this provider in place instead of the config one).
                    var clientOpts = ctx.Configuration.GetSection(SlackClientOptions.SectionName)
                        .Get<SlackClientOptions>() ?? new();
                    if (clientOpts.UseWebViewCredentials)
                        services.AddSingleton<ISlackCredentialProvider, WebView2SlackCredentialProvider>();

                    services.AddSlackClientApiHuddleDetection(ctx.Configuration);
                }
                else
                {
                    services.AddSlackApiHuddleDetection(ctx.Configuration);
                }

                // Windows presentation layer.
                services.AddSingleton<INotificationService, WpfNotificationService>();

                // WPF ViewModels & Views (singletons so the DI container owns lifetime)
                services.AddSingleton<TrayViewModel>();
                services.AddSingleton<MainWindow>();
            });
}

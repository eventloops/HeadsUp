using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Events;
using SlackHuddleNotifier.Configuration;
using SlackHuddleNotifier.Services;
using SlackHuddleNotifier.ViewModels;
using SlackHuddleNotifier.Views;
using SlackNet.Events;
using SlackNet.Extensions.DependencyInjection;
using System.IO;
using System.Windows;

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
                   .AddEnvironmentVariables("SHN_")
                   .AddCommandLine(args);
            })
            .ConfigureServices((ctx, services) =>
            {
                // Options
                services.Configure<SlackOptions>(ctx.Configuration.GetSection(SlackOptions.SectionName));
                services.Configure<NotificationOptions>(ctx.Configuration.GetSection(NotificationOptions.SectionName));

                // SlackNet - Socket Mode client. The huddle monitor doubles as the
                // message event handler, so resolve the same singleton instance.
                var slackOpts = ctx.Configuration.GetSection(SlackOptions.SectionName).Get<SlackOptions>() ?? new();
                services.AddSlackNet(c =>
                {
                    c.UseApiToken(slackOpts.BotToken);
                    c.UseAppLevelToken(slackOpts.AppToken);
                    c.RegisterEventHandler<MessageEvent>(sp => sp.GetRequiredService<SlackHuddleMonitor>());
                });

                // Core services
                services.AddSingleton<SlackHuddleMonitor>();
                services.AddSingleton<IHuddleEventSource>(sp => sp.GetRequiredService<SlackHuddleMonitor>());
                services.AddHostedService(sp => sp.GetRequiredService<SlackHuddleMonitor>());

                services.AddSingleton<INotificationService, WpfNotificationService>();
                services.AddHostedService<HuddleNotificationCoordinator>();

                // WPF ViewModels & Views (singletons so the DI container owns lifetime)
                services.AddSingleton<TrayViewModel>();
                services.AddSingleton<MainWindow>();
            });
}

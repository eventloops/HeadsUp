using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Events;
using HeadsUp.Configuration;
using HeadsUp.Services;
using HeadsUp.ViewModels;
using HeadsUp.Views;
using System.IO;

namespace HeadsUp;

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
                    "HeadsUp", "logs", "log-.txt"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7)
            .CreateLogger();

        try
        {
            Log.Information("Starting Heads-Up...");

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
                services.AddHeadsUpCore(ctx.Configuration);

                // Detection source: the client-session prototype (SlackClientApi) or the
                // official Slack app (SlackApi).
                var detection = ctx.Configuration.GetSection(DetectionOptions.SectionName)
                    .Get<DetectionOptions>() ?? new();
                if (detection.UseSlackClientApi)
                {
                    // WebView2 credentials survive rotation but are Windows-only, so they're
                    // registered here in the WPF head (before the core call, whose TryAdd then
                    // leaves this provider in place instead of the config one).
                    var clientOpts = ctx.Configuration.GetSection(SlackClientOptions.SectionName)
                        .Get<SlackClientOptions>() ?? new();
                    if (clientOpts.UseWebViewCredentials)
                    {
                        // Gate the WebView2 sign-in behind the welcome screen on first run.
                        services.AddSingleton<ConnectionGate>();
                        services.AddSingleton<ISlackCredentialProvider, WebView2SlackCredentialProvider>();
                    }

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

                // On-demand windows (fresh instance each open).
                services.AddTransient<DashboardViewModel>();
                services.AddTransient<DashboardWindow>();
                services.AddTransient<AboutWindow>();
                services.AddTransient<WelcomeViewModel>();
                services.AddTransient<WelcomeWindow>();
            });
}

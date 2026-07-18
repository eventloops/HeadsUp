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
                // Application pipeline (options, coordinator) + infrastructure (status, stores).
                services.AddHeadsUpApplication(ctx.Configuration);
                services.AddHeadsUpInfrastructure(ctx.Configuration);

                // Detection sources: each configured connector runs side by side (legacy
                // single-Mode configs resolve to one connector).
                var detection = ctx.Configuration.GetSection(DetectionOptions.SectionName)
                    .Get<DetectionOptions>() ?? new();
                var connectors = detection.ResolveConnectors();

                // WebView2 credentials survive rotation but are Windows-only, so they're
                // registered here in the WPF head (before AddConnector, whose TryAdd then
                // leaves this provider in place instead of the config one). Registered once
                // if any Slack client-session connector is configured to use it.
                var wantsWebView = connectors.Any(c =>
                    string.Equals(c.Type, DetectionOptions.SlackClientApi, StringComparison.OrdinalIgnoreCase));
                if (wantsWebView)
                {
                    var clientOpts = ctx.Configuration.GetSection(SlackClientOptions.SectionName)
                        .Get<SlackClientOptions>() ?? new();
                    if (clientOpts.UseWebViewCredentials)
                    {
                        // Gate the WebView2 sign-in behind the welcome screen on first run.
                        services.AddSingleton<ConnectionGate>();
                        services.AddSingleton<ISlackCredentialProvider, WebView2SlackCredentialProvider>();
                    }
                }

                // The sanctioned Slack-app path signs in through the browser with PKCE and
                // stores its tokens encrypted, so it replaces the config-file provider that
                // AddConnector would otherwise TryAdd. Registered here rather than in
                // Infrastructure because at-rest encryption is platform-specific (DPAPI).
                var wantsSlackApi = connectors.Any(c =>
                    string.Equals(c.Type, DetectionOptions.SlackApi, StringComparison.OrdinalIgnoreCase));
                if (wantsSlackApi)
                {
                    services.AddSingleton<ConnectionGate>();
                    services.AddSingleton<ITokenProtector, DpapiTokenProtector>();
                    services.AddSingleton<ISlackTokenStore, FileSlackTokenStore>();
                    services.AddSingleton<IBrowserLauncher, SystemBrowserLauncher>();
                    services.AddSingleton<ISlackAppSetupPrompt, WpfSlackAppSetupPrompt>();
                    services.AddSingleton<LoopbackOAuthListener>();
                    services.AddHttpClient<SlackOAuthClient>();
                    services.AddSingleton<ISlackAppCredentialProvider, OAuthSlackAppCredentialProvider>();
                }

                foreach (var connector in connectors)
                    services.AddConnector(ctx.Configuration, connector);

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

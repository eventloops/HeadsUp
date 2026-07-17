using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using HeadsUp.Configuration;
using HeadsUp.Services;
using SlackNet.Events;
using SlackNet.Extensions.DependencyInjection;

namespace HeadsUp;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the platform-neutral pipeline: options and the notification
    /// coordinator. The platform head must also register an
    /// <see cref="IAlertSource"/> (e.g. <see cref="AddSlackApiHuddleDetection"/>)
    /// and its own <see cref="INotificationService"/>.
    /// </summary>
    public static IServiceCollection AddHeadsUpCore(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<SlackOptions>(configuration.GetSection(SlackOptions.SectionName));
        services.Configure<NotificationOptions>(configuration.GetSection(NotificationOptions.SectionName));
        services.Configure<DetectionOptions>(configuration.GetSection(DetectionOptions.SectionName));
        services.Configure<AlertOptions>(configuration.GetSection(AlertOptions.SectionName));
        services.Configure<PriorityOptions>(configuration.GetSection(PriorityOptions.SectionName));

        // Shared live status the tray + dashboard bind to; each detection source updates its
        // own SourceStatus entry. Seeded once, per resolved connector, so every configured
        // source shows a card in a "Starting…" state before it connects.
        services.AddSingleton(_ =>
        {
            var detection = configuration.GetSection(DetectionOptions.SectionName).Get<DetectionOptions>() ?? new();
            var status = new AppStatus();
            foreach (var connector in detection.ResolveConnectors())
            {
                var (id, name) = ConnectorIdentity(connector.Type);
                status.Seed(id, name, connector.Type);
            }
            return status;
        });

        // Persist dashboard edits back to appsettings.Local.json (hot-reloaded).
        services.AddSingleton<IPrioritySettingsStore, JsonPrioritySettingsStore>();
        services.AddSingleton<IAlertSettingsStore, JsonAlertSettingsStore>();

        // In-memory log of surfaced alerts, shown in the dashboard's "Recent alerts".
        services.AddSingleton<IAlertHistory, AlertHistory>();

        // Resolves per-connector deep links for opening an alert; connectors contribute
        // their own IAlertLinkResolver. Falls back to the raw link when none match.
        services.AddSingleton<AlertLinks>();

        // Snooze / Slack-DND gate. Concrete registered too: the tray drives snooze and the
        // DND poller feeds it, while the coordinator only reads it through IAlertGate.
        services.AddSingleton<SnoozeService>();
        services.AddSingleton<IAlertGate>(sp => sp.GetRequiredService<SnoozeService>());

        services.AddHostedService<HuddleNotificationCoordinator>();

        return services;
    }

    /// <summary>
    /// Registers the detection source(s) for a connector by its route type. Slack routes
    /// map to the Socket Mode or client-session source; unknown types are ignored (logged
    /// by the caller). Platform-specific extras (e.g. the WPF WebView2 credential provider)
    /// are registered by the head before calling this.
    /// </summary>
    public static IServiceCollection AddConnector(
        this IServiceCollection services, IConfiguration configuration, ConnectorOptions connector)
    {
        if (string.Equals(connector.Type, DetectionOptions.SlackClientApi, StringComparison.OrdinalIgnoreCase))
            return services.AddSlackClientApiHuddleDetection(configuration);

        if (string.Equals(connector.Type, DetectionOptions.SlackApi, StringComparison.OrdinalIgnoreCase))
            return services.AddSlackApiHuddleDetection(configuration);

        // Unknown connector type: leave it unregistered rather than fail startup.
        return services;
    }

    /// <summary>Maps a connector route type to its source identity. Both Slack routes are "slack".</summary>
    private static (string Id, string Name) ConnectorIdentity(string type)
    {
        if (string.Equals(type, DetectionOptions.SlackClientApi, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(type, DetectionOptions.SlackApi, StringComparison.OrdinalIgnoreCase))
            return (SlackSource.Id, SlackSource.Name);

        // Future connectors: fall back to the route type as both id and display name.
        return (type.ToLowerInvariant(), type);
    }

    /// <summary>
    /// Detection via the Slack API: Socket Mode with the user's tokens.
    /// The huddle monitor doubles as the message event handler, so the
    /// same singleton instance is resolved for both roles.
    /// </summary>
    public static IServiceCollection AddSlackApiHuddleDetection(
        this IServiceCollection services, IConfiguration configuration)
    {
        var slackOpts = configuration.GetSection(SlackOptions.SectionName).Get<SlackOptions>() ?? new();
        services.AddSlackNet(c =>
        {
            c.UseApiToken(slackOpts.UserToken);
            c.UseAppLevelToken(slackOpts.AppToken);
            c.RegisterEventHandler<MessageEvent>(sp => sp.GetRequiredService<SlackHuddleMonitor>());
        });

        services.AddSingleton<SlackHuddleMonitor>();
        services.AddSingleton<IAlertSource>(sp => sp.GetRequiredService<SlackHuddleMonitor>());
        services.AddHostedService(sp => sp.GetRequiredService<SlackHuddleMonitor>());
        services.AddSingleton<IAlertLinkResolver, SlackAlertLinkResolver>();

        return services;
    }

    /// <summary>
    /// Detection via the unofficial Slack client API: an xoxc client token plus
    /// the "d" session cookie, exactly what a browser/desktop session uses. No
    /// Slack app and no admin approval required. Prototype - see
    /// <see cref="Services.SlackWebHuddleSource"/> for the caveats.
    /// </summary>
    public static IServiceCollection AddSlackClientApiHuddleDetection(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<SlackClientOptions>(configuration.GetSection(SlackClientOptions.SectionName));

        // Default credential source is static config (browser-paste). A platform head may
        // register a live ISlackCredentialProvider (e.g. WebView2) BEFORE calling this, and
        // TryAdd leaves that one in place.
        services.TryAddSingleton<ISlackCredentialProvider, ConfigSlackCredentialProvider>();

        // Workspace directory for choosing priority users by name (client-API mode).
        services.TryAddSingleton<IUserDirectory, SlackWebUserDirectory>();

        services.AddSingleton<SlackWebHuddleSource>();
        services.AddSingleton<IAlertSource>(sp => sp.GetRequiredService<SlackWebHuddleSource>());
        services.AddHostedService(sp => sp.GetRequiredService<SlackWebHuddleSource>());
        services.AddSingleton<IAlertLinkResolver, SlackAlertLinkResolver>();

        return services;
    }
}

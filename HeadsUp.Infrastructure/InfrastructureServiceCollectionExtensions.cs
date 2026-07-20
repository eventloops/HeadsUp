using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using HeadsUp.Configuration;
using HeadsUp.Models;
using HeadsUp.Services;
using SlackNet.Events;
using SlackNet.Extensions.DependencyInjection;

namespace HeadsUp;

public static class InfrastructureServiceCollectionExtensions
{
    /// <summary>
    /// Registers the infrastructure layer: the shared per-source live status (seeded per
    /// configured connector) and the JSON settings-store adapters. Connectors are added
    /// separately with <see cref="AddConnector"/>.
    /// </summary>
    public static IServiceCollection AddHeadsUpInfrastructure(
        this IServiceCollection services, IConfiguration configuration)
    {
        // Shared live status the tray + dashboard bind to; each detection source updates its
        // own SourceStatus entry. Seeded once, per resolved connector, so every configured
        // source shows a card in a "Starting…" state before it connects. (Seeding needs the
        // connector→source mapping, which is Slack knowledge — hence it lives here.)
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

        return services;
    }

    /// <summary>
    /// Registers the detection source(s) for a connector by its route type. Slack routes
    /// map to the Socket Mode or client-session source; unknown types are ignored.
    /// Platform-specific extras (e.g. the WPF WebView2 credential provider) are registered
    /// by the head before calling this.
    /// </summary>
    public static IServiceCollection AddConnector(
        this IServiceCollection services, IConfiguration configuration, ConnectorOptions connector)
    {
        if (string.Equals(connector.Type, DetectionOptions.SlackClientApi, StringComparison.OrdinalIgnoreCase))
            return services.AddSlackClientApiHuddleDetection(configuration);

        if (string.Equals(connector.Type, DetectionOptions.SlackApi, StringComparison.OrdinalIgnoreCase))
            return services.AddSlackApiHuddleDetection(configuration);

        if (string.Equals(connector.Type, DetectionOptions.SlackRelay, StringComparison.OrdinalIgnoreCase))
            return services.AddSlackRelayDetection(configuration);

        // Unknown connector type: leave it unregistered rather than fail startup.
        return services;
    }

    /// <summary>Maps a connector route type to its source identity. All Slack routes are "slack".</summary>
    private static (SourceId Id, string Name) ConnectorIdentity(string type)
    {
        if (string.Equals(type, DetectionOptions.SlackClientApi, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(type, DetectionOptions.SlackApi, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(type, DetectionOptions.SlackRelay, StringComparison.OrdinalIgnoreCase))
            return (SlackSource.SourceId, SlackSource.Name);

        // Future connectors: fall back to the route type as both id and display name.
        return (new SourceId(type), type);
    }

    /// <summary>
    /// Detection via the Slack API: Socket Mode with the user's own Slack app. The huddle
    /// monitor doubles as the message event handler, so the same singleton is resolved for
    /// both roles.
    ///
    /// Clients are built per connection rather than registered up front, because OAuth tokens
    /// aren't known at startup and rotate afterwards — see <see cref="ISlackClientFactory"/>.
    /// </summary>
    public static IServiceCollection AddSlackApiHuddleDetection(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<SlackOptions>(configuration.GetSection(SlackOptions.SectionName));

        services.TryAddSingleton<ISlackClientFactory, SlackClientFactory>();

        // Default credential source is static config (hand-pasted tokens). A platform head may
        // register a live ISlackAppCredentialProvider (e.g. the PKCE OAuth one) BEFORE calling
        // this, and TryAdd leaves that one in place.
        services.TryAddSingleton<ISlackAppCredentialProvider, ConfigSlackAppCredentialProvider>();

        // Workspace directory for choosing priority users by name (SlackApi mode).
        services.TryAddSingleton<IUserDirectory, SlackApiUserDirectory>();

        services.AddSingleton<SlackHuddleMonitor>();
        services.AddSingleton<IAlertSource>(sp => sp.GetRequiredService<SlackHuddleMonitor>());
        services.AddHostedService(sp => sp.GetRequiredService<SlackHuddleMonitor>());
        services.AddSingleton<IAlertLinkResolver, SlackAlertLinkResolver>();

        return services;
    }

    /// <summary>
    /// Detection via the Heads-Up relay: the company runs one Slack app, and the desktop
    /// receives only the events Slack says this user may see. Classification stays local, so
    /// the relay never learns anyone's priority people, keywords or muted channels.
    /// </summary>
    public static IServiceCollection AddSlackRelayDetection(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<RelayClientOptions>(configuration.GetSection(RelayClientOptions.SectionName));

        // A platform head may register a provider that can actually sign the user in; TryAdd
        // leaves that one in place.
        services.TryAddSingleton<IRelayCredentialProvider, ConfigRelayCredentialProvider>();

        // In relay mode the desktop holds no Slack token, so it cannot search the workspace
        // itself. Registered explicitly because the dashboard requires an IUserDirectory —
        // without one, opening it threw rather than degrading.
        services.TryAddSingleton<IUserDirectory, UnavailableUserDirectory>();

        services.AddSingleton<SlackRelaySource>();
        services.AddSingleton<IAlertSource>(sp => sp.GetRequiredService<SlackRelaySource>());
        services.AddHostedService(sp => sp.GetRequiredService<SlackRelaySource>());
        services.AddSingleton<IAlertLinkResolver, SlackAlertLinkResolver>();

        return services;
    }

    /// <summary>
    /// Detection via the unofficial Slack client API: an xoxc client token plus the "d"
    /// session cookie. No Slack app and no admin approval required. Prototype — see
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

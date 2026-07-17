using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using HeadsUp.Configuration;
using HeadsUp.Services;

namespace HeadsUp;

public static class ApplicationServiceCollectionExtensions
{
    /// <summary>
    /// Registers the application layer: the alert options, the notification coordinator, and
    /// the shared services it orchestrates (history, snooze gate, deep-link resolution). The
    /// composition root must also add the infrastructure (<see cref="InfrastructureServiceCollectionExtensions.AddHeadsUpInfrastructure"/>
    /// and the connectors) and its own <see cref="INotificationService"/>.
    /// </summary>
    public static IServiceCollection AddHeadsUpApplication(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<NotificationOptions>(configuration.GetSection(NotificationOptions.SectionName));
        services.Configure<DetectionOptions>(configuration.GetSection(DetectionOptions.SectionName));
        services.Configure<AlertOptions>(configuration.GetSection(AlertOptions.SectionName));
        services.Configure<PriorityOptions>(configuration.GetSection(PriorityOptions.SectionName));

        // In-memory log of surfaced alerts, shown in the dashboard's "Recent alerts".
        services.AddSingleton<IAlertHistory, AlertHistory>();

        // Resolves per-connector deep links for opening an alert; connectors contribute
        // their own IAlertLinkResolver. Falls back to the raw link when none match.
        services.AddSingleton<AlertLinks>();

        // Snooze / per-source-DND gate. Concrete registered too: the tray drives snooze and
        // the DND poller feeds it, while the coordinator only reads it through IAlertGate.
        services.AddSingleton<SnoozeService>();
        services.AddSingleton<IAlertGate>(sp => sp.GetRequiredService<SnoozeService>());

        services.AddHostedService<HuddleNotificationCoordinator>();

        return services;
    }
}

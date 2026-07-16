using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SlackHuddleNotifier.Configuration;
using SlackHuddleNotifier.Services;
using SlackNet.Events;
using SlackNet.Extensions.DependencyInjection;

namespace SlackHuddleNotifier;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the platform-neutral pipeline: options, SlackNet Socket Mode,
    /// the huddle monitor and the notification coordinator.
    /// The platform head must register its own <see cref="INotificationService"/>.
    /// </summary>
    public static IServiceCollection AddSlackHuddleNotifierCore(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<SlackOptions>(configuration.GetSection(SlackOptions.SectionName));
        services.Configure<NotificationOptions>(configuration.GetSection(NotificationOptions.SectionName));

        // SlackNet - Socket Mode client. The huddle monitor doubles as the
        // message event handler, so resolve the same singleton instance.
        var slackOpts = configuration.GetSection(SlackOptions.SectionName).Get<SlackOptions>() ?? new();
        services.AddSlackNet(c =>
        {
            c.UseApiToken(slackOpts.BotToken);
            c.UseAppLevelToken(slackOpts.AppToken);
            c.RegisterEventHandler<MessageEvent>(sp => sp.GetRequiredService<SlackHuddleMonitor>());
        });

        services.AddSingleton<SlackHuddleMonitor>();
        services.AddSingleton<IHuddleEventSource>(sp => sp.GetRequiredService<SlackHuddleMonitor>());
        services.AddHostedService(sp => sp.GetRequiredService<SlackHuddleMonitor>());
        services.AddHostedService<HuddleNotificationCoordinator>();

        return services;
    }
}

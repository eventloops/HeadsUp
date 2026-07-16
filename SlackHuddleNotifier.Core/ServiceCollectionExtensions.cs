using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SlackHuddleNotifier.Configuration;
using SlackHuddleNotifier.Services;
using SlackNet.Events;
using SlackNet.Extensions.DependencyInjection;

namespace SlackHuddleNotifier;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the platform-neutral pipeline: options and the notification
    /// coordinator. The platform head must also register an
    /// <see cref="IHuddleEventSource"/> (e.g. <see cref="AddSlackApiHuddleDetection"/>)
    /// and its own <see cref="INotificationService"/>.
    /// </summary>
    public static IServiceCollection AddSlackHuddleNotifierCore(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<SlackOptions>(configuration.GetSection(SlackOptions.SectionName));
        services.Configure<NotificationOptions>(configuration.GetSection(NotificationOptions.SectionName));
        services.Configure<DetectionOptions>(configuration.GetSection(DetectionOptions.SectionName));

        services.AddHostedService<HuddleNotificationCoordinator>();

        return services;
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
        services.AddSingleton<IHuddleEventSource>(sp => sp.GetRequiredService<SlackHuddleMonitor>());
        services.AddHostedService(sp => sp.GetRequiredService<SlackHuddleMonitor>());

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

        services.AddSingleton<SlackWebHuddleSource>();
        services.AddSingleton<IHuddleEventSource>(sp => sp.GetRequiredService<SlackWebHuddleSource>());
        services.AddHostedService(sp => sp.GetRequiredService<SlackWebHuddleSource>());

        return services;
    }
}

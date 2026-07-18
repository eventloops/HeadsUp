using SlackNet;
using SlackNet.Events;

namespace HeadsUp.Services;

/// <summary>
/// Builds SlackNet clients from credentials that are only known at runtime.
///
/// SlackNet binds both tokens at builder time, which is fine for hand-pasted config but not
/// for OAuth: the user token doesn't exist until the user signs in, and it rotates
/// afterwards. So rather than resolving pre-configured clients from DI, the monitor asks for
/// a fresh pair once it actually holds credentials.
/// </summary>
public interface ISlackClientFactory
{
    /// <summary>
    /// Creates an API client and a Socket Mode client bound to <paramref name="credentials"/>,
    /// with <paramref name="eventHandler"/> registered for message events.
    /// </summary>
    SlackClients Create(SlackAppCredentials credentials, IEventHandler<MessageEvent> eventHandler);

    /// <summary>
    /// Creates a Web API client alone, for callers that make requests but don't consume the
    /// event stream (e.g. the workspace directory).
    /// </summary>
    ISlackApiClient CreateApiClient(SlackAppCredentials credentials);
}

/// <summary>A matched API + Socket Mode client pair for one set of credentials.</summary>
public sealed record SlackClients(ISlackApiClient Api, ISlackSocketModeClient Socket);

/// <summary>The real factory, backed by <see cref="SlackServiceBuilder"/>.</summary>
public sealed class SlackClientFactory : ISlackClientFactory
{
    public SlackClients Create(SlackAppCredentials credentials, IEventHandler<MessageEvent> eventHandler)
    {
        var builder = new SlackServiceBuilder()
            .UseApiToken(credentials.UserToken)
            .UseAppLevelToken(credentials.AppToken)
            .RegisterEventHandler(_ => eventHandler);

        return new SlackClients(builder.GetApiClient(), builder.GetSocketModeClient());
    }

    public ISlackApiClient CreateApiClient(SlackAppCredentials credentials) =>
        new SlackServiceBuilder()
            .UseApiToken(credentials.UserToken)
            .GetApiClient();
}

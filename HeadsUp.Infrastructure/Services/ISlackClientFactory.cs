using SlackNet;
using SlackNet.SocketMode;

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
    /// Creates a Web API client alone, for callers that make requests but don't consume the
    /// event stream (e.g. the workspace directory).
    /// </summary>
    ISlackApiClient CreateApiClient(SlackAppCredentials credentials);

    /// <summary>
    /// Creates a Socket Mode client exposing <b>raw</b> frames.
    ///
    /// Needed because SlackNet's typed <c>MessageEvent</c> has no <c>room</c> property, and a
    /// huddle's initiator lives only in <c>room.created_by</c> — the typed API cannot see who
    /// started a huddle at all.
    /// </summary>
    ICoreSocketModeClient CreateSocketClient(SlackAppCredentials credentials);
}

/// <summary>The real factory, backed by <see cref="SlackServiceBuilder"/>.</summary>
public sealed class SlackClientFactory : ISlackClientFactory
{
    public ISlackApiClient CreateApiClient(SlackAppCredentials credentials) =>
        new SlackServiceBuilder()
            .UseApiToken(credentials.UserToken)
            .GetApiClient();

    public ICoreSocketModeClient CreateSocketClient(SlackAppCredentials credentials) =>
        new CoreSocketModeClient(credentials.AppToken);
}

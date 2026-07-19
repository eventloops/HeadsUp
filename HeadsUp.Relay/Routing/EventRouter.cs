using Microsoft.Extensions.Logging;

namespace HeadsUp.Relay.Routing;

/// <summary>
/// Delivers each Slack event to exactly the desktops of the users Slack says may see it.
///
/// This is the whole security surface of the relay. Slack sends an event <i>once</i>, however
/// many people can see it, so the relay has to fan it out — and a fan-out bug is precisely how
/// one person's direct messages would end up on another person's screen. Two rules keep that
/// from being possible:
///
/// <list type="number">
/// <item>The only way to send anything is via <see cref="IUserConnectionRegistry.For"/>, which
/// demands a user id. No broadcast primitive exists anywhere in the relay.</item>
/// <item>The recipient list comes solely from Slack's own <c>authorizations</c> — never from
/// the payload's channel membership, never from anything the client asserts.</item>
/// </list>
///
/// The relay also does no interpretation: it forwards the raw payload and lets each desktop
/// apply its own rules. Priority people, keywords and muted channels stay on the user's
/// machine, so the server never learns them.
/// </summary>
public sealed class EventRouter
{
    private readonly IUserConnectionRegistry _registry;
    private readonly IEventAuthorizationResolver _authorizations;
    private readonly ILogger<EventRouter> _logger;

    public EventRouter(
        IUserConnectionRegistry registry,
        IEventAuthorizationResolver authorizations,
        ILogger<EventRouter> logger)
    {
        _registry = registry;
        _authorizations = authorizations;
        _logger = logger;
    }

    /// <summary>
    /// Routes one event. Returns the number of connections it was delivered to, which is zero
    /// whenever the authorized users simply aren't running Heads-Up — the common case, and not
    /// an error.
    /// </summary>
    public async Task<int> RouteAsync(SlackRelayEvent relayEvent, CancellationToken ct)
    {
        var recipients = await _authorizations.ResolveAsync(relayEvent, ct);
        if (recipients.Count == 0)
        {
            _logger.LogDebug("Event has no resolvable authorizations; dropping it.");
            return 0;
        }

        var delivered = 0;
        foreach (var userId in recipients.Distinct(StringComparer.Ordinal))
        {
            // Note the absence of any fallback here: a user with no connections gets nothing.
            // Anything that "helpfully" widened this would be the disclosure bug.
            foreach (var connection in _registry.For(userId))
            {
                try
                {
                    await connection.SendAsync(relayEvent.Payload, ct);
                    delivered++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One dead client must not stop the others receiving their alert.
                    _logger.LogWarning(ex, "Could not deliver an event to a connection for {UserId}.", userId);
                }
            }
        }

        return delivered;
    }
}

namespace HeadsUp.Relay.Routing;

/// <summary>
/// A Slack event as the relay cares about it: the raw payload to forward, plus who is allowed
/// to see it.
/// </summary>
/// <param name="Payload">The raw event JSON, forwarded to the desktop untouched.</param>
/// <param name="Authorizations">
/// User ids from the event's <c>authorizations</c> array. Slack <b>truncates</b> this to a
/// single entry when an event is visible to many people, so it is not safe to treat as complete
/// — see <see cref="EventContext"/>.
/// </param>
/// <param name="EventContext">
/// Opaque handle used with <c>apps.event.authorizations.list</c> to enumerate the full set of
/// authorizations when the inline list was truncated.
/// </param>
public sealed record SlackRelayEvent(string Payload, IReadOnlyList<string> Authorizations, string? EventContext);

/// <summary>
/// Answers "which users is this event visible to?".
///
/// This exists because the inline <c>authorizations</c> array is truncated for widely-visible
/// events. Trusting it verbatim silently under-delivers — some users would never see alerts for
/// busy channels — so the full set is resolved through Slack when a context is available.
/// </summary>
public interface IEventAuthorizationResolver
{
    /// <summary>
    /// The complete set of user ids that may receive <paramref name="relayEvent"/>. Returning
    /// fewer costs an alert; returning more would be a disclosure, so implementations must only
    /// ever return ids Slack has affirmed.
    /// </summary>
    Task<IReadOnlyList<string>> ResolveAsync(SlackRelayEvent relayEvent, CancellationToken ct);
}

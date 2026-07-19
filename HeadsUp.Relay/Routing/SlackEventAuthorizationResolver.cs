using Microsoft.Extensions.Logging;
using SlackNet;

namespace HeadsUp.Relay.Routing;

/// <summary>
/// Resolves an event's authorized users, expanding Slack's truncated inline list via
/// <c>apps.event.authorizations.list</c>.
///
/// Slack includes only one entry in the payload's <c>authorizations</c> array when an event is
/// visible to many people. Using it as-is would mean that in any busy shared channel, exactly
/// one person gets the alert and everyone else silently misses it — a bug that would look like
/// flakiness rather than a routing fault, so it's worth the extra call.
/// </summary>
public sealed class SlackEventAuthorizationResolver : IEventAuthorizationResolver
{
    private readonly ISlackApiClient _api;
    private readonly ILogger<SlackEventAuthorizationResolver> _logger;

    public SlackEventAuthorizationResolver(ISlackApiClient api, ILogger<SlackEventAuthorizationResolver> logger)
    {
        _api = api;
        _logger = logger;
    }

    public async Task<IReadOnlyList<string>> ResolveAsync(SlackRelayEvent relayEvent, CancellationToken ct)
    {
        // No context to expand with: the inline list is all there is.
        if (string.IsNullOrEmpty(relayEvent.EventContext))
            return relayEvent.Authorizations;

        try
        {
            var full = await ListAuthorizationsAsync(relayEvent.EventContext, ct);

            // Union rather than replace: if the lookup came back short for any reason, we still
            // honour what Slack already told us inline.
            return full.Count == 0
                ? relayEvent.Authorizations
                : full.Union(relayEvent.Authorizations, StringComparer.Ordinal).ToList();
        }
        catch (Exception ex)
        {
            // Degrade to the inline list. Under-delivering is the safe failure: someone may miss
            // an alert, but nobody receives one that isn't theirs.
            _logger.LogWarning(ex,
                "Could not expand event authorizations; falling back to the inline list of {Count}.",
                relayEvent.Authorizations.Count);
            return relayEvent.Authorizations;
        }
    }

    private async Task<IReadOnlyList<string>> ListAuthorizationsAsync(string eventContext, CancellationToken ct)
    {
        var userIds = new List<string>();
        string? cursor = null;

        do
        {
            // SlackNet has no wrapper for apps.event.authorizations.list, so call it raw.
            // Note this one needs the *app-level* token, which the relay's client is built with.
            var args = new Dictionary<string, object> { ["event_context"] = eventContext };
            if (!string.IsNullOrEmpty(cursor))
                args["cursor"] = cursor;

            var response = await _api.Get<AuthorizationsListResponse>(
                "apps.event.authorizations.list", args, ct);

            foreach (var authorization in response.Authorizations)
            {
                // Bot installations aren't desktops and have no one to notify.
                if (!authorization.IsBot && !string.IsNullOrEmpty(authorization.UserId))
                    userIds.Add(authorization.UserId);
            }

            cursor = response.ResponseMetadata?.NextCursor;
        }
        while (!string.IsNullOrEmpty(cursor) && !ct.IsCancellationRequested);

        return userIds;
    }

    // Minimal shapes for the raw call; only the fields routing actually depends on.
    internal sealed class AuthorizationsListResponse
    {
        public IList<Authorization> Authorizations { get; set; } = [];
        public ResponseMetadata? ResponseMetadata { get; set; }
    }

    internal sealed class Authorization
    {
        public string? UserId { get; set; }
        public bool IsBot { get; set; }
    }

    internal sealed class ResponseMetadata
    {
        public string? NextCursor { get; set; }
    }
}

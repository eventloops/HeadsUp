using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace HeadsUp.Relay.Routing;

/// <summary>
/// Caches authorization lookups so the relay doesn't ask Slack who can see a channel on every
/// single message.
///
/// Without this the relay calls <c>apps.event.authorizations.list</c> once per event, because
/// every event carries an <c>event_context</c>. That method allows roughly 600 requests a
/// minute, and Slack's own event ceiling is 30,000 an hour (~500 a minute) — so an active
/// workspace runs at ~83% of the limit with no headroom, and every alert pays for a network
/// round trip on a product whose entire value is being fast.
///
/// The cache key is the <c>event_context</c> itself. It is base64 JSON carrying the team, app
/// and <b>channel</b> id, so two messages in the same channel produce byte-identical contexts —
/// confirmed against live traffic. That makes it a per-channel key without parsing anything.
///
/// Membership changes are the reason the TTL is short: a stale entry means someone who has
/// left a channel keeps receiving its events until it expires. That is a correctness question,
/// not a performance one, which is why this is measured in a minute or two rather than hours.
/// </summary>
public sealed class CachingEventAuthorizationResolver : IEventAuthorizationResolver
{
    private readonly IEventAuthorizationResolver _inner;
    private readonly TimeSpan _ttl;
    private readonly TimeProvider _clock;
    private readonly ILogger<CachingEventAuthorizationResolver> _logger;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);

    public CachingEventAuthorizationResolver(
        IEventAuthorizationResolver inner,
        ILogger<CachingEventAuthorizationResolver> logger,
        TimeSpan? ttl = null,
        TimeProvider? clock = null)
    {
        _inner = inner;
        _logger = logger;
        _ttl = ttl ?? TimeSpan.FromMinutes(1);
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<IReadOnlyList<string>> ResolveAsync(SlackRelayEvent relayEvent, CancellationToken ct)
    {
        // Nothing to key on: the inline list is all there is, and the inner resolver won't
        // call Slack either.
        if (string.IsNullOrEmpty(relayEvent.EventContext))
            return relayEvent.Authorizations;

        var now = _clock.GetUtcNow();

        if (_cache.TryGetValue(relayEvent.EventContext, out var entry) && entry.ExpiresAt > now)
            return Combine(entry.UserIds, relayEvent.Authorizations);

        var resolved = await _inner.ResolveAsync(relayEvent, ct);

        // Cached even when the lookup degraded to the inline list. A failure is then pinned for
        // at most one TTL, and every event still contributes its own inline authorizations
        // below — so the worst case stays under-delivery, never delivery to the wrong person.
        _cache[relayEvent.EventContext] = new CacheEntry(resolved, now + _ttl);

        return Combine(resolved, relayEvent.Authorizations);
    }

    /// <summary>
    /// Always unions the cached set with this event's own inline authorizations, so a user
    /// Slack names right now is never withheld because of a stale entry.
    /// </summary>
    private static IReadOnlyList<string> Combine(IReadOnlyList<string> cached, IReadOnlyList<string> inline)
    {
        if (inline.Count == 0)
            return cached;

        return cached.Union(inline, StringComparer.Ordinal).ToList();
    }

    /// <summary>Drops expired entries. Called periodically so the map can't grow unbounded.</summary>
    public int Prune()
    {
        var now = _clock.GetUtcNow();
        var removed = 0;

        foreach (var (key, entry) in _cache)
        {
            if (entry.ExpiresAt <= now && _cache.TryRemove(key, out _))
                removed++;
        }

        return removed;
    }

    internal int Count => _cache.Count;

    private sealed record CacheEntry(IReadOnlyList<string> UserIds, DateTimeOffset ExpiresAt);
}

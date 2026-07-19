using HeadsUp.Relay.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace HeadsUp.Relay.Tests;

/// <summary>
/// The cache exists to keep the relay off Slack's ~600/min authorizations limit, but it must
/// not buy that by delivering events to people who should no longer receive them — so these
/// cover expiry and the union with each event's own inline list as much as the hit rate.
/// </summary>
public class CachingEventAuthorizationResolverTests
{
    private readonly CountingResolver _inner = new();
    private readonly FakeTimeProvider _clock = new();

    private CachingEventAuthorizationResolver Resolver(TimeSpan? ttl = null) =>
        new(_inner, NullLogger<CachingEventAuthorizationResolver>.Instance,
            ttl ?? TimeSpan.FromMinutes(1), _clock);

    /// <summary>Events in one channel share an event_context; different channels don't.</summary>
    private static SlackRelayEvent Event(string context, params string[] inline) =>
        new("{}", inline, context);

    // ------------------------------------------------------------------
    // Staying off the rate limit
    // ------------------------------------------------------------------

    [Fact]
    public async Task Repeat_events_in_one_channel_hit_slack_once()
    {
        _inner.Result = ["U_ALICE", "U_BOB"];
        var resolver = Resolver();

        for (var i = 0; i < 20; i++)
            await resolver.ResolveAsync(Event("ctx-general"), CancellationToken.None);

        // 20 messages in #general previously meant 20 API calls.
        Assert.Equal(1, _inner.Calls);
    }

    [Fact]
    public async Task Different_channels_are_cached_separately()
    {
        var resolver = Resolver();

        await resolver.ResolveAsync(Event("ctx-general"), CancellationToken.None);
        await resolver.ResolveAsync(Event("ctx-random"), CancellationToken.None);
        await resolver.ResolveAsync(Event("ctx-general"), CancellationToken.None);

        Assert.Equal(2, _inner.Calls);
    }

    [Fact]
    public async Task An_event_without_a_context_never_reaches_slack()
    {
        var resolved = await Resolver().ResolveAsync(
            Event(context: "", "U_ALICE"), CancellationToken.None);

        Assert.Equal(0, _inner.Calls);
        Assert.Equal(["U_ALICE"], resolved);
    }

    // ------------------------------------------------------------------
    // Not going stale in a way that matters
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_stale_entry_is_refetched_after_the_ttl()
    {
        // Someone leaving a channel must stop receiving its events promptly; this is the
        // window in which that takes effect.
        _inner.Result = ["U_ALICE", "U_BOB"];
        var resolver = Resolver(TimeSpan.FromMinutes(1));
        await resolver.ResolveAsync(Event("ctx"), CancellationToken.None);

        _clock.Advance(TimeSpan.FromMinutes(2));
        _inner.Result = ["U_ALICE"]; // U_BOB has left
        var resolved = await resolver.ResolveAsync(Event("ctx"), CancellationToken.None);

        Assert.Equal(2, _inner.Calls);
        Assert.Equal(["U_ALICE"], resolved);
    }

    [Fact]
    public async Task An_entry_is_still_used_just_before_it_expires()
    {
        var resolver = Resolver(TimeSpan.FromMinutes(1));
        await resolver.ResolveAsync(Event("ctx"), CancellationToken.None);

        _clock.Advance(TimeSpan.FromSeconds(59));
        await resolver.ResolveAsync(Event("ctx"), CancellationToken.None);

        Assert.Equal(1, _inner.Calls);
    }

    // ------------------------------------------------------------------
    // Correctness of the returned set
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_user_named_on_this_event_is_included_even_on_a_cache_hit()
    {
        // A newly-authorized user must not be withheld until the cache expires.
        _inner.Result = ["U_ALICE"];
        var resolver = Resolver();
        await resolver.ResolveAsync(Event("ctx"), CancellationToken.None);

        var resolved = await resolver.ResolveAsync(Event("ctx", "U_NEW"), CancellationToken.None);

        Assert.Contains("U_ALICE", resolved);
        Assert.Contains("U_NEW", resolved);
    }

    [Fact]
    public async Task A_cache_hit_does_not_duplicate_recipients()
    {
        _inner.Result = ["U_ALICE"];
        var resolver = Resolver();
        await resolver.ResolveAsync(Event("ctx"), CancellationToken.None);

        var resolved = await resolver.ResolveAsync(Event("ctx", "U_ALICE"), CancellationToken.None);

        Assert.Equal(["U_ALICE"], resolved);
    }

    [Fact]
    public async Task A_degraded_lookup_is_not_pinned_beyond_the_ttl()
    {
        // If Slack was briefly unavailable, recovery must not wait on a long-lived cache.
        _inner.Result = [];
        var resolver = Resolver(TimeSpan.FromMinutes(1));
        await resolver.ResolveAsync(Event("ctx", "U_ALICE"), CancellationToken.None);

        _clock.Advance(TimeSpan.FromMinutes(2));
        _inner.Result = ["U_ALICE", "U_BOB"];
        var resolved = await resolver.ResolveAsync(Event("ctx"), CancellationToken.None);

        Assert.Contains("U_BOB", resolved);
    }

    // ------------------------------------------------------------------
    // Housekeeping
    // ------------------------------------------------------------------

    [Fact]
    public async Task Pruning_drops_only_expired_entries()
    {
        var resolver = Resolver(TimeSpan.FromMinutes(1));
        await resolver.ResolveAsync(Event("ctx-old"), CancellationToken.None);
        _clock.Advance(TimeSpan.FromMinutes(2));
        await resolver.ResolveAsync(Event("ctx-new"), CancellationToken.None);

        var removed = resolver.Prune();

        Assert.Equal(1, removed);
        Assert.Equal(1, resolver.Count);
    }

    private sealed class CountingResolver : IEventAuthorizationResolver
    {
        public int Calls { get; private set; }
        public IReadOnlyList<string> Result { get; set; } = ["U_ALICE"];

        public Task<IReadOnlyList<string>> ResolveAsync(SlackRelayEvent relayEvent, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(Result);
        }
    }
}

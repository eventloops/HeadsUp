using HeadsUp.Relay.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SlackNet;
using SlackNet.WebApi;
using Xunit;

namespace HeadsUp.Relay.Tests;

/// <summary>
/// Enrichment sits on the delivery path of every event, so its failure modes are measured in
/// API calls per message and in bytes retained for the life of the process.
/// </summary>
public class EventEnricherTests
{
    private readonly ISlackApiClient _api = Substitute.For<ISlackApiClient>();
    private readonly FakeTimeProvider _clock = new();

    private EventEnricher Enricher() =>
        new(_api, NullLogger<EventEnricher>.Instance, _clock);

    private const string Message =
        """{"type":"message","user":"U_SARAH","channel":"C1","text":"hi"}""";

    private void SetupUser(string id, string name) =>
        _api.Users.Info(id, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new User { Id = id, Name = name, Profile = new UserProfile { DisplayName = name } });

    private void SetupChannel(string id, string name) =>
        _api.Conversations.Info(id, Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new Conversation { Id = id, Name = name });

    [Fact]
    public async Task Names_are_attached_without_disturbing_the_event()
    {
        SetupUser("U_SARAH", "Sarah Whitfield");
        SetupChannel("C1", "project-atlas");

        var enriched = await Enricher().EnrichAsync(Message, CancellationToken.None);

        Assert.Contains("Sarah Whitfield", enriched);
        Assert.Contains("project-atlas", enriched);
        // The Slack event itself must survive intact — the desktop still classifies from it.
        Assert.Contains("\"text\":\"hi\"", enriched);
    }

    [Fact]
    public async Task Repeat_events_reuse_the_cache_rather_than_re_asking_slack()
    {
        SetupUser("U_SARAH", "Sarah Whitfield");
        SetupChannel("C1", "project-atlas");
        var enricher = Enricher();

        for (var i = 0; i < 10; i++)
            await enricher.EnrichAsync(Message, CancellationToken.None);

        await _api.Users.Received(1).Info("U_SARAH", Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_failing_lookup_is_not_retried_on_every_message()
    {
        // A deactivated account or a rate limit otherwise costs one API call per message from
        // that source — exactly when calls are least affordable.
        _api.Users.Info("U_SARAH", Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new SlackException(new ErrorResponse { Error = "user_not_found" }));
        var enricher = Enricher();

        for (var i = 0; i < 5; i++)
            await enricher.EnrichAsync(Message, CancellationToken.None);

        await _api.Users.Received(1).Info("U_SARAH", Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_cached_failure_expires_so_a_rate_limit_recovers_on_its_own()
    {
        _api.Users.Info("U_SARAH", Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new SlackException(new ErrorResponse { Error = "ratelimited" }));
        var enricher = Enricher();
        await enricher.EnrichAsync(Message, CancellationToken.None);

        _clock.Advance(TimeSpan.FromMinutes(5));
        SetupUser("U_SARAH", "Sarah Whitfield");
        var enriched = await enricher.EnrichAsync(Message, CancellationToken.None);

        Assert.Contains("Sarah Whitfield", enriched);
    }

    [Fact]
    public async Task A_failed_lookup_still_forwards_the_event()
    {
        _api.Users.Info(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new SlackException(new ErrorResponse { Error = "user_not_found" }));

        var enriched = await Enricher().EnrichAsync(Message, CancellationToken.None);

        // An alert naming an id still interrupts the right person at the right moment.
        Assert.Contains("U_SARAH", enriched);
        Assert.Contains("\"text\":\"hi\"", enriched);
    }

    [Fact]
    public async Task Expired_entries_are_pruned_so_the_cache_is_not_a_slow_leak()
    {
        SetupUser("U_SARAH", "Sarah Whitfield");
        SetupChannel("C1", "project-atlas");
        var enricher = Enricher();
        await enricher.EnrichAsync(Message, CancellationToken.None);

        Assert.Equal(2, enricher.CachedCount);

        _clock.Advance(TimeSpan.FromHours(2));
        var removed = enricher.Prune();

        Assert.Equal(2, removed);
        Assert.Equal(0, enricher.CachedCount);
    }

    [Fact]
    public async Task Live_entries_survive_pruning()
    {
        SetupUser("U_SARAH", "Sarah Whitfield");
        SetupChannel("C1", "project-atlas");
        var enricher = Enricher();
        await enricher.EnrichAsync(Message, CancellationToken.None);

        _clock.Advance(TimeSpan.FromMinutes(5));

        Assert.Equal(0, enricher.Prune());
        Assert.Equal(2, enricher.CachedCount);
    }

    [Fact]
    public async Task A_huddle_is_enriched_from_its_initiator_not_from_slackbot()
    {
        // user is USLACKBOT on every huddle event; enriching from it would attribute every
        // huddle to Slackbot in a second place.
        SetupUser("U_SARAH", "Sarah Whitfield");
        var huddle = """
            {"type":"message","user":"USLACKBOT","channel":"D1","subtype":"huddle_thread",
             "room":{"created_by":"U_SARAH"}}
            """;

        var enriched = await Enricher().EnrichAsync(huddle, CancellationToken.None);

        Assert.Contains("Sarah Whitfield", enriched);
    }

    [Fact]
    public async Task Without_a_bot_token_events_pass_through_untouched()
    {
        var enricher = new EventEnricher(null, NullLogger<EventEnricher>.Instance, _clock);

        Assert.Equal(Message, await enricher.EnrichAsync(Message, CancellationToken.None));
    }
}

using HeadsUp.Relay.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SlackNet;
using Xunit;

namespace HeadsUp.Relay.Tests;

/// <summary>
/// Slack truncates an event's inline authorizations to one entry when many people can see it.
/// Trusting that verbatim would mean exactly one person gets alerted for any busy channel and
/// everyone else silently misses out — so these tests pin the expansion, and the fallback that
/// keeps a Slack outage from turning into lost alerts.
/// </summary>
public class SlackEventAuthorizationResolverTests
{
    private readonly ISlackApiClient _api = Substitute.For<ISlackApiClient>();

    private SlackEventAuthorizationResolver Resolver() =>
        new(_api, NullLogger<SlackEventAuthorizationResolver>.Instance);

    private void SetupAuthorizations(params (string UserId, bool IsBot)[] entries) =>
        _api.Get<SlackEventAuthorizationResolver.AuthorizationsListResponse>(
                "apps.event.authorizations.list", Arg.Any<Dictionary<string, object>>(), Arg.Any<CancellationToken>())
            .Returns(new SlackEventAuthorizationResolver.AuthorizationsListResponse
            {
                Authorizations = entries
                    .Select(e => new SlackEventAuthorizationResolver.Authorization { UserId = e.UserId, IsBot = e.IsBot })
                    .ToList(),
            });

    [Fact]
    public async Task Without_an_event_context_the_inline_list_is_used_as_is()
    {
        var relayEvent = new SlackRelayEvent("{}", ["U_ALICE"], EventContext: null);

        var resolved = await Resolver().ResolveAsync(relayEvent, CancellationToken.None);

        Assert.Equal(["U_ALICE"], resolved);
        await _api.DidNotReceiveWithAnyArgs()
            .Get<SlackEventAuthorizationResolver.AuthorizationsListResponse>(default!, default!, default);
    }

    [Fact]
    public async Task A_truncated_list_is_expanded_to_every_authorized_user()
    {
        // Slack sent only U_ALICE inline, but three people can actually see the event.
        SetupAuthorizations(("U_ALICE", false), ("U_BOB", false), ("U_CAROL", false));
        var relayEvent = new SlackRelayEvent("{}", ["U_ALICE"], "ctx-1");

        var resolved = await Resolver().ResolveAsync(relayEvent, CancellationToken.None);

        Assert.Equal(["U_ALICE", "U_BOB", "U_CAROL"], resolved.OrderBy(x => x));
    }

    [Fact]
    public async Task Bot_authorizations_are_dropped()
    {
        // Bots have no desktop and nobody to notify.
        SetupAuthorizations(("U_ALICE", false), ("B_BOT", true));
        var relayEvent = new SlackRelayEvent("{}", [], "ctx-1");

        var resolved = await Resolver().ResolveAsync(relayEvent, CancellationToken.None);

        Assert.Equal(["U_ALICE"], resolved);
    }

    [Fact]
    public async Task The_inline_user_is_kept_even_if_the_lookup_omits_them()
    {
        SetupAuthorizations(("U_BOB", false));
        var relayEvent = new SlackRelayEvent("{}", ["U_ALICE"], "ctx-1");

        var resolved = await Resolver().ResolveAsync(relayEvent, CancellationToken.None);

        // Union, not replace: Slack already told us U_ALICE can see this.
        Assert.Contains("U_ALICE", resolved);
        Assert.Contains("U_BOB", resolved);
    }

    [Fact]
    public async Task A_failed_lookup_falls_back_to_the_inline_list()
    {
        // Under-delivering is the safe failure: someone may miss an alert, but nobody
        // receives one that isn't theirs.
        _api.Get<SlackEventAuthorizationResolver.AuthorizationsListResponse>(
                Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("slack unavailable"));
        var relayEvent = new SlackRelayEvent("{}", ["U_ALICE"], "ctx-1");

        var resolved = await Resolver().ResolveAsync(relayEvent, CancellationToken.None);

        Assert.Equal(["U_ALICE"], resolved);
    }

    [Fact]
    public async Task An_empty_lookup_result_falls_back_rather_than_dropping_the_event()
    {
        SetupAuthorizations();
        var relayEvent = new SlackRelayEvent("{}", ["U_ALICE"], "ctx-1");

        var resolved = await Resolver().ResolveAsync(relayEvent, CancellationToken.None);

        Assert.Equal(["U_ALICE"], resolved);
    }

    [Fact]
    public async Task The_event_context_is_passed_to_slack()
    {
        SetupAuthorizations(("U_ALICE", false));
        var relayEvent = new SlackRelayEvent("{}", [], "ctx-abc");

        await Resolver().ResolveAsync(relayEvent, CancellationToken.None);

        await _api.Received().Get<SlackEventAuthorizationResolver.AuthorizationsListResponse>(
            "apps.event.authorizations.list",
            Arg.Is<Dictionary<string, object>>(a => (string)a["event_context"] == "ctx-abc"),
            Arg.Any<CancellationToken>());
    }
}

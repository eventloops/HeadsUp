using HeadsUp.Models;
using HeadsUp.Services;
using Xunit;

namespace HeadsUp.Tests;

public class AlertLinksTests
{
    private static Alert AlertWith(string link, string sourceId = "slack") => new(
        InitiatorUserId: "U1",
        InitiatorDisplayName: "Sarah",
        InitiatorAvatarUrl: null,
        ChannelId: "C1",
        ChannelName: "general",
        IsDirectMessage: false,
        Link: link,
        IsPriorityUser: false,
        OccurredAt: DateTimeOffset.Now,
        SourceId: sourceId,
        SourceName: sourceId);

    // ------------------------------------------------------------------
    // SlackAlertLinkResolver (moved out of AlertLauncher)
    // ------------------------------------------------------------------

    [Fact]
    public void Slack_resolver_prefers_the_huddle_app_scheme()
    {
        var resolver = new SlackAlertLinkResolver();
        Assert.Equal(
            new[] { "slack://huddle?team=T123&id=C456" },
            resolver.Targets(AlertWith("https://app.slack.com/huddle/T123/C456")).ToList());
    }

    [Fact]
    public void Slack_resolver_prefers_the_channel_app_scheme()
    {
        var resolver = new SlackAlertLinkResolver();
        Assert.Equal(
            new[] { "slack://channel?team=T123&id=C456" },
            resolver.Targets(AlertWith("https://app.slack.com/client/T123/C456")).ToList());
    }

    [Fact]
    public void Slack_resolver_yields_nothing_for_an_unrecognized_link()
    {
        var resolver = new SlackAlertLinkResolver();
        Assert.Empty(resolver.Targets(AlertWith("slack://channel?id=C456")));
    }

    // ------------------------------------------------------------------
    // AlertLinks (dispatch by source + raw-link fallback)
    // ------------------------------------------------------------------

    [Fact]
    public void Dispatches_to_the_matching_resolver_then_appends_the_raw_link()
    {
        var links = new AlertLinks(new[] { new SlackAlertLinkResolver() });

        Assert.Equal(
            new[] { "slack://huddle?team=T1&id=C1", "https://app.slack.com/huddle/T1/C1" },
            links.Targets(AlertWith("https://app.slack.com/huddle/T1/C1")));
    }

    [Fact]
    public void Unknown_source_falls_back_to_just_the_raw_link()
    {
        var links = new AlertLinks(new[] { new SlackAlertLinkResolver() });

        Assert.Equal(
            new[] { "https://teams.example/chat/1" },
            links.Targets(AlertWith("https://teams.example/chat/1", sourceId: "teams")));
    }

    [Fact]
    public void Duplicate_resolvers_for_a_source_do_not_throw()
    {
        // Two Slack routes could each register a resolver; the first wins, no collision.
        var links = new AlertLinks(new[] { new SlackAlertLinkResolver(), new SlackAlertLinkResolver() });

        Assert.Equal(
            new[] { "slack://channel?team=T1&id=C1", "https://app.slack.com/client/T1/C1" },
            links.Targets(AlertWith("https://app.slack.com/client/T1/C1")));
    }
}

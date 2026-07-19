using HeadsUp.Configuration;
using HeadsUp.Models;
using HeadsUp.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HeadsUp.Tests;

/// <summary>
/// Classification of relayed events. The payloads are the ones captured from live Socket Mode
/// traffic, so what's tested is the shape Slack actually sends rather than the shape the docs
/// imply — which matters most for huddles, where the obvious field is the wrong one.
/// </summary>
public class SlackRelaySourceTests
{
    private const string SelfId = "U_SELF";

    private readonly AppStatus _status = new();
    private DetectionOptions _detection = new();
    private PriorityOptions _priority = new();
    private AlertOptions _alerts = new()
    {
        Huddles = true,
        Mentions = true,
        DirectMessages = true,
        Keywords = true,
        KeywordList = ["deploy"],
    };

    private SlackRelaySource Source()
    {
        var source = new SlackRelaySource(
            new StubRelayCredentials(),
            new StaticMonitor<RelayClientOptions>(new RelayClientOptions()),
            new StaticMonitor<DetectionOptions>(_detection),
            new StaticMonitor<AlertOptions>(_alerts),
            new StaticMonitor<PriorityOptions>(_priority),
            _status,
            NullLogger<SlackRelaySource>.Instance)
        {
            _selfUserId = SelfId,
        };
        return source;
    }

    private Alert? Handle(string json)
    {
        var source = Source();
        Alert? raised = null;
        source.AlertRaised += (_, a) => raised = a;
        source.HandleEvent(json);
        return raised;
    }

    // Captured shape: user is USLACKBOT, the initiator is room.created_by.
    private const string DmHuddle = """
        {"type":"message","user":"USLACKBOT","channel":"D1","channel_type":"im",
         "subtype":"huddle_thread",
         "room":{"created_by":"U_SARAH","is_dm_call":true,"has_ended":false,
                 "huddle_link":"https://app.slack.com/huddle/T1/D1"}}
        """;

    // ------------------------------------------------------------------
    // Huddles
    // ------------------------------------------------------------------

    [Fact]
    public void A_huddle_is_attributed_to_whoever_started_it()
    {
        var alert = Handle(DmHuddle);

        // Not USLACKBOT, which is what the event's own user field says.
        Assert.Equal(new UserId("U_SARAH"), alert!.InitiatorUserId);
    }

    [Fact]
    public void A_huddle_uses_slacks_own_deep_link()
    {
        Assert.Equal("https://app.slack.com/huddle/T1/D1", Handle(DmHuddle)!.Link);
    }

    [Fact]
    public void A_huddle_from_a_priority_person_is_marked_priority()
    {
        // This only works because attribution is right; matching on USLACKBOT would never hit.
        _priority = new PriorityOptions { Users = [new PriorityUser { Source = "slack", Id = "U_SARAH" }] };

        Assert.True(Handle(DmHuddle)!.IsPriorityUser);
    }

    [Fact]
    public void Your_own_huddle_does_not_alert()
    {
        var json = DmHuddle.Replace("U_SARAH", SelfId);

        Assert.Null(Handle(json));
    }

    [Fact]
    public void Your_own_huddle_alerts_when_the_debug_toggle_is_on()
    {
        _detection = new DetectionOptions { NotifyOwnHuddles = true };
        var json = DmHuddle.Replace("U_SARAH", SelfId);

        Assert.NotNull(Handle(json));
    }

    [Fact]
    public void An_ended_huddle_does_not_alert()
    {
        var json = """
            {"type":"message","user":"USLACKBOT","channel":"D1","subtype":"huddle_thread",
             "room":{"created_by":"U_SARAH","has_ended":true,"is_dm_call":true}}
            """;

        Assert.Null(Handle(json));
    }

    [Fact]
    public void The_message_changed_wrapper_from_a_huddle_ending_does_not_alert()
    {
        // Slack wraps the ended huddle; the nested message still says huddle_thread.
        var json = """
            {"type":"message","subtype":"message_changed","channel":"D1","hidden":true,
             "message":{"user":"USLACKBOT","subtype":"huddle_thread",
                        "room":{"created_by":"U_SARAH","has_ended":true}}}
            """;

        Assert.Null(Handle(json));
    }

    [Fact]
    public void Huddles_can_be_switched_off()
    {
        _alerts = new AlertOptions { Huddles = false };

        Assert.Null(Handle(DmHuddle));
    }

    [Fact]
    public void A_huddle_in_a_muted_channel_does_not_alert()
    {
        _alerts = new AlertOptions
        {
            Huddles = true,
            MutedChannels = { new MutedChannel { Source = "slack", Id = "D1", Name = "dm" } },
        };

        Assert.Null(Handle(DmHuddle));
    }

    [Fact]
    public void A_repeat_huddle_in_the_same_channel_is_suppressed()
    {
        var source = Source();
        var count = 0;
        source.AlertRaised += (_, _) => count++;

        source.HandleEvent(DmHuddle);
        source.HandleEvent(DmHuddle);

        Assert.Equal(1, count);
    }

    // ------------------------------------------------------------------
    // Messages
    // ------------------------------------------------------------------

    [Fact]
    public void A_direct_message_alerts()
    {
        var json = """{"type":"message","user":"U_SARAH","channel":"D1","text":"you around?"}""";

        var alert = Handle(json);

        Assert.Equal(AlertKind.DirectMessage, alert!.Kind);
        Assert.True(alert.IsDirectMessage);
    }

    [Fact]
    public void A_mention_alerts()
    {
        var json = $$"""{"type":"message","user":"U_SARAH","channel":"C1","text":"<@{{SelfId}}> look at this"}""";

        Assert.Equal(AlertKind.Mention, Handle(json)!.Kind);
    }

    [Fact]
    public void A_keyword_alerts()
    {
        var json = """{"type":"message","user":"U_SARAH","channel":"C1","text":"starting the deploy now"}""";

        Assert.Equal(AlertKind.Keyword, Handle(json)!.Kind);
    }

    [Fact]
    public void An_unremarkable_channel_message_does_not_alert()
    {
        var json = """{"type":"message","user":"U_SARAH","channel":"C1","text":"morning all"}""";

        Assert.Null(Handle(json));
    }

    [Fact]
    public void Your_own_message_does_not_alert()
    {
        var json = $$"""{"type":"message","user":"{{SelfId}}","channel":"D1","text":"hello"}""";

        Assert.Null(Handle(json));
    }

    [Fact]
    public void An_edited_message_does_not_alert()
    {
        var json = """
            {"type":"message","subtype":"message_changed","channel":"D1",
             "message":{"user":"U_SARAH","text":"edited"}}
            """;

        Assert.Null(Handle(json));
    }

    [Fact]
    public void A_non_message_event_is_ignored()
    {
        Assert.Null(Handle("""{"type":"reaction_added","user":"U_SARAH"}"""));
    }

    private sealed class StubRelayCredentials : IRelayCredentialProvider
    {
        public string SourceDescription => "test";
        public Task<RelaySession?> GetSessionAsync(bool forceRefresh, CancellationToken ct) =>
            Task.FromResult<RelaySession?>(new RelaySession("token", SelfId));
    }
}

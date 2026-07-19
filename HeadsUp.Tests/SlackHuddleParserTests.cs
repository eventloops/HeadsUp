using System.Text.Json;
using HeadsUp.Services;
using Xunit;

namespace HeadsUp.Tests;

/// <summary>
/// Built from real Socket Mode payloads captured against a live workspace, with credentials
/// removed. The shapes here are observed, not inferred from documentation — which matters,
/// because the field that identifies the huddle initiator is not the obvious one.
/// </summary>
public class SlackHuddleParserTests
{
    /// <summary>A 1:1 huddle started in a DM. Note user is USLACKBOT, not the initiator.</summary>
    private const string DmHuddle = """
        {
          "type": "message",
          "user": "USLACKBOT",
          "channel": "D0BJDDQSSTE",
          "channel_type": "im",
          "subtype": "huddle_thread",
          "text": "",
          "room": {
            "id": "R0BJ6FB79GD",
            "created_by": "U0BK42E575E",
            "participants": ["U0BK42E575E"],
            "is_dm_call": true,
            "has_ended": false,
            "call_family": "huddle",
            "huddle_link": "https://app.slack.com/huddle/T0BJ9NB5GCS/D0BJDDQSSTE"
          }
        }
        """;

    /// <summary>The same, started in a channel rather than a DM.</summary>
    private const string ChannelHuddle = """
        {
          "type": "message",
          "user": "USLACKBOT",
          "channel": "C0BK42DHNU8",
          "channel_type": "channel",
          "subtype": "huddle_thread",
          "room": {
            "id": "R0BJ6FBTTB7",
            "created_by": "U0BK42E575E",
            "is_dm_call": false,
            "has_ended": false,
            "huddle_link": "https://app.slack.com/huddle/T0BJ9NB5GCS/C0BK42DHNU8"
          }
        }
        """;

    private static HuddleFacts? Parse(string json) =>
        SlackHuddleParser.Parse(JsonDocument.Parse(json).RootElement);

    // ------------------------------------------------------------------
    // The trap
    // ------------------------------------------------------------------

    [Fact]
    public void The_initiator_is_read_from_room_created_by_not_user()
    {
        var facts = Parse(DmHuddle);

        // "user" is USLACKBOT on every huddle event. Reading it would attribute every huddle
        // to Slackbot, and silently break both priority-user matching and own-huddle suppression.
        Assert.Equal("U0BK42E575E", facts!.InitiatorUserId);
        Assert.NotEqual("USLACKBOT", facts.InitiatorUserId);
    }

    [Fact]
    public void A_dm_huddle_is_recognised_as_a_direct_message()
    {
        var facts = Parse(DmHuddle);

        Assert.True(facts!.IsDirectMessage);
        Assert.Equal("D0BJDDQSSTE", facts.ChannelId);
    }

    [Fact]
    public void A_channel_huddle_is_not_a_direct_message()
    {
        var facts = Parse(ChannelHuddle);

        Assert.False(facts!.IsDirectMessage);
        Assert.Equal("C0BK42DHNU8", facts.ChannelId);
        Assert.Equal("U0BK42E575E", facts.InitiatorUserId);
    }

    [Fact]
    public void Slacks_own_huddle_link_is_captured()
    {
        // Better than constructing a link ourselves — this is what Slack's client uses.
        Assert.Equal(
            "https://app.slack.com/huddle/T0BJ9NB5GCS/D0BJDDQSSTE",
            Parse(DmHuddle)!.HuddleLink);
    }

    // ------------------------------------------------------------------
    // Events that must not raise an alert
    // ------------------------------------------------------------------

    [Fact]
    public void An_ordinary_message_is_not_a_huddle()
    {
        var json = """{"type":"message","user":"U1","channel":"C1","text":"probe one"}""";

        Assert.Null(Parse(json));
    }

    [Fact]
    public void A_huddle_ending_is_reported_as_ended()
    {
        var json = """
            {"type":"message","user":"USLACKBOT","channel":"D1","subtype":"huddle_thread",
             "room":{"created_by":"U2","has_ended":true,"was_missed":true,"is_dm_call":true}}
            """;

        Assert.True(Parse(json)!.HasEnded);
    }

    [Fact]
    public void The_message_changed_wrapper_slack_sends_when_a_huddle_ends_is_not_a_start()
    {
        // Slack wraps the ended huddle in message_changed, and the *nested* message still says
        // huddle_thread. Alerting on the wrapper would fire when the huddle is already over.
        var json = """
            {"type":"message","subtype":"message_changed","channel":"D1","hidden":true,
             "message":{"user":"USLACKBOT","subtype":"huddle_thread",
                        "room":{"created_by":"U2","has_ended":true}}}
            """;

        Assert.Null(Parse(json));
    }

    [Fact]
    public void The_message_deleted_wrapper_is_not_a_start()
    {
        var json = """
            {"type":"message","subtype":"message_deleted","channel":"D1","hidden":true,
             "previous_message":{"user":"USLACKBOT","subtype":"huddle_thread",
                                 "room":{"created_by":"U2","has_ended":true}}}
            """;

        Assert.Null(Parse(json));
    }

    // ------------------------------------------------------------------
    // Robustness
    // ------------------------------------------------------------------

    [Fact]
    public void A_legacy_call_without_a_room_falls_back_to_user()
    {
        // sh_room_created predates huddles and may carry no room object.
        var json = """{"type":"message","user":"U9","channel":"C1","subtype":"sh_room_created"}""";

        var facts = Parse(json);

        Assert.Equal("U9", facts!.InitiatorUserId);
        Assert.Null(facts.HuddleLink);
    }

    [Fact]
    public void A_huddle_without_created_by_falls_back_rather_than_dropping_the_alert()
    {
        var json = """
            {"type":"message","user":"USLACKBOT","channel":"C1","subtype":"huddle_thread",
             "room":{"is_dm_call":false}}
            """;

        // Wrong attribution beats no alert: the huddle is still worth surfacing.
        Assert.Equal("USLACKBOT", Parse(json)!.InitiatorUserId);
    }
}

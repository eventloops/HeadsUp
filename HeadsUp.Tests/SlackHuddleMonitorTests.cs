using HeadsUp.Configuration;
using HeadsUp.Models;
using HeadsUp.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SlackNet;
using SlackNet.Events;
using SlackNet.WebApi;
using Xunit;

namespace HeadsUp.Tests;

public class SlackHuddleMonitorTests
{
    private const string SelfId = "USELF";
    private const string TeamId = "T123";

    private readonly ISlackSocketModeClient _socket = Substitute.For<ISlackSocketModeClient>();
    private readonly ISlackApiClient _api = Substitute.For<ISlackApiClient>();

    private SlackOptions _slackOptions = new() { AppToken = "xapp-1", UserToken = "xoxp-1" };
    private DetectionOptions _detectionOptions = new();
    private AlertOptions _alertOptions = new()
    {
        Huddles = true,
        Mentions = true,
        DirectMessages = true,
        Keywords = true,
        KeywordList = ["deploy"]
    };

    private SlackHuddleMonitor CreateMonitor() => new(
        _socket,
        _api,
        new StaticMonitor<SlackOptions>(_slackOptions),
        new StaticMonitor<DetectionOptions>(_detectionOptions),
        new StaticMonitor<AlertOptions>(_alertOptions),
        NullLogger<SlackHuddleMonitor>.Instance);

    /// <summary>Creates a monitor that has "connected": auth.test resolved self/team ids.</summary>
    private async Task<SlackHuddleMonitor> StartedMonitor()
    {
        _api.Auth.Test().Returns(new AuthTestResponse { UserId = SelfId, TeamId = TeamId });
        SetupConversation("C1", name: "general", isMember: true);
        SetupUser("U2", displayName: "Sarah Whitfield", image72: "https://img/72.png");

        var monitor = CreateMonitor();
        await monitor.StartAsync(CancellationToken.None);
        return monitor;
    }

    private void SetupConversation(string id, string? name = null, bool isMember = false,
        bool isIm = false, bool isMpim = false) =>
        _api.Conversations.Info(id, Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new Conversation { Id = id, Name = name, IsMember = isMember, IsIm = isIm, IsMpim = isMpim });

    private void SetupUser(string id, string displayName, string? image72 = null) =>
        _api.Users.Info(id, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new User { Id = id, Name = "handle", Profile = new UserProfile { DisplayName = displayName, Image72 = image72 } });

    private static MessageEvent Huddle(string user, string channel, string subtype = "huddle_thread") =>
        new() { Subtype = subtype, User = user, Channel = channel };

    private static MessageEvent Message(string user, string channel, string text) =>
        new() { User = user, Channel = channel, Text = text };

    // ------------------------------------------------------------------
    // Startup
    // ------------------------------------------------------------------

    [Fact]
    public async Task Missing_tokens_skip_the_connection_entirely()
    {
        _slackOptions = new SlackOptions(); // no tokens
        var monitor = CreateMonitor();

        await monitor.StartAsync(CancellationToken.None);

        await _socket.DidNotReceiveWithAnyArgs().Connect();
    }

    [Fact]
    public async Task Startup_connects_and_captures_identity()
    {
        await StartedMonitor();

        await _socket.ReceivedWithAnyArgs(1).Connect();
        await _api.Auth.Received(1).Test();
    }

    [Fact]
    public async Task Auth_failure_is_tolerated()
    {
        _api.Auth.Test().ThrowsAsync(new SlackException(new ErrorResponse { Error = "invalid_auth" }));
        var monitor = CreateMonitor();

        await monitor.StartAsync(CancellationToken.None); // must not throw
    }

    [Fact]
    public async Task Stop_disconnects_when_connected()
    {
        _socket.Connected.Returns(true);
        var monitor = CreateMonitor();

        await monitor.StopAsync(CancellationToken.None);

        _socket.Received(1).Disconnect();
    }

    [Fact]
    public async Task Stop_is_a_noop_when_not_connected()
    {
        _socket.Connected.Returns(false);
        var monitor = CreateMonitor();

        await monitor.StopAsync(CancellationToken.None);

        _socket.DidNotReceive().Disconnect();
    }

    // ------------------------------------------------------------------
    // Huddles
    // ------------------------------------------------------------------

    [Fact]
    public async Task Huddle_in_a_member_channel_raises_a_full_alert()
    {
        var monitor = await StartedMonitor();
        var waiter = new AlertWaiter(monitor);

        await monitor.Handle(Huddle("U2", "C1"));

        var alert = await waiter.Alert;
        Assert.Equal(AlertKind.Huddle, alert.Kind);
        Assert.Equal("U2", alert.InitiatorUserId);
        Assert.Equal("Sarah Whitfield", alert.InitiatorDisplayName);
        Assert.Equal("https://img/72.png", alert.InitiatorAvatarUrl);
        Assert.Equal("general", alert.ChannelName);
        Assert.False(alert.IsDirectMessage);
        Assert.False(alert.IsPriorityUser);
        Assert.Equal($"https://app.slack.com/huddle/{TeamId}/C1", alert.Link);
    }

    [Fact]
    public async Task Legacy_call_subtype_also_raises()
    {
        var monitor = await StartedMonitor();
        var waiter = new AlertWaiter(monitor);

        await monitor.Handle(Huddle("U2", "C1", subtype: "sh_room_created"));

        Assert.Equal(AlertKind.Huddle, (await waiter.Alert).Kind);
    }

    [Fact]
    public async Task Huddles_toggle_off_suppresses_the_alert()
    {
        _alertOptions.Huddles = false;
        var monitor = await StartedMonitor();
        var waiter = new AlertWaiter(monitor);

        await monitor.Handle(Huddle("U2", "C1"));

        Assert.False(await waiter.RaisedWithin(TimeSpan.FromMilliseconds(200)));
    }

    [Fact]
    public async Task Own_huddles_are_suppressed_by_default()
    {
        var monitor = await StartedMonitor();
        var waiter = new AlertWaiter(monitor);

        await monitor.Handle(Huddle(SelfId, "C1"));

        Assert.False(await waiter.RaisedWithin(TimeSpan.FromMilliseconds(200)));
    }

    [Fact]
    public async Task NotifyOwnHuddles_lets_your_own_huddle_through()
    {
        _detectionOptions = new DetectionOptions { NotifyOwnHuddles = true };
        SetupUser(SelfId, displayName: "Me");
        var monitor = await StartedMonitor();
        var waiter = new AlertWaiter(monitor);

        await monitor.Handle(Huddle(SelfId, "C1"));

        Assert.Equal(SelfId, (await waiter.Alert).InitiatorUserId);
    }

    [Fact]
    public async Task A_second_huddle_in_the_same_channel_is_deduped()
    {
        var monitor = await StartedMonitor();
        var alerts = new List<Alert>();
        monitor.AlertRaised += (_, a) => alerts.Add(a);

        await monitor.Handle(Huddle("U2", "C1"));
        await monitor.Handle(Huddle("U2", "C1"));

        await Task.Delay(200); // let the resolve/raise pipeline drain
        Assert.Single(alerts);
    }

    [Fact]
    public async Task Non_member_channels_are_gated_out()
    {
        var monitor = await StartedMonitor();
        SetupConversation("C9", name: "private-elsewhere", isMember: false);
        var waiter = new AlertWaiter(monitor);

        await monitor.Handle(Huddle("U2", "C9"));

        Assert.False(await waiter.RaisedWithin(TimeSpan.FromMilliseconds(200)));
    }

    [Fact]
    public async Task Dms_pass_the_membership_gate_by_definition()
    {
        var monitor = await StartedMonitor();
        SetupConversation("D5", isIm: true, isMember: false);
        var waiter = new AlertWaiter(monitor);

        await monitor.Handle(Huddle("U2", "D5"));

        var alert = await waiter.Alert;
        Assert.True(alert.IsDirectMessage);
        Assert.Equal("D5", alert.ChannelName); // DMs have no name; falls back to the id
    }

    [Fact]
    public async Task Channel_lookup_failure_fails_open()
    {
        var monitor = await StartedMonitor();
        _api.Conversations.Info("CX", Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new SlackException(new ErrorResponse { Error = "channel_not_found" }));
        var waiter = new AlertWaiter(monitor);

        await monitor.Handle(Huddle("U2", "CX"));

        Assert.Equal("CX", (await waiter.Alert).ChannelName);
    }

    [Fact]
    public async Task User_lookup_failure_falls_back_to_the_id()
    {
        var monitor = await StartedMonitor();
        _api.Users.Info("U404", Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new SlackException(new ErrorResponse { Error = "user_not_found" }));
        var waiter = new AlertWaiter(monitor);

        await monitor.Handle(Huddle("U404", "C1"));

        var alert = await waiter.Alert;
        Assert.Equal("U404", alert.InitiatorDisplayName);
        Assert.Null(alert.InitiatorAvatarUrl);
    }

    [Fact]
    public async Task Priority_users_are_flagged()
    {
        _slackOptions.PriorityUsers = ["u2"]; // case-insensitive match against U2
        var monitor = await StartedMonitor();
        var waiter = new AlertWaiter(monitor);

        await monitor.Handle(Huddle("U2", "C1"));

        Assert.True((await waiter.Alert).IsPriorityUser);
    }

    [Fact]
    public async Task Missing_user_or_channel_is_ignored()
    {
        var monitor = await StartedMonitor();
        var waiter = new AlertWaiter(monitor);

        await monitor.Handle(new MessageEvent { Subtype = "huddle_thread", Channel = "C1" }); // no user

        Assert.False(await waiter.RaisedWithin(TimeSpan.FromMilliseconds(200)));
    }

    // ------------------------------------------------------------------
    // Messages (mentions / DMs / keywords) - SlackApi-mode parity
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_mention_raises_with_preview()
    {
        var monitor = await StartedMonitor();
        var waiter = new AlertWaiter(monitor);

        await monitor.Handle(Message("U2", "C1", $"<@{SelfId}> can you review?"));

        var alert = await waiter.Alert;
        Assert.Equal(AlertKind.Mention, alert.Kind);
        Assert.Equal("can you review?", alert.Preview);
        Assert.Equal($"https://app.slack.com/client/{TeamId}/C1", alert.Link);
    }

    [Fact]
    public async Task A_dm_raises_when_enabled()
    {
        var monitor = await StartedMonitor();
        var waiter = new AlertWaiter(monitor);

        await monitor.Handle(Message("U2", "D5", "hi there"));

        var alert = await waiter.Alert;
        Assert.Equal(AlertKind.DirectMessage, alert.Kind);
        Assert.True(alert.IsDirectMessage);
    }

    [Fact]
    public async Task A_keyword_match_raises_when_enabled()
    {
        var monitor = await StartedMonitor();
        var waiter = new AlertWaiter(monitor);

        await monitor.Handle(Message("U2", "C1", "the deploy finished"));

        Assert.Equal(AlertKind.Keyword, (await waiter.Alert).Kind);
    }

    [Fact]
    public async Task Your_own_messages_never_alert()
    {
        var monitor = await StartedMonitor();
        var waiter = new AlertWaiter(monitor);

        await monitor.Handle(Message(SelfId, "D5", "note to self: deploy"));

        Assert.False(await waiter.RaisedWithin(TimeSpan.FromMilliseconds(200)));
    }

    [Fact]
    public async Task Non_huddle_subtypes_are_ignored()
    {
        var monitor = await StartedMonitor();
        var waiter = new AlertWaiter(monitor);

        await monitor.Handle(new MessageEvent
        {
            Subtype = "message_changed",
            User = "U2",
            Channel = "C1",
            Text = "deploy edited in"
        });

        Assert.False(await waiter.RaisedWithin(TimeSpan.FromMilliseconds(200)));
    }

    [Fact]
    public async Task Message_channel_lookup_failure_still_alerts()
    {
        var monitor = await StartedMonitor();
        _api.Conversations.Info("CX", Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new SlackException(new ErrorResponse { Error = "nope" }));
        var waiter = new AlertWaiter(monitor);

        await monitor.Handle(Message("U2", "CX", "deploy it"));

        Assert.Equal("CX", (await waiter.Alert).ChannelName);
    }

    [Fact]
    public async Task Messages_missing_user_or_channel_are_ignored()
    {
        var monitor = await StartedMonitor();
        var waiter = new AlertWaiter(monitor);

        await monitor.Handle(new MessageEvent { Text = "deploy" }); // no user, no channel

        Assert.False(await waiter.RaisedWithin(TimeSpan.FromMilliseconds(200)));
    }

    [Fact]
    public async Task Unmatched_plain_messages_do_not_alert()
    {
        var monitor = await StartedMonitor();
        var waiter = new AlertWaiter(monitor);

        await monitor.Handle(Message("U2", "C1", "nothing to see")); // no mention/DM/keyword

        Assert.False(await waiter.RaisedWithin(TimeSpan.FromMilliseconds(200)));
    }

    [Fact]
    public async Task A_throwing_subscriber_is_contained_on_huddles()
    {
        var monitor = await StartedMonitor();
        var seen = false;
        monitor.AlertRaised += (_, _) =>
        {
            seen = true;
            throw new InvalidOperationException("subscriber boom");
        };

        await monitor.Handle(Huddle("U2", "C1")); // must not throw

        Assert.True(seen);
    }

    [Fact]
    public async Task A_throwing_subscriber_is_contained_on_messages()
    {
        var monitor = await StartedMonitor();
        var seen = false;
        monitor.AlertRaised += (_, _) =>
        {
            seen = true;
            throw new InvalidOperationException("subscriber boom");
        };

        await monitor.Handle(Message("U2", "D5", "hello")); // must not throw

        Assert.True(seen);
    }
}

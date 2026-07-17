using HeadsUp.Configuration;
using HeadsUp.Models;
using HeadsUp.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HeadsUp.Tests;

/// <summary>
/// Frame-level tests: feed raw WebSocket JSON into the (internal) frame handler with
/// no live socket. The API client is absent, so user/channel resolution falls back to
/// raw ids - which conveniently also exercises those failure paths.
/// </summary>
public class SlackWebHuddleSourceFrameTests
{
    private const string SelfId = "USELF";

    private AlertOptions _alerts = new()
    {
        Huddles = true,
        Mentions = true,
        DirectMessages = true,
        Keywords = true,
        KeywordList = ["deploy"]
    };
    private DetectionOptions _detection = new();

    private SlackWebHuddleSource CreateSource(bool authenticated = true)
    {
        var source = new SlackWebHuddleSource(
            new StaticCredentialProvider(null),
            new StaticMonitor<DetectionOptions>(_detection),
            new StaticMonitor<AlertOptions>(_alerts),
            new StaticMonitor<PriorityOptions>(new() { Users = { new PriorityUser { Source = "slack", Id = "UPRIO" } } }),
            new SnoozeService(),
            new AppStatus(),
            NullLogger<SlackWebHuddleSource>.Instance);

        if (authenticated)
        {
            source._selfUserId = SelfId;
            source._teamId = "T123";
        }
        return source;
    }

    // ------------------------------------------------------------------
    // Frame plumbing
    // ------------------------------------------------------------------

    [Fact]
    public async Task Malformed_and_irrelevant_frames_are_ignored()
    {
        var source = CreateSource();
        var waiter = new AlertWaiter(source);

        source.HandleFrame("{not json");
        source.HandleFrame("""{ "type": "presence_change", "user": "U2" }""");
        source.HandleFrame("""{ "type": "message", "subtype": "message_changed", "channel": "C1", "user": "U2", "text": "deploy" }""");
        source.HandleFrame("""{ "type": "message", "channel": "C1", "text": "deploy" }"""); // no user

        Assert.False(await waiter.RaisedWithin(TimeSpan.FromMilliseconds(200)));
    }

    // ------------------------------------------------------------------
    // Huddles
    // ------------------------------------------------------------------

    [Fact]
    public async Task Huddle_initiator_comes_from_room_created_by()
    {
        var source = CreateSource();
        var waiter = new AlertWaiter(source);

        // Real huddle_thread messages are authored by USLACKBOT; the human is in room.created_by.
        source.HandleFrame("""
            { "type": "message", "subtype": "huddle_thread", "channel": "C1",
              "user": "USLACKBOT", "room": { "created_by": "U2" } }
            """);

        var alert = await waiter.Alert;
        Assert.Equal("U2", alert.InitiatorUserId);
        Assert.Equal(AlertKind.Huddle, alert.Kind);
        Assert.Equal("U2", alert.InitiatorDisplayName); // no API client -> id fallback
        Assert.Equal("https://app.slack.com/huddle/T123/C1", alert.Link);
    }

    [Fact]
    public async Task Huddle_falls_back_to_the_top_level_user()
    {
        var source = CreateSource();
        var waiter = new AlertWaiter(source);

        source.HandleFrame("""
            { "type": "message", "subtype": "sh_room_created", "channel": "C1", "user": "U7" }
            """);

        Assert.Equal("U7", (await waiter.Alert).InitiatorUserId);
    }

    [Fact]
    public async Task Huddles_toggle_off_suppresses()
    {
        _alerts.Huddles = false;
        var source = CreateSource();
        var waiter = new AlertWaiter(source);

        source.HandleFrame("""
            { "type": "message", "subtype": "huddle_thread", "channel": "C1", "room": { "created_by": "U2" } }
            """);

        Assert.False(await waiter.RaisedWithin(TimeSpan.FromMilliseconds(200)));
    }

    [Fact]
    public async Task A_muted_channel_suppresses_its_huddles()
    {
        _alerts.MutedChannels.Add(new HeadsUp.Configuration.MutedChannel { Id = "C1", Name = "general" });
        var source = CreateSource();
        var waiter = new AlertWaiter(source);

        source.HandleFrame("""
            { "type": "message", "subtype": "huddle_thread", "channel": "C1", "room": { "created_by": "U2" } }
            """);

        Assert.False(await waiter.RaisedWithin(TimeSpan.FromMilliseconds(200)));
    }

    [Fact]
    public async Task Own_huddles_are_suppressed_unless_debug_toggle_set()
    {
        var source = CreateSource();
        var waiter = new AlertWaiter(source);

        source.HandleFrame($$"""
            { "type": "message", "subtype": "huddle_thread", "channel": "C1", "room": { "created_by": "{{SelfId}}" } }
            """);

        Assert.False(await waiter.RaisedWithin(TimeSpan.FromMilliseconds(200)));
    }

    [Fact]
    public async Task NotifyOwnHuddles_lets_your_own_through()
    {
        _detection = new DetectionOptions { NotifyOwnHuddles = true };
        var source = CreateSource();
        var waiter = new AlertWaiter(source);

        source.HandleFrame($$"""
            { "type": "message", "subtype": "huddle_thread", "channel": "C1", "room": { "created_by": "{{SelfId}}" } }
            """);

        Assert.Equal(SelfId, (await waiter.Alert).InitiatorUserId);
    }

    [Fact]
    public async Task Same_channel_huddles_are_deduped_but_other_channels_still_fire()
    {
        var source = CreateSource();
        var alerts = new List<Alert>();
        source.AlertRaised += (_, a) => alerts.Add(a);

        source.HandleFrame("""{ "type": "message", "subtype": "huddle_thread", "channel": "C1", "room": { "created_by": "U2" } }""");
        source.HandleFrame("""{ "type": "message", "subtype": "huddle_thread", "channel": "C1", "room": { "created_by": "U3" } }""");
        source.HandleFrame("""{ "type": "message", "subtype": "huddle_thread", "channel": "C2", "room": { "created_by": "U2" } }""");

        await Task.Delay(200);
        Assert.Equal(2, alerts.Count);
        Assert.Equal(new[] { "C1", "C2" }, alerts.Select(a => a.ChannelId));
    }

    [Fact]
    public async Task Priority_users_are_flagged_case_insensitively()
    {
        var source = CreateSource();
        var waiter = new AlertWaiter(source);

        source.HandleFrame("""{ "type": "message", "subtype": "huddle_thread", "channel": "C1", "room": { "created_by": "uprio" } }""");

        Assert.True((await waiter.Alert).IsPriorityUser);
    }

    [Fact]
    public async Task Without_a_team_id_the_link_falls_back_to_the_channel_scheme()
    {
        var source = CreateSource(authenticated: false);
        var waiter = new AlertWaiter(source);

        source.HandleFrame("""{ "type": "message", "subtype": "huddle_thread", "channel": "C1", "room": { "created_by": "U2" } }""");

        Assert.Equal("slack://channel?id=C1", (await waiter.Alert).Link);
    }

    // ------------------------------------------------------------------
    // Messages
    // ------------------------------------------------------------------

    [Fact]
    public async Task Mention_frame_raises_with_stripped_preview()
    {
        var source = CreateSource();
        var waiter = new AlertWaiter(source);

        source.HandleFrame($$"""
            { "type": "message", "channel": "C1", "user": "U2", "text": "<@{{SelfId}}> deploy review?" }
            """);

        var alert = await waiter.Alert;
        Assert.Equal(AlertKind.Mention, alert.Kind);
        Assert.Equal("deploy review?", alert.Preview);
        Assert.Equal("https://app.slack.com/client/T123/C1", alert.Link);
    }

    [Fact]
    public async Task Dm_frame_raises_as_direct_message()
    {
        var source = CreateSource();
        var waiter = new AlertWaiter(source);

        source.HandleFrame("""{ "type": "message", "channel": "D9", "user": "U2", "text": "hello" }""");

        var alert = await waiter.Alert;
        Assert.Equal(AlertKind.DirectMessage, alert.Kind);
        Assert.True(alert.IsDirectMessage);
        Assert.Equal("D9", alert.ChannelName); // DMs skip channel resolution
    }

    [Fact]
    public async Task Keyword_frame_raises_in_channels()
    {
        var source = CreateSource();
        var waiter = new AlertWaiter(source);

        source.HandleFrame("""{ "type": "message", "channel": "C1", "user": "U2", "text": "the deploy is done" }""");

        Assert.Equal(AlertKind.Keyword, (await waiter.Alert).Kind);
    }

    [Fact]
    public async Task Own_messages_never_alert()
    {
        var source = CreateSource();
        var waiter = new AlertWaiter(source);

        source.HandleFrame($$"""{ "type": "message", "channel": "D9", "user": "{{SelfId}}", "text": "deploy note" }""");

        Assert.False(await waiter.RaisedWithin(TimeSpan.FromMilliseconds(200)));
    }

    [Fact]
    public async Task Unmatched_plain_messages_do_not_alert()
    {
        var source = CreateSource();
        var waiter = new AlertWaiter(source);

        // Channel message: not a DM, no mention, no keyword hit.
        source.HandleFrame("""{ "type": "message", "channel": "C1", "user": "U2", "text": "nothing to see" }""");

        Assert.False(await waiter.RaisedWithin(TimeSpan.FromMilliseconds(200)));
    }

    [Fact]
    public async Task Huddle_without_any_user_is_ignored()
    {
        var source = CreateSource();
        var waiter = new AlertWaiter(source);

        source.HandleFrame("""{ "type": "message", "subtype": "huddle_thread", "channel": "C1" }""");

        Assert.False(await waiter.RaisedWithin(TimeSpan.FromMilliseconds(200)));
    }

    [Fact]
    public async Task A_throwing_subscriber_is_contained_on_huddles()
    {
        var source = CreateSource();
        var seen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.AlertRaised += (_, _) =>
        {
            seen.TrySetResult();
            throw new InvalidOperationException("subscriber boom");
        };

        source.HandleFrame("""{ "type": "message", "subtype": "huddle_thread", "channel": "C1", "room": { "created_by": "U2" } }""");

        await seen.Task.WaitAsync(TimeSpan.FromSeconds(5)); // pipeline swallowed the throw
    }

    [Fact]
    public async Task A_throwing_subscriber_is_contained_on_messages()
    {
        var source = CreateSource();
        var seen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.AlertRaised += (_, _) =>
        {
            seen.TrySetResult();
            throw new InvalidOperationException("subscriber boom");
        };

        source.HandleFrame("""{ "type": "message", "channel": "D9", "user": "U2", "text": "hello" }""");

        await seen.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }
}

/// <summary>
/// End-to-end tests against <see cref="FakeSlackServer"/>: the real background service
/// authenticates over HTTP, opens the real WebSocket, and reacts to pushed frames.
/// Also the only place SlackWebClient's HTTP path runs under test.
/// </summary>
public class SlackWebHuddleSourceIntegrationTests
{
    private static SlackWebHuddleSource CreateSource(
        FakeSlackServer server, out AppStatus status, out StaticCredentialProvider creds,
        SnoozeService? snooze = null)
    {
        creds = new StaticCredentialProvider(
            new SlackCredentials("xoxc-test", "d=xoxd-test", server.BaseUrl));
        status = new AppStatus();
        return new SlackWebHuddleSource(
            creds,
            new StaticMonitor<DetectionOptions>(new()),
            new StaticMonitor<AlertOptions>(new()),
            new StaticMonitor<PriorityOptions>(new()),
            snooze ?? new SnoozeService(),
            status,
            NullLogger<SlackWebHuddleSource>.Instance);
    }

    private static async Task WaitForStatusAsync(AppStatus status, Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline,
                $"Timed out waiting for status. Last: '{status.StatusMessage}'");
            await Task.Delay(25);
        }
    }

    [Fact]
    public async Task Connects_authenticates_and_raises_a_pushed_huddle()
    {
        using var server = new FakeSlackServer();
        var source = CreateSource(server, out var status, out _);
        source._pingInterval = TimeSpan.FromMilliseconds(50); // exercise the keep-alive quickly
        var waiter = new AlertWaiter(source);

        await source.StartAsync(CancellationToken.None);
        try
        {
            await WaitForStatusAsync(status, () => status.IsConnected);

            Assert.Equal("Watching for huddles", status.StatusMessage);
            Assert.Equal("cameron", status.Account);
            Assert.Equal("Procode", status.Team);
            Assert.Equal("test credentials", status.CredentialSource);

            // The keep-alive loop pings over the socket.
            Assert.Contains("\"type\":\"ping\"", await server.FirstClientFrame);

            await server.SendTextFrameAsync("""
                { "type": "message", "subtype": "huddle_thread", "channel": "C1",
                  "user": "USLACKBOT", "room": { "created_by": "U2" } }
                """);

            var alert = await waiter.Alert;
            Assert.Equal("U2", alert.InitiatorUserId);
            Assert.Equal("Sarah Whitfield", alert.InitiatorDisplayName);      // via /api/users.info
            Assert.Equal("https://img/72.png", alert.InitiatorAvatarUrl);
            Assert.Equal("general", alert.ChannelName);                       // via /api/conversations.info
            Assert.Equal("https://app.slack.com/huddle/T123/C1", alert.Link); // team id from auth.test
        }
        finally
        {
            await source.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Membership_gate_and_unknown_users_resolve_over_the_real_api()
    {
        using var server = new FakeSlackServer();
        var source = CreateSource(server, out var status, out _);
        var alerts = new List<HeadsUp.Models.Alert>();
        source.AlertRaised += (_, a) => alerts.Add(a);

        await source.StartAsync(CancellationToken.None);
        try
        {
            await WaitForStatusAsync(status, () => status.IsConnected);

            // A huddle in a channel the user is NOT a member of: gated out.
            await server.SendTextFrameAsync("""
                { "type": "message", "subtype": "huddle_thread", "channel": "CPRIVATE",
                  "room": { "created_by": "U2" } }
                """);

            // A huddle by a user the API can't resolve: display name falls back to the id.
            await server.SendTextFrameAsync("""
                { "type": "message", "subtype": "huddle_thread", "channel": "C1",
                  "room": { "created_by": "UNKNOWN1" } }
                """);

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (alerts.Count == 0 && DateTime.UtcNow < deadline)
                await Task.Delay(25);

            var alert = Assert.Single(alerts); // CPRIVATE never surfaced
            Assert.Equal("C1", alert.ChannelId);
            Assert.Equal("UNKNOWN1", alert.InitiatorDisplayName);
            Assert.Null(alert.InitiatorAvatarUrl);
        }
        finally
        {
            await source.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_channel_mention_resolves_the_channel_name_over_the_real_api()
    {
        using var server = new FakeSlackServer();
        var source = CreateSource(server, out var status, out _);
        var waiter = new AlertWaiter(source);

        await source.StartAsync(CancellationToken.None);
        try
        {
            await WaitForStatusAsync(status, () => status.IsConnected);

            // auth.test identified us as USELF; a channel message mentioning us alerts.
            await server.SendTextFrameAsync("""
                { "type": "message", "channel": "C1", "user": "U2", "text": "<@USELF> ping" }
                """);

            var alert = await waiter.Alert;
            Assert.Equal(HeadsUp.Models.AlertKind.Mention, alert.Kind);
            Assert.Equal("general", alert.ChannelName);            // resolved via /api/conversations.info
            Assert.Equal("Sarah Whitfield", alert.InitiatorDisplayName);
        }
        finally
        {
            await source.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Slack_dnd_is_polled_and_feeds_the_snooze_gate()
    {
        using var server = new FakeSlackServer { DndUntil = DateTimeOffset.Now.AddMinutes(30) };
        var snooze = new SnoozeService();
        var source = CreateSource(server, out var status, out _, snooze);
        source._dndPollInterval = TimeSpan.FromMilliseconds(50);

        await source.StartAsync(CancellationToken.None);
        try
        {
            await WaitForStatusAsync(status, () => status.IsConnected);

            // The poller picks up Slack DND and mutes the gate.
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (!snooze.AnySourceDndActive && DateTime.UtcNow < deadline)
                await Task.Delay(25);
            Assert.True(snooze.IsMuted(out var reason));
            Assert.Equal("Do Not Disturb is on in Slack", reason);
        }
        finally
        {
            await source.StopAsync(CancellationToken.None);
        }

        // Disconnecting clears the DND-based mute so it can't get stuck on.
        Assert.False(snooze.AnySourceDndActive);
    }

    [Fact]
    public async Task A_gracefully_closed_socket_reconnects()
    {
        using var server = new FakeSlackServer();
        var source = CreateSource(server, out var status, out _);
        source._reconnectDelay = TimeSpan.FromMilliseconds(50);

        await source.StartAsync(CancellationToken.None);
        try
        {
            await WaitForStatusAsync(status, () => status.IsConnected);
            Assert.Equal(1, server.WebSocketConnections);

            await server.CloseWebSocketAsync();

            await WaitForStatusAsync(status, () => server.WebSocketConnections >= 2);
        }
        finally
        {
            await source.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Missing_credentials_wait_for_sign_in()
    {
        var status = new AppStatus();
        var source = new SlackWebHuddleSource(
            new StaticCredentialProvider(null),
            new StaticMonitor<DetectionOptions>(new()),
            new StaticMonitor<AlertOptions>(new()),
            new StaticMonitor<PriorityOptions>(new()),
            new SnoozeService(),
            status,
            NullLogger<SlackWebHuddleSource>.Instance);

        await source.StartAsync(CancellationToken.None);
        try
        {
            await WaitForStatusAsync(status, () => status.StatusMessage == "Waiting for sign-in…");
            Assert.False(status.IsConnected);
        }
        finally
        {
            await source.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Rejected_credentials_trigger_a_refresh_cycle()
    {
        using var server = new FakeSlackServer { Mode = FakeSlackServer.Behavior.AuthFail };
        var source = CreateSource(server, out var status, out var creds);
        source._reconnectDelay = TimeSpan.FromMilliseconds(50);

        await source.StartAsync(CancellationToken.None);
        try
        {
            await WaitForStatusAsync(status, () => status.StatusMessage == "Reconnecting…");
            Assert.False(status.IsConnected);

            // The retry loop re-fetches credentials (now with forceRefresh).
            await WaitForStatusAsync(status, () => creds.Calls >= 2);
        }
        finally
        {
            await source.StopAsync(CancellationToken.None);
        }
    }

    [Theory]
    [InlineData(FakeSlackServer.Behavior.RtmFail)]
    [InlineData(FakeSlackServer.Behavior.RtmNoUrl)]
    public async Task A_failed_rtm_connect_lands_in_reconnect(FakeSlackServer.Behavior mode)
    {
        using var server = new FakeSlackServer { Mode = mode };
        var source = CreateSource(server, out var status, out _);

        await source.StartAsync(CancellationToken.None);
        try
        {
            // auth.test succeeds (account captured) but rtm.connect fails -> retry path.
            await WaitForStatusAsync(status,
                () => status.Account == "cameron" && status.StatusMessage == "Reconnecting…");
        }
        finally
        {
            await source.StopAsync(CancellationToken.None);
        }
    }
}

/// <summary>Drives <see cref="SlackWebUserDirectory"/> against the fake server (paging + filtering).</summary>
public class SlackWebUserDirectoryIntegrationTests
{
    [Fact]
    public async Task Directory_pages_filters_and_sorts_the_member_list()
    {
        using var server = new FakeSlackServer();
        var directory = new SlackWebUserDirectory(
            new StaticCredentialProvider(new SlackCredentials("xoxc-test", "d=xoxd-test", server.BaseUrl)),
            NullLogger<SlackWebUserDirectory>.Instance);

        var users = await directory.GetUsersAsync();

        // Bot, deleted user, and USLACKBOT are filtered; both pages present; sorted by name.
        Assert.Equal(new[] { "U3", "U2" }, users.Select(u => u.Id)); // Alex before Sarah
        Assert.Equal(2, server.UsersListCalls);

        // Cached on the second call...
        await directory.GetUsersAsync();
        Assert.Equal(2, server.UsersListCalls);

        // ...unless a refresh is forced.
        await directory.GetUsersAsync(forceRefresh: true);
        Assert.Equal(4, server.UsersListCalls);

        // Lookup by id resolves from the cache.
        var sarah = await directory.FindByIdAsync("U2");
        Assert.Equal("Sarah Whitfield", sarah!.DisplayName);
        Assert.Null(await directory.FindByIdAsync("UNOPE"));
    }

    [Fact]
    public async Task A_failing_users_list_yields_an_empty_directory()
    {
        using var server = new FakeSlackServer { Mode = FakeSlackServer.Behavior.UsersListFail };
        var directory = new SlackWebUserDirectory(
            new StaticCredentialProvider(new SlackCredentials("xoxc-test", "d=xoxd-test", server.BaseUrl)),
            NullLogger<SlackWebUserDirectory>.Instance);

        Assert.Empty(await directory.GetUsersAsync());
    }

    [Fact]
    public async Task Concurrent_loads_share_one_fetch()
    {
        using var server = new FakeSlackServer();
        var directory = new SlackWebUserDirectory(
            new DelayedCredentialProvider(server.BaseUrl),
            NullLogger<SlackWebUserDirectory>.Instance);

        // Both calls pass the fast-path cache check before either fetch completes; the
        // second then re-checks the cache inside the gate and reuses the first result.
        var results = await Task.WhenAll(directory.GetUsersAsync(), directory.GetUsersAsync());

        Assert.Equal(2, server.UsersListCalls); // exactly one paged fetch happened
        Assert.Same(results[0], results[1]);
    }

    private sealed class DelayedCredentialProvider(string baseUrl) : ISlackCredentialProvider
    {
        public string SourceDescription => "delayed test credentials";

        public async Task<SlackCredentials?> GetCredentialsAsync(bool forceRefresh, CancellationToken ct)
        {
            await Task.Delay(100, ct); // hold the first caller inside the gate
            return new SlackCredentials("xoxc-test", "d=xoxd-test", baseUrl);
        }
    }
}

using HeadsUp.Relay.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HeadsUp.Relay.Tests;

/// <summary>
/// The relay's one genuinely dangerous behaviour is fan-out: Slack delivers an event once, and
/// the relay decides who sees it. These tests exist to pin down that a user can only ever
/// receive events Slack authorized *them* for — the rest of the relay is plumbing by comparison.
/// </summary>
public class EventRouterTests
{
    private readonly UserConnectionRegistry _registry = new();
    private StubResolver _resolver = new([]);

    private EventRouter Router() =>
        new(_registry, _resolver, NullLogger<EventRouter>.Instance);

    private static SlackRelayEvent Event(string payload = "{\"type\":\"message\"}", string? context = null) =>
        new(payload, [], context);

    // ------------------------------------------------------------------
    // Isolation — the property that matters
    // ------------------------------------------------------------------

    [Fact]
    public async Task An_event_reaches_only_the_authorized_user()
    {
        var alice = Connect("U_ALICE");
        var bob = Connect("U_BOB");
        _resolver = new StubResolver(["U_ALICE"]);

        await Router().RouteAsync(Event("alice's DM"), CancellationToken.None);

        Assert.Equal(["alice's DM"], alice.Received);
        Assert.Empty(bob.Received);
    }

    [Fact]
    public async Task An_event_authorized_for_nobody_reaches_nobody()
    {
        var alice = Connect("U_ALICE");
        _resolver = new StubResolver([]);

        var delivered = await Router().RouteAsync(Event(), CancellationToken.None);

        Assert.Equal(0, delivered);
        Assert.Empty(alice.Received);
    }

    [Fact]
    public async Task An_unknown_authorized_user_does_not_leak_to_connected_users()
    {
        // The dangerous shape: someone is authorized, but isn't the one who's connected.
        var bob = Connect("U_BOB");
        _resolver = new StubResolver(["U_ALICE"]);

        var delivered = await Router().RouteAsync(Event(), CancellationToken.None);

        Assert.Equal(0, delivered);
        Assert.Empty(bob.Received);
    }

    [Fact]
    public async Task A_shared_channel_event_reaches_every_authorized_user_and_no_one_else()
    {
        var alice = Connect("U_ALICE");
        var bob = Connect("U_BOB");
        var carol = Connect("U_CAROL");
        _resolver = new StubResolver(["U_ALICE", "U_BOB"]);

        await Router().RouteAsync(Event("channel message"), CancellationToken.None);

        Assert.Single(alice.Received);
        Assert.Single(bob.Received);
        Assert.Empty(carol.Received);
    }

    [Fact]
    public async Task A_disconnected_user_stops_receiving_events()
    {
        var alice = Connect("U_ALICE", out var registration);
        _resolver = new StubResolver(["U_ALICE"]);

        registration.Dispose();
        await Router().RouteAsync(Event(), CancellationToken.None);

        Assert.Empty(alice.Received);
    }

    // ------------------------------------------------------------------
    // Delivery mechanics
    // ------------------------------------------------------------------

    [Fact]
    public async Task Every_machine_a_user_is_signed_in_on_receives_the_event()
    {
        var laptop = Connect("U_ALICE");
        var desktop = Connect("U_ALICE");
        _resolver = new StubResolver(["U_ALICE"]);

        var delivered = await Router().RouteAsync(Event(), CancellationToken.None);

        Assert.Equal(2, delivered);
        Assert.Single(laptop.Received);
        Assert.Single(desktop.Received);
    }

    [Fact]
    public async Task A_duplicated_authorization_still_delivers_once()
    {
        var alice = Connect("U_ALICE");
        _resolver = new StubResolver(["U_ALICE", "U_ALICE"]);

        var delivered = await Router().RouteAsync(Event(), CancellationToken.None);

        Assert.Equal(1, delivered);
        Assert.Single(alice.Received);
    }

    [Fact]
    public async Task One_failing_client_does_not_stop_the_others()
    {
        var broken = Connect("U_ALICE", faulty: true);
        var bob = Connect("U_BOB");
        _resolver = new StubResolver(["U_ALICE", "U_BOB"]);

        var delivered = await Router().RouteAsync(Event(), CancellationToken.None);

        Assert.Equal(1, delivered);
        Assert.Single(bob.Received);
    }

    [Fact]
    public async Task The_payload_is_forwarded_untouched()
    {
        // The desktop applies the rules, so the relay must not reinterpret the event.
        const string raw = """{"type":"message","subtype":"huddle_thread","user":"U2","channel":"C1"}""";
        var alice = Connect("U_ALICE");
        _resolver = new StubResolver(["U_ALICE"]);

        await Router().RouteAsync(Event(raw), CancellationToken.None);

        Assert.Equal(raw, alice.Received.Single());
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private RecordingConnection Connect(string userId, bool faulty = false) =>
        Connect(userId, out _, faulty);

    private RecordingConnection Connect(string userId, out IDisposable registration, bool faulty = false)
    {
        var connection = new RecordingConnection(userId, faulty);
        registration = _registry.Add(connection);
        return connection;
    }

    private sealed class RecordingConnection(string userId, bool faulty) : IUserConnection
    {
        public string UserId { get; } = userId;
        public List<string> Received { get; } = [];

        public Task SendAsync(string payload, CancellationToken ct)
        {
            if (faulty)
                throw new IOException("client went away");

            Received.Add(payload);
            return Task.CompletedTask;
        }
    }

    private sealed class StubResolver(IReadOnlyList<string> userIds) : IEventAuthorizationResolver
    {
        public Task<IReadOnlyList<string>> ResolveAsync(SlackRelayEvent relayEvent, CancellationToken ct) =>
            Task.FromResult(userIds);
    }
}

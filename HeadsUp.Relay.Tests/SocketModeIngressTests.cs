using HeadsUp.Relay.Ingress;
using HeadsUp.Relay.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SlackNet.SocketMode;
using Xunit;

namespace HeadsUp.Relay.Tests;

/// <summary>
/// Frame handling, driven directly rather than through a live socket. The behaviour worth
/// pinning is that acknowledgement is independent of everything else: Slack redelivers
/// unacknowledged envelopes and drops connections whose acks lag, so one user's routing problem
/// must never become a connection problem for the whole org.
/// </summary>
public class SocketModeIngressTests
{
    private readonly ICoreSocketModeClient _socket = Substitute.For<ICoreSocketModeClient>();
    private readonly IUserConnectionRegistry _registry = new UserConnectionRegistry();
    private readonly StubResolver _resolver = new();

    private SocketModeIngress Ingress() =>
        new(_socket,
            new EventRouter(_registry, _resolver, NullLogger<EventRouter>.Instance),
            // No bot token, so enrichment is a pass-through — what a relay without one does.
            new EventEnricher(null, NullLogger<EventEnricher>.Instance),
            NullLogger<SocketModeIngress>.Instance);

    private static RawSocketMessage Frame(string json, int socketId = 1) =>
        new() { Message = json, SocketId = socketId };

    private const string DmEnvelope = """
        {"envelope_id":"env-1","type":"events_api","payload":{
          "event_context":"ctx-1",
          "authorizations":[{"user_id":"U_ALICE","is_bot":false}],
          "event":{"type":"message","user":"U_BOB","channel":"D1","text":"hi"}}}
        """;

    // ------------------------------------------------------------------
    // Acknowledgement
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_routable_frame_is_acknowledged()
    {
        await Ingress().HandleAsync(Frame(DmEnvelope), CancellationToken.None);

        await _socket.Received(1).Send(1, Arg.Is<Acknowledgement>(a => a.EnvelopeId == "env-1"));
    }

    [Fact]
    public async Task A_frame_we_do_not_route_is_still_acknowledged()
    {
        // Slack redelivers anything unacknowledged, so silence would mean an endless retry loop.
        await Ingress().HandleAsync(Frame("""{"envelope_id":"env-9","type":"disconnect"}"""), CancellationToken.None);

        await _socket.Received(1).Send(Arg.Any<int>(), Arg.Is<Acknowledgement>(a => a.EnvelopeId == "env-9"));
    }

    [Fact]
    public async Task A_routing_failure_does_not_prevent_acknowledgement()
    {
        _resolver.Throw = true;

        await Ingress().HandleAsync(Frame(DmEnvelope), CancellationToken.None);

        // Ack happens first and unconditionally; a bad event must not cost us the connection.
        await _socket.Received(1).Send(1, Arg.Is<Acknowledgement>(a => a.EnvelopeId == "env-1"));
    }

    [Fact]
    public async Task A_failed_acknowledgement_does_not_stop_delivery()
    {
        _socket.Send(Arg.Any<int>(), Arg.Any<Acknowledgement>()).ThrowsAsync(new IOException("socket died"));
        var alice = Connect("U_ALICE");
        _resolver.UserIds = ["U_ALICE"];

        await Ingress().HandleAsync(Frame(DmEnvelope), CancellationToken.None);

        Assert.Single(alice.Received);
    }

    [Fact]
    public async Task A_malformed_frame_is_survivable()
    {
        await Ingress().HandleAsync(Frame("not json"), CancellationToken.None); // must not throw

        await _socket.DidNotReceiveWithAnyArgs().Send(default, default!);
    }

    [Fact]
    public async Task The_acknowledgement_goes_back_on_the_socket_that_received_it()
    {
        // Slack requires the ack on the same connection, which matters once there are several.
        await Ingress().HandleAsync(Frame(DmEnvelope, socketId: 7), CancellationToken.None);

        await _socket.Received(1).Send(7, Arg.Any<Acknowledgement>());
    }

    // ------------------------------------------------------------------
    // Routing hand-off
    // ------------------------------------------------------------------

    [Fact]
    public async Task An_event_reaches_only_its_authorized_user()
    {
        var alice = Connect("U_ALICE");
        var bob = Connect("U_BOB");
        _resolver.UserIds = ["U_ALICE"];

        await Ingress().HandleAsync(Frame(DmEnvelope), CancellationToken.None);

        Assert.Single(alice.Received);
        Assert.Empty(bob.Received);
        Assert.DoesNotContain("authorizations", alice.Received.Single());
    }

    private RecordingConnection Connect(string userId)
    {
        var connection = new RecordingConnection(userId);
        _registry.Add(connection);
        return connection;
    }

    private sealed class RecordingConnection(string userId) : IUserConnection
    {
        public string UserId { get; } = userId;
        public List<string> Received { get; } = [];

        public Task SendAsync(string payload, CancellationToken ct)
        {
            Received.Add(payload);
            return Task.CompletedTask;
        }
    }

    private sealed class StubResolver : IEventAuthorizationResolver
    {
        public IReadOnlyList<string> UserIds { get; set; } = [];
        public bool Throw { get; set; }

        public Task<IReadOnlyList<string>> ResolveAsync(SlackRelayEvent relayEvent, CancellationToken ct) =>
            Throw
                ? Task.FromException<IReadOnlyList<string>>(new InvalidOperationException("resolver failed"))
                : Task.FromResult(UserIds);
    }
}

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SlackNet.SocketMode;

namespace HeadsUp.Relay.Ingress;

using HeadsUp.Relay.Routing;

/// <summary>
/// The relay's single connection to Slack.
///
/// One process holds the app-level token and every event for the org arrives here, which is
/// exactly why desktops can't do this themselves: with several connections open on one app,
/// Slack sends each payload to an arbitrary one of them, so users would receive a random
/// subset of each other's events. Centralising the connection is what makes a single shared
/// Slack app correct.
///
/// SlackNet owns the socket lifecycle (connect, reconnect, refresh). This class subscribes to
/// the raw frames instead of SlackNet's typed dispatch because routing needs the envelope's
/// <c>authorizations</c> and <c>event_context</c>, which the typed message event doesn't carry.
/// </summary>
public sealed class SocketModeIngress : BackgroundService
{
    private readonly ICoreSocketModeClient _socket;
    private readonly EventRouter _router;
    private readonly ILogger<SocketModeIngress> _logger;

    public SocketModeIngress(ICoreSocketModeClient socket, EventRouter router, ILogger<SocketModeIngress> logger)
    {
        _socket = socket;
        _router = router;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var subscription = _socket.RawSocketMessages.Subscribe(raw => _ = HandleAsync(raw, stoppingToken));

        // More than one connection so a restart can overlap: Slack keeps delivering on the old
        // socket while the new one comes up, rather than dropping events in the gap.
        await _socket.Connect(
            new SocketModeConnectionOptions { NumberOfConnections = 2 },
            cancellationToken: stoppingToken);

        _logger.LogInformation("Relay connected to Slack via Socket Mode.");

        // Nothing more to do on this thread: frames arrive on the subscription above, and
        // SlackNet handles reconnects itself. Hold until shutdown.
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Relay disconnecting from Slack.");
        }
    }

    /// <summary>
    /// Acknowledges the frame, then routes it. Internal so tests can drive it without a socket.
    /// </summary>
    internal async Task HandleAsync(RawSocketMessage raw, CancellationToken ct)
    {
        // Every frame verbatim at Debug, so the relay doubles as a probe: start a huddle and
        // read the log to see exactly what Slack pushes over Socket Mode. Worth having because
        // the Events API and the client RTM stream do not carry the same message subtypes, and
        // huddle detection depends on which ones actually arrive here.
        // Redacted: Slack includes the app's verification token in every event payload, so
        // logging frames verbatim would write a credential to disk on every message.
        _logger.LogDebug("Socket frame on {SocketId}: {Frame}",
            raw.SocketId, HeadsUp.Services.SlackEnvelopeParser.Redact(raw.Message));

        ParsedEnvelope parsed;
        try
        {
            parsed = RelayEnvelope.Parse(raw.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not parse a Socket Mode frame; skipping it.");
            return;
        }

        // Acknowledge first and unconditionally. Slack redelivers unacknowledged envelopes and
        // will drop the connection if acks lag, so a routing problem must not become a
        // connection problem for every other user.
        if (parsed.EnvelopeId is { } envelopeId)
        {
            try
            {
                await _socket.Send(raw.SocketId, new Acknowledgement { EnvelopeId = envelopeId });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not acknowledge envelope {EnvelopeId}.", envelopeId);
            }
        }

        if (parsed.Event is null)
            return;

        try
        {
            var delivered = await _router.RouteAsync(parsed.Event, ct);
            _logger.LogDebug("Routed an event to {Count} connection(s).", delivered);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to route an event.");
        }
    }
}

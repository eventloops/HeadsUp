using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using HeadsUp.Configuration;
using HeadsUp.Models;

namespace HeadsUp.Services;

/// <summary>
/// Detection via the Heads-Up relay: the company runs one Slack app, and this connects to the
/// relay over a WebSocket to receive the events Slack says this user may see.
///
/// The relay exists because Socket Mode authenticates with an app-level token that Slack only
/// mints once per app, and distributes payloads arbitrarily across connections sharing it — so
/// desktops cannot each hold one. Moving that single connection server-side is what allows one
/// shared app and a one-click sign-in.
///
/// Everything that decides <i>whether to interrupt someone</i> still happens here: priority
/// people, keywords, muted channels and DND never leave the machine, so the relay routes
/// without knowing anyone's rules.
/// </summary>
public sealed class SlackRelaySource : BackgroundService, IAlertSource
{
    /// <summary>Cooldown before a new huddle in the same channel fires another notification.</summary>
    private static readonly TimeSpan DedupeCooldown = TimeSpan.FromMinutes(5);

    // Internal and instance-scoped so tests can shrink the waits.
    internal TimeSpan _reconnectDelay = TimeSpan.FromSeconds(10);

    private readonly IRelayCredentialProvider _credentials;
    private readonly IOptionsMonitor<RelayClientOptions> _options;
    private readonly IOptionsMonitor<DetectionOptions> _detectionOptions;
    private readonly IOptionsMonitor<AlertOptions> _alertOptions;
    private readonly IOptionsMonitor<PriorityOptions> _priorityOptions;
    private readonly AppStatus _status;
    private readonly ILogger<SlackRelaySource> _logger;

    private readonly Dictionary<string, DateTimeOffset> _recentHuddles = [];

    public SourceId SourceId => SlackSource.SourceId;
    public string SourceName => SlackSource.Name;

    private SourceStatus Status => _status.GetOrAdd(SourceId, SourceName);

    public event EventHandler<Alert>? AlertRaised;

    public SlackRelaySource(
        IRelayCredentialProvider credentials,
        IOptionsMonitor<RelayClientOptions> options,
        IOptionsMonitor<DetectionOptions> detectionOptions,
        IOptionsMonitor<AlertOptions> alertOptions,
        IOptionsMonitor<PriorityOptions> priorityOptions,
        AppStatus status,
        ILogger<SlackRelaySource> logger)
    {
        _credentials = credentials;
        _options = options;
        _detectionOptions = detectionOptions;
        _alertOptions = alertOptions;
        _priorityOptions = priorityOptions;
        _status = status;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Status.Mode = DetectionOptions.SlackRelay;
        Status.CredentialSource = _credentials.SourceDescription;

        var refreshNext = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                refreshNext = !await ConnectAndRunAsync(refreshNext, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // A rejected session must force a fresh one next round. Treating every failure
                // as transient meant that after the relay rotated its signing key, the desktop
                // retried the same dead session forever behind "Reconnecting…" and never asked
                // the user to sign in again.
                refreshNext = IsAuthRejection(ex);

                Status.SetConnection(false, refreshNext ? "Sign-in expired" : "Reconnecting…");
                _logger.LogWarning(ex, "Relay connection ended; retrying in {Delay}s.",
                    _reconnectDelay.TotalSeconds);
            }

            try
            {
                await Task.Delay(_reconnectDelay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// True when the relay refused our session, as opposed to the connection merely failing.
    /// The handshake is plain HTTP, so a rejection surfaces as 401/403 on the upgrade rather
    /// than as anything WebSocket-specific.
    /// </summary>
    internal static bool IsAuthRejection(Exception ex) =>
        ex is WebSocketException { InnerException: HttpRequestException http } &&
            http.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
        || ex is HttpRequestException direct &&
            direct.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
        // .NET surfaces the upgrade status in the message when no typed inner exception exists.
        || ex is WebSocketException && (ex.Message.Contains("401") || ex.Message.Contains("403"));

    /// <summary>
    /// Signs in if needed, opens one socket, and pumps it until it closes. Returns false when
    /// there's no session yet (caller forces a fresh sign-in next round).
    /// </summary>
    internal async Task<bool> ConnectAndRunAsync(bool forceRefresh, CancellationToken stoppingToken)
    {
        var session = await _credentials.GetSessionAsync(forceRefresh, stoppingToken);
        if (session is null)
        {
            // Said plainly, because a silent unauthorized state is indistinguishable from a
            // broken app — the user simply never gets alerts and has no way to tell why.
            Status.SetConnection(false, "Not signed in — sign in to receive alerts");
            _logger.LogDebug("No relay session yet; waiting for sign-in.");
            return false;
        }

        var relayUrl = _options.CurrentValue.EventsUrl;
        if (string.IsNullOrWhiteSpace(relayUrl))
        {
            Status.SetConnection(false, "Relay address not configured");
            _logger.LogWarning("Relay:EventsUrl is not configured; cannot connect.");
            return false;
        }

        using var socket = new ClientWebSocket();
        // The session goes in a header rather than the query string, so it stays out of proxy
        // and access logs.
        socket.Options.SetRequestHeader("Authorization", $"Bearer {session.Token}");

        await socket.ConnectAsync(new Uri(relayUrl), stoppingToken);

        _selfUserId = session.SlackUserId;
        _logger.LogInformation("Connected to the Heads-Up relay as {UserId}.", session.SlackUserId);
        Status.SetConnection(true, "Watching for huddles");
        Status.SetAccount(session.SlackUserId, null);

        await ReceiveLoopAsync(socket, stoppingToken);

        Status.SetConnection(false, "Disconnected");
        return true;
    }

    internal string? _selfUserId;

    private async Task ReceiveLoopAsync(WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        var message = new StringBuilder();

        while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    _logger.LogInformation("Relay closed the connection: {Status}.", result.CloseStatus);
                    return;
                }

                message.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            }
            while (!result.EndOfMessage);

            var payload = message.ToString();
            message.Clear();

            try
            {
                HandleEvent(payload);
            }
            catch (Exception ex)
            {
                // One malformed event must not drop the connection for every later one.
                _logger.LogWarning(ex, "Could not handle a relayed event.");
            }
        }
    }

    /// <summary>Classifies one relayed Slack event. Internal so tests can feed events directly.</summary>
    internal void HandleEvent(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (root.Str("type") != "message")
            return;

        var alerts = _alertOptions.CurrentValue;
        var prefs = alerts.ToPreferences();

        if (SlackHuddleParser.Parse(root) is { } huddle)
        {
            if (!alerts.Huddles || huddle.HasEnded)
                return;

            if (AlertPolicy.IsChannelMuted(new ChannelId(huddle.ChannelId ?? string.Empty), SourceId, prefs.Muted))
                return;

            RaiseHuddle(huddle);
            return;
        }

        // Only plain user messages are candidates for mention/DM/keyword alerts — skip edits,
        // deletes, joins and bot posts.
        if (root.Str("subtype") is null)
            HandleMessage(root, prefs);
    }

    private void RaiseHuddle(HuddleFacts huddle)
    {
        var userId = huddle.InitiatorUserId;
        var channelId = huddle.ChannelId;
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(channelId))
            return;

        // No popup for huddles you started yourself, unless the debug toggle is on.
        if (userId.Equals(_selfUserId, StringComparison.OrdinalIgnoreCase)
            && !_detectionOptions.CurrentValue.NotifyOwnHuddles)
            return;

        var now = DateTimeOffset.Now;
        lock (_recentHuddles)
        {
            if (_recentHuddles.TryGetValue(channelId, out var last) && now - last < DedupeCooldown)
                return;
            _recentHuddles[channelId] = now;
        }

        AlertRaised?.Invoke(this, new Alert(
            InitiatorUserId: new UserId(userId),
            InitiatorDisplayName: userId,
            InitiatorAvatarUrl: null,
            ChannelId: new ChannelId(channelId),
            ChannelName: channelId,
            IsDirectMessage: huddle.IsDirectMessage,
            // Slack's own deep link, which is more reliable than one we assemble.
            Link: huddle.HuddleLink ?? $"slack://channel?id={channelId}",
            IsPriorityUser: _priorityOptions.CurrentValue.IsPriority(SourceId, new UserId(userId)),
            OccurredAt: now,
            SourceId: SourceId,
            SourceName: SourceName));
    }

    private void HandleMessage(JsonElement root, AlertPreferences prefs)
    {
        var userId = root.Str("user");
        var channelId = root.Str("channel");
        var text = root.Str("text") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(channelId))
            return;

        // Never alert on your own messages.
        if (userId.Equals(_selfUserId, StringComparison.OrdinalIgnoreCase))
            return;

        var isMention = SlackMessageParser.IsMention(text, _selfUserId);
        var isDm = SlackMessageParser.IsDirectMessage(channelId);
        if (AlertPolicy.Classify(isMention, isDm, text, new ChannelId(channelId), SourceId, prefs) is not { } kind)
            return;

        AlertRaised?.Invoke(this, new Alert(
            InitiatorUserId: new UserId(userId),
            InitiatorDisplayName: userId,
            InitiatorAvatarUrl: null,
            ChannelId: new ChannelId(channelId),
            ChannelName: channelId,
            IsDirectMessage: isDm,
            Link: $"slack://channel?id={channelId}",
            IsPriorityUser: _priorityOptions.CurrentValue.IsPriority(SourceId, new UserId(userId)),
            OccurredAt: DateTimeOffset.Now,
            SourceId: SourceId,
            SourceName: SourceName,
            Kind: kind,
            Preview: SlackMessageParser.Preview(text)));
    }
}

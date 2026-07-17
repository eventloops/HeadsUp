using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using HeadsUp.Configuration;
using HeadsUp.Models;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace HeadsUp.Services;

/// <summary>
/// Detects huddles through the <b>unofficial</b> Slack client API - the same
/// session your browser or the Slack desktop app uses (an <c>xoxc-...</c> client
/// token plus the <c>d</c> session cookie). Needs no Slack app and no admin
/// approval, which is the whole point of this mode.
///
/// It mirrors what the web client does: <c>rtm.connect</c> hands back a WebSocket
/// URL, and huddle starts arrive over it as <c>message</c> events with subtype
/// <c>huddle_thread</c> (legacy calls: <c>sh_room_created</c>) - the exact same
/// signal the Socket Mode <see cref="SlackHuddleMonitor"/> handles. Initiator and
/// channel details are resolved through the client API's <c>users.info</c> and
/// <c>conversations.info</c>.
///
/// Prototype, and honestly so: these endpoints are undocumented and can change
/// without notice, and driving Slack with a session token is against its Terms of
/// Service. Fine for a personal notifier; know what it is. Every received frame is
/// logged at Debug so this source doubles as a probe - start a huddle and watch
/// the log to confirm exactly what Slack pushes.
/// </summary>
public sealed class SlackWebHuddleSource : BackgroundService, IAlertSource
{
    /// <summary>Cooldown before a new huddle in the same channel fires another notification.</summary>
    private static readonly TimeSpan DedupeCooldown = TimeSpan.FromMinutes(5);

    // Internal and instance-scoped so integration tests can shrink the waits.
    internal TimeSpan _pingInterval = TimeSpan.FromSeconds(20);
    internal TimeSpan _reconnectDelay = TimeSpan.FromSeconds(10);
    internal TimeSpan _dndPollInterval = TimeSpan.FromSeconds(60);

    private readonly ISlackCredentialProvider _credentials;
    private readonly IOptionsMonitor<SlackOptions> _slackOptions;
    private readonly IOptionsMonitor<DetectionOptions> _detectionOptions;
    private readonly IOptionsMonitor<AlertOptions> _alertOptions;
    private readonly SnoozeService _snooze;
    private readonly AppStatus _status;
    private readonly ILogger<SlackWebHuddleSource> _logger;

    // Channel ID -> last time we notified for it, to avoid duplicate events.
    private readonly ConcurrentDictionary<string, DateTimeOffset> _recentHuddles = new();

    private SlackWebClient? _client;
    // Internal (not private) so frame-level tests can simulate an authenticated session.
    internal string? _teamId;
    internal string? _selfUserId;
    private int _messageId;

    public event EventHandler<Alert>? AlertRaised;

    public SlackWebHuddleSource(
        ISlackCredentialProvider credentials,
        IOptionsMonitor<SlackOptions> slackOptions,
        IOptionsMonitor<DetectionOptions> detectionOptions,
        IOptionsMonitor<AlertOptions> alertOptions,
        SnoozeService snooze,
        AppStatus status,
        ILogger<SlackWebHuddleSource> logger)
    {
        _credentials = credentials;
        _slackOptions = slackOptions;
        _detectionOptions = detectionOptions;
        _alertOptions = alertOptions;
        _snooze = snooze;
        _status = status;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _status.CredentialSource = _credentials.SourceDescription;

        // Outer loop: (re)acquire credentials, authenticate, then run the socket until it
        // drops. A missing or rejected credential forces a refresh on the next round, which
        // is how session rotation is absorbed when a live provider (e.g. WebView2) is used.
        var refreshNext = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // ConnectAndRunAsync returns false when creds are missing/rejected.
                refreshNext = !await ConnectAndRunAsync(refreshNext, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // A dropped socket lands here; keep the same creds and let the next
                // auth.test decide whether they have actually gone stale.
                _status.SetConnection(false, "Reconnecting…");
                _logger.LogWarning(ex, "Slack client connection ended; retrying in {Delay}s.",
                    _reconnectDelay.TotalSeconds);
                refreshNext = false;
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
    /// Fetches credentials, authenticates, and runs one WebSocket session to completion.
    /// Returns <c>false</c> when credentials are missing or rejected (caller should force a
    /// refresh next round); <c>true</c> after a session ran and ended of its own accord.
    /// </summary>
    private async Task<bool> ConnectAndRunAsync(bool forceRefresh, CancellationToken stoppingToken)
    {
        var creds = await _credentials.GetCredentialsAsync(forceRefresh, stoppingToken);
        if (creds is null)
        {
            _status.SetConnection(false, "Waiting for sign-in…");
            _logger.LogWarning(
                "No Slack credentials available yet from {Source}; waiting {Delay}s before retry.",
                _credentials.SourceDescription, _reconnectDelay.TotalSeconds);
            return false;
        }

        _client?.Dispose();
        _client = new SlackWebClient(creds.Token, creds.Cookie, creds.WorkspaceUrl);

        // auth.test doubles as a credential check and gives us the team/self ids.
        var auth = await _client.AuthTestAsync(stoppingToken);
        if (!auth.Bool("ok"))
        {
            _status.SetConnection(false, "Reconnecting…");
            _logger.LogWarning(
                "Slack auth.test failed: {Error}. Refreshing credentials from {Source} and retrying.",
                auth.Str("error") ?? "unknown", _credentials.SourceDescription);
            return false;
        }

        _teamId = auth.Str("team_id");
        _selfUserId = auth.Str("user_id");
        _status.SetAccount(auth.Str("user"), auth.Str("team"));
        _logger.LogInformation(
            "Slack client session OK: authenticated as {User} in team {Team}.",
            auth.Str("user"), auth.Str("team"));

        await RunConnectionAsync(stoppingToken);
        return true;
    }

    private async Task RunConnectionAsync(CancellationToken stoppingToken)
    {
        var rtm = await _client!.RtmConnectAsync(stoppingToken);
        if (!rtm.Bool("ok"))
            throw new InvalidOperationException($"rtm.connect failed: {rtm.Str("error") ?? "unknown"}");

        var url = rtm.Str("url");
        if (string.IsNullOrWhiteSpace(url))
            throw new InvalidOperationException("rtm.connect returned no WebSocket URL.");

        using var ws = new ClientWebSocket();
        // The gateway authenticates the socket from the session cookie on the upgrade
        // request, exactly like the HTTP calls. Without it the server accepts the
        // handshake then immediately sends {"type":"error","error":{"msg":"invalid_auth"}}
        // and closes. Mirror the same cookie + UA the client uses.
        ws.Options.SetRequestHeader("Cookie", _client!.CookieHeader);
        ws.Options.SetRequestHeader("User-Agent", _client.UserAgent);
        ws.Options.SetRequestHeader("Origin", "https://app.slack.com");
        await ws.ConnectAsync(new Uri(url), stoppingToken);
        _logger.LogInformation("Connected to Slack real-time WebSocket. Watching for huddles.");
        _status.SetConnection(true, "Watching for huddles");

        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var pingTask = KeepAliveAsync(ws, sessionCts.Token);
        var dndTask = PollDndAsync(sessionCts.Token);

        try
        {
            await ReceiveLoopAsync(ws, stoppingToken);
        }
        finally
        {
            sessionCts.Cancel();
            try { await Task.WhenAll(pingTask, dndTask); } catch { /* teardown is best effort */ }
            _snooze.SetSlackDnd(null); // don't keep suppressing on stale DND once disconnected
        }
    }

    /// <summary>Polls <c>dnd.info</c> so Heads-Up honors the user's Slack Do Not Disturb.</summary>
    private async Task PollDndAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(_dndPollInterval);
            do
            {
                await UpdateDndOnceAsync(ct);
            }
            while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException) { /* normal on disconnect/shutdown */ }
        catch (Exception ex) { _logger.LogDebug(ex, "DND poll loop ended."); }
    }

    private async Task UpdateDndOnceAsync(CancellationToken ct)
    {
        try
        {
            var dnd = await _client!.DndInfoAsync(ct);
            if (dnd.Bool("ok"))
                _snooze.SetSlackDnd(DndUntil(dnd));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "dnd.info poll failed.");
        }
    }

    /// <summary>
    /// The moment the user's Slack DND currently ends, or null if it isn't active now.
    /// Manual Slack snooze (<c>snooze_endtime</c>) takes precedence over the scheduled DND
    /// window (<c>next_dnd_start_ts</c>..<c>next_dnd_end_ts</c>).
    /// </summary>
    internal static DateTimeOffset? DndUntil(JsonElement dnd)
    {
        var now = DateTimeOffset.Now.ToUnixTimeSeconds();

        if (dnd.Bool("snooze_enabled")
            && dnd.TryGetProperty("snooze_endtime", out var se) && se.TryGetInt64(out var snoozeEnd)
            && snoozeEnd > now)
            return DateTimeOffset.FromUnixTimeSeconds(snoozeEnd);

        if (dnd.Bool("dnd_enabled")
            && dnd.TryGetProperty("next_dnd_start_ts", out var s) && s.TryGetInt64(out var start)
            && dnd.TryGetProperty("next_dnd_end_ts", out var e) && e.TryGetInt64(out var end)
            && start <= now && now < end)
            return DateTimeOffset.FromUnixTimeSeconds(end);

        return null;
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        var builder = new StringBuilder();

        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            builder.Clear();
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    _logger.LogInformation("Slack WebSocket closed by server: {Status} {Reason}.",
                        result.CloseStatus, result.CloseStatusDescription);
                    return;
                }

                builder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            }
            while (!result.EndOfMessage);

            HandleFrame(builder.ToString());
        }
    }

    // Internal so tests can feed frames without a live socket.
    internal void HandleFrame(string json)
    {
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(json);
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return; // ignore anything that isn't well-formed JSON
        }

        var type = root.Str("type");
        var subtype = root.Str("subtype");

        // Verbose trace so this source doubles as a probe.
        _logger.LogDebug("WS event type={Type} subtype={Subtype}", type ?? "?", subtype ?? "-");

        if (type != "message")
            return;

        var alerts = _alertOptions.CurrentValue;

        if (AlertClassifier.IsHuddleSubtype(subtype))
        {
            if (alerts.Huddles && !AlertClassifier.IsChannelMuted(root.Str("channel") ?? string.Empty, alerts))
                HandleHuddle(root, subtype);
            return;
        }

        // Only plain user messages (no subtype) are candidates for mention/DM/keyword
        // alerts - skip edits, deletes, joins, bot posts, etc.
        if (subtype is null)
            HandleMessage(root, alerts);
    }

    private void HandleHuddle(JsonElement root, string? subtype)
    {
        var channel = root.Str("channel");

        // A real huddle_thread message is authored by USLACKBOT, so the top-level
        // "user" is never the initiator - the actual starter is room.created_by.
        var user = root.Obj("room") is { } room ? room.Str("created_by") : null;
        user ??= root.Str("user");

        // Logged before any self/dedupe filtering so a huddle you start yourself
        // still confirms the signal works, even though it won't raise a popup.
        _logger.LogInformation(
            "Huddle signal received over client API: user={User} channel={Channel} subtype={Subtype}.",
            user ?? "?", channel ?? "?", subtype);

        _ = RaiseAlertAsync(user, channel);
    }

    private void HandleMessage(JsonElement root, AlertOptions alerts)
    {
        var user = root.Str("user");
        var channel = root.Str("channel");
        var text = root.Str("text") ?? string.Empty;
        if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(channel))
            return;

        // Never alert on your own messages.
        if (user.Equals(_selfUserId, StringComparison.OrdinalIgnoreCase))
            return;

        if (AlertClassifier.ClassifyMessage(text, channel, _selfUserId, alerts) is not { } kind)
            return;

        _logger.LogInformation("{Kind} alert: user={User} channel={Channel}.", kind, user, channel);
        _ = RaiseMessageAlertAsync(user, channel, text, kind);
    }

    private async Task RaiseMessageAlertAsync(string userId, string channelId, string text, AlertKind kind)
    {
        try
        {
            var (displayName, avatarUrl) = await ResolveUserAsync(userId);

            var isDm = channelId.StartsWith('D');
            var channelName = channelId;
            if (!isDm)
            {
                try
                {
                    var conv = await _client!.ConversationInfoAsync(channelId);
                    if (conv.Bool("ok") && conv.Obj("channel") is { } ch)
                        channelName = ch.Str("name") ?? channelId;
                }
                catch (Exception ex) { _logger.LogWarning(ex, "Could not resolve channel {Channel}.", channelId); }
            }

            var isPriority = _slackOptions.CurrentValue.PriorityUsers.Contains(userId, StringComparer.OrdinalIgnoreCase);
            var link = string.IsNullOrEmpty(_teamId)
                ? $"slack://channel?id={channelId}"
                : $"https://app.slack.com/client/{_teamId}/{channelId}";

            AlertRaised?.Invoke(this, new Alert(
                InitiatorUserId: userId,
                InitiatorDisplayName: displayName,
                InitiatorAvatarUrl: avatarUrl,
                ChannelId: channelId,
                ChannelName: channelName,
                IsDirectMessage: isDm,
                Link: link,
                IsPriorityUser: isPriority,
                OccurredAt: DateTimeOffset.Now,
                Kind: kind,
                Preview: AlertClassifier.Preview(text)));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error building message alert.");
        }
    }

    private async Task KeepAliveAsync(ClientWebSocket ws, CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(_pingInterval);
            while (await timer.WaitForNextTickAsync(ct))
            {
                if (ws.State != WebSocketState.Open)
                    return;

                var ping = $"{{\"id\":{Interlocked.Increment(ref _messageId)},\"type\":\"ping\"}}";
                var bytes = Encoding.UTF8.GetBytes(ping);
                await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal on disconnect/shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Keep-alive ping loop ended.");
        }
    }

    private async Task RaiseAlertAsync(string? userId, string? channelId)
    {
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(channelId))
            return;

        // No popup for huddles you started yourself, unless the debug toggle is on.
        if (userId.Equals(_selfUserId, StringComparison.OrdinalIgnoreCase)
            && !_detectionOptions.CurrentValue.NotifyOwnHuddles)
        {
            _logger.LogDebug("Ignoring own huddle in channel {Channel}.", channelId);
            return;
        }

        var now = DateTimeOffset.Now;
        var lastSeen = _recentHuddles.GetOrAdd(channelId, now);
        if (lastSeen != now && now - lastSeen < DedupeCooldown)
        {
            _logger.LogDebug("Huddle already active in channel {Channel}; skipping.", channelId);
            return;
        }
        _recentHuddles[channelId] = now;

        try
        {
            // Resolve the conversation first: it doubles as the membership gate.
            // DMs and group DMs are the user's by definition; fail open on error.
            string channelName = channelId;
            bool isDm = channelId.StartsWith('D');
            try
            {
                var conv = await _client!.ConversationInfoAsync(channelId);
                if (conv.Bool("ok") && conv.Obj("channel") is { } channel)
                {
                    isDm = channel.Bool("is_im");
                    bool isMpim = channel.Bool("is_mpim");
                    bool isMember = channel.Bool("is_member");
                    if (!isDm && !isMpim && !isMember)
                    {
                        _logger.LogDebug("Ignoring huddle in {Channel}: user is not a member.", channelId);
                        return;
                    }

                    channelName = channel.Str("name") ?? channelId;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not resolve channel info for {ChannelId}.", channelId);
            }

            var (displayName, avatarUrl) = await ResolveUserAsync(userId);
            var isPriority = _slackOptions.CurrentValue.PriorityUsers.Contains(userId, StringComparer.OrdinalIgnoreCase);

            // With a team id Slack can deep-link straight into the huddle;
            // otherwise fall back to opening the channel.
            var huddleLink = string.IsNullOrEmpty(_teamId)
                ? $"slack://channel?id={channelId}"
                : $"https://app.slack.com/huddle/{_teamId}/{channelId}";

            var huddleEvent = new Alert(
                InitiatorUserId: userId,
                InitiatorDisplayName: displayName,
                InitiatorAvatarUrl: avatarUrl,
                ChannelId: channelId,
                ChannelName: channelName,
                IsDirectMessage: isDm,
                Link: huddleLink,
                IsPriorityUser: isPriority,
                OccurredAt: now);

            _logger.LogInformation(
                "Huddle started by {User} in {Channel}{Priority}.",
                displayName, channelName, isPriority ? " [PRIORITY]" : string.Empty);

            AlertRaised?.Invoke(this, huddleEvent);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error while building huddle event.");
        }
    }

    private async Task<(string DisplayName, string? AvatarUrl)> ResolveUserAsync(string userId)
    {
        try
        {
            var info = await _client!.UserInfoAsync(userId);
            if (info.Bool("ok") && info.Obj("user") is { } user)
            {
                var profile = user.Obj("profile");
                var displayName = FirstNonEmpty(
                    profile?.Str("display_name"),
                    profile?.Str("real_name"),
                    user.Str("name")) ?? userId;
                var avatarUrl = FirstNonEmpty(
                    profile?.Str("image_72"),
                    profile?.Str("image_192"));
                return (displayName, avatarUrl);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not resolve user name for {UserId}.", userId);
        }

        return (userId, null);
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}

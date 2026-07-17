using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using HeadsUp.Configuration;
using HeadsUp.Models;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

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
    private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(10);

    private readonly ISlackCredentialProvider _credentials;
    private readonly IOptionsMonitor<SlackOptions> _slackOptions;
    private readonly IOptionsMonitor<DetectionOptions> _detectionOptions;
    private readonly IOptionsMonitor<AlertOptions> _alertOptions;
    private readonly AppStatus _status;
    private readonly ILogger<SlackWebHuddleSource> _logger;

    // Channel ID -> last time we notified for it, to avoid duplicate events.
    private readonly ConcurrentDictionary<string, DateTimeOffset> _recentHuddles = new();

    private SlackWebClient? _client;
    private string? _teamId;
    private string? _selfUserId;
    private int _messageId;

    public event EventHandler<Alert>? AlertRaised;

    public SlackWebHuddleSource(
        ISlackCredentialProvider credentials,
        IOptionsMonitor<SlackOptions> slackOptions,
        IOptionsMonitor<DetectionOptions> detectionOptions,
        IOptionsMonitor<AlertOptions> alertOptions,
        AppStatus status,
        ILogger<SlackWebHuddleSource> logger)
    {
        _credentials = credentials;
        _slackOptions = slackOptions;
        _detectionOptions = detectionOptions;
        _alertOptions = alertOptions;
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
                    ReconnectDelay.TotalSeconds);
                refreshNext = false;
            }

            try
            {
                await Task.Delay(ReconnectDelay, stoppingToken);
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
                _credentials.SourceDescription, ReconnectDelay.TotalSeconds);
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

        using var pingCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var pingTask = KeepAliveAsync(ws, pingCts.Token);

        try
        {
            await ReceiveLoopAsync(ws, stoppingToken);
        }
        finally
        {
            pingCts.Cancel();
            try { await pingTask; } catch { /* keep-alive teardown is best effort */ }
        }
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

    private void HandleFrame(string json)
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

        if (subtype is "huddle_thread" or "sh_room_created")
        {
            if (alerts.Huddles)
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

        var isMention = _selfUserId is not null &&
                        text.Contains($"<@{_selfUserId}>", StringComparison.OrdinalIgnoreCase);
        var isDm = channel.StartsWith('D');

        // Priority: a direct @mention beats "it's a DM" beats a keyword match.
        AlertKind kind;
        if (alerts.Mentions && isMention) kind = AlertKind.Mention;
        else if (alerts.DirectMessages && isDm) kind = AlertKind.DirectMessage;
        else if (alerts.Keywords && MatchesKeyword(text, alerts.KeywordList)) kind = AlertKind.Keyword;
        else return;

        _logger.LogInformation("{Kind} alert: user={User} channel={Channel}.", kind, user, channel);
        _ = RaiseMessageAlertAsync(user, channel, text, kind);
    }

    private static bool MatchesKeyword(string text, IReadOnlyList<string> keywords)
    {
        foreach (var k in keywords)
            if (!string.IsNullOrWhiteSpace(k) && text.Contains(k, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
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
                Preview: Preview(text)));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error building message alert.");
        }
    }

    /// <summary>Strip Slack's angle-bracket tokens (mentions/links/channels) and clamp length.</summary>
    private static string Preview(string text)
    {
        text = Regex.Replace(text, "<[^>]*>", string.Empty).Replace('\n', ' ').Trim();
        return text.Length > 140 ? text[..140] + "…" : text;
    }

    private async Task KeepAliveAsync(ClientWebSocket ws, CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(PingInterval);
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

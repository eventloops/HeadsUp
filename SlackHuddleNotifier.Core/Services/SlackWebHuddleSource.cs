using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SlackHuddleNotifier.Configuration;
using SlackHuddleNotifier.Models;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace SlackHuddleNotifier.Services;

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
public sealed class SlackWebHuddleSource : BackgroundService, IHuddleEventSource
{
    /// <summary>Cooldown before a new huddle in the same channel fires another notification.</summary>
    private static readonly TimeSpan DedupeCooldown = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(10);

    private readonly SlackClientOptions _clientOptions;
    private readonly SlackOptions _slackOptions;
    private readonly ILogger<SlackWebHuddleSource> _logger;

    // Channel ID -> last time we notified for it, to avoid duplicate events.
    private readonly ConcurrentDictionary<string, DateTimeOffset> _recentHuddles = new();

    private SlackWebClient? _client;
    private string? _teamId;
    private string? _selfUserId;
    private int _messageId;

    public event EventHandler<HuddleEvent>? HuddleStarted;

    public SlackWebHuddleSource(
        IOptions<SlackClientOptions> clientOptions,
        IOptions<SlackOptions> slackOptions,
        ILogger<SlackWebHuddleSource> logger)
    {
        _clientOptions = clientOptions.Value;
        _slackOptions = slackOptions.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(_clientOptions.ClientToken) ||
            string.IsNullOrWhiteSpace(_clientOptions.Cookie))
        {
            _logger.LogWarning(
                "SlackClient credentials are not configured. Set SlackClient:ClientToken " +
                "(xoxc-...) and SlackClient:Cookie (the 'd' cookie) in appsettings.json and " +
                "restart. See the README for how to extract them.");
            return;
        }

        _client = new SlackWebClient(
            _clientOptions.ClientToken, _clientOptions.Cookie, _clientOptions.WorkspaceUrl);

        // auth.test doubles as a credential check and gives us the team/self ids.
        try
        {
            var auth = await _client.AuthTestAsync(stoppingToken);
            if (!auth.Bool("ok"))
            {
                _logger.LogError(
                    "Slack auth.test failed: {Error}. The session is likely expired or the token/" +
                    "cookie is wrong - re-extract both from a logged-in Slack session.",
                    auth.Str("error") ?? "unknown");
                return;
            }

            _teamId = auth.Str("team_id");
            _selfUserId = auth.Str("user_id");
            _logger.LogInformation(
                "Slack client session OK: authenticated as {User} in team {Team}.",
                auth.Str("user"), auth.Str("team"));
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Slack auth.test call failed.");
            return;
        }

        // Connect / reconnect loop: rtm.connect hands back a fresh URL each time.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunConnectionAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Slack client connection ended; retrying in {Delay}s.",
                    ReconnectDelay.TotalSeconds);
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
        if (subtype is not ("huddle_thread" or "sh_room_created"))
            return;

        var channel = root.Str("channel");

        // A real huddle_thread message is authored by USLACKBOT, so the top-level
        // "user" is never the initiator - the actual starter is room.created_by.
        // Reading "user" here mislabels every huddle as Slackbot and defeats the
        // self-filter (own huddles would still pop). Fall back to the top-level
        // user only for other/legacy shapes that carry no room object.
        var user = root.Obj("room") is { } room ? room.Str("created_by") : null;
        user ??= root.Str("user");

        // Logged before any self/dedupe filtering so a huddle you start yourself
        // still confirms the signal works, even though it won't raise a popup.
        _logger.LogInformation(
            "Huddle signal received over client API: user={User} channel={Channel} subtype={Subtype}.",
            user ?? "?", channel ?? "?", subtype);

        // Resolving names/channel hits the API; don't block the read loop on it.
        _ = RaiseHuddleEventAsync(user, channel);
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

    private async Task RaiseHuddleEventAsync(string? userId, string? channelId)
    {
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(channelId))
            return;

        // No popup for huddles you started yourself.
        if (userId.Equals(_selfUserId, StringComparison.OrdinalIgnoreCase))
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
            var isPriority = _slackOptions.PriorityUsers.Contains(userId, StringComparer.OrdinalIgnoreCase);

            // With a team id Slack can deep-link straight into the huddle;
            // otherwise fall back to opening the channel.
            var huddleLink = string.IsNullOrEmpty(_teamId)
                ? $"slack://channel?id={channelId}"
                : $"https://app.slack.com/huddle/{_teamId}/{channelId}";

            var huddleEvent = new HuddleEvent(
                InitiatorUserId: userId,
                InitiatorDisplayName: displayName,
                InitiatorAvatarUrl: avatarUrl,
                ChannelId: channelId,
                ChannelName: channelName,
                IsDirectMessage: isDm,
                HuddleLink: huddleLink,
                IsPriorityUser: isPriority,
                OccurredAt: now);

            _logger.LogInformation(
                "Huddle started by {User} in {Channel}{Priority}.",
                displayName, channelName, isPriority ? " [PRIORITY]" : string.Empty);

            HuddleStarted?.Invoke(this, huddleEvent);
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

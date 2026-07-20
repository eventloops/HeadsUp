using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using HeadsUp.Configuration;
using HeadsUp.Models;
using SlackNet;
using SlackNet.SocketMode;
using System.Text.Json;
using System.Collections.Concurrent;

namespace HeadsUp.Services;

/// <summary>
/// Connects to Slack via Socket Mode and raises <see cref="AlertRaised"/> whenever a huddle
/// start is detected. Slack surfaces huddle starts as channel messages with subtype
/// "huddle_thread" (and legacy calls as "sh_room_created").
///
/// This is the per-user Slack app path, used when there is no relay — each user creates their
/// own app, because Socket Mode's app-level token cannot be shared across machines.
/// Credentials arrive from <see cref="ISlackAppCredentialProvider"/> rather than config,
/// because under OAuth they don't exist until the user signs in and they rotate afterwards.
///
/// It consumes <b>raw</b> frames rather than SlackNet's typed dispatch. That isn't a
/// preference: a huddle's initiator lives only in <c>room.created_by</c>, and SlackNet's
/// <c>MessageEvent</c> has no <c>room</c> property — so via the typed API every huddle is
/// attributed to USLACKBOT, which also silently defeats priority-user matching and the
/// suppression of your own huddles.
/// </summary>
public sealed class SlackHuddleMonitor : BackgroundService, IAlertSource
{
    /// <summary>Cooldown before a new huddle in the same channel fires another notification.</summary>
    private static readonly TimeSpan DedupeCooldown = TimeSpan.FromMinutes(5);

    // Internal and instance-scoped so tests can shrink the waits.
    internal TimeSpan _reconnectDelay = TimeSpan.FromSeconds(10);
    internal TimeSpan _connectionPollInterval = TimeSpan.FromSeconds(15);

    private readonly ISlackAppCredentialProvider _credentials;
    private readonly ISlackClientFactory _clientFactory;
    private readonly IOptionsMonitor<DetectionOptions> _detectionOptions;
    private readonly IOptionsMonitor<AlertOptions> _alertOptions;
    private readonly IOptionsMonitor<PriorityOptions> _priorityOptions;
    private readonly AppStatus _status;
    private readonly ILogger<SlackHuddleMonitor> _logger;

    // Channel ID -> last time we notified for it, to avoid duplicate events.
    private readonly ConcurrentDictionary<string, DateTimeOffset> _recentHuddles = new();

    // Bound once a connection is established; null until then, so event handling no-ops.
    private ISlackApiClient? _apiClient;
    // What _apiClient was built from, so a renewed token can be noticed without rebuilding blindly.
    private string? _boundUserToken;
    internal string? _teamId;
    internal string? _selfUserId;

    public SourceId SourceId => SlackSource.SourceId;
    public string SourceName => SlackSource.Name;

    /// <summary>This source's own status entry (created on first use if not pre-seeded).</summary>
    private SourceStatus Status => _status.GetOrAdd(SourceId, SourceName);

    public event EventHandler<Alert>? AlertRaised;

    public SlackHuddleMonitor(
        ISlackAppCredentialProvider credentials,
        ISlackClientFactory clientFactory,
        IOptionsMonitor<DetectionOptions> detectionOptions,
        IOptionsMonitor<AlertOptions> alertOptions,
        IOptionsMonitor<PriorityOptions> priorityOptions,
        AppStatus status,
        ILogger<SlackHuddleMonitor> logger)
    {
        _credentials = credentials;
        _clientFactory = clientFactory;
        _detectionOptions = detectionOptions;
        _alertOptions = alertOptions;
        _priorityOptions = priorityOptions;
        _status = status;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Status.Mode = DetectionOptions.SlackApi;
        Status.CredentialSource = _credentials.SourceDescription;

        // Outer loop: (re)acquire credentials, connect, then hold the connection until it
        // drops. A missing or rejected credential forces a refresh next round, which is how
        // OAuth token rotation is absorbed when a live provider is in play.
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
                // Slack rejecting our token must force a renewal next round; anything else is
                // transient and should reuse the credentials we have.
                refreshNext = IsAuthRejection(ex);

                Status.SetConnection(false, refreshNext ? "Reauthenticating…" : "Reconnecting…");
                _logger.LogWarning(ex, "Slack Socket Mode connection ended; retrying in {Delay}s.",
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
    /// True when Slack refused our credentials rather than the connection merely failing.
    /// SlackNet surfaces these as auth errors on connect or on the first API call.
    /// </summary>
    internal static bool IsAuthRejection(Exception ex) =>
        ex is SlackException slack &&
            (slack.Message.Contains("invalid_auth", StringComparison.OrdinalIgnoreCase) ||
             slack.Message.Contains("token_expired", StringComparison.OrdinalIgnoreCase) ||
             slack.Message.Contains("token_revoked", StringComparison.OrdinalIgnoreCase) ||
             slack.Message.Contains("not_authed", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Fetches credentials, opens one Socket Mode connection, and holds it until it drops.
    /// Returns <c>false</c> when credentials are missing (caller should force a refresh next
    /// round); <c>true</c> after a connection ran and ended of its own accord.
    /// </summary>
    internal async Task<bool> ConnectAndRunAsync(bool forceRefresh, CancellationToken stoppingToken)
    {
        var creds = await _credentials.GetCredentialsAsync(forceRefresh, stoppingToken);
        if (creds is null)
        {
            Status.SetConnection(false, "Not signed in");

            // A Warning naming the fix, because in config mode there is no sign-in flow to
            // prompt: the operator's only route is pasting tokens, and a Debug line they never
            // see leaves the app looking merely quiet.
            _logger.LogWarning(
                "No Slack app credentials available. Sign in, or set Slack:AppToken (xapp-…) " +
                "and Slack:UserToken (xoxp-…) in appsettings for headless use.");
            return false;
        }

        _apiClient = _clientFactory.CreateApiClient(creds);
        _boundUserToken = creds.UserToken;
        var socket = _clientFactory.CreateSocketClient(creds);

        // Raw frames rather than SlackNet's typed dispatch: the typed MessageEvent has no
        // room property, and a huddle's initiator lives only in room.created_by — so through
        // the typed API every huddle would be attributed to Slackbot.
        using var subscription = socket.RawSocketMessages.Subscribe(
            raw => _ = HandleFrameAsync(socket, raw));

        await socket.Connect(cancellationToken: stoppingToken);
        _logger.LogInformation("Connected to Slack via Socket Mode.");
        Status.SetConnection(true, "Watching for huddles");

        try
        {
            var auth = await _apiClient.Auth.Test();
            _teamId = auth.TeamId;
            _selfUserId = auth.UserId;
            Status.SetAccount(auth.User, auth.Team);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "auth.test failed; huddle links will omit the team id.");
        }

        try
        {
            // SlackNet owns the socket's own retry logic, so this loop notices a connection
            // that has genuinely gone away — and renews the Web API token in place. The user
            // token rotates roughly every 12 hours while the app-level token never expires, so
            // a healthy connection outlives its API credentials; without this the enrichment
            // calls quietly start failing and the membership gate, which fails open on error,
            // would begin admitting huddles from channels the user isn't in.
            while (socket.Connected && !stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(_connectionPollInterval, stoppingToken);
                await RenewApiClientAsync(stoppingToken);
            }
        }
        finally
        {
            // In a finally because shutdown cancels the delay above: without it, quitting with
            // a live connection left the socket open and its envelopes unacknowledged, so Slack
            // redelivered them on the next launch as stale duplicate alerts.
            socket.Disconnect();
            Status.SetConnection(false, "Disconnected");
            _logger.LogInformation("Disconnected from Slack.");
        }

        return true;
    }

    /// <summary>
    /// Rebinds the Web API client when the user token has been renewed underneath us. Cheap and
    /// silent in the common case: the provider returns the cached token until it nears expiry.
    /// </summary>
    private async Task RenewApiClientAsync(CancellationToken ct)
    {
        try
        {
            var current = await _credentials.GetExistingCredentialsAsync(ct);
            if (current is null || current.UserToken == _boundUserToken)
                return;

            _apiClient = _clientFactory.CreateApiClient(current);
            _boundUserToken = current.UserToken;
            _logger.LogDebug("Rebound the Slack API client to a renewed user token.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Keep the existing client; the next poll tries again.
            _logger.LogDebug(ex, "Could not renew the Slack API credentials this round.");
        }
    }

    // ------------------------------------------------------------------
    // Frame handling
    // ------------------------------------------------------------------

    /// <summary>Acknowledges a frame and handles its event. Slack redelivers unacknowledged envelopes.</summary>
    private async Task HandleFrameAsync(ICoreSocketModeClient socket, RawSocketMessage raw)
    {
        try
        {
            var envelope = SlackEnvelopeParser.Parse(raw.Message);

            if (envelope.EnvelopeId is { } envelopeId)
                await socket.Send(raw.SocketId, new Acknowledgement { EnvelopeId = envelopeId });

            if (envelope.EventJson is { } eventJson)
                await HandleEventAsync(eventJson);
        }
        catch (Exception ex)
        {
            // One bad frame must not end the connection for everything after it.
            _logger.LogWarning(ex, "Could not handle a Socket Mode frame.");
        }
    }

    /// <summary>Classifies one Slack event. Internal so tests can feed events without a socket.</summary>
    internal async Task HandleEventAsync(string eventJson)
    {
        // Events can only arrive over a live connection, which is also what binds the API
        // client — but capture it once so a concurrent reconnect can't null it mid-handle.
        if (_apiClient is not { } api)
            return;

        using var document = JsonDocument.Parse(eventJson);
        var root = document.RootElement;

        if (root.Str("type") != "message")
            return;

        var alerts = _alertOptions.CurrentValue;
        var prefs = alerts.ToPreferences();

        if (SlackHuddleParser.Parse(root) is { } huddle)
        {
            // An ended huddle arrives wrapped in message_changed/message_deleted; alerting on
            // those would fire once the huddle is already over.
            if (!alerts.Huddles || huddle.HasEnded)
                return;

            if (AlertPolicy.IsChannelMuted(new ChannelId(huddle.ChannelId ?? string.Empty), SourceId, prefs.Muted))
                return;

            _logger.LogDebug("Huddle detected. Channel={Channel} Initiator={User}",
                huddle.ChannelId, huddle.InitiatorUserId);

            await RaiseAlertAsync(api, huddle.InitiatorUserId, huddle.ChannelId, huddle.HuddleLink);
            return;
        }

        // Only plain user messages are candidates for mention/DM/keyword alerts — skip edits,
        // deletes, joins and bot posts.
        if (root.Str("subtype") is null)
            await HandleMessageAsync(api, root, prefs);
    }

    private async Task HandleMessageAsync(ISlackApiClient api, JsonElement message, AlertPreferences prefs)
    {
        var userId = message.Str("user");
        var channelId = message.Str("channel");
        var text = message.Str("text") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(channelId))
            return;

        // Never alert on your own messages.
        if (userId.Equals(_selfUserId, StringComparison.OrdinalIgnoreCase))
            return;

        var isMention = SlackMessageParser.IsMention(text, _selfUserId);
        var isDm = SlackMessageParser.IsDirectMessage(channelId);
        if (AlertPolicy.Classify(isMention, isDm, text, new ChannelId(channelId), SourceId, prefs) is not { } kind)
            return;

        _logger.LogInformation("{Kind} alert: user={User} channel={Channel}.", kind, userId, channelId);

        try
        {
            var (displayName, avatarUrl) = await ResolveUserAsync(api, userId);

            var channelName = channelId;
            if (!isDm)
            {
                try
                {
                    var convInfo = await api.Conversations.Info(channelId);
                    channelName = FirstNonEmpty(convInfo?.Name) ?? channelId;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not resolve channel {Channel}.", channelId);
                }
            }

            var isPriority = _priorityOptions.CurrentValue.IsPriority(SourceId, new UserId(userId));
            var link = string.IsNullOrEmpty(_teamId)
                ? $"slack://channel?id={channelId}"
                : $"https://app.slack.com/client/{_teamId}/{channelId}";

            AlertRaised?.Invoke(this, new Alert(
                InitiatorUserId: new UserId(userId),
                InitiatorDisplayName: displayName,
                InitiatorAvatarUrl: avatarUrl,
                ChannelId: new ChannelId(channelId),
                ChannelName: channelName,
                IsDirectMessage: isDm,
                Link: link,
                IsPriorityUser: isPriority,
                OccurredAt: DateTimeOffset.Now,
                SourceId: SourceId,
                SourceName: SourceName,
                Kind: kind,
                Preview: SlackMessageParser.Preview(text)));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error building message alert.");
        }
    }

    private async Task RaiseAlertAsync(
        ISlackApiClient api, string? userId, string? channelId, string? huddleLink = null)
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
            // With a user token, events should only arrive for conversations the
            // user is in, but this keeps a rogue/misconfigured app from notifying
            // about channels they aren't a member of. DMs and group DMs are
            // theirs by definition; fail open if the API call errors.
            string channelName = channelId;
            bool isDm = SlackMessageParser.IsDirectMessage(channelId);
            try
            {
                var convInfo = await api.Conversations.Info(channelId);
                if (convInfo is not null)
                {
                    if (!convInfo.IsIm && !convInfo.IsMpim && !convInfo.IsMember)
                    {
                        _logger.LogDebug(
                            "Ignoring huddle in {Channel}: user is not a member.", channelId);
                        return;
                    }

                    isDm = convInfo.IsIm;
                    channelName = FirstNonEmpty(convInfo.Name) ?? channelId;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not resolve channel info for {ChannelId}.", channelId);
            }

            var (displayName, avatarUrl) = await ResolveUserAsync(api, userId);
            var isPriority = _priorityOptions.CurrentValue.IsPriority(SourceId, new UserId(userId));

            // Slack supplies its own huddle link in room.huddle_link; prefer it over one we
            // assemble. Fall back to constructing one, then to just opening the channel.
            huddleLink ??= string.IsNullOrEmpty(_teamId)
                ? $"slack://channel?id={channelId}"
                : $"https://app.slack.com/huddle/{_teamId}/{channelId}";

            var huddleEvent = new Alert(
                InitiatorUserId: new UserId(userId),
                InitiatorDisplayName: displayName,
                InitiatorAvatarUrl: avatarUrl,
                ChannelId: new ChannelId(channelId),
                ChannelName: channelName,
                IsDirectMessage: isDm,
                Link: huddleLink,
                IsPriorityUser: isPriority,
                OccurredAt: now,
                SourceId: SourceId,
                SourceName: SourceName);

            _logger.LogInformation(
                "Huddle started by {User} in {Channel}{Priority}.",
                displayName, channelName,
                isPriority ? " [PRIORITY]" : string.Empty);

            AlertRaised?.Invoke(this, huddleEvent);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error while building huddle event.");
        }
    }

    private async Task<(string DisplayName, string? AvatarUrl)> ResolveUserAsync(ISlackApiClient api, string userId)
    {
        string displayName = userId;
        string? avatarUrl = null;

        try
        {
            var userInfo = await api.Users.Info(userId);
            displayName = FirstNonEmpty(
                userInfo?.Profile?.DisplayName,
                userInfo?.Profile?.RealName,
                userInfo?.Name) ?? userId;
            avatarUrl = FirstNonEmpty(
                userInfo?.Profile?.Image72,
                userInfo?.Profile?.Image192);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not resolve user name for {UserId}.", userId);
        }

        return (displayName, avatarUrl);
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}

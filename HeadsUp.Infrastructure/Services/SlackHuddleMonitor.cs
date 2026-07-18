using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using HeadsUp.Configuration;
using HeadsUp.Models;
using SlackNet;
using SlackNet.Events;
using System.Collections.Concurrent;

namespace HeadsUp.Services;

/// <summary>
/// Connects to Slack via Socket Mode and raises <see cref="AlertRaised"/> whenever
/// a huddle start is detected. Slack surfaces huddle starts as channel messages with
/// subtype "huddle_thread" (and legacy calls as "sh_room_created"), so this class registers
/// itself with SlackNet as an <see cref="IEventHandler{MessageEvent}"/>.
///
/// This is the <b>sanctioned</b> path: a real Slack app, its own app-level token, and a user
/// OAuth token. Credentials arrive from <see cref="ISlackAppCredentialProvider"/> rather than
/// being read from config directly, because under OAuth they don't exist until the user signs
/// in and they rotate afterwards — so the clients are built per connection attempt.
/// </summary>
public sealed class SlackHuddleMonitor : BackgroundService, IAlertSource, IEventHandler<MessageEvent>
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
                Status.SetConnection(false, "Reconnecting…");
                _logger.LogWarning(ex, "Slack Socket Mode connection ended; retrying in {Delay}s.",
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
            _logger.LogDebug("No Slack app credentials available yet; waiting.");
            return false;
        }

        var clients = _clientFactory.Create(creds, this);
        _apiClient = clients.Api;
        var socket = clients.Socket;

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

        // SlackNet owns the socket's own retry logic, so all this loop does is notice a
        // connection that has genuinely gone away and let the outer loop rebuild it with
        // freshly-fetched (possibly rotated) credentials.
        while (socket.Connected && !stoppingToken.IsCancellationRequested)
            await Task.Delay(_connectionPollInterval, stoppingToken);

        socket.Disconnect();
        Status.SetConnection(false, "Disconnected");
        _logger.LogInformation("Disconnected from Slack.");
        return true;
    }

    // ------------------------------------------------------------------
    // SlackNet event handler
    // ------------------------------------------------------------------

    public async Task Handle(MessageEvent slackEvent)
    {
        // Events can only arrive over a live connection, which is also what binds the API
        // client — but capture it once so a concurrent reconnect can't null it mid-handle.
        if (_apiClient is not { } api)
            return;

        var alerts = _alertOptions.CurrentValue;
        var prefs = alerts.ToPreferences();

        // Huddle starts surface as a channel message with subtype "huddle_thread";
        // legacy Slack Calls use "sh_room_created".
        if (SlackMessageParser.IsHuddleSubtype(slackEvent.Subtype))
        {
            if (!alerts.Huddles ||
                AlertPolicy.IsChannelMuted(new ChannelId(slackEvent.Channel ?? string.Empty), SourceId, prefs.Muted))
                return;

            _logger.LogDebug("Huddle-related message detected. Channel={Channel} Subtype={Subtype}",
                slackEvent.Channel, slackEvent.Subtype);

            await RaiseAlertAsync(api, slackEvent.User, slackEvent.Channel);
            return;
        }

        // Only plain user messages (no subtype) are candidates for mention/DM/keyword
        // alerts - skip edits, deletes, joins, bot posts, etc.
        if (slackEvent.Subtype is null)
            await HandleMessageAsync(api, slackEvent, prefs);
    }

    private async Task HandleMessageAsync(ISlackApiClient api, MessageEvent slackEvent, AlertPreferences prefs)
    {
        var userId = slackEvent.User;
        var channelId = slackEvent.Channel;
        var text = slackEvent.Text ?? string.Empty;
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

    private async Task RaiseAlertAsync(ISlackApiClient api, string? userId, string? channelId)
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

            // With a team id Slack can deep-link straight into the huddle;
            // otherwise fall back to opening the channel.
            var huddleLink = string.IsNullOrEmpty(_teamId)
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

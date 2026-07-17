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
/// subtype "huddle_thread" (and legacy calls as "sh_room_created"), so this class is
/// registered with SlackNet as an <see cref="IEventHandler{MessageEvent}"/>.
/// </summary>
public sealed class SlackHuddleMonitor : IAlertSource, IEventHandler<MessageEvent>, IHostedService
{
    /// <summary>Cooldown before a new huddle in the same channel fires another notification.</summary>
    private static readonly TimeSpan DedupeCooldown = TimeSpan.FromMinutes(5);

    private readonly ISlackSocketModeClient _socketClient;
    private readonly ISlackApiClient _apiClient;
    private readonly IOptionsMonitor<SlackOptions> _slackOptions;
    private readonly IOptionsMonitor<DetectionOptions> _detectionOptions;
    private readonly IOptionsMonitor<AlertOptions> _alertOptions;
    private readonly IOptionsMonitor<PriorityOptions> _priorityOptions;
    private readonly AppStatus _status;
    private readonly ILogger<SlackHuddleMonitor> _logger;

    // Channel ID -> last time we notified for it, to avoid duplicate events.
    private readonly ConcurrentDictionary<string, DateTimeOffset> _recentHuddles = new();

    private string? _teamId;
    private string? _selfUserId;

    public string SourceId => SlackSource.Id;
    public string SourceName => SlackSource.Name;

    /// <summary>This source's own status entry (created on first use if not pre-seeded).</summary>
    private SourceStatus Status => _status.GetOrAdd(SourceId, SourceName);

    public event EventHandler<Alert>? AlertRaised;

    public SlackHuddleMonitor(
        ISlackSocketModeClient socketClient,
        ISlackApiClient apiClient,
        IOptionsMonitor<SlackOptions> slackOptions,
        IOptionsMonitor<DetectionOptions> detectionOptions,
        IOptionsMonitor<AlertOptions> alertOptions,
        IOptionsMonitor<PriorityOptions> priorityOptions,
        AppStatus status,
        ILogger<SlackHuddleMonitor> logger)
    {
        _socketClient = socketClient;
        _apiClient = apiClient;
        _slackOptions = slackOptions;
        _detectionOptions = detectionOptions;
        _alertOptions = alertOptions;
        _priorityOptions = priorityOptions;
        _status = status;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        Status.Mode = DetectionOptions.SlackApi;
        Status.CredentialSource = "appsettings (Slack app tokens)";

        var slack = _slackOptions.CurrentValue;
        if (string.IsNullOrWhiteSpace(slack.AppToken) ||
            string.IsNullOrWhiteSpace(slack.UserToken))
        {
            Status.SetConnection(false, "Slack tokens not configured");
            _logger.LogWarning(
                "Slack tokens are not configured. Edit appsettings.json and restart. " +
                "AppToken (xapp-...) and UserToken (xoxp-...) are both required.");
            return;
        }

        await _socketClient.Connect(cancellationToken: cancellationToken);
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
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (_socketClient.Connected)
        {
            _socketClient.Disconnect();
            Status.SetConnection(false, "Disconnected");
            _logger.LogInformation("Disconnected from Slack.");
        }

        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------
    // SlackNet event handler
    // ------------------------------------------------------------------

    public async Task Handle(MessageEvent slackEvent)
    {
        var alerts = _alertOptions.CurrentValue;

        // Huddle starts surface as a channel message with subtype "huddle_thread";
        // legacy Slack Calls use "sh_room_created".
        if (AlertClassifier.IsHuddleSubtype(slackEvent.Subtype))
        {
            if (!alerts.Huddles || AlertClassifier.IsChannelMuted(slackEvent.Channel ?? string.Empty, SourceId, alerts))
                return;

            _logger.LogDebug("Huddle-related message detected. Channel={Channel} Subtype={Subtype}",
                slackEvent.Channel, slackEvent.Subtype);

            await RaiseAlertAsync(slackEvent.User, slackEvent.Channel);
            return;
        }

        // Only plain user messages (no subtype) are candidates for mention/DM/keyword
        // alerts - skip edits, deletes, joins, bot posts, etc.
        if (slackEvent.Subtype is null)
            await HandleMessageAsync(slackEvent, alerts);
    }

    private async Task HandleMessageAsync(MessageEvent slackEvent, AlertOptions alerts)
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
        if (AlertClassifier.Classify(isMention, isDm, text, channelId, SourceId, alerts) is not { } kind)
            return;

        _logger.LogInformation("{Kind} alert: user={User} channel={Channel}.", kind, userId, channelId);

        try
        {
            var (displayName, avatarUrl) = await ResolveUserAsync(userId);

            var channelName = channelId;
            if (!isDm)
            {
                try
                {
                    var convInfo = await _apiClient.Conversations.Info(channelId);
                    channelName = FirstNonEmpty(convInfo?.Name) ?? channelId;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not resolve channel {Channel}.", channelId);
                }
            }

            var isPriority = _priorityOptions.CurrentValue.IsPriority(SourceId, userId);
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
                Preview: SlackMessageParser.Preview(text)));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error building message alert.");
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
            // With a user token, events should only arrive for conversations the
            // user is in, but this keeps a rogue/misconfigured app from notifying
            // about channels they aren't a member of. DMs and group DMs are
            // theirs by definition; fail open if the API call errors.
            string channelName = channelId;
            bool isDm = SlackMessageParser.IsDirectMessage(channelId);
            try
            {
                var convInfo = await _apiClient.Conversations.Info(channelId);
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

            var (displayName, avatarUrl) = await ResolveUserAsync(userId);
            var isPriority = _priorityOptions.CurrentValue.IsPriority(SourceId, userId);

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
                displayName, channelName,
                isPriority ? " [PRIORITY]" : string.Empty);

            AlertRaised?.Invoke(this, huddleEvent);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error while building huddle event.");
        }
    }

    private async Task<(string DisplayName, string? AvatarUrl)> ResolveUserAsync(string userId)
    {
        string displayName = userId;
        string? avatarUrl = null;

        try
        {
            var userInfo = await _apiClient.Users.Info(userId);
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

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SlackHuddleNotifier.Configuration;
using SlackHuddleNotifier.Models;
using SlackNet;
using SlackNet.Events;
using System.Collections.Concurrent;

namespace SlackHuddleNotifier.Services;

/// <summary>
/// Connects to Slack via Socket Mode and raises <see cref="HuddleStarted"/> whenever
/// a huddle start is detected. Slack surfaces huddle starts as channel messages with
/// subtype "huddle_thread" (and legacy calls as "sh_room_created"), so this class is
/// registered with SlackNet as an <see cref="IEventHandler{MessageEvent}"/>.
/// </summary>
public sealed class SlackHuddleMonitor : IHuddleEventSource, IEventHandler<MessageEvent>, IHostedService
{
    /// <summary>Cooldown before a new huddle in the same channel fires another notification.</summary>
    private static readonly TimeSpan DedupeCooldown = TimeSpan.FromMinutes(5);

    private readonly ISlackSocketModeClient _socketClient;
    private readonly ISlackApiClient _apiClient;
    private readonly IOptionsMonitor<SlackOptions> _slackOptions;
    private readonly ILogger<SlackHuddleMonitor> _logger;

    // Channel ID -> last time we notified for it, to avoid duplicate events.
    private readonly ConcurrentDictionary<string, DateTimeOffset> _recentHuddles = new();

    private string? _teamId;
    private string? _selfUserId;

    public event EventHandler<HuddleEvent>? HuddleStarted;

    public SlackHuddleMonitor(
        ISlackSocketModeClient socketClient,
        ISlackApiClient apiClient,
        IOptionsMonitor<SlackOptions> slackOptions,
        ILogger<SlackHuddleMonitor> logger)
    {
        _socketClient = socketClient;
        _apiClient = apiClient;
        _slackOptions = slackOptions;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var slack = _slackOptions.CurrentValue;
        if (string.IsNullOrWhiteSpace(slack.AppToken) ||
            string.IsNullOrWhiteSpace(slack.UserToken))
        {
            _logger.LogWarning(
                "Slack tokens are not configured. Edit appsettings.json and restart. " +
                "AppToken (xapp-...) and UserToken (xoxp-...) are both required.");
            return;
        }

        await _socketClient.Connect(cancellationToken: cancellationToken);
        _logger.LogInformation("Connected to Slack via Socket Mode.");

        try
        {
            var auth = await _apiClient.Auth.Test();
            _teamId = auth.TeamId;
            _selfUserId = auth.UserId;
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
            _logger.LogInformation("Disconnected from Slack.");
        }

        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------
    // SlackNet event handler
    // ------------------------------------------------------------------

    public async Task Handle(MessageEvent slackEvent)
    {
        // Huddle starts surface as a channel message with subtype "huddle_thread";
        // legacy Slack Calls use "sh_room_created".
        if (slackEvent.Subtype is not ("huddle_thread" or "sh_room_created"))
            return;

        _logger.LogDebug("Huddle-related message detected. Channel={Channel} Subtype={Subtype}",
            slackEvent.Channel, slackEvent.Subtype);

        await RaiseHuddleEventAsync(slackEvent.User, slackEvent.Channel);
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
            // With a user token, events should only arrive for conversations the
            // user is in, but this keeps a rogue/misconfigured app from notifying
            // about channels they aren't a member of. DMs and group DMs are
            // theirs by definition; fail open if the API call errors.
            string channelName = channelId;
            bool isDm = channelId.StartsWith('D');
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
            var isPriority = _slackOptions.CurrentValue.PriorityUsers.Contains(userId, StringComparer.OrdinalIgnoreCase);

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
                displayName, channelName,
                isPriority ? " [PRIORITY]" : string.Empty);

            HuddleStarted?.Invoke(this, huddleEvent);
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

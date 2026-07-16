using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SlackHuddleNotifier.Configuration;
using SlackHuddleNotifier.Models;
using SlackNet;
using SlackNet.Events;

namespace SlackHuddleNotifier.Services;

/// <summary>
/// Connects to Slack via Socket Mode and raises <see cref="HuddleStarted"/> whenever
/// a huddle_call_started (or huddle channel message) is detected.
/// </summary>
public sealed class SlackHuddleMonitor : IHuddleEventSource, IHostedService, IAsyncDisposable
{
    private readonly ISlackSocketModeClient _socketClient;
    private readonly ISlackApiClient _apiClient;
    private readonly SlackOptions _slackOptions;
    private readonly ILogger<SlackHuddleMonitor> _logger;

    // Track channels that already have an active huddle to avoid duplicate events.
    private readonly HashSet<string> _activeHuddleChannels = [];
    private readonly SemaphoreSlim _lock = new(1, 1);

    public event EventHandler<HuddleEvent>? HuddleStarted;

    public SlackHuddleMonitor(
        ISlackSocketModeClient socketClient,
        ISlackApiClient apiClient,
        IOptions<SlackOptions> slackOptions,
        ILogger<SlackHuddleMonitor> logger)
    {
        _socketClient = socketClient;
        _apiClient = apiClient;
        _slackOptions = slackOptions.Value;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_slackOptions.AppToken) ||
            string.IsNullOrWhiteSpace(_slackOptions.BotToken))
        {
            _logger.LogWarning(
                "Slack tokens are not configured. Edit appsettings.json and restart. " +
                "AppToken (xapp-...) and BotToken (xoxb-...) are both required.");
            return;
        }

        _socketClient.On<MessageEvent>(HandleMessageEventAsync);
        _socketClient.On<HuddleCallStartedEvent>(HandleHuddleCallStartedAsync);

        await _socketClient.Connect(cancellationToken);
        _logger.LogInformation("Connected to Slack via Socket Mode.");
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _socketClient.Disconnect();
        _logger.LogInformation("Disconnected from Slack.");
    }

    // ------------------------------------------------------------------
    // Event handlers
    // ------------------------------------------------------------------

    private async Task HandleHuddleCallStartedAsync(HuddleCallStartedEvent e)
    {
        // HuddleCallStartedEvent is a dedicated event type from SlackNet (if available).
        _logger.LogDebug("HuddleCallStarted event received. Channel={Channel}", e.ChannelId);
        await RaiseHuddleEventAsync(e.InitiatorUserId, e.ChannelId);
    }

    private async Task HandleMessageEventAsync(MessageEvent msg)
    {
        // Huddle start also surfaces as a message with subtype "huddle_thread"
        // or as a message whose thread_ts matches a huddle channel thread.
        // We inspect the raw payload for the "huddle" property as a fallback.
        if (msg.Subtype is "huddle_thread" or "sh_room_created")
        {
            _logger.LogDebug("Huddle-related message detected. Channel={Channel} Subtype={Subtype}",
                msg.Channel, msg.Subtype);
            await RaiseHuddleEventAsync(msg.User, msg.Channel);
        }
    }

    private async Task RaiseHuddleEventAsync(string? userId, string? channelId)
    {
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(channelId))
            return;

        await _lock.WaitAsync();
        try
        {
            if (!_activeHuddleChannels.Add(channelId))
            {
                _logger.LogDebug("Huddle already active in channel {Channel}; skipping.", channelId);
                return;
            }
        }
        finally
        {
            _lock.Release();
        }

        try
        {
            var (displayName, channelName) = await ResolveNamesAsync(userId, channelId);
            var isPriority = _slackOptions.PriorityUsers.Contains(userId, StringComparer.OrdinalIgnoreCase);
            var huddleLink = $"slack://channel?team=&id={channelId}";

            var huddleEvent = new HuddleEvent(
                InitiatorUserId: userId,
                InitiatorDisplayName: displayName,
                ChannelId: channelId,
                ChannelName: channelName,
                HuddleLink: huddleLink,
                IsPriorityUser: isPriority,
                OccurredAt: DateTimeOffset.Now);

            _logger.LogInformation(
                "Huddle started by {User} in {Channel}{Priority}.",
                displayName, channelName,
                isPriority ? " [PRIORITY]" : string.Empty);

            HuddleStarted?.Invoke(this, huddleEvent);

            // Remove the channel from the active set after a cooldown so that
            // a second huddle in the same channel later will fire again.
            _ = Task.Delay(TimeSpan.FromMinutes(5))
                    .ContinueWith(_ =>
                    {
                        _lock.Wait();
                        try { _activeHuddleChannels.Remove(channelId); }
                        finally { _lock.Release(); }
                    });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error while building huddle event.");
        }
    }

    private async Task<(string DisplayName, string ChannelName)> ResolveNamesAsync(
        string userId, string channelId)
    {
        string displayName = userId;
        string channelName = channelId;

        try
        {
            var userInfo = await _apiClient.Users.Info(userId);
            displayName = userInfo?.Profile?.DisplayName
                          ?? userInfo?.Profile?.RealName
                          ?? userId;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not resolve user name for {UserId}.", userId);
        }

        try
        {
            var convInfo = await _apiClient.Conversations.Info(channelId);
            channelName = convInfo?.Name ?? channelId;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not resolve channel name for {ChannelId}.", channelId);
        }

        return (displayName, channelName);
    }

    public async ValueTask DisposeAsync()
    {
        _lock.Dispose();
        if (_socketClient is IAsyncDisposable ad)
            await ad.DisposeAsync();
        else if (_socketClient is IDisposable d)
            d.Dispose();
    }
}

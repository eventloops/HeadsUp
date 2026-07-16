using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SlackHuddleNotifier.Configuration;
using SlackHuddleNotifier.Models;
using System.Text.RegularExpressions;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace SlackHuddleNotifier.Services;

/// <summary>
/// Detects huddles by watching the Windows toast notifications that the Slack
/// desktop app posts, instead of connecting to the Slack API. Zero Slack app
/// setup, and inherently limited to conversations the user is in - but it only
/// fires when Slack actually shows a notification (muted channels or paused
/// notifications produce nothing), and it parses display text rather than
/// structured data.
/// </summary>
public sealed partial class WindowsNotificationHuddleSource : BackgroundService, IHuddleEventSource
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    [GeneratedRegex(@"^(?<name>.+?)\s+(?:started|is inviting)", RegexOptions.IgnoreCase)]
    private static partial Regex InitiatorRegex();

    private readonly SlackOptions _slackOptions;
    private readonly ILogger<WindowsNotificationHuddleSource> _logger;
    private readonly HashSet<uint> _seenIds = [];

    public event EventHandler<HuddleEvent>? HuddleStarted;

    public WindowsNotificationHuddleSource(
        IOptions<SlackOptions> slackOptions,
        ILogger<WindowsNotificationHuddleSource> logger)
    {
        _slackOptions = slackOptions.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var listener = UserNotificationListener.Current;

        var access = await listener.RequestAccessAsync();
        if (access != UserNotificationListenerAccessStatus.Allowed)
        {
            _logger.LogWarning(
                "Notification access is {Status}. Enable it under Windows Settings > " +
                "Privacy & security > Notifications > 'Let apps access notifications', " +
                "then restart. Alternatively set Detection:Mode to SlackApi.", access);
            return;
        }

        _logger.LogInformation("Watching Slack's Windows notifications for huddles.");

        // Seed with whatever is already in the notification centre so old
        // toasts don't replay as fresh huddles on startup.
        foreach (var existing in await listener.GetNotificationsAsync(NotificationKinds.Toast))
            _seenIds.Add(existing.Id);

        using var timer = new PeriodicTimer(PollInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var notifications = await listener.GetNotificationsAsync(NotificationKinds.Toast);
                var currentIds = new HashSet<uint>(notifications.Select(n => n.Id));

                foreach (var notification in notifications)
                {
                    if (!_seenIds.Add(notification.Id))
                        continue;

                    HandleNotification(notification);
                }

                // Ids of dismissed toasts never come back; stop tracking them.
                _seenIds.RemoveWhere(id => !currentIds.Contains(id));
                _seenIds.UnionWith(currentIds);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to poll notifications.");
            }
        }
    }

    private void HandleNotification(UserNotification notification)
    {
        string appName;
        try
        {
            appName = notification.AppInfo?.DisplayInfo?.DisplayName ?? string.Empty;
        }
        catch
        {
            return; // some system notifications have no resolvable AppInfo
        }

        if (!appName.Contains("Slack", StringComparison.OrdinalIgnoreCase))
            return;

        var binding = notification.Notification?.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric);
        var texts = binding?.GetTextElements().Select(t => t.Text).Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
        if (texts is not { Count: > 0 })
            return;

        var title = texts[0];
        var body = string.Join(" ", texts.Skip(1));
        var combined = $"{title} {body}";

        if (!combined.Contains("huddle", StringComparison.OrdinalIgnoreCase))
            return;

        // Best-effort parsing: "Sarah started a huddle" / "Sarah is inviting you...".
        // In DM toasts the title is the sender's name; in channels it's "#channel".
        var isChannel = title.StartsWith('#');
        var match = InitiatorRegex().Match(body);
        var initiator = match.Success ? match.Groups["name"].Value.Trim()
                      : !isChannel    ? title
                      : "Someone";

        var isPriority = _slackOptions.PriorityUsers.Contains(initiator, StringComparer.OrdinalIgnoreCase);

        _logger.LogInformation(
            "Huddle toast detected: {Initiator} in {Where}{Priority}.",
            initiator, title, isPriority ? " [PRIORITY]" : string.Empty);

        HuddleStarted?.Invoke(this, new HuddleEvent(
            InitiatorUserId: initiator,
            InitiatorDisplayName: initiator,
            InitiatorAvatarUrl: null,
            ChannelId: $"toast_{notification.Id}",
            ChannelName: isChannel ? title.TrimStart('#') : title,
            IsDirectMessage: !isChannel,
            HuddleLink: "slack://open",
            IsPriorityUser: isPriority,
            OccurredAt: notification.CreationTime));
    }
}

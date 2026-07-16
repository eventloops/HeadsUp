using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SlackHuddleNotifier.Models;

namespace SlackHuddleNotifier.Services;

/// <summary>
/// Background service that wires the <see cref="IHuddleEventSource"/> to the
/// <see cref="INotificationService"/> and keeps the pipeline alive.
/// </summary>
public sealed class HuddleNotificationCoordinator : BackgroundService
{
    private readonly IHuddleEventSource _eventSource;
    private readonly INotificationService _notificationService;
    private readonly ILogger<HuddleNotificationCoordinator> _logger;

    public HuddleNotificationCoordinator(
        IHuddleEventSource eventSource,
        INotificationService notificationService,
        ILogger<HuddleNotificationCoordinator> logger)
    {
        _eventSource = eventSource;
        _notificationService = notificationService;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _eventSource.HuddleStarted += OnHuddleStarted;
        _logger.LogInformation("Huddle notification coordinator is running.");

        // Keep alive until cancellation.
        return Task.Delay(Timeout.Infinite, stoppingToken)
                   .ContinueWith(_ =>
                   {
                       _eventSource.HuddleStarted -= OnHuddleStarted;
                       _logger.LogInformation("Huddle notification coordinator stopped.");
                   }, TaskScheduler.Default);
    }

    private void OnHuddleStarted(object? sender, HuddleEvent e)
    {
        // Fire-and-forget with logging; do not block the Slack event thread.
        _ = _notificationService.ShowHuddleNotificationAsync(e)
            .ContinueWith(t =>
            {
                if (t.IsFaulted)
                    _logger.LogError(t.Exception, "Unhandled error in notification pipeline.");
            }, TaskScheduler.Default);
    }
}

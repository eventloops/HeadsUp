using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using HeadsUp.Models;

namespace HeadsUp.Services;

/// <summary>
/// Background service that wires the <see cref="IAlertSource"/> to the
/// <see cref="INotificationService"/> and keeps the pipeline alive.
/// </summary>
public sealed class HuddleNotificationCoordinator : BackgroundService
{
    private readonly IAlertSource _eventSource;
    private readonly INotificationService _notificationService;
    private readonly ILogger<HuddleNotificationCoordinator> _logger;

    public HuddleNotificationCoordinator(
        IAlertSource eventSource,
        INotificationService notificationService,
        ILogger<HuddleNotificationCoordinator> logger)
    {
        _eventSource = eventSource;
        _notificationService = notificationService;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _eventSource.AlertRaised += OnAlertRaised;
        _logger.LogInformation("Huddle notification coordinator is running.");

        // Keep alive until cancellation.
        return Task.Delay(Timeout.Infinite, stoppingToken)
                   .ContinueWith(_ =>
                   {
                       _eventSource.AlertRaised -= OnAlertRaised;
                       _logger.LogInformation("Huddle notification coordinator stopped.");
                   }, TaskScheduler.Default);
    }

    private void OnAlertRaised(object? sender, Alert e)
    {
        // Fire-and-forget with logging; do not block the Slack event thread.
        _ = _notificationService.ShowAlertAsync(e)
            .ContinueWith(t =>
            {
                if (t.IsFaulted)
                    _logger.LogError(t.Exception, "Unhandled error in notification pipeline.");
            }, TaskScheduler.Default);
    }
}

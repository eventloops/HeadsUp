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
    private readonly IAlertHistory _history;
    private readonly IAlertGate _gate;
    private readonly ILogger<HuddleNotificationCoordinator> _logger;

    public HuddleNotificationCoordinator(
        IAlertSource eventSource,
        INotificationService notificationService,
        IAlertHistory history,
        IAlertGate gate,
        ILogger<HuddleNotificationCoordinator> logger)
    {
        _eventSource = eventSource;
        _notificationService = notificationService;
        _history = history;
        _gate = gate;
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
        // Log it to history first so the dashboard reflects it even if the popup is missed.
        _history.Record(e);

        // Snooze / Slack DND silences the popup + sound, but the alert is still in history.
        if (_gate.IsMuted(out var reason))
        {
            _logger.LogDebug("Alert suppressed ({Reason}); recorded to history only.", reason);
            return;
        }

        // Fire-and-forget with logging; do not block the Slack event thread.
        _ = _notificationService.ShowAlertAsync(e)
            .ContinueWith(t =>
            {
                if (t.IsFaulted)
                    _logger.LogError(t.Exception, "Unhandled error in notification pipeline.");
            }, TaskScheduler.Default);
    }
}

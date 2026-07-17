using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using HeadsUp.Models;

namespace HeadsUp.Services;

/// <summary>
/// Background service that wires every registered <see cref="IAlertSource"/> to the
/// <see cref="INotificationService"/> and keeps the pipeline alive. Each alert is
/// stamped with the raising source's identity before it is recorded or shown, so the
/// rest of the pipeline (history, deep links, per-source status) can tell connectors apart.
/// </summary>
public sealed class HuddleNotificationCoordinator : BackgroundService
{
    private readonly IReadOnlyList<IAlertSource> _sources;
    private readonly INotificationService _notificationService;
    private readonly IAlertHistory _history;
    private readonly IAlertGate _gate;
    private readonly ILogger<HuddleNotificationCoordinator> _logger;

    // Per-source handlers, kept so each can be detached on shutdown.
    private readonly List<(IAlertSource Source, EventHandler<Alert> Handler)> _handlers = new();

    public HuddleNotificationCoordinator(
        IEnumerable<IAlertSource> sources,
        INotificationService notificationService,
        IAlertHistory history,
        IAlertGate gate,
        ILogger<HuddleNotificationCoordinator> logger)
    {
        _sources = sources.ToList();
        _notificationService = notificationService;
        _history = history;
        _gate = gate;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        foreach (var source in _sources)
        {
            EventHandler<Alert> handler = (_, alert) => OnAlertRaised(alert);
            source.AlertRaised += handler;
            _handlers.Add((source, handler));
        }

        _logger.LogInformation(
            "Huddle notification coordinator is running with {Count} source(s): {Sources}.",
            _sources.Count, string.Join(", ", _sources.Select(s => s.SourceName)));

        // Keep alive until cancellation.
        return Task.Delay(Timeout.Infinite, stoppingToken)
                   .ContinueWith(_ =>
                   {
                       foreach (var (source, handler) in _handlers)
                           source.AlertRaised -= handler;
                       _handlers.Clear();
                       _logger.LogInformation("Huddle notification coordinator stopped.");
                   }, TaskScheduler.Default);
    }

    private void OnAlertRaised(Alert e)
    {
        // The raising source already stamped its identity on the alert.
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

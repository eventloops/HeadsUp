using HeadsUp.Models;
using HeadsUp.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HeadsUp.Tests;

public class HuddleNotificationCoordinatorTests
{
    private static Alert SomeAlert() => new(
        InitiatorUserId: "U1",
        InitiatorDisplayName: "Sarah",
        InitiatorAvatarUrl: null,
        ChannelId: "C1",
        ChannelName: "general",
        IsDirectMessage: false,
        Link: "https://example",
        IsPriorityUser: false,
        OccurredAt: DateTimeOffset.Now);

    private readonly FakeAlertSource _source = new();
    private readonly RecordingNotificationService _notifications = new();
    private readonly AlertHistory _history = new();

    private HuddleNotificationCoordinator CreateCoordinator() => new(
        _source, _notifications, _history, NullLogger<HuddleNotificationCoordinator>.Instance);

    [Fact]
    public async Task Alerts_flow_from_source_to_notification_service_and_history()
    {
        var coordinator = CreateCoordinator();
        await coordinator.StartAsync(CancellationToken.None);
        try
        {
            var alert = SomeAlert();
            _source.Raise(alert);

            Assert.Same(alert, await _notifications.FirstAlert);
            Assert.Same(alert, Assert.Single(_history.Recent)); // also recorded
        }
        finally
        {
            await coordinator.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_faulting_notification_service_does_not_crash_the_pipeline()
    {
        _notifications.ThrowOnShow = new InvalidOperationException("boom");
        var coordinator = CreateCoordinator();

        await coordinator.StartAsync(CancellationToken.None);
        try
        {
            _source.Raise(SomeAlert()); // faults inside the fire-and-forget continuation

            // A second alert still flows once the service recovers.
            _notifications.ThrowOnShow = null;
            var alert = SomeAlert();
            _source.Raise(alert);

            Assert.Same(alert, await _notifications.FirstAlert);
            Assert.Equal(2, _history.Recent.Count); // both were recorded regardless of the fault
        }
        finally
        {
            await coordinator.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Stopping_unsubscribes_from_the_source()
    {
        var coordinator = CreateCoordinator();
        await coordinator.StartAsync(CancellationToken.None);
        await coordinator.StopAsync(CancellationToken.None);

        // Give the unsubscribe continuation a beat, then raise: nothing should arrive.
        await Task.Delay(100);
        _source.Raise(SomeAlert());
        await Task.Delay(100);

        Assert.Empty(_notifications.Shown);
        Assert.Empty(_history.Recent);
    }
}

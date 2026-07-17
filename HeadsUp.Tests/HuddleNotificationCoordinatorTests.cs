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

    [Fact]
    public async Task Alerts_flow_from_source_to_notification_service()
    {
        var source = new FakeAlertSource();
        var notifications = new RecordingNotificationService();
        var coordinator = new HuddleNotificationCoordinator(
            source, notifications, NullLogger<HuddleNotificationCoordinator>.Instance);

        await coordinator.StartAsync(CancellationToken.None);
        try
        {
            var alert = SomeAlert();
            source.Raise(alert);

            Assert.Same(alert, await notifications.FirstAlert);
        }
        finally
        {
            await coordinator.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_faulting_notification_service_does_not_crash_the_pipeline()
    {
        var source = new FakeAlertSource();
        var notifications = new RecordingNotificationService
        {
            ThrowOnShow = new InvalidOperationException("boom")
        };
        var coordinator = new HuddleNotificationCoordinator(
            source, notifications, NullLogger<HuddleNotificationCoordinator>.Instance);

        await coordinator.StartAsync(CancellationToken.None);
        try
        {
            source.Raise(SomeAlert()); // faults inside the fire-and-forget continuation

            // A second alert still flows once the service recovers.
            notifications.ThrowOnShow = null;
            var alert = SomeAlert();
            source.Raise(alert);

            Assert.Same(alert, await notifications.FirstAlert);
        }
        finally
        {
            await coordinator.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Stopping_unsubscribes_from_the_source()
    {
        var source = new FakeAlertSource();
        var notifications = new RecordingNotificationService();
        var coordinator = new HuddleNotificationCoordinator(
            source, notifications, NullLogger<HuddleNotificationCoordinator>.Instance);

        await coordinator.StartAsync(CancellationToken.None);
        await coordinator.StopAsync(CancellationToken.None);

        // Give the unsubscribe continuation a beat, then raise: nothing should arrive.
        await Task.Delay(100);
        source.Raise(SomeAlert());
        await Task.Delay(100);

        Assert.Empty(notifications.Shown);
    }
}

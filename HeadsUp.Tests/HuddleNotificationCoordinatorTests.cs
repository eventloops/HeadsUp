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

    private readonly FakeAlertSource _source = new("slack", "Slack");
    private readonly RecordingNotificationService _notifications = new();
    private readonly AlertHistory _history = new();
    private readonly SnoozeService _gate = new();

    private HuddleNotificationCoordinator CreateCoordinator(params IAlertSource[] sources) => new(
        sources.Length > 0 ? sources : new IAlertSource[] { _source },
        _notifications, _history, _gate, NullLogger<HuddleNotificationCoordinator>.Instance);

    [Fact]
    public async Task Alerts_flow_from_source_to_notification_service_and_history()
    {
        var coordinator = CreateCoordinator();
        await coordinator.StartAsync(CancellationToken.None);
        try
        {
            var alert = SomeAlert();
            _source.Raise(alert);

            var shown = await _notifications.FirstAlert;
            Assert.Equal(alert.InitiatorUserId, shown.InitiatorUserId);
            Assert.Equal("slack", shown.SourceId);      // stamped by the coordinator
            Assert.Equal("Slack", shown.SourceName);
            Assert.Equal("slack", Assert.Single(_history.Recent).SourceId); // also recorded, stamped
        }
        finally
        {
            await coordinator.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Alerts_from_multiple_sources_all_flow_through_stamped_with_their_identity()
    {
        var slack = new FakeAlertSource("slack", "Slack");
        var teams = new FakeAlertSource("teams", "Teams");
        var coordinator = CreateCoordinator(slack, teams);

        await coordinator.StartAsync(CancellationToken.None);
        try
        {
            slack.Raise(SomeAlert());
            teams.Raise(SomeAlert());
            await Task.Delay(150);

            Assert.Equal(2, _notifications.Shown.Count);
            Assert.Contains(_notifications.Shown, a => a.SourceId == "slack" && a.SourceName == "Slack");
            Assert.Contains(_notifications.Shown, a => a.SourceId == "teams" && a.SourceName == "Teams");
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

            Assert.Equal(alert.InitiatorUserId, (await _notifications.FirstAlert).InitiatorUserId);
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

    [Fact]
    public async Task While_snoozed_the_popup_is_suppressed_but_history_still_records()
    {
        _gate.SnoozeFor(TimeSpan.FromHours(1));
        var coordinator = CreateCoordinator();

        await coordinator.StartAsync(CancellationToken.None);
        try
        {
            var alert = SomeAlert();
            _source.Raise(alert);
            await Task.Delay(150); // let the fire-and-forget path settle

            Assert.Empty(_notifications.Shown);                 // popup suppressed
            Assert.Equal(alert.InitiatorUserId, Assert.Single(_history.Recent).InitiatorUserId); // but still recorded

            // Resuming lets the next alert through.
            _gate.Resume();
            _source.Raise(alert);
            Assert.Equal(alert.InitiatorUserId, (await _notifications.FirstAlert).InitiatorUserId);
        }
        finally
        {
            await coordinator.StopAsync(CancellationToken.None);
        }
    }
}

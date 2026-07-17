using HeadsUp.Models;
using HeadsUp.Services;
using Xunit;

namespace HeadsUp.Tests;

public class AlertHistoryTests
{
    private static Alert Make(string channel = "C1") => new(
        InitiatorUserId: new UserId("U1"),
        InitiatorDisplayName: "Sarah",
        InitiatorAvatarUrl: null,
        ChannelId: new ChannelId(channel),
        ChannelName: channel,
        IsDirectMessage: false,
        Link: "https://example",
        IsPriorityUser: false,
        OccurredAt: DateTimeOffset.Now,
        SourceId: new SourceId("slack"),
        SourceName: "Slack");

    [Fact]
    public void Starts_empty()
    {
        Assert.Empty(new AlertHistory().Recent);
    }

    [Fact]
    public void Records_newest_first()
    {
        var history = new AlertHistory();
        var a = Make("C1");
        var b = Make("C2");

        history.Record(a);
        history.Record(b);

        Assert.Equal(new[] { b, a }, history.Recent);
    }

    [Fact]
    public void Evicts_the_oldest_past_capacity()
    {
        var history = new AlertHistory();
        for (var i = 0; i < AlertHistory.Capacity + 25; i++)
            history.Record(Make($"C{i}"));

        var recent = history.Recent;
        Assert.Equal(AlertHistory.Capacity, recent.Count);
        Assert.Equal($"C{AlertHistory.Capacity + 24}", recent[0].ChannelId.Value);   // newest kept
        Assert.Equal($"C{25}", recent[^1].ChannelId.Value);                          // oldest survivor
    }

    [Fact]
    public void Record_raises_changed()
    {
        var history = new AlertHistory();
        var count = 0;
        history.Changed += (_, _) => count++;

        history.Record(Make());

        Assert.Equal(1, count);
    }

    [Fact]
    public void Clear_empties_and_raises_changed_once()
    {
        var history = new AlertHistory();
        history.Record(Make());
        var count = 0;
        history.Changed += (_, _) => count++;

        history.Clear();

        Assert.Empty(history.Recent);
        Assert.Equal(1, count);
    }

    [Fact]
    public void Clear_on_empty_history_does_not_raise()
    {
        var history = new AlertHistory();
        var count = 0;
        history.Changed += (_, _) => count++;

        history.Clear();

        Assert.Equal(0, count);
    }

    [Fact]
    public void Recent_is_an_isolated_snapshot()
    {
        var history = new AlertHistory();
        history.Record(Make());

        var snapshot = history.Recent;
        history.Record(Make("C2")); // mutate after snapshotting

        Assert.Single(snapshot); // snapshot is unaffected
        Assert.Equal(2, history.Recent.Count);
    }

    [Fact]
    public async Task Concurrent_records_all_land_and_stay_bounded()
    {
        var history = new AlertHistory();

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 500; i++)
                history.Record(Make());
        })));

        Assert.Equal(AlertHistory.Capacity, history.Recent.Count);
    }
}

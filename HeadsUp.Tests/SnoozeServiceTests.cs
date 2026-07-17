using HeadsUp.Services;
using System.Text.Json;
using Xunit;

namespace HeadsUp.Tests;

public class SnoozeServiceTests
{
    [Fact]
    public void Starts_unmuted()
    {
        using var s = new SnoozeService();
        Assert.False(s.IsMuted(out var reason));
        Assert.Equal(string.Empty, reason);
        Assert.Null(s.SnoozedUntil);
        Assert.False(s.AnySourceDndActive);
    }

    [Fact]
    public void Snooze_for_a_duration_mutes_with_a_reason()
    {
        using var s = new SnoozeService();
        var changed = 0;
        s.Changed += (_, _) => changed++;

        s.SnoozeFor(TimeSpan.FromHours(1));

        Assert.True(s.IsMuted(out var reason));
        Assert.StartsWith("Snoozed until", reason);
        Assert.NotNull(s.SnoozedUntil);
        Assert.Equal(1, changed);
    }

    [Fact]
    public void Resume_clears_a_manual_snooze()
    {
        using var s = new SnoozeService();
        s.SnoozeFor(TimeSpan.FromHours(1));

        s.Resume();

        Assert.False(s.IsMuted(out _));
        Assert.Null(s.SnoozedUntil);
    }

    [Fact]
    public void An_expired_snooze_is_not_muted()
    {
        using var s = new SnoozeService();
        s.SnoozeUntil(DateTimeOffset.Now.AddSeconds(-1)); // already in the past
        Assert.False(s.IsMuted(out _));
        Assert.Null(s.SnoozedUntil);
    }

    [Fact]
    public void Source_dnd_mutes_with_the_sources_name_in_the_reason()
    {
        using var s = new SnoozeService();
        s.SetSourceDnd("slack", "Slack", DateTimeOffset.Now.AddMinutes(30));

        Assert.True(s.IsMuted(out var reason));
        Assert.Equal("Do Not Disturb is on in Slack", reason);
        Assert.True(s.AnySourceDndActive);
    }

    [Fact]
    public void A_manual_snooze_reason_wins_over_source_dnd()
    {
        using var s = new SnoozeService();
        s.SetSourceDnd("slack", "Slack", DateTimeOffset.Now.AddMinutes(30));
        s.SnoozeFor(TimeSpan.FromHours(1));

        Assert.True(s.IsMuted(out var reason));
        Assert.StartsWith("Snoozed until", reason);
    }

    [Fact]
    public void Setting_the_same_dnd_value_does_not_re_raise()
    {
        using var s = new SnoozeService();
        var until = DateTimeOffset.Now.AddMinutes(30);
        var changed = 0;
        s.Changed += (_, _) => changed++;

        s.SetSourceDnd("slack", "Slack", until);
        s.SetSourceDnd("slack", "Slack", until); // unchanged

        Assert.Equal(1, changed);
    }

    [Fact]
    public void Clearing_dnd_unmutes()
    {
        using var s = new SnoozeService();
        s.SetSourceDnd("slack", "Slack", DateTimeOffset.Now.AddMinutes(30));
        s.SetSourceDnd("slack", "Slack", null);

        Assert.False(s.AnySourceDndActive);
        Assert.False(s.IsMuted(out _));
    }

    [Fact]
    public void Dnd_is_tracked_per_source_and_clearing_one_leaves_the_other()
    {
        using var s = new SnoozeService();
        s.SetSourceDnd("slack", "Slack", DateTimeOffset.Now.AddMinutes(30));
        s.SetSourceDnd("teams", "Teams", DateTimeOffset.Now.AddMinutes(30));

        // Clearing Slack still leaves Teams DND active and muting.
        s.SetSourceDnd("slack", "Slack", null);
        Assert.True(s.AnySourceDndActive);
        Assert.True(s.IsMuted(out var reason));
        Assert.Equal("Do Not Disturb is on in Teams", reason);

        s.SetSourceDnd("teams", "Teams", null);
        Assert.False(s.AnySourceDndActive);
    }

    [Fact]
    public async Task A_snooze_expires_on_its_own_and_raises_changed()
    {
        using var s = new SnoozeService();
        var changes = 0;
        s.Changed += (_, _) => Interlocked.Increment(ref changes);

        s.SnoozeFor(TimeSpan.FromMilliseconds(150));
        Assert.True(s.IsMuted(out _)); // changes == 1 (the snooze itself)

        await Task.Delay(600); // well past expiry

        Assert.False(s.IsMuted(out _));                                   // time-based: expired
        Assert.True(changes >= 2, $"expected an expiry notification; saw {changes}"); // timer fired
    }

    // ------------------------------------------------------------------
    // DndUntil parsing (SlackWebHuddleSource.DndUntil)
    // ------------------------------------------------------------------

    private static JsonElement Dnd(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [Fact]
    public void DndUntil_reads_active_manual_snooze()
    {
        var end = DateTimeOffset.Now.AddMinutes(20).ToUnixTimeSeconds();
        var until = SlackWebHuddleSource.DndUntil(Dnd($$"""
            { "ok": true, "snooze_enabled": true, "snooze_endtime": {{end}} }
            """));
        Assert.Equal(end, until!.Value.ToUnixTimeSeconds());
    }

    [Fact]
    public void DndUntil_reads_an_active_scheduled_window()
    {
        var start = DateTimeOffset.Now.AddMinutes(-5).ToUnixTimeSeconds();
        var end = DateTimeOffset.Now.AddMinutes(55).ToUnixTimeSeconds();
        var until = SlackWebHuddleSource.DndUntil(Dnd($$"""
            { "ok": true, "dnd_enabled": true, "next_dnd_start_ts": {{start}}, "next_dnd_end_ts": {{end}} }
            """));
        Assert.Equal(end, until!.Value.ToUnixTimeSeconds());
    }

    [Fact]
    public void DndUntil_is_null_for_a_future_window()
    {
        var start = DateTimeOffset.Now.AddHours(1).ToUnixTimeSeconds();
        var end = DateTimeOffset.Now.AddHours(2).ToUnixTimeSeconds();
        Assert.Null(SlackWebHuddleSource.DndUntil(Dnd($$"""
            { "ok": true, "dnd_enabled": true, "next_dnd_start_ts": {{start}}, "next_dnd_end_ts": {{end}} }
            """)));
    }

    [Fact]
    public void DndUntil_is_null_when_nothing_is_active()
    {
        Assert.Null(SlackWebHuddleSource.DndUntil(Dnd("""
            { "ok": true, "dnd_enabled": false, "snooze_enabled": false }
            """)));
    }

    [Fact]
    public void DndUntil_ignores_an_expired_manual_snooze()
    {
        var end = DateTimeOffset.Now.AddMinutes(-1).ToUnixTimeSeconds();
        Assert.Null(SlackWebHuddleSource.DndUntil(Dnd($$"""
            { "ok": true, "snooze_enabled": true, "snooze_endtime": {{end}} }
            """)));
    }
}

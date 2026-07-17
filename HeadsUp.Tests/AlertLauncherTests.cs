using HeadsUp.Services;
using Xunit;

namespace HeadsUp.Tests;

public class AlertLauncherTests
{
    // Open iterates the given targets until one launches.

    [Fact]
    public void Open_launches_the_first_target_and_stops()
    {
        var launched = new List<string>();
        AlertLauncher.Open(new[] { "slack://huddle?team=T1&id=C1", "https://app.slack.com/huddle/T1/C1" },
            t => { launched.Add(t); return true; });

        Assert.Equal(new[] { "slack://huddle?team=T1&id=C1" }, launched);
    }

    [Fact]
    public void Open_falls_back_when_the_first_launch_throws()
    {
        var launched = new List<string>();
        AlertLauncher.Open(new[] { "slack://huddle?team=T1&id=C1", "https://app.slack.com/huddle/T1/C1" }, t =>
        {
            launched.Add(t);
            if (t.StartsWith("slack://")) throw new InvalidOperationException("no app");
            return true;
        });

        Assert.Equal(
            new[] { "slack://huddle?team=T1&id=C1", "https://app.slack.com/huddle/T1/C1" },
            launched);
    }

    [Fact]
    public void Open_moves_on_when_a_launcher_declines_without_throwing()
    {
        var launched = new List<string>();
        AlertLauncher.Open(new[] { "slack://huddle?team=T1&id=C1", "https://app.slack.com/huddle/T1/C1" }, t =>
        {
            launched.Add(t);
            return !t.StartsWith("slack://"); // decline the app link, accept the web one
        });

        Assert.Equal(2, launched.Count);
    }

    [Fact]
    public void Open_tolerates_every_target_failing()
    {
        AlertLauncher.Open(new[] { "slack://huddle?team=T1&id=C1", "https://app.slack.com/huddle/T1/C1" },
            _ => throw new InvalidOperationException("nothing works")); // must not throw
    }

    [Fact]
    public void Open_single_link_launches_that_link()
    {
        var launched = new List<string>();
        AlertLauncher.Open("https://example.com", t => { launched.Add(t); return true; });

        Assert.Equal(new[] { "https://example.com" }, launched);
    }
}

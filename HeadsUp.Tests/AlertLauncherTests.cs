using HeadsUp.Services;
using Xunit;

namespace HeadsUp.Tests;

public class AlertLauncherTests
{
    // ------------------------------------------------------------------
    // Targets (desktop deep link first, web fallback last)
    // ------------------------------------------------------------------

    [Fact]
    public void Huddle_link_prefers_the_slack_app_huddle_scheme()
    {
        Assert.Equal(
            new[] { "slack://huddle?team=T123&id=C456", "https://app.slack.com/huddle/T123/C456" },
            AlertLauncher.Targets("https://app.slack.com/huddle/T123/C456").ToList());
    }

    [Fact]
    public void Client_link_prefers_the_slack_app_channel_scheme()
    {
        Assert.Equal(
            new[] { "slack://channel?team=T123&id=C456", "https://app.slack.com/client/T123/C456" },
            AlertLauncher.Targets("https://app.slack.com/client/T123/C456").ToList());
    }

    [Fact]
    public void Unrecognized_link_is_used_as_is()
    {
        Assert.Equal(
            new[] { "slack://channel?id=C456" },
            AlertLauncher.Targets("slack://channel?id=C456").ToList());
    }

    // ------------------------------------------------------------------
    // Open (iterate targets until one launches)
    // ------------------------------------------------------------------

    [Fact]
    public void Open_launches_the_first_target_and_stops()
    {
        var launched = new List<string>();
        AlertLauncher.Open("https://app.slack.com/huddle/T1/C1", t => { launched.Add(t); return true; });

        Assert.Equal(new[] { "slack://huddle?team=T1&id=C1" }, launched);
    }

    [Fact]
    public void Open_falls_back_to_the_web_link_when_the_app_launch_throws()
    {
        var launched = new List<string>();
        AlertLauncher.Open("https://app.slack.com/huddle/T1/C1", t =>
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
        AlertLauncher.Open("https://app.slack.com/huddle/T1/C1", t =>
        {
            launched.Add(t);
            return !t.StartsWith("slack://"); // decline the app link, accept the web one
        });

        Assert.Equal(2, launched.Count);
    }

    [Fact]
    public void Open_tolerates_every_target_failing()
    {
        AlertLauncher.Open("https://app.slack.com/huddle/T1/C1",
            _ => throw new InvalidOperationException("nothing works")); // must not throw
    }
}

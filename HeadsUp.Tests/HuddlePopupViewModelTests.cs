using HeadsUp.Models;
using HeadsUp.ViewModels;
using Xunit;

namespace HeadsUp.Tests;

public class HuddlePopupViewModelTests
{
    private static Alert MakeAlert(
        string displayName = "Sarah Whitfield",
        string channelName = "project-atlas",
        bool isDm = false,
        bool isPriority = false,
        AlertKind kind = AlertKind.Huddle,
        string preview = "") =>
        new(
            InitiatorUserId: "U000TEST",
            InitiatorDisplayName: displayName,
            InitiatorAvatarUrl: null,
            ChannelId: isDm ? "D000TEST" : "C000TEST",
            ChannelName: channelName,
            IsDirectMessage: isDm,
            Link: "https://app.slack.com/huddle/T000TEST/C000TEST",
            IsPriorityUser: isPriority,
            OccurredAt: DateTimeOffset.Now,
            Kind: kind,
            Preview: preview);

    private static HuddlePopupViewModel Populated(Alert alert)
    {
        var vm = new HuddlePopupViewModel();
        vm.Populate(alert);
        return vm;
    }

    // ------------------------------------------------------------------
    // Initials
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("Sarah Whitfield", "SW")]
    [InlineData("Anna Maria Jones", "AJ")] // first + last, middle skipped
    [InlineData("Bono", "B")]
    [InlineData("", "?")]
    public void Initials_come_from_first_and_last_name(string name, string expected) =>
        Assert.Equal(expected, Populated(MakeAlert(displayName: name)).Initials);

    // ------------------------------------------------------------------
    // Channel display & subtitle
    // ------------------------------------------------------------------

    [Fact]
    public void Channel_alert_shows_hash_channel_name()
    {
        Assert.Equal("#project-atlas", Populated(MakeAlert()).ChannelDisplay);
    }

    [Fact]
    public void Dm_alert_shows_dm_with_initiator()
    {
        Assert.Equal("DM with Sarah Whitfield", Populated(MakeAlert(isDm: true)).ChannelDisplay);
    }

    [Fact]
    public void Subtitle_is_channel_and_time_when_no_preview()
    {
        var vm = Populated(MakeAlert());
        Assert.StartsWith("#project-atlas · ", vm.Subtitle);
    }

    [Fact]
    public void Subtitle_is_the_preview_when_present()
    {
        var vm = Populated(MakeAlert(kind: AlertKind.Mention, preview: "can you look at this?"));
        Assert.Equal("can you look at this?", vm.Subtitle);
    }

    // ------------------------------------------------------------------
    // Kind mapping
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(AlertKind.Huddle, "Huddle started", "Join huddle")]
    [InlineData(AlertKind.Mention, "Mentioned you", "Open")]
    [InlineData(AlertKind.DirectMessage, "Direct message", "Open")]
    [InlineData(AlertKind.Keyword, "Keyword match", "Open")]
    public void Headline_and_action_follow_the_alert_kind(AlertKind kind, string headline, string action)
    {
        var vm = Populated(MakeAlert(kind: kind));
        Assert.Equal(headline, vm.Headline);
        Assert.Equal(action, vm.ActionLabel);
    }

    [Fact]
    public void Priority_flag_flows_through()
    {
        Assert.True(Populated(MakeAlert(isPriority: true)).IsPriorityUser);
        Assert.False(Populated(MakeAlert()).IsPriorityUser);
    }

    [Fact]
    public void Time_display_is_lowercase()
    {
        var vm = Populated(MakeAlert());
        Assert.Equal(vm.TimeDisplay.ToLowerInvariant(), vm.TimeDisplay);
        Assert.NotEmpty(vm.TimeDisplay);
    }

    // ------------------------------------------------------------------
    // Commands
    // ------------------------------------------------------------------

    [Fact]
    public void Dismiss_requests_close()
    {
        var vm = Populated(MakeAlert());
        var closed = false;
        vm.CloseRequested += (_, _) => closed = true;

        vm.DismissCommand.Execute(null);

        Assert.True(closed);
    }

    [Fact]
    public void Join_launches_the_first_working_target_and_closes()
    {
        var vm = Populated(MakeAlert());
        var launched = new List<string>();
        vm.Launch = target => { launched.Add(target); return true; };
        var closed = false;
        vm.CloseRequested += (_, _) => closed = true;

        vm.JoinHuddleCommand.Execute(null);

        Assert.Equal(new[] { "slack://huddle?team=T000TEST&id=C000TEST" }, launched);
        Assert.True(closed);
    }

    [Fact]
    public void Join_falls_back_to_the_web_link_when_the_app_launch_fails()
    {
        var vm = Populated(MakeAlert());
        var launched = new List<string>();
        vm.Launch = target =>
        {
            launched.Add(target);
            if (target.StartsWith("slack://"))
                throw new InvalidOperationException("no slack app installed");
            return true;
        };

        vm.JoinHuddleCommand.Execute(null);

        Assert.Equal(
            new[]
            {
                "slack://huddle?team=T000TEST&id=C000TEST",
                "https://app.slack.com/huddle/T000TEST/C000TEST"
            },
            launched);
    }

    [Fact]
    public void Join_still_closes_when_every_launch_fails()
    {
        var vm = Populated(MakeAlert());
        vm.Launch = _ => throw new InvalidOperationException("nothing works");
        var closed = false;
        vm.CloseRequested += (_, _) => closed = true;

        vm.JoinHuddleCommand.Execute(null);

        Assert.True(closed);
    }

    [Fact]
    public void Join_moves_on_when_a_launcher_declines_without_throwing()
    {
        var vm = Populated(MakeAlert());
        var launched = new List<string>();
        vm.Launch = target =>
        {
            launched.Add(target);
            return !target.StartsWith("slack://"); // decline the app link, accept the web one
        };

        vm.JoinHuddleCommand.Execute(null);

        Assert.Equal(2, launched.Count);
    }
}

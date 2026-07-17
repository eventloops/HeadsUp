using HeadsUp.Models;
using HeadsUp.Services;
using HeadsUp.ViewModels;
using Xunit;

namespace HeadsUp.Wpf.Tests;

public class TrayViewModelTests
{
    [Fact]
    public void Status_is_the_shared_instance()
    {
        var status = new AppStatus();
        var vm = new TrayViewModel(status, new SnoozeService());
        Assert.Same(status, vm.Status);
    }

    [Fact]
    public void Commands_raise_their_events()
    {
        var vm = new TrayViewModel(new AppStatus(), new SnoozeService());
        var raised = new List<string>();
        vm.OpenDashboardRequested += (_, _) => raised.Add("dashboard");
        vm.AboutRequested += (_, _) => raised.Add("about");
        vm.ExitRequested += (_, _) => raised.Add("exit");

        vm.OpenDashboardCommand.Execute(null);
        vm.ShowAboutCommand.Execute(null);
        vm.ExitCommand.Execute(null);

        Assert.Equal(new[] { "dashboard", "about", "exit" }, raised);
    }

    [Fact]
    public void Snoozing_mutes_and_reflects_state_resuming_clears_it()
    {
        var snooze = new SnoozeService();
        var vm = new TrayViewModel(new AppStatus(), snooze);
        Assert.False(vm.IsMuted);

        vm.Snooze30Command.Execute(null);

        Assert.True(vm.IsMuted);
        Assert.True(vm.IsManuallySnoozed);
        Assert.Contains("Snoozed until", vm.MuteStatus);

        vm.ResumeCommand.Execute(null);

        Assert.False(vm.IsMuted);
        Assert.False(vm.IsManuallySnoozed);
    }

    [Fact]
    public void Slack_dnd_mutes_but_is_not_manually_snoozed()
    {
        var snooze = new SnoozeService();
        var vm = new TrayViewModel(new AppStatus(), snooze);

        snooze.SetSourceDnd(new SourceId("slack"), "Slack", DateTimeOffset.Now.AddHours(1));

        Assert.True(vm.IsMuted);
        Assert.False(vm.IsManuallySnoozed); // can't "resume" Slack DND from the tray
        Assert.Contains("Do Not Disturb", vm.MuteStatus);
    }
}

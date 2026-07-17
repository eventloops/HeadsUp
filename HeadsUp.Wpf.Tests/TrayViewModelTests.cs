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
        var vm = new TrayViewModel(status);
        Assert.Same(status, vm.Status);
    }

    [Fact]
    public void Commands_raise_their_events()
    {
        var vm = new TrayViewModel(new AppStatus());
        var raised = new List<string>();
        vm.OpenDashboardRequested += (_, _) => raised.Add("dashboard");
        vm.AboutRequested += (_, _) => raised.Add("about");
        vm.ExitRequested += (_, _) => raised.Add("exit");

        vm.OpenDashboardCommand.Execute(null);
        vm.ShowAboutCommand.Execute(null);
        vm.ExitCommand.Execute(null);

        Assert.Equal(new[] { "dashboard", "about", "exit" }, raised);
    }
}

using HeadsUp.Configuration;
using HeadsUp.Services;
using HeadsUp.ViewModels;
using HeadsUp.Views;
using System.Windows;
using Xunit;

namespace HeadsUp.Wpf.Tests;

/// <summary>
/// Loads the real windows on a single STA thread with the app theme merged, so a broken
/// XAML tree — a missing theme brush, a malformed template, a mistyped binding target —
/// fails the build instead of blowing up the first time a user opens the window.
/// One Application/thread for all of them (WPF allows only one Application per process).
/// </summary>
public class WindowSmokeTests
{
    private sealed class StubDirectory : IUserDirectory
    {
        public bool IsAvailable => true;
        public Task<IReadOnlyList<DirectoryUser>> GetUsersAsync(bool forceRefresh = false, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DirectoryUser>>(Array.Empty<DirectoryUser>());
        public Task<DirectoryUser?> FindByIdAsync(string userId, CancellationToken ct = default) =>
            Task.FromResult<DirectoryUser?>(null);
    }

    private sealed class StubBrowser : IBrowserLauncher
    {
        public void Open(string url) { }
    }

    private sealed class StubPriorityStore : IPrioritySettingsStore
    {
        public IReadOnlyList<string> CurrentUserIds(string sourceId) => Array.Empty<string>();
        public Task SetAsync(string sourceId, IEnumerable<string> userIds, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class StubAlertStore : IAlertSettingsStore
    {
        public AlertOptions Current { get; } = new()
        {
            MutedChannels = { new MutedChannel { Id = "C9", Name = "random" } }
        };
        public Task SaveAsync(AlertOptions options, CancellationToken ct = default) => Task.CompletedTask;
    }

    [Fact]
    public void All_themed_windows_load_and_render()
    {
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                if (Application.Current is null)
                    _ = new Application();
                Application.Current!.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri("pack://application:,,,/HeadsUp;component/Themes/Colors.Dark.xaml")
                });
                Application.Current.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri("pack://application:,,,/HeadsUp;component/Themes/Controls.xaml")
                });

                ShowThenClose(BuildDashboard());
                ShowThenClose(new WelcomeWindow(new WelcomeViewModel(new StubAlertStore(), new AppStatus())));
                // Also render the welcome's "Connecting…" state.
                var connectingVm = new WelcomeViewModel(new StubAlertStore(), new AppStatus());
                connectingVm.ConnectCommand.Execute(null);
                ShowThenClose(new WelcomeWindow(connectingVm));

                ShowThenClose(new SlackAppSetupWindow(new SlackAppSetupViewModel(new StubBrowser())));
                // Also render the setup window's validation-error state.
                var invalidVm = new SlackAppSetupViewModel(new StubBrowser()) { ClientId = "nope", AppToken = "nope" };
                invalidVm.ConfirmCommand.Execute(null);
                ShowThenClose(new SlackAppSetupWindow(invalidVm));
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Window smoke test timed out.");

        Assert.Null(failure);
    }

    private static DashboardWindow BuildDashboard()
    {
        var history = new AlertHistory();
        history.Record(new Models.Alert(
            InitiatorUserId: new Models.UserId("U1"), InitiatorDisplayName: "Sarah Whitfield", InitiatorAvatarUrl: null,
            ChannelId: new Models.ChannelId("C1"), ChannelName: "project-atlas", IsDirectMessage: false,
            Link: "https://app.slack.com/huddle/T1/C1", IsPriorityUser: true,
            OccurredAt: DateTimeOffset.Now, SourceId: new Models.SourceId("slack"), SourceName: "Slack",
            Kind: Models.AlertKind.Mention, Preview: "can you take a look?"));

        var snooze = new SnoozeService();
        snooze.SnoozeFor(TimeSpan.FromMinutes(30)); // exercise the muted banner too

        var status = new AppStatus();
        var slack = status.Seed(new Models.SourceId("slack"), "Slack", "SlackClientApi"); // renders one source card
        slack.SetConnection(true, "Watching for huddles");
        slack.SetAccount("cameron.lambert", "Procode");

        var links = new AlertLinks(new[] { new SlackAlertLinkResolver() });
        var vm = new DashboardViewModel(
            status, new StubDirectory(), new StubPriorityStore(), new StubAlertStore(), history, snooze, links);
        return new DashboardWindow(vm);
    }

    private static void ShowThenClose(Window w)
    {
        w.ShowActivated = false;
        w.Left = -32000; w.Top = -32000;
        w.Show();
        w.UpdateLayout();
        w.Close();
    }
}

using HeadsUp.Configuration;
using HeadsUp.Services;
using HeadsUp.ViewModels;
using HeadsUp.Views;
using System.Windows;
using Xunit;

namespace HeadsUp.Wpf.Tests;

/// <summary>
/// Loads the real dashboard window on an STA thread so a broken XAML tree — a bad
/// StaticResource, a malformed template, a mistyped binding target — fails the build
/// instead of blowing up the first time a user opens the dashboard.
/// </summary>
public class DashboardWindowSmokeTests
{
    private sealed class StubDirectory : IUserDirectory
    {
        public bool IsAvailable => true;
        public Task<IReadOnlyList<DirectoryUser>> GetUsersAsync(bool forceRefresh = false, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DirectoryUser>>(Array.Empty<DirectoryUser>());
        public Task<DirectoryUser?> FindByIdAsync(string userId, CancellationToken ct = default) =>
            Task.FromResult<DirectoryUser?>(null);
    }

    private sealed class StubPriorityStore : IPrioritySettingsStore
    {
        public IReadOnlyList<string> CurrentUserIds => Array.Empty<string>();
        public Task SetAsync(IEnumerable<string> userIds, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class StubAlertStore : IAlertSettingsStore
    {
        public AlertOptions Current { get; } = new()
        {
            MutedChannels = { new MutedChannel { Id = "C9", Name = "random" } } // render a chip
        };
        public Task SaveAsync(AlertOptions options, CancellationToken ct = default) => Task.CompletedTask;
    }

    [Fact]
    public void Dashboard_window_loads_and_renders_with_a_populated_history()
    {
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                var history = new AlertHistory();
                history.Record(new Models.Alert(
                    InitiatorUserId: "U1",
                    InitiatorDisplayName: "Sarah Whitfield",
                    InitiatorAvatarUrl: null,
                    ChannelId: "C1",
                    ChannelName: "project-atlas",
                    IsDirectMessage: false,
                    Link: "https://app.slack.com/huddle/T1/C1",
                    IsPriorityUser: true,
                    OccurredAt: DateTimeOffset.Now,
                    Kind: Models.AlertKind.Mention,
                    Preview: "can you take a look?"));

                var snooze = new SnoozeService();
                snooze.SnoozeFor(TimeSpan.FromMinutes(30)); // render the muted banner too

                var vm = new DashboardViewModel(
                    new AppStatus { Mode = "SlackClientApi" },
                    new StubDirectory(), new StubPriorityStore(), new StubAlertStore(), history, snooze);

                // Constructing parses the XAML; Show applies every control template.
                var window = new DashboardWindow(vm) { ShowActivated = false, Left = -32000, Top = -32000 };
                window.Show();
                window.UpdateLayout();
                window.Close();
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
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Dashboard smoke test timed out.");

        Assert.Null(failure);
    }
}

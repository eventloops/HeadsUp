using HeadsUp.Configuration;
using HeadsUp.Models;
using HeadsUp.Services;
using HeadsUp.ViewModels;
using Xunit;

namespace HeadsUp.Wpf.Tests;

public class DashboardViewModelTests
{
    // ------------------------------------------------------------------
    // Fakes
    // ------------------------------------------------------------------

    private sealed class FakeDirectory : IUserDirectory
    {
        public List<DirectoryUser> Users { get; } = [];
        public bool Throw { get; set; }
        public bool IsAvailable => true;

        public Task<IReadOnlyList<DirectoryUser>> GetUsersAsync(bool forceRefresh = false, CancellationToken ct = default) =>
            Throw
                ? throw new InvalidOperationException("directory unavailable")
                : Task.FromResult<IReadOnlyList<DirectoryUser>>(Users);

        public Task<DirectoryUser?> FindByIdAsync(string userId, CancellationToken ct = default) =>
            Task.FromResult(Users.FirstOrDefault(u => u.Id == userId));
    }

    private sealed class FakePriorityStore : IPrioritySettingsStore
    {
        public List<string> Current { get; set; } = [];
        public List<string>? LastSaved { get; private set; }
        public string? LastSavedSource { get; private set; }
        public bool Throw { get; set; }

        public IReadOnlyList<string> CurrentUserIds(string sourceId) => Current;

        public Task SetAsync(string sourceId, IEnumerable<string> userIds, CancellationToken ct = default)
        {
            if (Throw)
                throw new InvalidOperationException("disk full");
            LastSavedSource = sourceId;
            LastSaved = userIds.ToList();
            return Task.CompletedTask;
        }
    }

    private sealed class FakeAlertStore : IAlertSettingsStore
    {
        public AlertOptions Current { get; set; } = new();
        public AlertOptions? LastSaved { get; private set; }
        public bool Throw { get; set; }

        public Task SaveAsync(AlertOptions options, CancellationToken ct = default)
        {
            if (Throw)
                throw new InvalidOperationException("disk full");
            LastSaved = options;
            return Task.CompletedTask;
        }
    }

    private readonly FakeDirectory _directory = new();
    private readonly FakePriorityStore _priorityStore = new();
    private readonly FakeAlertStore _alertStore = new();
    private readonly AlertHistory _history = new();
    private readonly SnoozeService _snooze = new();
    private readonly AppStatus _status = new();
    private readonly AlertLinks _links = new(new[] { new SlackAlertLinkResolver() });

    private static DirectoryUser User(string id, string name, string? real = null) =>
        new(id, name, real, null);

    private static Alert Alert(string channel = "general", bool dm = false, AlertKind kind = AlertKind.Huddle) => new(
        InitiatorUserId: new UserId("U1"),
        InitiatorDisplayName: "Sarah Whitfield",
        InitiatorAvatarUrl: null,
        ChannelId: new ChannelId(dm ? "D1" : "C1"),
        ChannelName: channel,
        IsDirectMessage: dm,
        Link: "https://app.slack.com/huddle/T1/C1",
        IsPriorityUser: false,
        OccurredAt: DateTimeOffset.Now,
        SourceId: new SourceId("slack"),
        SourceName: "Slack",
        Kind: kind);

    private DashboardViewModel CreateVm() =>
        new(_status, _directory, _priorityStore, _alertStore, _history, _snooze, _links);

    // ------------------------------------------------------------------
    // Alert settings round trip
    // ------------------------------------------------------------------

    [Fact]
    public void Construction_loads_alert_settings_without_saving_them_back()
    {
        _alertStore.Current = new AlertOptions
        {
            Huddles = false,
            Mentions = true,
            DirectMessages = true,
            Keywords = true,
            KeywordList = ["deploy", "urgent"]
        };

        var vm = CreateVm();

        Assert.Same(_status, vm.Status); // dashboard binds the shared live status
        Assert.False(vm.NotifyHuddles);
        Assert.True(vm.NotifyMentions);
        Assert.True(vm.NotifyDirectMessages);
        Assert.True(vm.NotifyKeywords);
        Assert.Equal("deploy, urgent", vm.KeywordsText);
        Assert.Null(_alertStore.LastSaved); // loading must not echo a save
    }

    [Fact]
    public void Toggling_an_alert_saves_the_full_set()
    {
        var vm = CreateVm();

        vm.NotifyDirectMessages = true;

        Assert.NotNull(_alertStore.LastSaved);
        Assert.True(_alertStore.LastSaved!.DirectMessages);
        Assert.True(_alertStore.LastSaved.Huddles); // defaults carried along
    }

    [Fact]
    public void Keywords_text_is_parsed_on_commas_and_newlines()
    {
        var vm = CreateVm();

        vm.KeywordsText = " deploy , urgent\nrollback\r\n  ,, ";

        Assert.Equal(new[] { "deploy", "urgent", "rollback" }, _alertStore.LastSaved!.KeywordList);
    }

    [Fact]
    public void A_failing_alert_save_surfaces_in_the_status_line()
    {
        var vm = CreateVm();
        _alertStore.Throw = true;

        vm.NotifyKeywords = true;

        Assert.Contains("Couldn't save alert settings", vm.DirectoryStatus);
    }

    // ------------------------------------------------------------------
    // LoadAsync
    // ------------------------------------------------------------------

    [Fact]
    public async Task Load_resolves_priority_ids_against_the_directory()
    {
        _directory.Users.AddRange([User("U1", "Sarah"), User("U2", "Alex")]);
        _priorityStore.Current = ["U2", "UGONE"];
        var vm = CreateVm();

        await vm.LoadAsync();

        Assert.Equal(2, vm.PriorityUsers.Count);
        Assert.Equal("Alex", vm.PriorityUsers[0].DisplayName);
        Assert.Equal("UGONE", vm.PriorityUsers[1].DisplayName); // unknown id shows as-is
        Assert.Equal("2 workspace members available.", vm.DirectoryStatus);
        Assert.False(vm.IsLoading);
    }

    [Fact]
    public async Task An_empty_directory_prompts_about_connectivity()
    {
        var vm = CreateVm();

        await vm.LoadAsync();

        Assert.Contains("Couldn't load members", vm.DirectoryStatus);
    }

    [Fact]
    public async Task A_failing_directory_reports_the_error()
    {
        _directory.Throw = true;
        var vm = CreateVm();

        await vm.LoadAsync();

        Assert.Contains("Error loading members", vm.DirectoryStatus);
        Assert.Contains("directory unavailable", vm.DirectoryStatus);
        Assert.False(vm.IsLoading);
    }

    // ------------------------------------------------------------------
    // Search
    // ------------------------------------------------------------------

    private async Task<DashboardViewModel> LoadedVm(params DirectoryUser[] users)
    {
        _directory.Users.AddRange(users);
        var vm = CreateVm();
        await vm.LoadAsync();
        return vm;
    }

    [Fact]
    public async Task Search_needs_at_least_two_characters()
    {
        var vm = await LoadedVm(User("U1", "Sarah"));

        vm.SearchText = "s";
        Assert.Empty(vm.SearchResults);

        vm.SearchText = "sa";
        Assert.Single(vm.SearchResults);
    }

    [Fact]
    public async Task Search_matches_the_label_case_insensitively()
    {
        var vm = await LoadedVm(
            User("U1", "Sarah", "Sarah Whitfield"),
            User("U2", "Alex"));

        vm.SearchText = "WHITF"; // matches the real-name part of the label
        Assert.Equal(new[] { "U1" }, vm.SearchResults.Select(u => u.Id));
    }

    [Fact]
    public async Task Search_excludes_users_already_prioritized()
    {
        _priorityStore.Current = ["U1"];
        var vm = await LoadedVm(User("U1", "Sarah"), User("U2", "Sam"));

        vm.SearchText = "sa";

        Assert.Equal(new[] { "U2" }, vm.SearchResults.Select(u => u.Id));
    }

    [Fact]
    public async Task Search_caps_at_25_results()
    {
        var vm = await LoadedVm(Enumerable.Range(0, 40)
            .Select(i => User($"U{i}", $"match-{i}")).ToArray());

        vm.SearchText = "match";

        Assert.Equal(25, vm.SearchResults.Count);
    }

    // ------------------------------------------------------------------
    // Add / remove
    // ------------------------------------------------------------------

    [Fact]
    public async Task Adding_a_user_persists_and_clears_the_search()
    {
        var vm = await LoadedVm(User("U1", "Sarah"));
        vm.SearchText = "sa";

        await vm.AddCommand.ExecuteAsync(vm.SearchResults.Single());

        Assert.Single(vm.PriorityUsers);
        Assert.Equal(new[] { "U1" }, _priorityStore.LastSaved!);
        Assert.Equal(string.Empty, vm.SearchText);
        Assert.Empty(vm.SearchResults);
    }

    [Fact]
    public async Task Adding_null_or_duplicates_is_ignored()
    {
        var vm = await LoadedVm(User("U1", "Sarah"));

        await vm.AddCommand.ExecuteAsync(null);
        await vm.AddCommand.ExecuteAsync(User("U1", "Sarah"));
        await vm.AddCommand.ExecuteAsync(User("U1", "Sarah")); // duplicate

        Assert.Single(vm.PriorityUsers);
    }

    [Fact]
    public async Task Removing_a_user_persists_the_shrunken_list()
    {
        _priorityStore.Current = ["U1", "U2"];
        var vm = await LoadedVm(User("U1", "Sarah"), User("U2", "Alex"));

        await vm.RemoveCommand.ExecuteAsync(vm.PriorityUsers[0]);

        Assert.Single(vm.PriorityUsers);
        Assert.Equal(new[] { "U2" }, _priorityStore.LastSaved!);

        await vm.RemoveCommand.ExecuteAsync(null); // no-op
        Assert.Single(vm.PriorityUsers);
    }

    [Fact]
    public async Task A_failing_priority_save_surfaces_in_the_status_line()
    {
        var vm = await LoadedVm(User("U1", "Sarah"));
        _priorityStore.Throw = true;

        await vm.AddCommand.ExecuteAsync(User("U1", "Sarah") with { Id = "U9" });

        Assert.Contains("Couldn't save priority users", vm.DirectoryStatus);
    }

    // ------------------------------------------------------------------
    // Recent alerts
    // ------------------------------------------------------------------

    [Fact]
    public void Construction_seeds_recent_alerts_newest_first()
    {
        _history.Record(Alert("general"));
        _history.Record(Alert("random"));

        var vm = CreateVm();

        Assert.Equal(new[] { "#random", "#general" }, vm.RecentAlerts.Select(r => r.Where));
    }

    [Fact]
    public void New_alerts_appear_live()
    {
        var vm = CreateVm();
        Assert.Empty(vm.RecentAlerts);

        _history.Record(Alert("general"));

        var row = Assert.Single(vm.RecentAlerts);
        Assert.Equal("Sarah Whitfield", row.InitiatorName);
        Assert.Equal("Huddle started", row.Headline);
    }

    [Fact]
    public void Clear_command_empties_history_and_list()
    {
        _history.Record(Alert());
        var vm = CreateVm();

        vm.ClearHistoryCommand.Execute(null);

        Assert.Empty(vm.RecentAlerts);
        Assert.Empty(_history.Recent);
    }

    [Fact]
    public void Open_command_launches_the_alert_link()
    {
        _history.Record(Alert());
        var vm = CreateVm();
        var launched = new List<string>();
        vm.Launch = t => { launched.Add(t); return true; };

        vm.OpenAlertCommand.Execute(vm.RecentAlerts[0]);

        Assert.Equal("slack://huddle?team=T1&id=C1", launched.Single());
    }

    [Fact]
    public void Open_command_ignores_null()
    {
        var vm = CreateVm();
        vm.Launch = _ => throw new InvalidOperationException("should not be called");

        vm.OpenAlertCommand.Execute(null); // no-op, must not throw
    }

    [Fact]
    public void Disposing_stops_live_updates()
    {
        var vm = CreateVm();
        vm.Dispose();

        _history.Record(Alert());

        Assert.Empty(vm.RecentAlerts);
    }

    // ------------------------------------------------------------------
    // Snooze status
    // ------------------------------------------------------------------

    [Fact]
    public void Snooze_state_is_reflected_and_updates_live()
    {
        var vm = CreateVm();
        Assert.False(vm.IsMuted);

        _snooze.SnoozeFor(TimeSpan.FromHours(1));

        Assert.True(vm.IsMuted);
        Assert.StartsWith("Snoozed until", vm.MuteStatus);
    }

    // ------------------------------------------------------------------
    // Muted channels
    // ------------------------------------------------------------------

    [Fact]
    public void Loads_existing_muted_channels()
    {
        _alertStore.Current = new AlertOptions
        {
            MutedChannels = { new MutedChannel { Id = "C1", Name = "general" } }
        };

        var vm = CreateVm();

        Assert.Equal(new[] { "general" }, vm.MutedChannels.Select(m => m.Name));
    }

    [Fact]
    public async Task Muting_from_a_history_row_adds_and_persists()
    {
        _history.Record(Alert(channel: "general"));
        var vm = CreateVm();

        await vm.MuteChannelCommand.ExecuteAsync(vm.RecentAlerts[0]);

        Assert.Equal(new[] { "C1" }, vm.MutedChannels.Select(m => m.Id));
        Assert.Equal(new[] { "C1" }, _alertStore.LastSaved!.MutedChannels.Select(m => m.Id));
    }

    [Fact]
    public async Task Muting_the_same_channel_twice_is_ignored()
    {
        _history.Record(Alert(channel: "general"));
        var vm = CreateVm();

        await vm.MuteChannelCommand.ExecuteAsync(vm.RecentAlerts[0]);
        await vm.MuteChannelCommand.ExecuteAsync(vm.RecentAlerts[0]);

        Assert.Single(vm.MutedChannels);
    }

    [Fact]
    public async Task Dm_rows_cannot_be_muted()
    {
        _history.Record(Alert(dm: true, kind: AlertKind.DirectMessage));
        var vm = CreateVm();

        await vm.MuteChannelCommand.ExecuteAsync(vm.RecentAlerts[0]);

        Assert.Empty(vm.MutedChannels);
    }

    [Fact]
    public async Task Unmuting_removes_and_persists()
    {
        _alertStore.Current = new AlertOptions
        {
            MutedChannels = { new MutedChannel { Id = "C1", Name = "general" } }
        };
        var vm = CreateVm();

        await vm.UnmuteCommand.ExecuteAsync(vm.MutedChannels[0]);

        Assert.Empty(vm.MutedChannels);
        Assert.Empty(_alertStore.LastSaved!.MutedChannels);
    }

    // ------------------------------------------------------------------
    // Navigation + theme
    // ------------------------------------------------------------------

    [Fact]
    public void Starts_on_the_overview_section()
    {
        Assert.Equal("Overview", CreateVm().SelectedSection);
    }

    [Fact]
    public void Picking_a_different_theme_requests_the_change()
    {
        var vm = CreateVm();
        AppTheme? requested = null;
        vm.ThemeChangeRequested += (_, t) => requested = t;

        // Whatever the current theme is, the other one triggers a change.
        var other = ThemeManager.Current == AppTheme.Dark ? "Light" : "Dark";
        vm.SelectedThemeName = other;

        Assert.NotNull(requested);
        Assert.Equal(other, requested.ToString());
    }

    [Fact]
    public void Picking_the_current_theme_does_nothing()
    {
        var vm = CreateVm();
        var raised = false;
        vm.ThemeChangeRequested += (_, _) => raised = true;

        vm.SelectedThemeName = ThemeManager.Current.ToString();

        Assert.False(raised);
    }
}

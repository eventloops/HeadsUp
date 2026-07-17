using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HeadsUp.Configuration;
using HeadsUp.Services;
using System.Collections.ObjectModel;

namespace HeadsUp.ViewModels;

/// <summary>
/// Backs the dashboard window: live status (bound straight to <see cref="AppStatus"/>) plus a
/// priority-users editor that searches the workspace directory and persists picks.
/// </summary>
public sealed partial class DashboardViewModel : ObservableObject
{
    private readonly IUserDirectory _directory;
    private readonly IPrioritySettingsStore _priorityStore;
    private readonly IAlertSettingsStore _alertStore;
    private IReadOnlyList<DirectoryUser> _allUsers = Array.Empty<DirectoryUser>();
    private bool _loadingAlerts;

    /// <summary>Live app status - bound directly by the view.</summary>
    public AppStatus Status { get; }

    /// <summary>Current priority users, resolved to names.</summary>
    public ObservableCollection<DirectoryUser> PriorityUsers { get; } = new();

    /// <summary>Directory matches for the current search text (excludes existing priority users).</summary>
    public ObservableCollection<DirectoryUser> SearchResults { get; } = new();

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _directoryStatus = string.Empty;

    // "Notify me about" settings, persisted to the Alerts config section.
    [ObservableProperty]
    private bool _notifyHuddles;

    [ObservableProperty]
    private bool _notifyMentions;

    [ObservableProperty]
    private bool _notifyDirectMessages;

    [ObservableProperty]
    private bool _notifyKeywords;

    [ObservableProperty]
    private string _keywordsText = string.Empty;

    public DashboardViewModel(
        AppStatus status,
        IUserDirectory directory,
        IPrioritySettingsStore priorityStore,
        IAlertSettingsStore alertStore)
    {
        Status = status;
        _directory = directory;
        _priorityStore = priorityStore;
        _alertStore = alertStore;
        LoadAlerts();
    }

    private void LoadAlerts()
    {
        _loadingAlerts = true;
        var a = _alertStore.Current;
        NotifyHuddles = a.Huddles;
        NotifyMentions = a.Mentions;
        NotifyDirectMessages = a.DirectMessages;
        NotifyKeywords = a.Keywords;
        KeywordsText = string.Join(", ", a.KeywordList);
        _loadingAlerts = false;
    }

    partial void OnNotifyHuddlesChanged(bool value) => _ = SaveAlertsAsync();
    partial void OnNotifyMentionsChanged(bool value) => _ = SaveAlertsAsync();
    partial void OnNotifyDirectMessagesChanged(bool value) => _ = SaveAlertsAsync();
    partial void OnNotifyKeywordsChanged(bool value) => _ = SaveAlertsAsync();
    partial void OnKeywordsTextChanged(string value) => _ = SaveAlertsAsync();

    private async Task SaveAlertsAsync()
    {
        if (_loadingAlerts)
            return;

        var options = new AlertOptions
        {
            Huddles = NotifyHuddles,
            Mentions = NotifyMentions,
            DirectMessages = NotifyDirectMessages,
            Keywords = NotifyKeywords,
            KeywordList = KeywordsText
                .Split(new[] { ',', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList()
        };

        try
        {
            await _alertStore.SaveAsync(options);
        }
        catch (Exception ex)
        {
            DirectoryStatus = $"Couldn't save alert settings: {ex.Message}";
        }
    }

    /// <summary>Loads the directory and current priority list. Called when the window opens.</summary>
    public async Task LoadAsync()
    {
        IsLoading = true;
        DirectoryStatus = "Loading workspace members…";
        try
        {
            _allUsers = await _directory.GetUsersAsync();

            PriorityUsers.Clear();
            foreach (var id in _priorityStore.CurrentUserIds)
            {
                var match = _allUsers.FirstOrDefault(u => u.Id == id);
                PriorityUsers.Add(match ?? new DirectoryUser(id, id, null, null));
            }

            DirectoryStatus = _allUsers.Count > 0
                ? $"{_allUsers.Count} workspace members available."
                : "Couldn't load members — is the app connected to Slack?";
            UpdateSearch();
        }
        catch (Exception ex)
        {
            DirectoryStatus = $"Error loading members: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    partial void OnSearchTextChanged(string value) => UpdateSearch();

    private void UpdateSearch()
    {
        SearchResults.Clear();
        var q = SearchText?.Trim();
        if (string.IsNullOrEmpty(q) || q.Length < 2)
            return;

        var existing = PriorityUsers.Select(u => u.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var user in _allUsers
                     .Where(u => !existing.Contains(u.Id))
                     .Where(u => u.Label.Contains(q, StringComparison.OrdinalIgnoreCase))
                     .Take(25))
            SearchResults.Add(user);
    }

    [RelayCommand]
    private async Task AddAsync(DirectoryUser? user)
    {
        if (user is null || PriorityUsers.Any(u => u.Id == user.Id))
            return;

        PriorityUsers.Add(user);
        SearchText = string.Empty;
        UpdateSearch();
        await PersistAsync();
    }

    [RelayCommand]
    private async Task RemoveAsync(DirectoryUser? user)
    {
        if (user is null)
            return;

        var existing = PriorityUsers.FirstOrDefault(u => u.Id == user.Id);
        if (existing is not null)
            PriorityUsers.Remove(existing);

        UpdateSearch();
        await PersistAsync();
    }

    private async Task PersistAsync()
    {
        try
        {
            await _priorityStore.SetAsync(PriorityUsers.Select(u => u.Id));
        }
        catch (Exception ex)
        {
            DirectoryStatus = $"Couldn't save priority users: {ex.Message}";
        }
    }
}

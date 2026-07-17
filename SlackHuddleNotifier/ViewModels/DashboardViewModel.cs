using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SlackHuddleNotifier.Services;
using System.Collections.ObjectModel;

namespace SlackHuddleNotifier.ViewModels;

/// <summary>
/// Backs the dashboard window: live status (bound straight to <see cref="AppStatus"/>) plus a
/// priority-users editor that searches the workspace directory and persists picks.
/// </summary>
public sealed partial class DashboardViewModel : ObservableObject
{
    private readonly IUserDirectory _directory;
    private readonly IPrioritySettingsStore _priorityStore;
    private IReadOnlyList<DirectoryUser> _allUsers = Array.Empty<DirectoryUser>();

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

    public DashboardViewModel(AppStatus status, IUserDirectory directory, IPrioritySettingsStore priorityStore)
    {
        Status = status;
        _directory = directory;
        _priorityStore = priorityStore;
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

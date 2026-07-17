using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace SlackHuddleNotifier.Services;

/// <summary>A workspace member, for choosing priority users by name.</summary>
public sealed record DirectoryUser(string Id, string DisplayName, string? RealName, string? AvatarUrl)
{
    /// <summary>Label for display/search: "Display Name (Real Name)" when they differ.</summary>
    public string Label =>
        string.IsNullOrWhiteSpace(RealName) || string.Equals(RealName, DisplayName, StringComparison.OrdinalIgnoreCase)
            ? DisplayName
            : $"{DisplayName} ({RealName})";
}

/// <summary>Looks up workspace members so priority users can be picked by name.</summary>
public interface IUserDirectory
{
    /// <summary>True when this directory can actually reach the workspace (e.g. has API access).</summary>
    bool IsAvailable { get; }

    /// <summary>Fetches and caches the member list; pass <paramref name="forceRefresh"/> to re-pull.</summary>
    Task<IReadOnlyList<DirectoryUser>> GetUsersAsync(bool forceRefresh = false, CancellationToken ct = default);

    /// <summary>Resolves a single user id to a directory entry (for showing existing priority ids by name).</summary>
    Task<DirectoryUser?> FindByIdAsync(string userId, CancellationToken ct = default);
}

/// <summary>
/// <see cref="IUserDirectory"/> backed by the unofficial client API's <c>users.list</c>,
/// using the same live credentials as huddle detection. Used in SlackClientApi mode.
/// </summary>
public sealed class SlackWebUserDirectory : IUserDirectory
{
    private const int PageLimit = 200;
    private const int MaxUsers = 5000; // safety cap on pagination

    private readonly ISlackCredentialProvider _credentials;
    private readonly ILogger<SlackWebUserDirectory> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<DirectoryUser>? _cache;

    public SlackWebUserDirectory(ISlackCredentialProvider credentials, ILogger<SlackWebUserDirectory> logger)
    {
        _credentials = credentials;
        _logger = logger;
    }

    public bool IsAvailable => true;

    public async Task<IReadOnlyList<DirectoryUser>> GetUsersAsync(bool forceRefresh = false, CancellationToken ct = default)
    {
        if (!forceRefresh && _cache is not null)
            return _cache;

        await _gate.WaitAsync(ct);
        try
        {
            if (!forceRefresh && _cache is not null)
                return _cache;

            var creds = await _credentials.GetCredentialsAsync(false, ct);
            if (creds is null)
                return _cache ?? Array.Empty<DirectoryUser>();

            using var client = new SlackWebClient(creds.Token, creds.Cookie, creds.WorkspaceUrl);
            var users = new List<DirectoryUser>();
            string? cursor = null;
            do
            {
                var resp = await client.UsersListAsync(cursor, PageLimit, ct);
                if (!resp.Bool("ok"))
                {
                    _logger.LogWarning("users.list failed: {Error}.", resp.Str("error") ?? "unknown");
                    break;
                }

                if (resp.TryGetProperty("members", out var members) && members.ValueKind == JsonValueKind.Array)
                {
                    foreach (var m in members.EnumerateArray())
                    {
                        var u = ToDirectoryUser(m);
                        if (u is not null)
                            users.Add(u);
                    }
                }

                cursor = resp.Obj("response_metadata")?.Str("next_cursor");
            }
            while (!string.IsNullOrEmpty(cursor) && users.Count < MaxUsers && !ct.IsCancellationRequested);

            _cache = users
                .OrderBy(u => u.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            _logger.LogInformation("Loaded {Count} workspace members for the directory.", _cache.Count);
            return _cache;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<DirectoryUser?> FindByIdAsync(string userId, CancellationToken ct = default)
    {
        var all = await GetUsersAsync(false, ct);
        return all.FirstOrDefault(u => string.Equals(u.Id, userId, StringComparison.OrdinalIgnoreCase));
    }

    private static DirectoryUser? ToDirectoryUser(JsonElement m)
    {
        var id = m.Str("id");
        if (id is null || m.Bool("deleted") || m.Bool("is_bot") || id == "USLACKBOT")
            return null; // hide deactivated accounts and bots

        var profile = m.Obj("profile");
        var display = FirstNonEmpty(profile?.Str("display_name"), profile?.Str("real_name"), m.Str("name")) ?? id;
        var real = profile?.Str("real_name");
        var avatar = FirstNonEmpty(profile?.Str("image_72"), profile?.Str("image_48"));
        return new DirectoryUser(id, display, real, avatar);
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}

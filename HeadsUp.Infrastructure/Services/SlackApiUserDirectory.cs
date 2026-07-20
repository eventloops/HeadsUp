using Microsoft.Extensions.Logging;
using SlackNet;

namespace HeadsUp.Services;

/// <summary>
/// <see cref="IUserDirectory"/> backed by the official Web API's <c>users.list</c>, using the
/// same OAuth credentials as huddle detection. The SlackApi-mode counterpart to
/// <see cref="SlackWebUserDirectory"/>; without it, picking priority users by name has no
/// address book to search in this mode.
/// </summary>
public sealed class SlackApiUserDirectory : IUserDirectory
{
    private const int PageLimit = 200;
    private const int MaxUsers = 5000; // safety cap on pagination

    private readonly ISlackAppCredentialProvider _credentials;
    private readonly ISlackClientFactory _clientFactory;
    private readonly ILogger<SlackApiUserDirectory> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<DirectoryUser>? _cache;

    public SlackApiUserDirectory(
        ISlackAppCredentialProvider credentials,
        ISlackClientFactory clientFactory,
        ILogger<SlackApiUserDirectory> logger)
    {
        _credentials = credentials;
        _clientFactory = clientFactory;
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

            var creds = await _credentials.GetExistingCredentialsAsync(ct);
            if (creds is null)
                return _cache ?? Array.Empty<DirectoryUser>();

            var api = _clientFactory.CreateApiClient(creds);
            var users = new List<DirectoryUser>();
            string? cursor = null;
            do
            {
                var page = await api.Users.List(cursor: cursor, limit: PageLimit, cancellationToken: ct);
                foreach (var m in page.Members)
                {
                    var u = ToDirectoryUser(m);
                    if (u is not null)
                        users.Add(u);
                }

                cursor = page.ResponseMetadata?.NextCursor;
            }
            while (!string.IsNullOrEmpty(cursor) && users.Count < MaxUsers && !ct.IsCancellationRequested);

            _cache = users
                .OrderBy(u => u.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            _logger.LogInformation("Loaded {Count} workspace members for the directory.", _cache.Count);
            return _cache;
        }
        catch (Exception ex)
        {
            // A directory that can't load should degrade to "search finds nothing", not take
            // the dashboard down with it.
            _logger.LogWarning(ex, "users.list failed; the directory will be empty.");
            return _cache ?? Array.Empty<DirectoryUser>();
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

    internal static DirectoryUser? ToDirectoryUser(User m)
    {
        if (string.IsNullOrEmpty(m.Id) || m.Deleted || m.IsBot || m.Id == "USLACKBOT")
            return null; // hide deactivated accounts and bots

        var display = FirstNonEmpty(m.Profile?.DisplayName, m.Profile?.RealName, m.Name) ?? m.Id;
        var real = m.Profile?.RealName;
        var avatar = FirstNonEmpty(m.Profile?.Image72, m.Profile?.Image48);
        return new DirectoryUser(m.Id, display, real, avatar);
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}

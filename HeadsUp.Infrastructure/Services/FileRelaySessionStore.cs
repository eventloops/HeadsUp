using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace HeadsUp.Services;

/// <summary>
/// Stores the relay session encrypted under the user's local app data, alongside — but separate
/// from — the Slack token store. Separate because they are different credentials with different
/// lifetimes: in relay mode the desktop holds no Slack token at all.
/// </summary>
public sealed class FileRelaySessionStore : IRelaySessionStore
{
    private readonly ITokenProtector _protector;
    private readonly ILogger<FileRelaySessionStore> _logger;
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FileRelaySessionStore(
        ITokenProtector protector, ILogger<FileRelaySessionStore> logger, string? path = null)
    {
        _protector = protector;
        _logger = logger;
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HeadsUp", "relay-session.dat");
    }

    public async Task<RelaySession?> LoadAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!File.Exists(_path))
                return null;

            var cipher = await File.ReadAllBytesAsync(_path, ct);
            var json = Encoding.UTF8.GetString(_protector.Unprotect(cipher));
            return JsonSerializer.Deserialize<RelaySession>(json);
        }
        catch (Exception ex)
        {
            // Undecryptable (copied from another machine) or corrupt: treat as not signed in
            // rather than wedging startup.
            _logger.LogWarning(ex, "Could not read the stored relay session; a fresh sign-in is needed.");
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(RelaySession session, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var plaintext = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(session));
            await File.WriteAllBytesAsync(_path, _protector.Protect(plaintext), ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ClearAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (File.Exists(_path))
                File.Delete(_path);
        }
        finally
        {
            _gate.Release();
        }
    }
}

/// <summary>
/// The directory for relay mode, where the desktop holds no Slack token and so cannot search
/// the workspace itself.
///
/// It reports <see cref="IsAvailable"/> false rather than pretending: picking priority people by
/// name needs a lookup this mode can't perform locally. Registering it is what stops the
/// dashboard failing to resolve <see cref="IUserDirectory"/> outright.
/// </summary>
public sealed class UnavailableUserDirectory : IUserDirectory
{
    public bool IsAvailable => false;

    public Task<IReadOnlyList<DirectoryUser>> GetUsersAsync(bool forceRefresh = false, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<DirectoryUser>>(Array.Empty<DirectoryUser>());

    public Task<DirectoryUser?> FindByIdAsync(string userId, CancellationToken ct = default) =>
        Task.FromResult<DirectoryUser?>(null);
}

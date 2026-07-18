using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace HeadsUp.Services;

/// <summary>
/// The persisted result of a Slack sign-in, plus the app identifiers needed to renew it.
/// </summary>
/// <param name="ClientId">The user's own Slack app client id (not a secret, but kept together).</param>
/// <param name="AppToken">App-level token (<c>xapp-…</c>) for Socket Mode. Hand-entered; never rotates.</param>
/// <param name="UserToken">User OAuth token (<c>xoxp-…</c>).</param>
/// <param name="RefreshToken">
/// Refresh token. Custom-URI-scheme PKCE flows <i>always</i> issue rotating tokens, so this is
/// expected to be present and must be persisted alongside the access token.
/// </param>
/// <param name="ExpiresAt">When <paramref name="UserToken"/> stops working, or null if it doesn't expire.</param>
public sealed record SlackTokenSet(
    string ClientId,
    string AppToken,
    string UserToken,
    string? RefreshToken,
    DateTimeOffset? ExpiresAt)
{
    /// <summary>
    /// True when the access token is expired or close enough that a call could land after it
    /// lapses. The skew is deliberately generous — refreshing early is cheap, a missed huddle
    /// alert is not.
    /// </summary>
    public bool NeedsRefresh(TimeSpan skew) =>
        ExpiresAt is { } exp && DateTimeOffset.UtcNow + skew >= exp;
}

/// <summary>Persists the Slack app token set between runs.</summary>
public interface ISlackTokenStore
{
    Task<SlackTokenSet?> LoadAsync(CancellationToken ct = default);
    Task SaveAsync(SlackTokenSet tokens, CancellationToken ct = default);
    Task ClearAsync(CancellationToken ct = default);
}

/// <summary>
/// Encrypts and decrypts at-rest secrets. Implemented per-platform (DPAPI on Windows) so the
/// store itself stays cross-platform.
/// </summary>
public interface ITokenProtector
{
    byte[] Protect(byte[] plaintext);
    byte[] Unprotect(byte[] ciphertext);
}

/// <summary>
/// A <see cref="ISlackTokenStore"/> that writes one encrypted file under the user's local app
/// data. Deliberately not <c>appsettings.Local.json</c>: these are live OAuth credentials that
/// rotate on their own, so they don't belong in a hand-edited, hot-reloaded config file.
/// </summary>
public sealed class FileSlackTokenStore : ISlackTokenStore
{
    private readonly ITokenProtector _protector;
    private readonly ILogger<FileSlackTokenStore> _logger;
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FileSlackTokenStore(ITokenProtector protector, ILogger<FileSlackTokenStore> logger, string? path = null)
    {
        _protector = protector;
        _logger = logger;
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HeadsUp", "slack-app-tokens.dat");
    }

    public async Task<SlackTokenSet?> LoadAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!File.Exists(_path))
                return null;

            var cipher = await File.ReadAllBytesAsync(_path, ct);
            var json = Encoding.UTF8.GetString(_protector.Unprotect(cipher));
            return JsonSerializer.Deserialize<SlackTokenSet>(json);
        }
        catch (Exception ex)
        {
            // A corrupt or undecryptable file (e.g. copied from another machine — DPAPI is
            // user+machine scoped) must not wedge startup; treat it as "not signed in".
            _logger.LogWarning(ex, "Could not read stored Slack tokens; a fresh sign-in will be needed.");
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(SlackTokenSet tokens, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var plaintext = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(tokens));
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

using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using HeadsUp.Configuration;

namespace HeadsUp.Services;

/// <summary>Persists the relay session between runs.</summary>
public interface IRelaySessionStore
{
    Task<RelaySession?> LoadAsync(CancellationToken ct = default);
    Task SaveAsync(RelaySession session, CancellationToken ct = default);
    Task ClearAsync(CancellationToken ct = default);
}

/// <summary>
/// Signs the user in to the relay through their browser, and keeps the resulting session.
///
/// Without this the SlackRelay connector was unreachable: the only provider was the config one,
/// which needed a session token hand-extracted from a redirect plus a user id that only exists
/// inside that token's claims — so the app sat on "Not signed in" with nothing anywhere to
/// click.
///
/// The desktop holds no Slack credential in this mode. It gets a relay session, and the relay
/// keeps the Slack side; that is the point of relay mode, and it is why the store here is
/// separate from the Slack token store.
/// </summary>
public sealed class RelaySignInCredentialProvider : IRelayCredentialProvider
{
    /// <summary>Distinct from the Slack sign-in port so both can be in flight at once.</summary>
    public const int CallbackPort = 32742;

    private static string ReturnTo => $"http://localhost:{CallbackPort}/relay";

    private readonly IRelaySessionStore _store;
    private readonly LoopbackOAuthListener _listener;
    private readonly IBrowserLauncher _browser;
    private readonly ConnectionGate _gate;
    private readonly IOptionsMonitor<RelayClientOptions> _options;
    private readonly AppStatus _status;
    private readonly ILogger<RelaySignInCredentialProvider> _logger;
    private readonly SemaphoreSlim _signIn = new(1, 1);

    private RelaySession? _cached;

    public RelaySignInCredentialProvider(
        IRelaySessionStore store,
        LoopbackOAuthListener listener,
        IBrowserLauncher browser,
        ConnectionGate gate,
        IOptionsMonitor<RelayClientOptions> options,
        AppStatus status,
        ILogger<RelaySignInCredentialProvider> logger)
    {
        _store = store;
        _listener = listener;
        _browser = browser;
        _gate = gate;
        _options = options;
        _status = status;
        _logger = logger;
    }

    public string SourceDescription => "Heads-Up relay (browser sign-in)";

    public async Task<RelaySession?> GetSessionAsync(bool forceRefresh, CancellationToken ct)
    {
        if (forceRefresh)
        {
            // The relay refused what we had, so keeping it would just loop.
            _cached = null;
            await _store.ClearAsync(ct);
        }

        _cached ??= await _store.LoadAsync(ct);
        if (_cached is not null)
            return _cached;

        // A real sign-in opens a browser, so hold until the user asks to connect.
        await _gate.WaitAsync(ct);

        await _signIn.WaitAsync(ct);
        try
        {
            // Another attempt may have finished while we waited.
            _cached ??= await _store.LoadAsync(ct);
            if (_cached is not null)
                return _cached;

            var session = await SignInAsync(ct);
            if (session is null)
                return null;

            await _store.SaveAsync(session, ct);
            _cached = session;
            return session;
        }
        finally
        {
            _signIn.Release();
        }
    }

    private async Task<RelaySession?> SignInAsync(CancellationToken ct)
    {
        var baseUrl = _options.CurrentValue.AuthStartUrl;
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            Status.SetConnection(false, "Relay address not configured");
            _logger.LogWarning("Relay:BaseUrl is not set, so there is nowhere to sign in.");
            return null;
        }

        // State is echoed back by the relay so a stray or stale callback can't be mistaken for
        // this attempt; the relay signs it in transit.
        var state = Guid.NewGuid().ToString("N");
        var url = $"{baseUrl}?return_to={Uri.EscapeDataString(ReturnTo)}&state={state}";

        Status.SetConnection(false, "Waiting for sign-in…");

        // Listen before opening the browser so a fast redirect can't arrive first.
        var callbackTask = _listener.WaitForSessionAsync(CallbackPort, ct);
        _browser.Open(url);

        var token = await callbackTask;
        if (string.IsNullOrWhiteSpace(token))
        {
            _logger.LogInformation("Relay sign-in was not completed.");
            return null;
        }

        if (ReadSubject(token) is not { } userId)
        {
            _logger.LogWarning("The relay returned a session with no subject claim.");
            return null;
        }

        _logger.LogInformation("Signed in to the relay as {UserId}.", userId);
        return new RelaySession(token, userId);
    }

    /// <summary>
    /// Reads the Slack user id from the session's <c>sub</c> claim.
    ///
    /// Deliberately not verified here: the desktop has no signing key, and nothing security-
    /// relevant hangs on this value locally — the relay re-validates the token on every
    /// connection and decides for itself whose events to send. The id is used only to suppress
    /// the user's own messages and to spot mentions of them.
    /// </summary>
    internal static string? ReadSubject(string jwt)
    {
        try
        {
            var parts = jwt.Split('.');
            if (parts.Length != 3)
                return null;

            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload += new string('=', (4 - payload.Length % 4) % 4);

            using var document = JsonDocument.Parse(Convert.FromBase64String(payload));
            return document.RootElement.TryGetProperty("sub", out var sub) ? sub.GetString() : null;
        }
        catch
        {
            return null;
        }
    }

    private SourceStatus Status => _status.GetOrAdd(SlackSource.SourceId, SlackSource.Name);
}

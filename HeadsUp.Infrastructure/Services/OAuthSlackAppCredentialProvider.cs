using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using HeadsUp.Configuration;

namespace HeadsUp.Services;

/// <summary>
/// Opens the user's browser to sign in to their own Slack app, then keeps the resulting token
/// alive. This is the sanctioned counterpart to <see cref="WebView2SlackCredentialProvider"/>
/// in the client-session mode.
///
/// Ordering matters here: it waits on <see cref="ConnectionGate"/> before doing anything
/// visible, so the welcome screen appears before a browser window is thrown at a first-run
/// user. It's also where token rotation is absorbed — custom-scheme/loopback PKCE flows always
/// issue rotating tokens, so a stored token is refreshed rather than re-prompting for sign-in.
/// </summary>
public sealed class OAuthSlackAppCredentialProvider : ISlackAppCredentialProvider
{
    /// <summary>Refresh this far ahead of expiry rather than racing it.</summary>
    private static readonly TimeSpan RefreshSkew = TimeSpan.FromMinutes(5);

    private readonly ISlackTokenStore _store;
    private readonly SlackOAuthClient _oauth;
    private readonly LoopbackOAuthListener _listener;
    private readonly IBrowserLauncher _browser;
    private readonly ISlackAppSetupPrompt _setupPrompt;
    private readonly ConnectionGate _gate;
    private readonly IOptionsMonitor<SlackOptions> _options;
    private readonly AppStatus _status;
    private readonly ILogger<OAuthSlackAppCredentialProvider> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    // Separate from _lock so a long interactive sign-in serialises without blocking reads.
    private readonly SemaphoreSlim _signIn = new(1, 1);

    private SlackTokenSet? _cached;

    public OAuthSlackAppCredentialProvider(
        ISlackTokenStore store,
        SlackOAuthClient oauth,
        LoopbackOAuthListener listener,
        IBrowserLauncher browser,
        ISlackAppSetupPrompt setupPrompt,
        ConnectionGate gate,
        IOptionsMonitor<SlackOptions> options,
        AppStatus status,
        ILogger<OAuthSlackAppCredentialProvider> logger)
    {
        _store = store;
        _oauth = oauth;
        _listener = listener;
        _browser = browser;
        _setupPrompt = setupPrompt;
        _gate = gate;
        _options = options;
        _status = status;
        _logger = logger;
    }

    public string SourceDescription => "Slack app (OAuth)";

    public async Task<SlackAppCredentials?> GetCredentialsAsync(bool forceRefresh, CancellationToken ct)
    {
        // The lock covers only the store read and the token renewal — both quick. The
        // interactive sign-in below deliberately runs outside it: holding a provider-wide lock
        // for however long a browser sign-in takes would block every other consumer, and the
        // workspace directory would hang instead of showing an empty list.
        var tokens = await RenewOrLoadAsync(forceRefresh, ct);
        if (tokens is not null)
            return ToCredentials(tokens);

        // Nothing usable stored: this is a real sign-in, so hold until the user asks for it.
        await _gate.WaitAsync(ct);

        await _signIn.WaitAsync(ct);
        try
        {
            // Another caller may have completed a sign-in while we waited for this lock.
            var existing = await RenewOrLoadAsync(false, ct);
            if (existing is not null)
                return ToCredentials(existing);

            var signedIn = await SignInAsync(ct);
            if (signedIn is null)
                return null;

            await _lock.WaitAsync(ct);
            try
            {
                _cached = signedIn;
            }
            finally
            {
                _lock.Release();
            }

            return ToCredentials(signedIn);
        }
        finally
        {
            _signIn.Release();
        }
    }

    public async Task<SlackAppCredentials?> GetExistingCredentialsAsync(CancellationToken ct)
    {
        var tokens = await RenewOrLoadAsync(false, ct);
        return tokens is null ? null : ToCredentials(tokens);
    }

    /// <summary>
    /// Loads the stored tokens and renews them if needed. Returns null when there is nothing
    /// usable — the caller decides whether that warrants an interactive sign-in.
    /// </summary>
    private async Task<SlackTokenSet?> RenewOrLoadAsync(bool forceRefresh, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var tokens = _cached ??= await _store.LoadAsync(ct);

            if (tokens is not null && (forceRefresh || tokens.NeedsRefresh(RefreshSkew)))
                tokens = await TryRefreshAsync(tokens, ct);

            _cached = tokens;
            return tokens;
        }
        finally
        {
            _lock.Release();
        }
    }

    private static SlackAppCredentials ToCredentials(SlackTokenSet tokens) =>
        new(tokens.ClientId, tokens.AppToken, tokens.UserToken);

    /// <summary>
    /// Renews an access token, returning null if it can't be renewed — a refresh token that
    /// has been revoked or aged out (Slack expires them after 30 days) means the user has to
    /// sign in again, which is a normal outcome rather than an error.
    /// </summary>
    private async Task<SlackTokenSet?> TryRefreshAsync(SlackTokenSet tokens, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tokens.RefreshToken))
            return tokens.NeedsRefresh(RefreshSkew) ? null : tokens;

        try
        {
            var renewed = await _oauth.RefreshAsync(tokens.ClientId, tokens.RefreshToken, ct);

            // Slack invalidates the old refresh token, so the new one must be persisted.
            var updated = tokens with
            {
                UserToken = renewed.AccessToken,
                RefreshToken = renewed.RefreshToken ?? tokens.RefreshToken,
                ExpiresAt = renewed.ExpiresAt,
            };

            await _store.SaveAsync(updated, ct);
            _logger.LogInformation("Renewed the Slack user token.");
            return updated;
        }
        catch (InvalidOperationException ex)
        {
            // Slack answered ok:false — the refresh token is revoked or has aged out (they
            // expire after 30 days). Only this genuinely means "sign in again", so only this
            // discards the stored credentials.
            _logger.LogWarning(ex, "Slack rejected the refresh token; a fresh sign-in is needed.");
            await _store.ClearAsync(CancellationToken.None);
            _cached = null;
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Transport failure: offline, DNS, Slack having a moment. The stored token is very
            // probably still good, so keep it and let the next round try again. Wiping here
            // would turn a brief network blip into a mandatory browser sign-in.
            _logger.LogWarning(ex, "Could not reach Slack to renew the token; keeping it and retrying.");
            return tokens.NeedsRefresh(TimeSpan.Zero) ? null : tokens;
        }
    }

    /// <summary>Runs the full PKCE authorization: browser, loopback redirect, code exchange.</summary>
    private async Task<SlackTokenSet?> SignInAsync(CancellationToken ct)
    {
        var setup = await GetSetupAsync(ct);
        if (setup is null)
            return null;

        var pkce = PkcePair.Create();
        var state = Guid.NewGuid().ToString("N");
        var url = SlackOAuthClient.BuildAuthorizeUrl(
            setup.ClientId, LoopbackOAuthListener.RedirectUri, pkce, state);

        Status.SetConnection(false, "Waiting for sign-in…");

        // Start listening before the browser opens, so a fast redirect can't beat us to it.
        var callbackTask = _listener.WaitForCallbackAsync(state, ct);
        _browser.Open(url);

        var callback = await callbackTask;
        if (callback is null)
        {
            _logger.LogInformation("Slack sign-in was not completed.");
            return null;
        }

        var tokens = await _oauth.ExchangeCodeAsync(
            setup.ClientId, callback.Code, LoopbackOAuthListener.RedirectUri, pkce, ct);

        var set = new SlackTokenSet(
            setup.ClientId, setup.AppToken, tokens.AccessToken, tokens.RefreshToken, tokens.ExpiresAt);

        await _store.SaveAsync(set, ct);
        _logger.LogInformation("Slack sign-in complete; tokens stored.");
        return set;
    }

    /// <summary>
    /// Resolves the app's client id and app-level token. Neither can come from OAuth — Slack
    /// only ever mints app-level tokens in its config UI — so they come from configuration, or
    /// failing that by asking the user (which is where the guided "create your app" flow lives).
    /// </summary>
    private async Task<SlackAppSetup?> GetSetupAsync(CancellationToken ct)
    {
        var opts = _options.CurrentValue;
        if (!string.IsNullOrWhiteSpace(opts.ClientId) && !string.IsNullOrWhiteSpace(opts.AppToken))
            return new SlackAppSetup(opts.ClientId, opts.AppToken);

        Status.SetConnection(false, "Slack app not set up");

        var entered = await _setupPrompt.RequestAsync(ct);
        if (entered is null)
        {
            _logger.LogWarning(
                "No Slack app configured. Create your Heads-Up Slack app, then enter its " +
                "client id and app-level token (xapp-…) to connect.");
            return null;
        }

        return entered;
    }

    private SourceStatus Status => _status.GetOrAdd(SlackSource.SourceId, SlackSource.Name);
}

/// <summary>The two values a user must supply from their own Slack app's settings page.</summary>
public sealed record SlackAppSetup(string ClientId, string AppToken);

/// <summary>
/// Asks the user to create their Heads-Up Slack app and hand back its client id and app-level
/// token. Implemented by the UI head; <see cref="NoSlackAppSetupPrompt"/> stands in when there
/// is no one to ask.
/// </summary>
public interface ISlackAppSetupPrompt
{
    /// <summary>Returns the entered values, or null if the user cancelled.</summary>
    Task<SlackAppSetup?> RequestAsync(CancellationToken ct);
}

/// <summary>
/// The headless fallback: never prompts, so the connector simply stays unconfigured until the
/// tokens are supplied through <c>appsettings</c>.
/// </summary>
public sealed class NoSlackAppSetupPrompt : ISlackAppSetupPrompt
{
    public Task<SlackAppSetup?> RequestAsync(CancellationToken ct) => Task.FromResult<SlackAppSetup?>(null);
}

/// <summary>Opens a URL in the user's default browser. Abstracted so the flow stays testable.</summary>
public interface IBrowserLauncher
{
    void Open(string url);
}

/// <summary>Launches the system default browser via the shell.</summary>
public sealed class SystemBrowserLauncher : IBrowserLauncher
{
    public void Open(string url) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
}

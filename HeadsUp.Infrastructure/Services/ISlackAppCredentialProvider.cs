using Microsoft.Extensions.Options;
using HeadsUp.Configuration;

namespace HeadsUp.Services;

/// <summary>
/// Credentials for the sanctioned Slack app (Socket Mode) path.
/// </summary>
/// <param name="ClientId">The Slack app's client id, used to start the OAuth flow.</param>
/// <param name="AppToken">The app-level token (<c>xapp-…</c>) that opens the Socket Mode connection.</param>
/// <param name="UserToken">The user OAuth token (<c>xoxp-…</c>) the Web API calls are made with.</param>
public sealed record SlackAppCredentials(string ClientId, string AppToken, string UserToken);

/// <summary>
/// Supplies Slack app credentials to <see cref="SlackHuddleMonitor"/>. Abstracted for the
/// same reason as <see cref="ISlackCredentialProvider"/>: the values can come from static
/// config (hand-pasted tokens) or from a live source that signs the user in and renews
/// tokens on its own (the WPF head's PKCE OAuth provider).
///
/// The live implementation is also where the connect flow is gated, so this interface is
/// what keeps <c>ConnectionGate</c> — a presentation concern — out of the monitor.
/// </summary>
public interface ISlackAppCredentialProvider
{
    /// <summary>Human-readable source name, for logs and the dashboard status card.</summary>
    string SourceDescription { get; }

    /// <summary>
    /// Returns the current credentials, or <c>null</c> if none are available yet (e.g. the
    /// user has not completed sign-in). Pass <paramref name="forceRefresh"/> after an auth
    /// failure to renew rather than return a cached — possibly rotated-out — token.
    /// </summary>
    Task<SlackAppCredentials?> GetCredentialsAsync(bool forceRefresh, CancellationToken ct);
}

/// <summary>
/// The default provider: reads tokens straight from <see cref="SlackOptions"/> (i.e.
/// <c>appsettings*.json</c>). Static, so <c>forceRefresh</c> only re-reads config — there is
/// no renewal. This is the hand-pasted-token mode, kept for headless/dev use; the WPF head
/// replaces it with the OAuth provider.
/// </summary>
public sealed class ConfigSlackAppCredentialProvider : ISlackAppCredentialProvider
{
    private readonly IOptionsMonitor<SlackOptions> _options;

    public ConfigSlackAppCredentialProvider(IOptionsMonitor<SlackOptions> options) => _options = options;

    public string SourceDescription => "appsettings (Slack app tokens)";

    public Task<SlackAppCredentials?> GetCredentialsAsync(bool forceRefresh, CancellationToken ct)
    {
        // Read current so re-pasted tokens take effect on the next fetch without a restart.
        var o = _options.CurrentValue;
        if (string.IsNullOrWhiteSpace(o.AppToken) || string.IsNullOrWhiteSpace(o.UserToken))
            return Task.FromResult<SlackAppCredentials?>(null);

        return Task.FromResult<SlackAppCredentials?>(
            new SlackAppCredentials(o.ClientId, o.AppToken, o.UserToken));
    }
}

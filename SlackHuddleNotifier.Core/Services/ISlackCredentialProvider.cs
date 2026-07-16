using Microsoft.Extensions.Options;
using SlackHuddleNotifier.Configuration;

namespace SlackHuddleNotifier.Services;

/// <summary>A live Slack client-session credential: the client token plus the cookie header.</summary>
/// <param name="Token">The <c>xoxc-…</c> client token.</param>
/// <param name="Cookie">The cookie header value, e.g. <c>d=xoxd-…; d-s=…</c> (or a bare <c>xoxd-…</c>).</param>
/// <param name="WorkspaceUrl">Workspace base URL, or empty to use the shared host.</param>
public sealed record SlackCredentials(string Token, string Cookie, string WorkspaceUrl);

/// <summary>
/// Supplies Slack client-session credentials to <see cref="SlackWebHuddleSource"/>.
/// Abstracted so the credential can come from static config (browser-paste) or a
/// live source that survives session rotation (e.g. an embedded WebView2 login).
/// </summary>
public interface ISlackCredentialProvider
{
    /// <summary>Human-readable source name, for logs.</summary>
    string SourceDescription { get; }

    /// <summary>
    /// Returns the current credentials, or <c>null</c> if none are available yet
    /// (e.g. the user has not signed in). Pass <paramref name="forceRefresh"/> after an
    /// auth failure to re-read live values rather than return a cached/rotated set.
    /// </summary>
    Task<SlackCredentials?> GetCredentialsAsync(bool forceRefresh, CancellationToken ct);
}

/// <summary>
/// The default provider: reads the token/cookie straight from <see cref="SlackClientOptions"/>
/// (i.e. <c>appsettings*.json</c>). Static, so <c>forceRefresh</c> is a no-op; when the
/// session rotates the user re-pastes. This is the browser-paste mode.
/// </summary>
public sealed class ConfigSlackCredentialProvider : ISlackCredentialProvider
{
    private readonly SlackClientOptions _options;

    public ConfigSlackCredentialProvider(IOptions<SlackClientOptions> options) => _options = options.Value;

    public string SourceDescription => "appsettings (SlackClient)";

    public Task<SlackCredentials?> GetCredentialsAsync(bool forceRefresh, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.ClientToken) || string.IsNullOrWhiteSpace(_options.Cookie))
            return Task.FromResult<SlackCredentials?>(null);

        return Task.FromResult<SlackCredentials?>(
            new SlackCredentials(_options.ClientToken, _options.Cookie, _options.WorkspaceUrl));
    }
}

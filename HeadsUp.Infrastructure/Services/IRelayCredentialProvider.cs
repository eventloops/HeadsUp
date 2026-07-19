using Microsoft.Extensions.Options;
using HeadsUp.Configuration;

namespace HeadsUp.Services;

/// <summary>A signed-in relay session: the token, and who the relay says it belongs to.</summary>
public sealed record RelaySession(string Token, string SlackUserId);

/// <summary>
/// Supplies the relay session the desktop connects with. Abstracted for the same reason as the
/// Slack credential providers: the session can come from static config (headless, testing) or
/// from a live sign-in that opens a browser — and the live one is where the connect flow is
/// gated, keeping presentation concerns out of the detection source.
/// </summary>
public interface IRelayCredentialProvider
{
    /// <summary>Human-readable source name, for logs and the dashboard status card.</summary>
    string SourceDescription { get; }

    /// <summary>
    /// The current session, or null if the user hasn't signed in. Pass
    /// <paramref name="forceRefresh"/> after a rejected connection to sign in again rather
    /// than retry a session the relay has stopped honouring.
    /// </summary>
    Task<RelaySession?> GetSessionAsync(bool forceRefresh, CancellationToken ct);
}

/// <summary>
/// Reads a session straight from configuration. Useful for headless runs and testing; the WPF
/// head replaces it with one that can actually sign the user in.
/// </summary>
public sealed class ConfigRelayCredentialProvider : IRelayCredentialProvider
{
    private readonly IOptionsMonitor<RelayClientOptions> _options;

    public ConfigRelayCredentialProvider(IOptionsMonitor<RelayClientOptions> options) => _options = options;

    public string SourceDescription => "appsettings (relay session)";

    public Task<RelaySession?> GetSessionAsync(bool forceRefresh, CancellationToken ct)
    {
        var o = _options.CurrentValue;
        if (string.IsNullOrWhiteSpace(o.Session) || string.IsNullOrWhiteSpace(o.SlackUserId))
            return Task.FromResult<RelaySession?>(null);

        return Task.FromResult<RelaySession?>(new RelaySession(o.Session, o.SlackUserId));
    }
}

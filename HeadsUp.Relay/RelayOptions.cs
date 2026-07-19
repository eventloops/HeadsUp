namespace HeadsUp.Relay;

/// <summary>
/// The relay's configuration: the org's single Slack app, as created once by an admin.
/// </summary>
public sealed class RelayOptions
{
    public const string SectionName = "Relay";

    /// <summary>
    /// App-level token (<c>xapp-…</c>) for the org's Slack app. This is the credential that
    /// makes the relay necessary: Slack mints exactly one per app and distributes events
    /// arbitrarily across connections sharing it, so it must live in one place.
    /// </summary>
    public string AppToken { get; set; } = string.Empty;

    /// <summary>
    /// The Slack app's client id. Used as the expected audience when verifying desktops'
    /// OpenID Connect id_tokens, so a token issued to another app can't be replayed here.
    /// </summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>
    /// A bot or user token for calling <c>apps.event.authorizations.list</c>, which is how a
    /// truncated authorizations list is expanded. Without it, events visible to many people
    /// reach only the one user Slack named inline.
    /// </summary>
    public string ApiToken { get; set; } = string.Empty;

    /// <summary>
    /// The Slack app's client secret. The relay is the OAuth client — it performs the code
    /// exchange so users face a single consent screen — which is why a server-side secret is
    /// appropriate here, unlike the desktop app.
    /// </summary>
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>
    /// The relay's own externally reachable base URL, e.g. <c>https://headsup.internal</c>.
    /// Slack redirects here after consent, so it must match the app's registered redirect URL.
    /// Only OAuth needs this reachable — event ingress dials out over Socket Mode.
    /// </summary>
    public string PublicUrl { get; set; } = string.Empty;

    /// <summary>
    /// Signs the relay's session tokens. Generated at startup when unset, which means sessions
    /// don't survive a restart; set it in any real deployment. Rotating it invalidates every
    /// session, which is the intended revocation mechanism.
    /// </summary>
    public string SessionSigningKey { get; set; } = string.Empty;

    /// <summary>How long a desktop stays signed in before it must authorize again.</summary>
    public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromDays(30);

    /// <summary>
    /// Logs every Socket Mode frame verbatim (credentials redacted). Diagnostic aid for
    /// confirming which message subtypes Slack actually delivers here.
    /// </summary>
    public bool LogRawFrames { get; set; }

    /// <summary>
    /// How long a channel's authorization list is reused before being re-fetched.
    ///
    /// Trades API calls against staleness, and the staleness matters: until an entry expires,
    /// someone who has left a channel still receives its events. Kept to a minute or two for
    /// that reason, not tuned purely for throughput.
    /// </summary>
    public TimeSpan AuthorizationCacheTtl { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Throws if the relay can't function, so misconfiguration fails at startup.</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(AppToken))
            throw new InvalidOperationException(
                "Relay:AppToken is required (the xapp-… app-level token for your Slack app).");

        if (string.IsNullOrWhiteSpace(ClientId))
            throw new InvalidOperationException(
                "Relay:ClientId is required; without it no desktop's identity can be verified.");
    }
}

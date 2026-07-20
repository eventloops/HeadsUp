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
    ///
    /// Needs two scopes: <c>connections:write</c> to open Socket Mode, and
    /// <c>authorizations:read</c> to expand a truncated authorizations list. Without the
    /// second, events visible to many people reach only the one user Slack names inline —
    /// a partial failure rather than an obvious one.
    ///
    /// Unlike user tokens, app-level tokens don't expire or rotate, so nothing here needs
    /// renewing.
    /// </summary>
    public string AppToken { get; set; } = string.Empty;

    /// <summary>
    /// The Slack app's client id. Used as the expected audience when verifying desktops'
    /// OpenID Connect id_tokens, so a token issued to another app can't be replayed here.
    /// </summary>
    public string ClientId { get; set; } = string.Empty;

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
                "Relay:AppToken is required (the xapp-… app-level token for your Slack app, " +
                "generated with the connections:write and authorizations:read scopes).");

        if (string.IsNullOrWhiteSpace(ClientId))
            throw new InvalidOperationException(
                "Relay:ClientId is required; without it no desktop's identity can be verified.");

        // Checked at boot rather than at first sign-in. Both are only reached once a user tries
        // to connect, so leaving them out produced a relay that looked healthy for days and
        // then failed with an opaque Slack error for the first person who tried to use it.
        if (string.IsNullOrWhiteSpace(ClientSecret))
            throw new InvalidOperationException(
                "Relay:ClientSecret is required — the relay performs the OAuth code exchange, " +
                "which is what lets users sign in with a single consent screen.");

        if (string.IsNullOrWhiteSpace(PublicUrl))
            throw new InvalidOperationException(
                "Relay:PublicUrl is required (e.g. https://headsup.internal). Slack redirects " +
                "here after consent, so it must match the app's registered redirect URL.");

        if (!Uri.TryCreate(PublicUrl, UriKind.Absolute, out _))
            throw new InvalidOperationException(
                $"Relay:PublicUrl must be an absolute URL; got '{PublicUrl}'.");
    }
}

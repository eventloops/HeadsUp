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

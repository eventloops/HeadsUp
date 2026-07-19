using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace HeadsUp.Relay.Authentication;

/// <summary>
/// Fetches Slack's public signing keys from their OpenID discovery document.
///
/// <see cref="ConfigurationManager{T}"/> handles the caching and periodic refresh, which
/// matters because Slack rotates these keys: a provider that fetched once at startup would
/// start rejecting every valid token the day the key changed.
/// </summary>
public sealed class SlackJwksKeyProvider : ISlackSigningKeyProvider
{
    private const string DiscoveryUrl = "https://slack.com/.well-known/openid-configuration";

    private readonly IConfigurationManager<OpenIdConnectConfiguration> _configuration;

    public SlackJwksKeyProvider(HttpClient http)
        : this(new ConfigurationManager<OpenIdConnectConfiguration>(
            DiscoveryUrl, new OpenIdConnectConfigurationRetriever(), new HttpDocumentRetriever(http)))
    {
    }

    internal SlackJwksKeyProvider(IConfigurationManager<OpenIdConnectConfiguration> configuration) =>
        _configuration = configuration;

    public async Task<IReadOnlyList<SecurityKey>> GetKeysAsync(CancellationToken ct)
    {
        var configuration = await _configuration.GetConfigurationAsync(ct);
        return [.. configuration.SigningKeys];
    }
}

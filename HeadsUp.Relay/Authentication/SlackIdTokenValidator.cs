using System.IdentityModel.Tokens.Jwt;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace HeadsUp.Relay.Authentication;

/// <summary>Supplies the keys Slack signs its id_tokens with.</summary>
public interface ISlackSigningKeyProvider
{
    /// <summary>Slack's current public keys, from their JWKS endpoint.</summary>
    Task<IReadOnlyList<SecurityKey>> GetKeysAsync(CancellationToken ct);
}

/// <summary>
/// Establishes which Slack user a connecting desktop is.
///
/// This is the hinge the relay's whole isolation guarantee hangs on. Routing is careful to send
/// each event only to its authorized users — but "which user is this connection?" is decided
/// here, so accepting a forged or unverified identity would quietly undo all of it. The user id
/// therefore comes only from a token Slack itself signed: never from a header, a query
/// parameter, or anything else the client simply asserts.
///
/// Using OpenID Connect rather than having clients present their Slack access token also means
/// the relay never holds a credential that could act as the user against Slack.
/// </summary>
public sealed class SlackIdTokenValidator
{
    private const string Issuer = "https://slack.com";

    /// <summary>Slack puts the workspace user id in a namespaced claim as well as <c>sub</c>.</summary>
    private const string UserIdClaim = "https://slack.com/user_id";

    private readonly ISlackSigningKeyProvider _keys;
    private readonly string _clientId;
    private readonly ILogger<SlackIdTokenValidator> _logger;
    private readonly JwtSecurityTokenHandler _handler;

    public SlackIdTokenValidator(
        ISlackSigningKeyProvider keys, string clientId, ILogger<SlackIdTokenValidator> logger)
    {
        _keys = keys;
        _clientId = clientId;
        _logger = logger;

        // By default this handler renames inbound claims to the legacy WS-Security URIs, which
        // silently turns "sub" into ClaimTypes.NameIdentifier and makes the lookup below miss.
        // Keep the JWT's own claim names.
        _handler = new JwtSecurityTokenHandler();
        _handler.InboundClaimTypeMap.Clear();
    }

    /// <summary>
    /// Returns the verified Slack user id, or null if the token is not one Slack issued to this
    /// app for a real user. Every failure is null rather than an exception — a bad token is an
    /// ordinary event on a public endpoint, not an exceptional one.
    /// </summary>
    public async Task<string?> ValidateAsync(string idToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(idToken))
            return null;

        try
        {
            var parameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = Issuer,

                // Pin the audience to our app, so a token minted for a different Slack app
                // cannot be replayed here to impersonate its holder.
                ValidateAudience = true,
                ValidAudience = _clientId,

                ValidateIssuerSigningKey = true,
                IssuerSigningKeys = await _keys.GetKeysAsync(ct),

                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromMinutes(2),

                // Slack signs with RS256; accepting anything else would open the door to the
                // classic "alg: none" and HMAC-confusion downgrades.
                ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                RequireSignedTokens = true,
                RequireExpirationTime = true,
            };

            var principal = _handler.ValidateToken(idToken, parameters, out _);

            var userId = principal.FindFirst(UserIdClaim)?.Value
                         ?? principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;

            if (string.IsNullOrWhiteSpace(userId))
            {
                _logger.LogWarning("A valid Slack id_token carried no user id claim.");
                return null;
            }

            return userId;
        }
        catch (SecurityTokenException ex)
        {
            _logger.LogWarning("Rejected a Slack id_token: {Reason}.", ex.Message);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not validate a Slack id_token.");
            return null;
        }
    }
}

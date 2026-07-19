using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace HeadsUp.Relay.Authentication;

/// <summary>
/// Issues and validates the relay's own session tokens.
///
/// Slack won't allow Sign in with Slack scopes and ordinary user scopes in one authorization
/// request — it rejects the combination outright — so using OpenID Connect for identity would
/// have meant putting users through two consent screens. Instead the relay is the OAuth client:
/// it performs the code exchange, learns the user's id from Slack's own response, and issues
/// its own token. One consent screen, and identity still originates with Slack.
///
/// A relay-issued session is also better suited to a long-lived socket than an <c>id_token</c>:
/// its lifetime is ours to choose, and it can be invalidated by rotating the signing key —
/// neither of which is true of a token minted by Slack.
///
/// The token is symmetric (HMAC) deliberately: the relay is both the only issuer and the only
/// verifier, so there is nothing for a public key to buy.
/// </summary>
public sealed class RelaySessionIssuer
{
    private const string Issuer = "headsup-relay";

    private readonly SigningCredentials _credentials;
    private readonly TokenValidationParameters _validation;
    private readonly TimeSpan _lifetime;
    private readonly ILogger<RelaySessionIssuer> _logger;
    private readonly JwtSecurityTokenHandler _handler;

    public RelaySessionIssuer(string signingKey, TimeSpan lifetime, ILogger<RelaySessionIssuer> logger)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey));
        _credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        _lifetime = lifetime;
        _logger = logger;

        _handler = new JwtSecurityTokenHandler();
        // Keep the JWT's own claim names rather than remapping "sub" to a WS-Security URI.
        _handler.InboundClaimTypeMap.Clear();

        _validation = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = Issuer,
            ValidateAudience = false, // single audience: this relay
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = key,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(1),
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            RequireSignedTokens = true,
            RequireExpirationTime = true,
        };
    }

    /// <summary>Mints a session for a Slack user id established through the OAuth exchange.</summary>
    public string Issue(string slackUserId) => IssueAt(slackUserId, DateTime.UtcNow, _lifetime);

    /// <summary>
    /// Mints a session with an explicit start and lifetime. Internal so tests can produce an
    /// already-expired session without waiting for one.
    /// </summary>
    internal string IssueAt(string slackUserId, DateTime notBefore, TimeSpan lifetime)
    {
        var token = new JwtSecurityToken(
            issuer: Issuer,
            claims: [new Claim(JwtRegisteredClaimNames.Sub, slackUserId)],
            notBefore: notBefore,
            expires: notBefore.Add(lifetime),
            signingCredentials: _credentials);

        return _handler.WriteToken(token);
    }

    /// <summary>
    /// Returns the Slack user id a session is for, or null if it isn't a session this relay
    /// issued and is still valid. Every rejection is null rather than an exception: bad tokens
    /// are routine on a network-facing endpoint.
    /// </summary>
    public string? Validate(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return null;

        try
        {
            var principal = _handler.ValidateToken(token, _validation, out _);
            var userId = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;

            if (string.IsNullOrWhiteSpace(userId))
            {
                _logger.LogWarning("A valid relay session carried no subject.");
                return null;
            }

            return userId;
        }
        catch (SecurityTokenException ex)
        {
            _logger.LogDebug("Rejected a relay session: {Reason}.", ex.Message);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not validate a relay session.");
            return null;
        }
    }

    /// <summary>
    /// A random signing key, for when none is configured. Sessions then don't survive a restart
    /// — every desktop has to sign in again — so this is a development convenience, and the
    /// caller is expected to warn about it.
    /// </summary>
    public static string GenerateSigningKey() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
}

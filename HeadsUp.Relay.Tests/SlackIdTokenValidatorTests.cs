using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using HeadsUp.Relay.Authentication;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace HeadsUp.Relay.Tests;

/// <summary>
/// Identity is where the relay's isolation guarantee is won or lost: routing only ever sends to
/// the user a connection claims to be, so accepting a forged token would quietly undo all of
/// it. These tests are mostly forgery attempts.
/// </summary>
public class SlackIdTokenValidatorTests
{
    private const string ClientId = "123.456";
    private const string UserIdClaim = "https://slack.com/user_id";

    private readonly RSA _slackKey = RSA.Create(2048);
    private readonly RSA _attackerKey = RSA.Create(2048);

    private SlackIdTokenValidator Validator() =>
        new(new StubKeyProvider(new RsaSecurityKey(_slackKey)), ClientId,
            NullLogger<SlackIdTokenValidator>.Instance);

    private string Token(
        RSA? key = null,
        string issuer = "https://slack.com",
        string audience = ClientId,
        string? userId = "U_ALICE",
        string? subject = "U_ALICE",
        DateTime? expires = null,
        string algorithm = SecurityAlgorithms.RsaSha256)
    {
        var claims = new List<Claim>();
        if (userId is not null)
            claims.Add(new Claim(UserIdClaim, userId));
        if (subject is not null)
            claims.Add(new Claim(JwtRegisteredClaimNames.Sub, subject));

        var credentials = new SigningCredentials(new RsaSecurityKey(key ?? _slackKey), algorithm);
        var expiry = expires ?? DateTime.UtcNow.AddMinutes(30);
        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: claims,
            // Keep notBefore ahead of expiry even when minting a deliberately expired token.
            notBefore: expiry.AddMinutes(-30),
            expires: expiry,
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    // ------------------------------------------------------------------
    // Accepting genuine tokens
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_token_slack_signed_for_this_app_yields_its_user_id()
    {
        var userId = await Validator().ValidateAsync(Token(), CancellationToken.None);

        Assert.Equal("U_ALICE", userId);
    }

    [Fact]
    public async Task The_namespaced_claim_is_preferred_over_sub()
    {
        // sub and the Slack claim can differ; the workspace user id is the one routing uses.
        var token = Token(userId: "U_REAL", subject: "U_OTHER");

        Assert.Equal("U_REAL", await Validator().ValidateAsync(token, CancellationToken.None));
    }

    [Fact]
    public async Task Sub_is_used_when_the_namespaced_claim_is_absent()
    {
        var token = Token(userId: null, subject: "U_ALICE");

        Assert.Equal("U_ALICE", await Validator().ValidateAsync(token, CancellationToken.None));
    }

    // ------------------------------------------------------------------
    // Forgery and misuse
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_token_signed_by_someone_else_is_rejected()
    {
        // The attack that matters: a well-formed token claiming to be Alice, signed by a key
        // that isn't Slack's.
        var forged = Token(key: _attackerKey);

        Assert.Null(await Validator().ValidateAsync(forged, CancellationToken.None));
    }

    [Fact]
    public async Task An_unsigned_token_is_rejected()
    {
        var unsigned = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            issuer: "https://slack.com",
            audience: ClientId,
            claims: [new Claim(UserIdClaim, "U_ALICE")],
            expires: DateTime.UtcNow.AddMinutes(30)));

        Assert.Null(await Validator().ValidateAsync(unsigned, CancellationToken.None));
    }

    [Fact]
    public async Task A_token_for_a_different_slack_app_is_rejected()
    {
        // Otherwise any other Slack app's id_token could be replayed here.
        var otherApp = Token(audience: "999.888");

        Assert.Null(await Validator().ValidateAsync(otherApp, CancellationToken.None));
    }

    [Fact]
    public async Task A_token_from_a_different_issuer_is_rejected()
    {
        Assert.Null(await Validator().ValidateAsync(
            Token(issuer: "https://not-slack.example"), CancellationToken.None));
    }

    [Fact]
    public async Task An_expired_token_is_rejected()
    {
        var expired = Token(expires: DateTime.UtcNow.AddMinutes(-10));

        Assert.Null(await Validator().ValidateAsync(expired, CancellationToken.None));
    }

    [Fact]
    public async Task A_token_with_no_user_id_is_rejected()
    {
        // Valid signature but nothing to route to; guessing would be the disclosure bug.
        Assert.Null(await Validator().ValidateAsync(
            Token(userId: null, subject: null), CancellationToken.None));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-jwt")]
    [InlineData("a.b.c")]
    public async Task Garbage_is_rejected_without_throwing(string token) =>
        Assert.Null(await Validator().ValidateAsync(token, CancellationToken.None));

    [Fact]
    public async Task A_key_lookup_failure_rejects_rather_than_admits()
    {
        // Failing open here would let anyone in during a Slack outage.
        var validator = new SlackIdTokenValidator(
            new ThrowingKeyProvider(), ClientId, NullLogger<SlackIdTokenValidator>.Instance);

        Assert.Null(await validator.ValidateAsync(Token(), CancellationToken.None));
    }

    private sealed class StubKeyProvider(params SecurityKey[] keys) : ISlackSigningKeyProvider
    {
        public Task<IReadOnlyList<SecurityKey>> GetKeysAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SecurityKey>>(keys);
    }

    private sealed class ThrowingKeyProvider : ISlackSigningKeyProvider
    {
        public Task<IReadOnlyList<SecurityKey>> GetKeysAsync(CancellationToken ct) =>
            Task.FromException<IReadOnlyList<SecurityKey>>(new HttpRequestException("jwks unreachable"));
    }
}

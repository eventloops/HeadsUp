using HeadsUp.Relay.Authentication;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HeadsUp.Relay.Tests;

/// <summary>
/// A relay session is what a desktop presents to say "I am U_ALICE", and the router trusts it
/// completely. Forging one would defeat every isolation guarantee downstream, so these are
/// mostly attempts to do exactly that.
/// </summary>
public class RelaySessionIssuerTests
{
    private const string Key = "a-signing-key-long-enough-for-hmac-sha256-use";
    private const string OtherKey = "a-different-signing-key-of-sufficient-length!";

    private static RelaySessionIssuer Issuer(string key = Key, TimeSpan? lifetime = null) =>
        new(key, lifetime ?? TimeSpan.FromDays(30), NullLogger<RelaySessionIssuer>.Instance);

    [Fact]
    public void A_session_round_trips_to_the_user_it_was_issued_for()
    {
        var issuer = Issuer();

        Assert.Equal("U_ALICE", issuer.Validate(issuer.Issue("U_ALICE")));
    }

    [Fact]
    public void A_session_signed_with_another_key_is_rejected()
    {
        // The forgery that matters: a well-formed token claiming to be Alice.
        var forged = Issuer(OtherKey).Issue("U_ALICE");

        Assert.Null(Issuer().Validate(forged));
    }

    [Fact]
    public void Rotating_the_signing_key_invalidates_existing_sessions()
    {
        // This is the intended revocation mechanism, so it needs to actually work.
        var session = Issuer().Issue("U_ALICE");

        Assert.Null(Issuer(OtherKey).Validate(session));
    }

    [Fact]
    public void A_session_carries_the_configured_lifetime()
    {
        // Expiry itself is enforced by the handler's ValidateLifetime, which needs a real clock
        // to exercise; what's worth pinning here is that the lifetime reaches the token at all,
        // since a missing or wrong exp would make sessions immortal.
        var session = Issuer(lifetime: TimeSpan.FromDays(7)).Issue("U_ALICE");

        var token = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().ReadJwtToken(session);

        Assert.Equal(DateTime.UtcNow.AddDays(7), token.ValidTo, TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void A_session_that_has_already_expired_is_rejected()
    {
        // Backdated well beyond the one-minute clock skew.
        var expired = Issuer().IssueAt("U_ALICE", DateTime.UtcNow.AddHours(-2), TimeSpan.FromMinutes(1));

        Assert.Null(Issuer().Validate(expired));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-jwt")]
    [InlineData("a.b.c")]
    [InlineData("eyJhbGciOiJub25lIn0.eyJzdWIiOiJVX0FMSUNFIn0.")]
    public void Garbage_and_unsigned_tokens_are_rejected(string? token) =>
        Assert.Null(Issuer().Validate(token));

    [Fact]
    public void Two_users_get_distinguishable_sessions()
    {
        var issuer = Issuer();

        var alice = issuer.Issue("U_ALICE");
        var bob = issuer.Issue("U_BOB");

        Assert.NotEqual(alice, bob);
        Assert.Equal("U_ALICE", issuer.Validate(alice));
        Assert.Equal("U_BOB", issuer.Validate(bob));
    }

    [Fact]
    public void A_tampered_payload_is_rejected()
    {
        // Flipping the subject without re-signing must not survive validation.
        var session = Issuer().Issue("U_ALICE");
        var parts = session.Split('.');
        var tampered = $"{parts[0]}.{Convert.ToBase64String("{\"sub\":\"U_BOB\"}"u8.ToArray()).TrimEnd('=')}.{parts[2]}";

        Assert.Null(Issuer().Validate(tampered));
    }

    [Fact]
    public void Generated_signing_keys_differ_between_runs()
    {
        // Restarting therefore invalidates sessions, which is why it's warned about.
        Assert.NotEqual(RelaySessionIssuer.GenerateSigningKey(), RelaySessionIssuer.GenerateSigningKey());
    }
}

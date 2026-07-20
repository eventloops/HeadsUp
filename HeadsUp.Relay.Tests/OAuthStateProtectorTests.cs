using HeadsUp.Relay.Authentication;
using HeadsUp.Relay.Endpoints;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace HeadsUp.Relay.Tests;

/// <summary>
/// The OAuth state carries the desktop's return address through Slack and back. If the callback
/// can't tell an address we issued from one an attacker appended, the loopback restriction at
/// /auth/start is bypassable and a crafted callback delivers a session wherever they like.
/// </summary>
public class OAuthStateProtectorTests
{
    private const string Key = "a-relay-signing-key-of-reasonable-length";
    private const string ReturnTo = "http://localhost:32741/callback";

    private readonly FakeTimeProvider _clock = new();

    private OAuthStateProtector Protector(string key = Key) => new(key, _clock);

    [Fact]
    public void A_state_round_trips_to_its_return_address()
    {
        var protector = Protector();

        Assert.Equal(ReturnTo, protector.Unprotect(protector.Protect(ReturnTo))?.ReturnTo);
    }

    [Fact]
    public void A_state_signed_with_another_key_is_rejected()
    {
        var forged = Protector("a-completely-different-signing-key-value").Protect("http://localhost:9999/evil");

        Assert.Null(Protector().Unprotect(forged));
    }

    [Fact]
    public void A_tampered_return_address_is_rejected()
    {
        // The whole point: swapping the address must invalidate the signature.
        // Layout is signature|nonce|issuedAt|desktopState|returnTo — swap the address only.
        var state = Protector().Protect(ReturnTo);
        var decoded = System.Text.Encoding.UTF8.GetString(FromBase64Url(state));
        var parts = decoded.Split('|', 5);
        var tampered = ToBase64Url($"{parts[0]}|{parts[1]}|{parts[2]}|{parts[3]}|http://localhost:9999/evil");

        Assert.Null(Protector().Unprotect(tampered));
    }

    [Fact]
    public void An_expired_state_is_rejected()
    {
        // Without this a state captured from a proxy log or browser history replays forever.
        var state = Protector().Protect(ReturnTo);

        _clock.Advance(TimeSpan.FromMinutes(20));

        Assert.Null(Protector().Unprotect(state));
    }

    [Fact]
    public void A_state_within_its_lifetime_is_accepted()
    {
        var state = Protector().Protect(ReturnTo);

        _clock.Advance(TimeSpan.FromMinutes(5));

        Assert.Equal(ReturnTo, Protector().Unprotect(state)?.ReturnTo);
    }

    [Fact]
    public void A_state_from_the_future_is_rejected()
    {
        // Issued by a clock an hour ahead — a fabricated timestamp, or clocks badly out of
        // step. FakeTimeProvider refuses to move backwards, so this uses two of them.
        var ahead = new FakeTimeProvider();
        ahead.Advance(TimeSpan.FromHours(1));
        var state = new OAuthStateProtector(Key, ahead).Protect(ReturnTo);

        Assert.Null(Protector().Unprotect(state));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-base64url")]
    [InlineData("YWJj")]           // valid base64url, wrong structure
    public void Garbage_is_rejected_without_throwing(string state) =>
        Assert.Null(Protector().Unprotect(state));

    [Fact]
    public void A_return_address_containing_a_separator_survives()
    {
        // The payload is delimited by '|', so a query string carrying one must not truncate it.
        const string withPipe = "http://localhost:32741/callback?a=1|2";

        var protector = Protector();

        Assert.Equal(withPipe, protector.Unprotect(protector.Protect(withPipe))?.ReturnTo);
    }

    // ------------------------------------------------------------------
    // The desktop's own state
    // ------------------------------------------------------------------

    [Fact]
    public void The_desktops_state_survives_the_round_trip()
    {
        // Echoing this back is what lets the desktop tell our redirect from any other local
        // process posting a session to its loopback port.
        var protector = Protector();

        var unprotected = protector.Unprotect(protector.Protect(ReturnTo, "abc123"));

        Assert.Equal("abc123", unprotected?.DesktopState);
        Assert.Equal(ReturnTo, unprotected?.ReturnTo);
    }

    [Fact]
    public void A_desktop_state_containing_a_separator_does_not_corrupt_the_return_address()
    {
        // Hex-encoded on the way in precisely so it can't introduce a delimiter.
        var protector = Protector();

        var unprotected = protector.Unprotect(protector.Protect(ReturnTo, "a|b|c"));

        Assert.Equal("a|b|c", unprotected?.DesktopState);
        Assert.Equal(ReturnTo, unprotected?.ReturnTo);
    }

    [Fact]
    public void A_tampered_desktop_state_is_rejected()
    {
        var state = Protector().Protect(ReturnTo, "abc123");
        var decoded = System.Text.Encoding.UTF8.GetString(FromBase64Url(state));
        var parts = decoded.Split('|', 5);
        var tampered = ToBase64Url(
            $"{parts[0]}|{parts[1]}|{parts[2]}|{Convert.ToHexString("evil"u8.ToArray())}|{parts[4]}");

        Assert.Null(Protector().Unprotect(tampered));
    }

    [Fact]
    public void An_absent_desktop_state_round_trips_as_empty()
    {
        var protector = Protector();

        Assert.Equal(string.Empty, protector.Unprotect(protector.Protect(ReturnTo))?.DesktopState);
    }

    // ------------------------------------------------------------------
    // Loopback restriction
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("http://localhost:32741/callback")]
    [InlineData("http://127.0.0.1:32741/callback")]
    [InlineData("http://[::1]:32741/callback")]
    public void Loopback_addresses_are_accepted(string url) =>
        Assert.True(RelayEndpoints.IsLoopback(url));

    [Theory]
    [InlineData("http://evil.example/callback")]
    [InlineData("https://localhost:32741/callback")]   // scheme must be http
    [InlineData("not a url")]
    [InlineData("")]
    public void Everything_else_is_rejected(string url) =>
        Assert.False(RelayEndpoints.IsLoopback(url));

    private static string ToBase64Url(string value) =>
        Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        return Convert.FromBase64String(padded);
    }
}

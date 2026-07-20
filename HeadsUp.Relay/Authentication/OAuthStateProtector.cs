using System.Security.Cryptography;
using System.Text;

namespace HeadsUp.Relay.Authentication;

/// <summary>
/// Signs the OAuth <c>state</c> parameter, which carries the desktop's loopback return address
/// through Slack and back.
///
/// It has to be signed: the callback cannot otherwise tell a return address we issued from one
/// an attacker appended, which would make the loopback restriction at <c>/auth/start</c>
/// bypassable and let a crafted callback deliver a session to a port of their choosing.
///
/// It also has to expire. A state without a lifetime verifies forever, so one captured from a
/// proxy log or browser history stays replayable indefinitely; a few minutes is far longer than
/// a consent screen takes.
///
/// Lives beside <see cref="RelaySessionIssuer"/> because both sign things with the relay's key,
/// and keeping lifetime and rotation semantics in one place is the point.
/// </summary>
public sealed class OAuthStateProtector
{
    /// <summary>Generous next to a consent screen, short next to a log-retention window.</summary>
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    private readonly string _key;
    private readonly TimeProvider _clock;

    public OAuthStateProtector(string key, TimeProvider? clock = null)
    {
        _key = key;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// Signs the desktop's return address and its own state value, together with an issue time
    /// and a nonce.
    ///
    /// The desktop's state is carried through so the callback can hand it back: without it the
    /// desktop has no way to tell our redirect from any other local process posting a session
    /// to its loopback port.
    ///
    /// The signature goes first so the return address can be taken as the whole remainder: a
    /// URL may legitimately contain the '|' separator (in a query string), and putting the
    /// signature last truncated exactly those addresses.
    /// </summary>
    public string Protect(string returnTo, string desktopState = "")
    {
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
        var issuedAt = _clock.GetUtcNow().ToUnixTimeSeconds();

        // Separator-free by construction, so returnTo stays the unambiguous remainder.
        var safeState = Convert.ToHexString(Encoding.UTF8.GetBytes(desktopState));

        var payload = $"{nonce}|{issuedAt}|{safeState}|{returnTo}";
        return Base64Url($"{Sign(payload)}|{payload}");
    }

    /// <summary>
    /// Returns the return address and the desktop's state, or null if the value wasn't issued
    /// by us, has been tampered with, or has expired.
    /// </summary>
    public (string ReturnTo, string DesktopState)? Unprotect(string state)
    {
        try
        {
            var decoded = Encoding.UTF8.GetString(FromBase64Url(state));

            // Limit of 5 so the final field keeps any '|' the return address contains.
            var parts = decoded.Split('|', 5);
            if (parts.Length != 5)
                return null;

            var signature = parts[0];
            var payload = $"{parts[1]}|{parts[2]}|{parts[3]}|{parts[4]}";

            // Fixed-time comparison: a signature check that leaks timing is a forgeable one.
            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(Sign(payload)), Encoding.UTF8.GetBytes(signature)))
                return null;

            if (!long.TryParse(parts[2], out var issuedAt))
                return null;

            var age = _clock.GetUtcNow() - DateTimeOffset.FromUnixTimeSeconds(issuedAt);
            if (age > Lifetime || age < -TimeSpan.FromMinutes(1))
                return null;

            var desktopState = Encoding.UTF8.GetString(Convert.FromHexString(parts[3]));
            return (parts[4], desktopState);
        }
        catch
        {
            return null;
        }
    }

    private string Sign(string payload) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(_key), Encoding.UTF8.GetBytes(payload)));

    private static string Base64Url(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        return Convert.FromBase64String(padded);
    }
}

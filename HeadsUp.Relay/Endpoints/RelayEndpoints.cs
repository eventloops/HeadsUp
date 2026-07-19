using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using HeadsUp.Relay.Authentication;
using HeadsUp.Relay.Routing;
using HeadsUp.Services;

namespace HeadsUp.Relay.Endpoints;

/// <summary>
/// The desktop-facing surface: sign in, and then a socket to receive events on.
/// </summary>
public static class RelayEndpoints
{
    /// <summary>Where Slack sends the user back to. Must match the app's registered redirect URL.</summary>
    public const string CallbackPath = "/auth/callback";

    public static void MapRelayEndpoints(this WebApplication app, RelayOptions options)
    {
        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

        app.MapGet("/auth/start", (HttpContext http, RelayOptions opts) =>
        {
            // The desktop tells us where to send the user back to on this machine. Only
            // loopback is accepted: an open redirect here would let an attacker harvest
            // sessions by pointing the callback at themselves.
            var returnTo = http.Request.Query["return_to"].ToString();
            if (!IsLoopback(returnTo))
                return Results.BadRequest("return_to must be a loopback URL.");

            // State carries the desktop's return address and is signed on the way out, so the
            // callback can trust it came from us rather than being fabricated.
            var state = StateProtector.Protect(returnTo, opts.SessionSigningKey);

            return Results.Redirect(SlackOAuthExchange.BuildAuthorizeUrl(
                opts.ClientId, opts.PublicUrl.TrimEnd('/') + CallbackPath, state, SlackOAuthClient.UserScopes));
        });

        app.MapGet(CallbackPath, async (
            HttpContext http,
            RelayOptions opts,
            SlackOAuthExchange exchange,
            RelaySessionIssuer sessions,
            ILoggerFactory loggers,
            CancellationToken ct) =>
        {
            var logger = loggers.CreateLogger("HeadsUp.Relay.Auth");

            var error = http.Request.Query["error"].ToString();
            if (!string.IsNullOrEmpty(error))
                return Results.Content($"Sign-in was cancelled ({error}). You can close this tab.", "text/plain");

            var code = http.Request.Query["code"].ToString();
            var state = http.Request.Query["state"].ToString();

            if (StateProtector.Unprotect(state, opts.SessionSigningKey) is not { } returnTo)
            {
                logger.LogWarning("Discarding an OAuth callback with an untrusted state parameter.");
                return Results.BadRequest("Invalid state.");
            }

            var identity = await exchange.ExchangeAsync(
                opts.ClientId, opts.ClientSecret, code,
                opts.PublicUrl.TrimEnd('/') + CallbackPath, ct);

            if (identity is null)
                return Results.BadRequest("Slack rejected the sign-in.");

            // The session is bound to the id Slack just told us consented — never to anything
            // the desktop asserted.
            var session = sessions.Issue(identity.SlackUserId);
            logger.LogInformation("Issued a relay session for {UserId}.", identity.SlackUserId);

            // Handed back over loopback, so the token never leaves the user's machine except
            // to us.
            var separator = returnTo.Contains('?') ? '&' : '?';
            return Results.Redirect($"{returnTo}{separator}session={Uri.EscapeDataString(session)}");
        });

        app.Map("/events", async (
            HttpContext http,
            RelaySessionIssuer sessions,
            IUserConnectionRegistry registry,
            ILoggerFactory loggers,
            CancellationToken ct) =>
        {
            if (!http.WebSockets.IsWebSocketRequest)
            {
                http.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            // The session travels in a header rather than the query string, so it stays out of
            // access logs and referrers. .NET's ClientWebSocket can set one; browsers can't,
            // which is fine — the client here is the desktop app.
            var header = http.Request.Headers.Authorization.ToString();
            var token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                ? header["Bearer ".Length..]
                : null;

            // This is the load-bearing line: the user id comes from a token we signed, so a
            // client cannot choose whose events it receives.
            if (sessions.Validate(token) is not { } userId)
            {
                http.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            var logger = loggers.CreateLogger("HeadsUp.Relay.Events");
            using var socket = await http.WebSockets.AcceptWebSocketAsync();
            var connection = new WebSocketUserConnection(userId, socket);

            using (registry.Add(connection))
            {
                logger.LogInformation("Desktop connected for {UserId}.", userId);
                await connection.WaitUntilClosedAsync(ct);
                logger.LogInformation("Desktop disconnected for {UserId}.", userId);
            }
        });
    }

    private static bool IsLoopback(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttp
        && (uri.Host is "localhost" or "127.0.0.1" or "[::1]");

    /// <summary>
    /// Signs the OAuth state so the callback can trust the return address came from us. Without
    /// this, the return_to check at /auth/start could be bypassed by crafting a callback URL
    /// directly.
    /// </summary>
    private static class StateProtector
    {
        public static string Protect(string returnTo, string key)
        {
            var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
            var payload = $"{nonce}|{returnTo}";
            return Base64Url($"{payload}|{Sign(payload, key)}");
        }

        public static string? Unprotect(string state, string key)
        {
            try
            {
                var decoded = Encoding.UTF8.GetString(FromBase64Url(state));
                var parts = decoded.Split('|');
                if (parts.Length != 3)
                    return null;

                var payload = $"{parts[0]}|{parts[1]}";
                var expected = Sign(payload, key);

                // Fixed-time comparison: a signature check that leaks timing is a forgeable one.
                return CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(parts[2]))
                    ? parts[1]
                    : null;
            }
            catch
            {
                return null;
            }
        }

        private static string Sign(string payload, string key) =>
            Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(payload)));

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
}

using System.Net;
using Microsoft.Extensions.Logging;

namespace HeadsUp.Services;

/// <summary>The authorization code handed back on the loopback redirect.</summary>
public sealed record OAuthCallbackResult(string Code, string State);

/// <summary>
/// Catches Slack's OAuth redirect on <c>http://localhost:{port}/callback</c>.
///
/// Loopback is preferred over a custom URI scheme: PKCE makes both valid desktop redirects,
/// but loopback needs no OS-level scheme registration and works the same on every head. The
/// port is fixed because the redirect URI has to match what's registered in the Slack app
/// manifest exactly — so a busy port is a hard, and clearly-reported, failure.
/// </summary>
public sealed class LoopbackOAuthListener
{
    /// <summary>Fixed port, mirrored in the app manifest's redirect URL.</summary>
    public const int Port = 32741;

    public static string RedirectUri => $"http://localhost:{Port}/callback";

    private const string SuccessHtml =
        "<html><body style='font-family:system-ui;text-align:center;padding-top:4rem'>" +
        "<h2>Heads-Up is connected.</h2><p>You can close this tab and return to the app.</p>" +
        "</body></html>";

    private const string WaitingHtml =
        "<html><body style='font-family:system-ui;text-align:center;padding-top:4rem'>" +
        "<h2>Heads-Up is waiting for Slack.</h2><p>Nothing to see here yet.</p>" +
        "</body></html>";

    private const string FailureHtml =
        "<html><body style='font-family:system-ui;text-align:center;padding-top:4rem'>" +
        "<h2>Sign-in did not complete.</h2><p>Return to Heads-Up and try again.</p>" +
        "</body></html>";

    private readonly ILogger<LoopbackOAuthListener> _logger;

    public LoopbackOAuthListener(ILogger<LoopbackOAuthListener> logger) => _logger = logger;

    /// <summary>
    /// Listens for a single redirect and returns its code, or null if the user abandoned the
    /// flow (cancellation) or Slack reported an error. <paramref name="expectedState"/> is
    /// checked before the code is accepted, which is what stops another local process from
    /// feeding us a code of its own.
    /// </summary>
    public async Task<OAuthCallbackResult?> WaitForCallbackAsync(string expectedState, CancellationToken ct)
    {
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{Port}/");

        try
        {
            listener.Start();
        }
        catch (HttpListenerException ex)
        {
            throw new InvalidOperationException(
                $"Could not listen on port {Port} for the Slack sign-in redirect. " +
                "Close whatever is using that port and try connecting again.", ex);
        }

        try
        {
            // GetContextAsync has no cancellation overload; stopping the listener is what
            // unblocks it, so tie that to the token.
            using var registration = ct.Register(() => listener.Stop());

            // Loop rather than accepting a single request: anything else on this port during
            // the sign-in window — a browser favicon fetch, another local process, a port
            // scanner — would otherwise consume the one accept and leave the real redirect
            // hitting a closed listener, failing the sign-in for reasons invisible to the user.
            while (!ct.IsCancellationRequested)
            {
                var context = await listener.GetContextAsync();
                var query = context.Request.QueryString;
                var code = query["code"];
                var state = query["state"];
                var error = query["error"];

                // Slack said no. That is an answer, so stop waiting.
                if (error is not null)
                {
                    await RespondAsync(context, FailureHtml);
                    _logger.LogWarning("Slack returned an OAuth error: {Error}.", error);
                    return null;
                }

                // Not the redirect: no authorization code at all. Answer politely and keep
                // waiting for the one we want.
                if (string.IsNullOrEmpty(code))
                {
                    await RespondAsync(context, WaitingHtml, HttpStatusCode.NotFound);
                    _logger.LogDebug("Ignoring an unrelated request while awaiting the OAuth redirect.");
                    continue;
                }

                // A code with the wrong state is not a stray request — it is either a stale
                // attempt or a forgery, and neither should be accepted.
                if (!string.Equals(state, expectedState, StringComparison.Ordinal))
                {
                    await RespondAsync(context, FailureHtml);
                    _logger.LogWarning("Discarding OAuth callback with a mismatched state parameter.");
                    return null;
                }

                await RespondAsync(context, SuccessHtml);
                return new OAuthCallbackResult(code, state!);
            }

            return null;
        }
        catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
        {
            // Expected when the listener is stopped by cancellation.
            _logger.LogDebug("Slack sign-in listener stopped before a redirect arrived.");
            return null;
        }
        finally
        {
            if (listener.IsListening)
                listener.Stop();
        }
    }

    private static async Task RespondAsync(
        HttpListenerContext context, string html, HttpStatusCode status = HttpStatusCode.OK)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(html);
        context.Response.StatusCode = (int)status;
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }
}

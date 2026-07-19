using HeadsUp.Services;
using Microsoft.Extensions.Logging;

namespace HeadsUp.Relay;

/// <summary>
/// Installs the Slack app for one user, by running the OAuth flow from the console.
///
/// A user-token-only Slack app has no one-click "Install to Workspace" button — installing it
/// *is* running the OAuth flow — so this exists to authorize the first user without needing the
/// relay's own sign-in endpoint reachable. In normal use people authorize through
/// <c>/auth/start</c> instead.
///
/// The relay itself keeps no user token: it reads events with the app-level token, so the token
/// printed here is only useful for ad-hoc API calls.
///
/// It reuses the same <see cref="SlackOAuthClient"/> and <see cref="LoopbackOAuthListener"/> as
/// the desktop app, so running it exercises the real implementation rather than a parallel one.
/// </summary>
public static class AuthorizeCommand
{
    public static async Task<int> RunAsync(RelayOptions options, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.ClientId))
        {
            Console.Error.WriteLine("Relay:ClientId must be set before authorizing.");
            return 1;
        }

        var oauth = new SlackOAuthClient(new HttpClient(), loggerFactory.CreateLogger<SlackOAuthClient>());
        var listener = new LoopbackOAuthListener(loggerFactory.CreateLogger<LoopbackOAuthListener>());

        var pkce = PkcePair.Create();
        var state = Guid.NewGuid().ToString("N");
        var url = SlackOAuthClient.BuildAuthorizeUrl(
            options.ClientId, LoopbackOAuthListener.RedirectUri, pkce, state);

        Console.WriteLine("Open this URL to authorize (it should open automatically):");
        Console.WriteLine();
        Console.WriteLine(url);
        Console.WriteLine();
        Console.WriteLine($"Waiting for the redirect on {LoopbackOAuthListener.RedirectUri} …");

        // Listen before opening the browser, so a fast redirect can't arrive first.
        var callbackTask = listener.WaitForCallbackAsync(state, ct);

        try
        {
            new SystemBrowserLauncher().Open(url);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not open a browser ({ex.Message}); open the URL above by hand.");
        }

        var callback = await callbackTask;
        if (callback is null)
        {
            Console.Error.WriteLine("Authorization did not complete.");
            return 1;
        }

        var tokens = await oauth.ExchangeCodeAsync(
            options.ClientId, callback.Code, LoopbackOAuthListener.RedirectUri, pkce, ct);

        Console.WriteLine();
        Console.WriteLine("Authorized — this user's events will now reach the relay.");
        Console.WriteLine("The relay needs no user token, so there is nothing to paste anywhere.");

        if (tokens.RefreshToken is not null)
            Console.WriteLine($"(the token issued is rotating and expires {tokens.ExpiresAt:u})");

        return 0;
    }
}

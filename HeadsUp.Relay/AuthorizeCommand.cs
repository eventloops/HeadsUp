using HeadsUp.Services;
using Microsoft.Extensions.Logging;

namespace HeadsUp.Relay;

/// <summary>
/// Runs the PKCE authorization flow from the console, printing the resulting user token.
///
/// A user-token-only Slack app has no one-click "Install to Workspace" button — installing it
/// *is* running the OAuth flow — so this doubles as the install step and as the way to obtain
/// the <see cref="RelayOptions.ApiToken"/> the relay needs for
/// <c>apps.event.authorizations.list</c>.
///
/// It deliberately reuses the same <see cref="SlackOAuthClient"/> and
/// <see cref="LoopbackOAuthListener"/> the desktop app uses, so exercising this exercises the
/// real implementation rather than a parallel one written for convenience.
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
        Console.WriteLine("Authorized. Put this in Relay:ApiToken in appsettings.Local.json:");
        Console.WriteLine();
        Console.WriteLine(tokens.AccessToken);
        Console.WriteLine();

        if (tokens.RefreshToken is not null)
            Console.WriteLine($"(rotating token; expires {tokens.ExpiresAt:u} — refresh token issued)");

        return 0;
    }
}

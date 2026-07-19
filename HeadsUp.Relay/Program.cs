using HeadsUp.Relay;
using HeadsUp.Relay.Authentication;
using HeadsUp.Relay.Ingress;
using HeadsUp.Relay.Routing;
using Microsoft.Extensions.Logging;
using SlackNet.Extensions.DependencyInjection;
using SlackNet.SocketMode;

var builder = WebApplication.CreateBuilder(args);

// Git-ignored local overrides for the Slack app credentials. The default builder only loads
// appsettings.json and appsettings.{Environment}.json, so this has to be added explicitly —
// the same file the WPF head uses for its secrets.
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

var options = builder.Configuration.GetSection(RelayOptions.SectionName).Get<RelayOptions>() ?? new RelayOptions();

// "authorize" runs the PKCE flow and exits, rather than starting the relay. A user-token-only
// Slack app has no one-click install, so this is how the app gets installed and how the
// ApiToken is obtained.
if (args.Contains("authorize", StringComparer.OrdinalIgnoreCase))
{
    using var authLoggerFactory = LoggerFactory.Create(b => b.AddConsole());
    return await AuthorizeCommand.RunAsync(options, authLoggerFactory, CancellationToken.None);
}

options.Validate();
builder.Services.AddSingleton(options);

// Raw-frame logging is a Debug-level diagnostic; opt in without changing global log levels.
if (options.LogRawFrames)
    builder.Logging.AddFilter("HeadsUp.Relay.Ingress.SocketModeIngress", LogLevel.Debug);

// The Web API client, used for apps.event.authorizations.list when an authorizations list
// comes back truncated. That method rejects user tokens with not_allowed_token_type — it is an
// app-level concern, so it gets the app-level token regardless of what ApiToken holds.
builder.Services.AddSlackNet(c => c.UseApiToken(options.AppToken));

// The Socket Mode connection itself. Ingress works from the raw frame stream rather than
// SlackNet's typed dispatch, because routing needs the envelope's authorizations and
// event_context, which the typed message event doesn't carry — and only the core client
// exposes those frames.
builder.Services.AddSingleton<ICoreSocketModeClient>(_ => new CoreSocketModeClient(options.AppToken));

// Routing. The registry is a singleton because it holds the live desktop connections.
builder.Services.AddSingleton<IUserConnectionRegistry, UserConnectionRegistry>();
builder.Services.AddSingleton<IEventAuthorizationResolver, SlackEventAuthorizationResolver>();
builder.Services.AddSingleton<EventRouter>();

// Identity. Keys come from Slack's JWKS and are refreshed on a schedule, since Slack rotates them.
builder.Services.AddHttpClient<ISlackSigningKeyProvider, SlackJwksKeyProvider>();
builder.Services.AddSingleton(sp => new SlackIdTokenValidator(
    sp.GetRequiredService<ISlackSigningKeyProvider>(),
    options.ClientId,
    sp.GetRequiredService<ILogger<SlackIdTokenValidator>>()));

builder.Services.AddHostedService<SocketModeIngress>();

var app = builder.Build();

// Liveness only — deliberately says nothing about who is connected.
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.Run();
return 0;

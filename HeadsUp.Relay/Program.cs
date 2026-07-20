using HeadsUp.Relay;
using HeadsUp.Relay.Authentication;
using HeadsUp.Relay.Endpoints;
using HeadsUp.Relay.Ingress;
using HeadsUp.Relay.Routing;
using Microsoft.Extensions.Logging;
using SlackNet;
using SlackNet.Extensions.DependencyInjection;
using SlackNet.SocketMode;

var builder = WebApplication.CreateBuilder(args);

// Git-ignored local overrides for the Slack app credentials. The default builder only loads
// appsettings.json and appsettings.{Environment}.json, so this has to be added explicitly —
// the same file the WPF head uses for its secrets.
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

var options = builder.Configuration.GetSection(RelayOptions.SectionName).Get<RelayOptions>() ?? new RelayOptions();

// "authorize" installs the app for one user and exits, rather than starting the relay. A
// user-token-only Slack app has no one-click install, so this bootstraps the first user
// without needing /auth/start reachable; everyone else signs in through that endpoint. The
// relay keeps no user token of its own.
if (args.Contains("authorize", StringComparer.OrdinalIgnoreCase))
{
    using var authLoggerFactory = LoggerFactory.Create(b => b.AddConsole());
    return await AuthorizeCommand.RunAsync(options, authLoggerFactory, CancellationToken.None);
}

// A generated key means sessions don't survive a restart, which is fine for development and
// surprising in production — so say so rather than letting it be discovered.
if (string.IsNullOrWhiteSpace(options.SessionSigningKey))
{
    options.SessionSigningKey = RelaySessionIssuer.GenerateSigningKey();
    Console.WriteLine("Relay:SessionSigningKey is unset; generated one. " +
                      "Desktops will have to sign in again after every restart.");
}

options.Validate();
builder.Services.AddSingleton(options);

// Raw-frame logging is a Debug-level diagnostic; opt in without changing global log levels.
if (options.LogRawFrames)
    builder.Logging.AddFilter("HeadsUp.Relay.Ingress.SocketModeIngress", LogLevel.Debug);

// The Web API client, used solely for apps.event.authorizations.list when an authorizations
// list comes back truncated. That method rejects user tokens (not_allowed_token_type) and
// needs the app-level token carrying authorizations:read. Since app-level tokens don't
// rotate, the relay holds no credential that needs renewing.
builder.Services.AddSlackNet(c => c.UseApiToken(options.AppToken));

// The Socket Mode connection itself. Ingress works from the raw frame stream rather than
// SlackNet's typed dispatch, because routing needs the envelope's authorizations and
// event_context, which the typed message event doesn't carry — and only the core client
// exposes those frames.
builder.Services.AddSingleton<ICoreSocketModeClient>(_ => new CoreSocketModeClient(options.AppToken));

// Routing. The registry is a singleton because it holds the live desktop connections.
builder.Services.AddSingleton<IUserConnectionRegistry, UserConnectionRegistry>();
// Cached in front of the Slack lookup: without it the relay calls
// apps.event.authorizations.list once per event, which runs at ~83% of that method's rate
// limit at Slack's own event ceiling and adds a round trip to every alert.
builder.Services.AddSingleton<SlackEventAuthorizationResolver>();
builder.Services.AddSingleton<CachingEventAuthorizationResolver>(sp => new CachingEventAuthorizationResolver(
    sp.GetRequiredService<SlackEventAuthorizationResolver>(),
    sp.GetRequiredService<ILogger<CachingEventAuthorizationResolver>>(),
    options.AuthorizationCacheTtl));
builder.Services.AddSingleton<IEventAuthorizationResolver>(
    sp => sp.GetRequiredService<CachingEventAuthorizationResolver>());
builder.Services.AddHostedService<AuthorizationCachePruner>();
builder.Services.AddSingleton<EventRouter>();

// Name resolution happens here so one lookup serves every desktop, and because relay-mode
// desktops hold no Slack credential of their own. Needs a bot token; without one the relay
// forwards ids and the desktop shows those instead.
builder.Services.AddSingleton(sp => new EventEnricher(
    string.IsNullOrWhiteSpace(options.BotToken)
        ? null
        : new SlackServiceBuilder().UseApiToken(options.BotToken).GetApiClient(),
    sp.GetRequiredService<ILogger<EventEnricher>>()));

// Identity. The relay is the OAuth client, so users face a single consent screen — Slack
// refuses to combine Sign in with Slack scopes with ordinary user scopes in one request.
builder.Services.AddHttpClient<SlackOAuthExchange>();
builder.Services.AddSingleton(new OAuthStateProtector(options.SessionSigningKey));
builder.Services.AddSingleton(sp => new RelaySessionIssuer(
    options.SessionSigningKey, options.SessionLifetime,
    sp.GetRequiredService<ILogger<RelaySessionIssuer>>()));

builder.Services.AddHostedService<SocketModeIngress>();

var app = builder.Build();

app.UseWebSockets();
app.MapRelayEndpoints(options);

app.Run();
return 0;

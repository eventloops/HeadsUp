using HeadsUp.Relay;
using HeadsUp.Relay.Authentication;
using HeadsUp.Relay.Ingress;
using HeadsUp.Relay.Routing;
using Microsoft.Extensions.Logging;
using SlackNet.Extensions.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

var options = builder.Configuration.GetSection(RelayOptions.SectionName).Get<RelayOptions>() ?? new RelayOptions();
options.Validate();
builder.Services.AddSingleton(options);

// Raw-frame logging is a Debug-level diagnostic; opt in without changing global log levels.
if (options.LogRawFrames)
    builder.Logging.AddFilter("HeadsUp.Relay.Ingress.SocketModeIngress", LogLevel.Debug);

// One SlackNet stack for the whole relay: the app-level token opens the single Socket Mode
// connection, and the API token is what apps.event.authorizations.list is called with.
// Registered through DI (rather than built by hand) so ICoreSocketModeClient — the raw frame
// stream ingress needs — resolves from the same underlying connection as everything else.
builder.Services.AddSlackNet(c => c
    .UseAppLevelToken(options.AppToken)
    .UseApiToken(string.IsNullOrWhiteSpace(options.ApiToken) ? options.AppToken : options.ApiToken));

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

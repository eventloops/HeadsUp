using HeadsUp.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace HeadsUp.Wpf.Tests;

/// <summary>
/// Builds the real host the WPF head builds, in SlackApi mode. DI misconfiguration is the
/// failure this catches: every unit around the OAuth path can pass while the composition
/// throws at startup, which no other test would notice.
/// </summary>
public class SlackApiHostWiringTests
{
    private static IHost BuildHost() =>
        HeadsUp.Program.CreateHostBuilder([
            "--Detection:Connectors:0:Type=SlackApi",
        ]).Build();

    [Fact]
    public void The_slack_api_connector_composes()
    {
        using var host = BuildHost();

        var monitor = host.Services.GetRequiredService<SlackHuddleMonitor>();
        Assert.Same(monitor, host.Services.GetRequiredService<IAlertSource>());
        Assert.Contains(host.Services.GetServices<IHostedService>(), s => s is SlackHuddleMonitor);
    }

    [Fact]
    public void The_head_supplies_the_oauth_provider_and_encrypted_storage()
    {
        using var host = BuildHost();

        // The head must win over the config-file provider that AddConnector would TryAdd.
        Assert.IsType<OAuthSlackAppCredentialProvider>(
            host.Services.GetRequiredService<ISlackAppCredentialProvider>());
        Assert.IsType<DpapiTokenProtector>(host.Services.GetRequiredService<ITokenProtector>());
        Assert.IsType<FileSlackTokenStore>(host.Services.GetRequiredService<ISlackTokenStore>());
        Assert.IsType<WpfSlackAppSetupPrompt>(host.Services.GetRequiredService<ISlackAppSetupPrompt>());
    }

    [Fact]
    public void The_oauth_dependencies_all_resolve()
    {
        using var host = BuildHost();

        Assert.NotNull(host.Services.GetRequiredService<SlackOAuthClient>());
        Assert.NotNull(host.Services.GetRequiredService<LoopbackOAuthListener>());
        Assert.NotNull(host.Services.GetRequiredService<ConnectionGate>());
        Assert.NotNull(host.Services.GetRequiredService<IUserDirectory>());
    }

    // ------------------------------------------------------------------
    // Relay mode
    // ------------------------------------------------------------------

    private static IHost BuildRelayHost() =>
        HeadsUp.Program.CreateHostBuilder([
            "--Detection:Connectors:0:Type=SlackRelay",
            "--Relay:BaseUrl=http://localhost:5285",
        ]).Build();

    [Fact]
    public void The_relay_connector_composes()
    {
        using var host = BuildRelayHost();

        var source = host.Services.GetRequiredService<SlackRelaySource>();
        Assert.Same(source, host.Services.GetRequiredService<IAlertSource>());
        Assert.Contains(host.Services.GetServices<IHostedService>(), s => s is SlackRelaySource);
    }

    [Fact]
    public void Relay_mode_has_a_way_to_sign_in()
    {
        using var host = BuildRelayHost();

        // The config-only provider needed a session and user id that no flow produced, so the
        // connector could never leave "Not signed in". The browser provider must win.
        Assert.IsType<RelaySignInCredentialProvider>(
            host.Services.GetRequiredService<IRelayCredentialProvider>());
        Assert.IsType<FileRelaySessionStore>(host.Services.GetRequiredService<IRelaySessionStore>());
    }

    [Fact]
    public void The_dashboard_can_be_opened_in_relay_mode()
    {
        // DashboardViewModel requires an IUserDirectory; without one registered, opening the
        // dashboard threw an unresolvable-service exception in the tray command path.
        using var host = BuildRelayHost();

        var directory = host.Services.GetRequiredService<IUserDirectory>();

        Assert.IsType<UnavailableUserDirectory>(directory);
        // Honest about it rather than pretending to be an empty workspace.
        Assert.False(directory.IsAvailable);
    }

    [Fact]
    public void Round_tripping_tokens_through_dpapi_preserves_them()
    {
        // Exercises the real DPAPI protector rather than the reversing stand-in the store tests use.
        var protector = new DpapiTokenProtector();
        var plaintext = "xoxp-a-real-looking-token"u8.ToArray();

        var cipher = protector.Protect(plaintext);

        Assert.NotEqual(plaintext, cipher);
        Assert.Equal(plaintext, protector.Unprotect(cipher));
    }
}

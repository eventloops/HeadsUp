using HeadsUp.Configuration;
using HeadsUp.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace HeadsUp.Tests;

public class ServiceCollectionExtensionsTests
{
    private static IConfiguration Config(params (string Key, string Value)[] pairs) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.ToDictionary(p => p.Key, p => (string?)p.Value))
            .Build();

    private static ServiceProvider BuildClientApiProvider(
        IConfiguration config, Action<IServiceCollection>? beforeCore = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<INotificationService, RecordingNotificationService>();
        beforeCore?.Invoke(services);
        services.AddHeadsUpApplication(config);
        services.AddHeadsUpInfrastructure(config);
        services.AddSlackClientApiHuddleDetection(config);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Core_registers_options_status_and_stores()
    {
        var config = Config(("Detection:Connectors:0:Type", "SlackClientApi"), ("Alerts:Keywords", "true"));
        using var provider = BuildClientApiProvider(config);

        Assert.Equal("SlackClientApi", provider.GetRequiredService<AppStatus>().Mode);
        Assert.Equal("Starting…", provider.GetRequiredService<AppStatus>().StatusMessage);
        Assert.True(provider.GetRequiredService<IOptionsMonitor<AlertOptions>>().CurrentValue.Keywords);
        Assert.IsType<JsonPrioritySettingsStore>(provider.GetRequiredService<IPrioritySettingsStore>());
        Assert.IsType<JsonAlertSettingsStore>(provider.GetRequiredService<IAlertSettingsStore>());
        Assert.IsType<AlertHistory>(provider.GetRequiredService<IAlertHistory>());
    }

    [Fact]
    public void Client_api_mode_wires_the_web_source_as_singleton_source_and_hosted_service()
    {
        using var provider = BuildClientApiProvider(Config(("Detection:Connectors:0:Type", "SlackClientApi")));

        var source = provider.GetRequiredService<IAlertSource>();
        Assert.IsType<SlackWebHuddleSource>(source);
        Assert.Same(source, provider.GetRequiredService<SlackWebHuddleSource>());

        var hosted = provider.GetServices<IHostedService>().ToList();
        Assert.Contains(hosted, s => s is SlackWebHuddleSource);
        Assert.Contains(hosted, s => s is HuddleNotificationCoordinator);
    }

    [Fact]
    public void Client_api_mode_defaults_to_config_credentials_and_web_directory()
    {
        using var provider = BuildClientApiProvider(Config());

        Assert.IsType<ConfigSlackCredentialProvider>(provider.GetRequiredService<ISlackCredentialProvider>());
        Assert.IsType<SlackWebUserDirectory>(provider.GetRequiredService<IUserDirectory>());
    }

    [Fact]
    public void A_platform_credential_provider_registered_first_wins()
    {
        var custom = new CustomCredentialProvider();
        using var provider = BuildClientApiProvider(
            Config(),
            beforeCore: s => s.AddSingleton<ISlackCredentialProvider>(custom));

        Assert.Same(custom, provider.GetRequiredService<ISlackCredentialProvider>());
    }

    [Fact]
    public void Slack_api_mode_wires_the_socket_monitor()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<INotificationService, RecordingNotificationService>();
        var config = Config(
            ("Detection:Connectors:0:Type", "SlackApi"),
            ("Slack:AppToken", "xapp-1"),
            ("Slack:UserToken", "xoxp-1"));
        services.AddHeadsUpApplication(config);
        services.AddHeadsUpInfrastructure(config);
        services.AddSlackApiHuddleDetection(config);
        using var provider = services.BuildServiceProvider();

        var source = provider.GetRequiredService<IAlertSource>();
        Assert.IsType<SlackHuddleMonitor>(source);
        Assert.Same(source, provider.GetRequiredService<SlackHuddleMonitor>());
        Assert.Contains(provider.GetServices<IHostedService>(), s => s is SlackHuddleMonitor);
    }

    [Fact]
    public void Slack_api_mode_defaults_to_config_credentials_and_the_api_directory()
    {
        using var provider = BuildSlackApiProvider();

        // Priority-people search needs an address book in this mode too, not just client-API mode.
        Assert.IsType<SlackApiUserDirectory>(provider.GetRequiredService<IUserDirectory>());
        Assert.IsType<ConfigSlackAppCredentialProvider>(provider.GetRequiredService<ISlackAppCredentialProvider>());
        Assert.IsType<SlackClientFactory>(provider.GetRequiredService<ISlackClientFactory>());
    }

    [Fact]
    public void A_head_registered_app_credential_provider_wins_over_the_config_one()
    {
        // How the WPF head substitutes the OAuth provider for the appsettings one.
        using var provider = BuildSlackApiProvider(s =>
            s.AddSingleton<ISlackAppCredentialProvider, CustomAppCredentialProvider>());

        Assert.IsType<CustomAppCredentialProvider>(provider.GetRequiredService<ISlackAppCredentialProvider>());
    }

    [Fact]
    public async Task Config_app_credentials_are_withheld_until_both_tokens_are_present()
    {
        using var provider = BuildSlackApiProvider(config: Config(
            ("Detection:Connectors:0:Type", "SlackApi"),
            ("Slack:ClientId", "123.456"),
            ("Slack:AppToken", "xapp-1"))); // no user token yet

        var creds = await provider.GetRequiredService<ISlackAppCredentialProvider>()
            .GetCredentialsAsync(false, CancellationToken.None);

        Assert.Null(creds);
    }

    private static ServiceProvider BuildSlackApiProvider(
        Action<IServiceCollection>? beforeCore = null, IConfiguration? config = null)
    {
        config ??= Config(
            ("Detection:Connectors:0:Type", "SlackApi"),
            ("Slack:ClientId", "123.456"),
            ("Slack:AppToken", "xapp-1"),
            ("Slack:UserToken", "xoxp-1"));

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<INotificationService, RecordingNotificationService>();
        beforeCore?.Invoke(services);
        services.AddHeadsUpApplication(config);
        services.AddHeadsUpInfrastructure(config);
        services.AddSlackApiHuddleDetection(config);
        return services.BuildServiceProvider();
    }

    private sealed class CustomAppCredentialProvider : ISlackAppCredentialProvider
    {
        public string SourceDescription => "custom";
        public Task<SlackAppCredentials?> GetCredentialsAsync(bool forceRefresh, CancellationToken ct) =>
            Task.FromResult<SlackAppCredentials?>(null);

        public Task<SlackAppCredentials?> GetExistingCredentialsAsync(CancellationToken ct) =>
            Task.FromResult<SlackAppCredentials?>(null);
    }

    private sealed class CustomCredentialProvider : ISlackCredentialProvider
    {
        public string SourceDescription => "custom";
        public Task<SlackCredentials?> GetCredentialsAsync(bool forceRefresh, CancellationToken ct) =>
            Task.FromResult<SlackCredentials?>(null);
    }
}

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
        services.AddHeadsUpCore(config);
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
        services.AddHeadsUpCore(config);
        services.AddSlackApiHuddleDetection(config);
        using var provider = services.BuildServiceProvider();

        var source = provider.GetRequiredService<IAlertSource>();
        Assert.IsType<SlackHuddleMonitor>(source);
        Assert.Same(source, provider.GetRequiredService<SlackHuddleMonitor>());
        Assert.Contains(provider.GetServices<IHostedService>(), s => s is SlackHuddleMonitor);
    }

    private sealed class CustomCredentialProvider : ISlackCredentialProvider
    {
        public string SourceDescription => "custom";
        public Task<SlackCredentials?> GetCredentialsAsync(bool forceRefresh, CancellationToken ct) =>
            Task.FromResult<SlackCredentials?>(null);
    }
}

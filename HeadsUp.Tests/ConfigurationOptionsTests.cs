using HeadsUp.Configuration;
using HeadsUp.Models;
using Xunit;

namespace HeadsUp.Tests;

public class ConfigurationOptionsTests
{
    // ------------------------------------------------------------------
    // DetectionOptions
    // ------------------------------------------------------------------

    [Fact]
    public void Detection_defaults_to_no_explicit_connectors()
    {
        var options = new DetectionOptions();
        Assert.Empty(options.Connectors);
        Assert.False(options.NotifyOwnHuddles);
    }

    [Fact]
    public void ResolveConnectors_defaults_to_a_single_client_api_connector_when_empty()
    {
        var connectors = new DetectionOptions().ResolveConnectors();

        Assert.Equal(DetectionOptions.SlackClientApi, Assert.Single(connectors).Type);
    }

    [Fact]
    public void ResolveConnectors_uses_the_configured_connector_list()
    {
        var options = new DetectionOptions
        {
            Connectors =
            {
                new ConnectorOptions { Type = "SlackApi" },
                new ConnectorOptions { Type = "Teams" },
            }
        };

        Assert.Equal(new[] { "SlackApi", "Teams" }, options.ResolveConnectors().Select(c => c.Type));
    }

    // ------------------------------------------------------------------
    // SlackClientOptions
    // ------------------------------------------------------------------

    [Fact]
    public void SlackClient_defaults_to_config_credentials()
    {
        var options = new SlackClientOptions();
        Assert.Equal("Config", options.CredentialSource);
        Assert.False(options.UseWebViewCredentials);
        Assert.Equal(string.Empty, options.ClientToken);
        Assert.Equal(string.Empty, options.Cookie);
        Assert.Equal(string.Empty, options.WorkspaceUrl);
    }

    [Theory]
    [InlineData("WebView2", true)]
    [InlineData("webview2", true)] // case-insensitive
    [InlineData("Config", false)]
    [InlineData("", false)]
    public void UseWebViewCredentials_matches_source_case_insensitively(string source, bool expected)
    {
        Assert.Equal(expected, new SlackClientOptions { CredentialSource = source }.UseWebViewCredentials);
    }

    // ------------------------------------------------------------------
    // AlertOptions / NotificationOptions / SlackOptions defaults
    // ------------------------------------------------------------------

    [Fact]
    public void Alerts_default_to_huddles_and_mentions_only()
    {
        var options = new AlertOptions();
        Assert.True(options.Huddles);
        Assert.True(options.Mentions);
        Assert.False(options.DirectMessages); // DMs can be frequent
        Assert.False(options.Keywords);
        Assert.Empty(options.KeywordList);
    }

    [Fact]
    public void Notification_defaults_to_system_sound_and_15s()
    {
        var options = new NotificationOptions();
        Assert.True(options.PlaySound);
        Assert.Equal(string.Empty, options.SoundFilePath);
        Assert.Equal(15, options.ToastDurationSeconds);
    }

    [Fact]
    public void Slack_defaults_to_empty_tokens()
    {
        var options = new SlackOptions();
        Assert.Equal(string.Empty, options.AppToken);
        Assert.Equal(string.Empty, options.UserToken);
    }

    // ------------------------------------------------------------------
    // PriorityOptions / MutedChannel namespacing
    // ------------------------------------------------------------------

    [Fact]
    public void Priority_defaults_to_no_users()
    {
        Assert.Empty(new PriorityOptions().Users);
    }

    [Fact]
    public void IsPriority_matches_on_source_and_id_case_insensitively()
    {
        var options = new PriorityOptions { Users = { new PriorityUser { Source = "slack", Id = "U1" } } };

        Assert.True(options.IsPriority(new SourceId("SLACK"), new UserId("u1")));
        Assert.False(options.IsPriority(new SourceId("teams"), new UserId("U1"))); // same id, different source
        Assert.False(options.IsPriority(new SourceId("slack"), new UserId("U2")));
    }

    [Fact]
    public void New_namespaced_entries_default_to_the_slack_source()
    {
        Assert.Equal("slack", new PriorityUser().Source);
        Assert.Equal("slack", new MutedChannel().Source);
    }
}

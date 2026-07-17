using HeadsUp.Configuration;
using Xunit;

namespace HeadsUp.Tests;

public class ConfigurationOptionsTests
{
    // ------------------------------------------------------------------
    // DetectionOptions
    // ------------------------------------------------------------------

    [Fact]
    public void Detection_defaults_to_the_client_api_prototype()
    {
        var options = new DetectionOptions();
        Assert.Equal(DetectionOptions.SlackClientApi, options.Mode);
        Assert.True(options.UseSlackClientApi);
        Assert.False(options.NotifyOwnHuddles);
    }

    [Theory]
    [InlineData("SlackClientApi", true)]
    [InlineData("slackclientapi", true)] // case-insensitive
    [InlineData("SLACKCLIENTAPI", true)]
    [InlineData("SlackApi", false)]
    [InlineData("", false)]
    [InlineData("garbage", false)]
    public void UseSlackClientApi_matches_mode_case_insensitively(string mode, bool expected)
    {
        Assert.Equal(expected, new DetectionOptions { Mode = mode }.UseSlackClientApi);
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
    public void Slack_defaults_to_empty_tokens_and_no_priority_users()
    {
        var options = new SlackOptions();
        Assert.Equal(string.Empty, options.AppToken);
        Assert.Equal(string.Empty, options.UserToken);
        Assert.Empty(options.PriorityUsers);
    }
}

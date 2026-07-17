using HeadsUp.Configuration;
using HeadsUp.Services;
using Xunit;

namespace HeadsUp.Tests;

public class CredentialProviderTests
{
    private static ConfigSlackCredentialProvider Provider(SlackClientOptions options) =>
        new(new StaticMonitor<SlackClientOptions>(options));

    [Fact]
    public void Source_description_names_appsettings()
    {
        Assert.Equal("appsettings (SlackClient)", Provider(new()).SourceDescription);
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("xoxc-token", "")]   // cookie missing
    [InlineData("", "d=xoxd-c")]     // token missing
    [InlineData("   ", "d=xoxd-c")]  // whitespace token
    public async Task Returns_null_until_both_values_are_present(string token, string cookie)
    {
        var creds = await Provider(new() { ClientToken = token, Cookie = cookie })
            .GetCredentialsAsync(forceRefresh: false, CancellationToken.None);

        Assert.Null(creds);
    }

    [Fact]
    public async Task Returns_the_configured_credentials()
    {
        var provider = Provider(new()
        {
            ClientToken = "xoxc-token",
            Cookie = "d=xoxd-cookie",
            WorkspaceUrl = "https://myteam.slack.com"
        });

        var creds = await provider.GetCredentialsAsync(forceRefresh: true, CancellationToken.None);

        Assert.NotNull(creds);
        Assert.Equal("xoxc-token", creds.Token);
        Assert.Equal("d=xoxd-cookie", creds.Cookie);
        Assert.Equal("https://myteam.slack.com", creds.WorkspaceUrl);
    }
}

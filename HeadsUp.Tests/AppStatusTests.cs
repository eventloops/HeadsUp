using HeadsUp.Services;
using Xunit;

namespace HeadsUp.Tests;

public class AppStatusTests
{
    [Fact]
    public void Defaults_are_disconnected_and_starting()
    {
        var status = new AppStatus();
        Assert.False(status.IsConnected);
        Assert.Equal("Starting…", status.StatusMessage);
        Assert.Null(status.Account);
        Assert.Null(status.Team);
        Assert.Equal(string.Empty, status.Mode);
        Assert.Equal(string.Empty, status.CredentialSource);
    }

    [Fact]
    public void SetConnection_updates_both_properties_and_notifies()
    {
        var status = new AppStatus();
        var changed = new List<string?>();
        status.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        status.SetConnection(true, "Watching for huddles");

        Assert.True(status.IsConnected);
        Assert.Equal("Watching for huddles", status.StatusMessage);
        Assert.Contains(nameof(AppStatus.IsConnected), changed);
        Assert.Contains(nameof(AppStatus.StatusMessage), changed);
    }

    [Fact]
    public void SetAccount_records_the_identity()
    {
        var status = new AppStatus();

        status.SetAccount("cameron.lambert", "Procode");

        Assert.Equal("cameron.lambert", status.Account);
        Assert.Equal("Procode", status.Team);
    }

    [Fact]
    public void Unchanged_values_do_not_notify()
    {
        var status = new AppStatus();
        status.SetConnection(true, "up");

        var changed = new List<string?>();
        status.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        status.SetConnection(true, "up"); // same values

        Assert.Empty(changed);
    }

    [Fact]
    public void Mode_and_credential_source_are_settable()
    {
        var status = new AppStatus { Mode = "SlackClientApi", CredentialSource = "WebView2 Slack session" };
        Assert.Equal("SlackClientApi", status.Mode);
        Assert.Equal("WebView2 Slack session", status.CredentialSource);
    }
}

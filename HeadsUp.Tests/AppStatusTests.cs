using HeadsUp.Models;
using HeadsUp.Services;
using Xunit;

namespace HeadsUp.Tests;

public class AppStatusTests
{
    [Fact]
    public void Empty_status_is_disconnected_and_starting()
    {
        var status = new AppStatus();
        Assert.False(status.IsConnected);
        Assert.False(status.AnyConnected);
        Assert.Equal("Starting…", status.StatusMessage);
        Assert.Null(status.Account);
        Assert.Null(status.Team);
        Assert.Equal(string.Empty, status.Mode);
        Assert.Equal(string.Empty, status.CredentialSource);
    }

    [Fact]
    public void GetOrAdd_is_idempotent_per_source()
    {
        var status = new AppStatus();
        var a = status.GetOrAdd(new SourceId("slack"), "Slack");
        var b = status.GetOrAdd(new SourceId("slack"), "Slack");

        Assert.Same(a, b);
        Assert.Single(status.Sources);
    }

    [Fact]
    public void Seed_registers_a_source_with_its_mode_in_a_starting_state()
    {
        var status = new AppStatus();
        var slack = status.Seed(new SourceId("slack"), "Slack", "SlackClientApi");

        Assert.Equal("SlackClientApi", slack.Mode);
        Assert.False(slack.IsConnected);
        Assert.Equal("SlackClientApi", status.Mode); // primary-source roll-up
    }

    [Fact]
    public void A_source_connecting_rolls_up_to_the_aggregate_and_notifies()
    {
        var status = new AppStatus();
        var slack = status.Seed(new SourceId("slack"), "Slack", "SlackClientApi");

        var changed = new List<string?>();
        status.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        slack.SetConnection(true, "Watching for huddles");
        slack.SetAccount("cameron.lambert", "Procode");

        Assert.True(status.AnyConnected);
        Assert.True(status.IsConnected);
        Assert.Equal("Watching for huddles", status.StatusMessage); // sole source's message
        Assert.Equal("cameron.lambert", status.Account);
        Assert.Equal("Procode", status.Team);
        Assert.Contains(nameof(AppStatus.IsConnected), changed);
        Assert.Contains(nameof(AppStatus.Account), changed);
    }

    [Fact]
    public void StatusMessage_summarizes_when_multiple_sources_are_present()
    {
        var status = new AppStatus();
        var slack = status.Seed(new SourceId("slack"), "Slack", "SlackClientApi");
        status.Seed(new SourceId("teams"), "Teams", "GraphApi");

        slack.SetConnection(true, "Watching for huddles");

        Assert.Equal("1/2 sources connected", status.StatusMessage);
        Assert.True(status.AnyConnected);
    }

    [Fact]
    public void NeedsSignIn_is_true_when_any_source_needs_sign_in()
    {
        var status = new AppStatus();
        var slack = status.Seed(new SourceId("slack"), "Slack", "SlackClientApi");
        Assert.False(status.NeedsSignIn);

        slack.NeedsSignIn = true;
        Assert.True(status.NeedsSignIn);
    }
}

public class SourceStatusTests
{
    [Fact]
    public void SetConnection_updates_both_properties_and_notifies()
    {
        var status = new SourceStatus(new SourceId("slack"), "Slack");
        var changed = new List<string?>();
        status.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        status.SetConnection(true, "Watching for huddles");

        Assert.True(status.IsConnected);
        Assert.Equal("Watching for huddles", status.StatusMessage);
        Assert.Contains(nameof(SourceStatus.IsConnected), changed);
        Assert.Contains(nameof(SourceStatus.StatusMessage), changed);
    }

    [Fact]
    public void SetAccount_records_the_identity()
    {
        var status = new SourceStatus(new SourceId("slack"), "Slack");
        status.SetAccount("cameron.lambert", "Procode");

        Assert.Equal("cameron.lambert", status.Account);
        Assert.Equal("Procode", status.Team);
    }

    [Fact]
    public void Unchanged_values_do_not_notify()
    {
        var status = new SourceStatus(new SourceId("slack"), "Slack");
        status.SetConnection(true, "up");

        var changed = new List<string?>();
        status.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        status.SetConnection(true, "up"); // same values

        Assert.Empty(changed);
    }
}

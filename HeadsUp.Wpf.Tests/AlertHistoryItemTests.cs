using HeadsUp.Models;
using HeadsUp.ViewModels;
using Xunit;

namespace HeadsUp.Wpf.Tests;

public class AlertHistoryItemTests
{
    private static Alert Make(
        bool dm = false,
        AlertKind kind = AlertKind.Huddle,
        string preview = "",
        string channel = "general",
        DateTimeOffset? at = null) =>
        new(
            InitiatorUserId: new UserId("U1"),
            InitiatorDisplayName: "Sarah Whitfield",
            InitiatorAvatarUrl: null,
            ChannelId: new ChannelId(dm ? "D1" : "C1"),
            ChannelName: channel,
            IsDirectMessage: dm,
            Link: "https://app.slack.com/huddle/T1/C1",
            IsPriorityUser: false,
            OccurredAt: at ?? DateTimeOffset.Now,
            SourceId: new SourceId("slack"),
            SourceName: "Slack",
            Kind: kind,
            Preview: preview);

    [Fact]
    public void Huddle_row_shows_channel_and_headline()
    {
        var item = new AlertHistoryItem(Make(channel: "project-atlas"));
        Assert.Equal("Huddle started", item.Headline);
        Assert.Equal("#project-atlas", item.Where);
        Assert.Equal("#project-atlas", item.Detail); // no preview -> just where
        Assert.Equal("SW", item.Initials);
    }

    [Fact]
    public void Message_row_appends_the_preview_to_detail()
    {
        var item = new AlertHistoryItem(Make(kind: AlertKind.Mention, preview: "can you review?"));
        Assert.Equal("Mentioned you", item.Headline);
        Assert.Equal("#general · can you review?", item.Detail);
    }

    [Fact]
    public void Dm_row_shows_dm_label()
    {
        var item = new AlertHistoryItem(Make(dm: true, kind: AlertKind.DirectMessage, preview: "hi"));
        Assert.Equal("DM", item.Where);
        Assert.Equal("DM · hi", item.Detail);
    }

    [Theory]
    [InlineData(0, "just now")]
    [InlineData(5, "5m ago")]
    public void Recent_times_are_relative(int minutesAgo, string expected)
    {
        var item = new AlertHistoryItem(Make(at: DateTimeOffset.Now.AddMinutes(-minutesAgo)));
        Assert.Equal(expected, item.TimeDisplay);
    }

    [Fact]
    public void Old_times_fall_back_to_a_clock()
    {
        var item = new AlertHistoryItem(Make(at: DateTimeOffset.Now.AddHours(-3)));
        Assert.Matches(@"\d{1,2}:\d{2} (am|pm)", item.TimeDisplay);
    }
}

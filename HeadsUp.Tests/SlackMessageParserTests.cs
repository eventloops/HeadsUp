using HeadsUp.Services;
using Xunit;

namespace HeadsUp.Tests;

public class SlackMessageParserTests
{
    private const string SelfId = "U111SELF";

    [Theory]
    [InlineData("huddle_thread", true)]
    [InlineData("sh_room_created", true)]
    [InlineData("channel_join", false)]
    [InlineData("message_changed", false)]
    [InlineData(null, false)]
    public void IsHuddleSubtype_matches_only_huddle_signals(string? subtype, bool expected) =>
        Assert.Equal(expected, SlackMessageParser.IsHuddleSubtype(subtype));

    [Fact]
    public void IsMention_matches_the_self_token_case_insensitively()
    {
        Assert.True(SlackMessageParser.IsMention($"hey <@{SelfId}> ping", SelfId));
        Assert.True(SlackMessageParser.IsMention($"hey <@{SelfId.ToLowerInvariant()}>", SelfId));
    }

    [Fact]
    public void IsMention_ignores_other_users_and_unknown_self()
    {
        Assert.False(SlackMessageParser.IsMention("<@U999OTHER> ping", SelfId));
        Assert.False(SlackMessageParser.IsMention($"<@{SelfId}> hi", selfUserId: null));
    }

    [Theory]
    [InlineData("D123", true)]
    [InlineData("C123", false)]
    [InlineData("G123", false)]
    public void IsDirectMessage_is_true_only_for_D_channels(string channelId, bool expected) =>
        Assert.Equal(expected, SlackMessageParser.IsDirectMessage(channelId));

    [Fact]
    public void Preview_strips_slack_tokens_and_newlines()
    {
        Assert.Equal("hey , the link",
            SlackMessageParser.Preview("hey <@U123>,\nthe link <https://example.com|here> "));
    }
}

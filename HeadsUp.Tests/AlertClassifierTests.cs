using HeadsUp.Configuration;
using HeadsUp.Models;
using HeadsUp.Services;
using Xunit;

namespace HeadsUp.Tests;

public class AlertClassifierTests
{
    private const string SelfId = "U111SELF";

    private static AlertOptions AllOn(params string[] keywords) => new()
    {
        Huddles = true,
        Mentions = true,
        DirectMessages = true,
        Keywords = true,
        KeywordList = keywords.ToList()
    };

    // ------------------------------------------------------------------
    // IsHuddleSubtype
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("huddle_thread", true)]
    [InlineData("sh_room_created", true)]
    [InlineData("channel_join", false)]
    [InlineData("message_changed", false)]
    [InlineData(null, false)]
    public void IsHuddleSubtype_matches_only_huddle_signals(string? subtype, bool expected) =>
        Assert.Equal(expected, AlertClassifier.IsHuddleSubtype(subtype));

    // ------------------------------------------------------------------
    // ClassifyMessage
    // ------------------------------------------------------------------

    [Fact]
    public void Mention_beats_dm_beats_keyword()
    {
        var alerts = AllOn("deploy");

        // A DM that @-mentions you and contains a keyword is still a Mention.
        Assert.Equal(AlertKind.Mention,
            AlertClassifier.ClassifyMessage($"deploy now <@{SelfId}>", "D123", SelfId, alerts));

        // A DM with a keyword (no mention) is a DirectMessage.
        Assert.Equal(AlertKind.DirectMessage,
            AlertClassifier.ClassifyMessage("deploy now", "D123", SelfId, alerts));

        // A channel message with a keyword is a Keyword.
        Assert.Equal(AlertKind.Keyword,
            AlertClassifier.ClassifyMessage("deploy now", "C123", SelfId, alerts));
    }

    [Fact]
    public void Plain_channel_message_does_not_alert()
    {
        Assert.Null(AlertClassifier.ClassifyMessage("hello world", "C123", SelfId, AllOn("deploy")));
    }

    [Fact]
    public void Mentions_of_someone_else_do_not_alert()
    {
        Assert.Null(AlertClassifier.ClassifyMessage("<@U999OTHER> ping", "C123", SelfId, AllOn()));
    }

    [Fact]
    public void Disabled_toggles_suppress_each_kind()
    {
        var offAlerts = new AlertOptions
        {
            Mentions = false,
            DirectMessages = false,
            Keywords = false,
            KeywordList = ["deploy"]
        };

        Assert.Null(AlertClassifier.ClassifyMessage($"<@{SelfId}> deploy", "D123", SelfId, offAlerts));
    }

    [Fact]
    public void Disabled_mentions_falls_through_to_dm()
    {
        var alerts = AllOn();
        alerts.Mentions = false;

        Assert.Equal(AlertKind.DirectMessage,
            AlertClassifier.ClassifyMessage($"<@{SelfId}> hi", "D123", SelfId, alerts));
    }

    [Fact]
    public void Unknown_self_id_never_matches_a_mention()
    {
        Assert.Null(AlertClassifier.ClassifyMessage("<@U111SELF> hi", "C123", null, AllOn()));
    }

    // ------------------------------------------------------------------
    // MatchesKeyword
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("The DEPLOY went out", true)]  // case-insensitive
    [InlineData("redeployment", true)]         // substring match
    [InlineData("nothing to see", false)]
    public void Keyword_matching_is_case_insensitive_substring(string text, bool expected) =>
        Assert.Equal(expected, AlertClassifier.MatchesKeyword(text, ["deploy"]));

    [Fact]
    public void Blank_keywords_are_ignored()
    {
        Assert.False(AlertClassifier.MatchesKeyword("anything", ["", "  "]));
    }

    // ------------------------------------------------------------------
    // Preview
    // ------------------------------------------------------------------

    [Fact]
    public void Preview_strips_slack_tokens_and_newlines()
    {
        // Angle-bracket tokens (mentions, links) are removed, newlines flattened, ends trimmed.
        Assert.Equal("hey , the link",
            AlertClassifier.Preview("hey <@U123>,\nthe link <https://example.com|here> "));
    }

    [Fact]
    public void Preview_clamps_long_text_at_140_chars()
    {
        var preview = AlertClassifier.Preview(new string('x', 200));
        Assert.Equal(141, preview.Length); // 140 + ellipsis
        Assert.EndsWith("…", preview);
    }

    [Fact]
    public void Preview_leaves_short_text_alone()
    {
        Assert.Equal("short and sweet", AlertClassifier.Preview("short and sweet"));
    }
}

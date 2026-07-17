using HeadsUp.Configuration;
using HeadsUp.Models;
using HeadsUp.Services;
using Xunit;

namespace HeadsUp.Tests;

public class AlertClassifierTests
{
    private const string Slack = "slack";

    private static AlertOptions AllOn(params string[] keywords) => new()
    {
        Huddles = true,
        Mentions = true,
        DirectMessages = true,
        Keywords = true,
        KeywordList = keywords.ToList()
    };

    // ------------------------------------------------------------------
    // Muted channels (now source-aware)
    // ------------------------------------------------------------------

    [Fact]
    public void IsChannelMuted_matches_case_insensitively_on_id_and_source()
    {
        var alerts = AllOn();
        alerts.MutedChannels.Add(new MutedChannel { Id = "C1", Name = "general", Source = Slack });

        Assert.True(AlertClassifier.IsChannelMuted("c1", "SLACK", alerts));
        Assert.False(AlertClassifier.IsChannelMuted("C2", Slack, alerts));
    }

    [Fact]
    public void A_muted_channel_only_silences_its_own_source()
    {
        var alerts = AllOn();
        alerts.MutedChannels.Add(new MutedChannel { Id = "C1", Name = "general", Source = Slack });

        // Same id, different source: not muted (ids can't collide across connectors).
        Assert.True(AlertClassifier.IsChannelMuted("C1", Slack, alerts));
        Assert.False(AlertClassifier.IsChannelMuted("C1", "teams", alerts));
    }

    [Fact]
    public void A_muted_channel_never_classifies_a_message()
    {
        var alerts = AllOn("deploy");
        alerts.MutedChannels.Add(new MutedChannel { Id = "C1", Name = "general", Source = Slack });

        // Would be a keyword hit, but the channel is muted for this source.
        Assert.Null(AlertClassifier.Classify(false, false, "deploy now", "C1", Slack, alerts));
        // A different channel still alerts.
        Assert.Equal(AlertKind.Keyword, AlertClassifier.Classify(false, false, "deploy now", "C2", Slack, alerts));
    }

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
    // Classify (source-neutral policy)
    // ------------------------------------------------------------------

    [Fact]
    public void Mention_beats_dm_beats_keyword()
    {
        var alerts = AllOn("deploy");

        // A DM that mentions you and contains a keyword is still a Mention.
        Assert.Equal(AlertKind.Mention,
            AlertClassifier.Classify(isMention: true, isDm: true, "deploy now", "D123", Slack, alerts));

        // A DM with a keyword (no mention) is a DirectMessage.
        Assert.Equal(AlertKind.DirectMessage,
            AlertClassifier.Classify(isMention: false, isDm: true, "deploy now", "D123", Slack, alerts));

        // A channel message with a keyword is a Keyword.
        Assert.Equal(AlertKind.Keyword,
            AlertClassifier.Classify(isMention: false, isDm: false, "deploy now", "C123", Slack, alerts));
    }

    [Fact]
    public void Plain_channel_message_does_not_alert()
    {
        Assert.Null(AlertClassifier.Classify(false, false, "hello world", "C123", Slack, AllOn("deploy")));
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

        Assert.Null(AlertClassifier.Classify(isMention: true, isDm: true, "deploy", "D123", Slack, offAlerts));
    }

    [Fact]
    public void Disabled_mentions_falls_through_to_dm()
    {
        var alerts = AllOn();
        alerts.Mentions = false;

        Assert.Equal(AlertKind.DirectMessage,
            AlertClassifier.Classify(isMention: true, isDm: true, "hi", "D123", Slack, alerts));
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
    // Truncate
    // ------------------------------------------------------------------

    [Fact]
    public void Truncate_flattens_newlines_and_trims()
    {
        Assert.Equal("a b", AlertClassifier.Truncate("a\nb "));
    }

    [Fact]
    public void Truncate_clamps_long_text_at_140_chars()
    {
        var preview = AlertClassifier.Truncate(new string('x', 200));
        Assert.Equal(141, preview.Length); // 140 + ellipsis
        Assert.EndsWith("…", preview);
    }

    [Fact]
    public void Truncate_leaves_short_text_alone()
    {
        Assert.Equal("short and sweet", AlertClassifier.Truncate("short and sweet"));
    }
}

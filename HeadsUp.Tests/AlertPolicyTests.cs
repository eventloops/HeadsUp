using HeadsUp.Configuration;
using HeadsUp.Models;
using HeadsUp.Services;
using Xunit;

namespace HeadsUp.Tests;

public class AlertPolicyTests
{
    private static readonly SourceId Slack = new("slack");

    private static AlertPreferences AllOn(params string[] keywords) => new AlertOptions
    {
        Huddles = true,
        Mentions = true,
        DirectMessages = true,
        Keywords = true,
        KeywordList = keywords.ToList()
    }.ToPreferences();

    private static AlertPreferences WithMuted(string channelId, string source, params string[] keywords)
    {
        var options = new AlertOptions
        {
            Huddles = true,
            Mentions = true,
            DirectMessages = true,
            Keywords = true,
            KeywordList = keywords.ToList(),
            MutedChannels = { new MutedChannel { Id = channelId, Name = "general", Source = source } }
        };
        return options.ToPreferences();
    }

    // ------------------------------------------------------------------
    // Muted channels (source-aware)
    // ------------------------------------------------------------------

    [Fact]
    public void IsChannelMuted_matches_case_insensitively_on_id_and_source()
    {
        var muted = WithMuted("C1", "slack").Muted;

        Assert.True(AlertPolicy.IsChannelMuted(new ChannelId("c1"), new SourceId("SLACK"), muted));
        Assert.False(AlertPolicy.IsChannelMuted(new ChannelId("C2"), Slack, muted));
    }

    [Fact]
    public void A_muted_channel_only_silences_its_own_source()
    {
        var muted = WithMuted("C1", "slack").Muted;

        Assert.True(AlertPolicy.IsChannelMuted(new ChannelId("C1"), Slack, muted));
        Assert.False(AlertPolicy.IsChannelMuted(new ChannelId("C1"), new SourceId("teams"), muted));
    }

    [Fact]
    public void A_muted_channel_never_classifies_a_message()
    {
        var prefs = WithMuted("C1", "slack", "deploy");

        Assert.Null(AlertPolicy.Classify(false, false, "deploy now", new ChannelId("C1"), Slack, prefs));
        Assert.Equal(AlertKind.Keyword,
            AlertPolicy.Classify(false, false, "deploy now", new ChannelId("C2"), Slack, prefs));
    }

    // ------------------------------------------------------------------
    // Classify (source-neutral policy)
    // ------------------------------------------------------------------

    [Fact]
    public void Mention_beats_dm_beats_keyword()
    {
        var prefs = AllOn("deploy");

        Assert.Equal(AlertKind.Mention,
            AlertPolicy.Classify(isMention: true, isDm: true, "deploy now", new ChannelId("D123"), Slack, prefs));
        Assert.Equal(AlertKind.DirectMessage,
            AlertPolicy.Classify(isMention: false, isDm: true, "deploy now", new ChannelId("D123"), Slack, prefs));
        Assert.Equal(AlertKind.Keyword,
            AlertPolicy.Classify(isMention: false, isDm: false, "deploy now", new ChannelId("C123"), Slack, prefs));
    }

    [Fact]
    public void Plain_channel_message_does_not_alert()
    {
        Assert.Null(AlertPolicy.Classify(false, false, "hello world", new ChannelId("C123"), Slack, AllOn("deploy")));
    }

    [Fact]
    public void Disabled_toggles_suppress_each_kind()
    {
        var prefs = new AlertOptions
        {
            Mentions = false,
            DirectMessages = false,
            Keywords = false,
            KeywordList = ["deploy"]
        }.ToPreferences();

        Assert.Null(AlertPolicy.Classify(isMention: true, isDm: true, "deploy", new ChannelId("D123"), Slack, prefs));
    }

    [Fact]
    public void Disabled_mentions_falls_through_to_dm()
    {
        var prefs = new AlertOptions
        {
            Mentions = false, DirectMessages = true, Keywords = true
        }.ToPreferences();

        Assert.Equal(AlertKind.DirectMessage,
            AlertPolicy.Classify(isMention: true, isDm: true, "hi", new ChannelId("D123"), Slack, prefs));
    }

    // ------------------------------------------------------------------
    // MatchesKeyword
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("The DEPLOY went out", true)]
    [InlineData("redeployment", true)]
    [InlineData("nothing to see", false)]
    public void Keyword_matching_is_case_insensitive_substring(string text, bool expected) =>
        Assert.Equal(expected, AlertPolicy.MatchesKeyword(text, [new Keyword("deploy")]));

    [Fact]
    public void Blank_keywords_are_ignored()
    {
        Assert.False(AlertPolicy.MatchesKeyword("anything", [new Keyword(""), new Keyword("  ")]));
    }

    // ------------------------------------------------------------------
    // Truncate
    // ------------------------------------------------------------------

    [Fact]
    public void Truncate_flattens_newlines_and_trims()
    {
        Assert.Equal("a b", AlertPolicy.Truncate("a\nb "));
    }

    [Fact]
    public void Truncate_clamps_long_text_at_140_chars()
    {
        var preview = AlertPolicy.Truncate(new string('x', 200));
        Assert.Equal(141, preview.Length);
        Assert.EndsWith("…", preview);
    }

    [Fact]
    public void Truncate_leaves_short_text_alone()
    {
        Assert.Equal("short and sweet", AlertPolicy.Truncate("short and sweet"));
    }
}

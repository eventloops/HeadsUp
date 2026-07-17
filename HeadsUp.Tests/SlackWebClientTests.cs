using HeadsUp.Services;
using System.Text.Json;
using Xunit;

namespace HeadsUp.Tests;

public class SlackWebClientTests
{
    // ------------------------------------------------------------------
    // Cookie normalization (observable via CookieHeader)
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("d=xoxd-abc; d-s=123", "d=xoxd-abc; d-s=123")] // full header stays as-is
    [InlineData("D=xoxd-abc", "D=xoxd-abc")]                   // case-insensitive prefix check
    [InlineData("xoxd-abc", "d=xoxd-abc")]                     // bare value gets the d= prefix
    [InlineData("  xoxd-abc  ", "d=xoxd-abc")]                 // trimmed
    [InlineData("", "d=")]                                     // empty stays harmless
    [InlineData("   ", "d=")]
    public void Cookie_header_is_normalized(string cookie, string expected)
    {
        using var client = new SlackWebClient("xoxc-token", cookie);
        Assert.Equal(expected, client.CookieHeader);
    }

    [Fact]
    public void UserAgent_presents_the_slack_desktop_shape()
    {
        using var client = new SlackWebClient("xoxc-token", "d=x");
        Assert.Equal(SlackWebClient.BrowserUserAgent, client.UserAgent);
        Assert.Contains("Slack/", client.UserAgent);
    }

    // ------------------------------------------------------------------
    // JsonElementExtensions (internal helpers over Slack's loose JSON)
    // ------------------------------------------------------------------

    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [Fact]
    public void Str_returns_string_properties_and_null_otherwise()
    {
        var el = Parse("""{ "name": "general", "count": 3, "nested": {} }""");
        Assert.Equal("general", el.Str("name"));
        Assert.Null(el.Str("count"));   // wrong kind
        Assert.Null(el.Str("nested"));  // wrong kind
        Assert.Null(el.Str("missing")); // absent
        Assert.Null(Parse("[1,2]").Str("name")); // not an object
    }

    [Fact]
    public void Bool_is_true_only_for_true()
    {
        var el = Parse("""{ "yes": true, "no": false, "text": "true" }""");
        Assert.True(el.Bool("yes"));
        Assert.False(el.Bool("no"));
        Assert.False(el.Bool("text"));    // wrong kind
        Assert.False(el.Bool("missing")); // absent
        Assert.False(Parse("42").Bool("yes")); // not an object
    }

    [Fact]
    public void Obj_returns_nested_objects_and_null_otherwise()
    {
        var el = Parse("""{ "profile": { "name": "x" }, "list": [], "s": "y" }""");
        Assert.NotNull(el.Obj("profile"));
        Assert.Equal("x", el.Obj("profile")!.Value.Str("name"));
        Assert.Null(el.Obj("list"));
        Assert.Null(el.Obj("s"));
        Assert.Null(el.Obj("missing"));
    }
}

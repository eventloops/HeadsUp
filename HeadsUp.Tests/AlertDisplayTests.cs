using HeadsUp.Models;
using HeadsUp.ViewModels;
using Xunit;

namespace HeadsUp.Tests;

public class AlertDisplayTests
{
    [Theory]
    [InlineData(AlertKind.Huddle, "Huddle started", "Join huddle")]
    [InlineData(AlertKind.Mention, "Mentioned you", "Open")]
    [InlineData(AlertKind.DirectMessage, "Direct message", "Open")]
    [InlineData(AlertKind.Keyword, "Keyword match", "Open")]
    public void Describe_maps_each_kind(AlertKind kind, string headline, string action)
    {
        var (h, icon, a) = AlertDisplay.Describe(kind);
        Assert.Equal(headline, h);
        Assert.Equal(action, a);
        Assert.False(string.IsNullOrEmpty(icon));
    }

    [Theory]
    [InlineData("Sarah Whitfield", "SW")]
    [InlineData("Anna Maria Jones", "AJ")]
    [InlineData("Bono", "B")]
    [InlineData("", "?")]
    [InlineData("   ", "?")]
    public void Initials_use_first_and_last_name(string name, string expected) =>
        Assert.Equal(expected, AlertDisplay.Initials(name));
}

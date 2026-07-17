using HeadsUp.Models;
using System.Text.RegularExpressions;

namespace HeadsUp.Services;

/// <summary>
/// Builds Slack desktop-app deep links (<c>slack://</c>) for an alert, preferred over
/// bouncing through the browser. Falls through to nothing when the link isn't a
/// recognized Slack URL — <see cref="AlertLinks"/> then uses the raw web link.
/// </summary>
public sealed class SlackAlertLinkResolver : IAlertLinkResolver
{
    public string SourceId => SlackSource.Id;

    public IEnumerable<string> Targets(Alert alert)
    {
        var link = alert.Link;

        var huddle = Regex.Match(link, @"app\.slack\.com/huddle/(?<team>[^/]+)/(?<channel>[^/?#]+)");
        if (huddle.Success)
            yield return $"slack://huddle?team={huddle.Groups["team"].Value}&id={huddle.Groups["channel"].Value}";

        var channel = Regex.Match(link, @"app\.slack\.com/client/(?<team>[^/]+)/(?<channel>[^/?#]+)");
        if (channel.Success)
            yield return $"slack://channel?team={channel.Groups["team"].Value}&id={channel.Groups["channel"].Value}";
    }
}

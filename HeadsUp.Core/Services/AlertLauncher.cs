using System.Diagnostics;
using System.Text.RegularExpressions;

namespace HeadsUp.Services;

/// <summary>
/// Opens an alert's target — preferring the desktop Slack app (a <c>slack://</c> deep
/// link) over bouncing through the browser, and falling back to the original web link
/// if a launch fails (e.g. the desktop app isn't installed). Shared by the huddle popup
/// and the dashboard's alert history.
/// </summary>
public static class AlertLauncher
{
    /// <summary>The real launcher: hands the target to the OS. Returns true once launched.</summary>
    public static readonly Func<string, bool> DefaultLaunch = target =>
    {
        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        return true;
    };

    /// <summary>Tries each target in order until <paramref name="launch"/> handles one.</summary>
    public static void Open(string link, Func<string, bool> launch)
    {
        foreach (var target in Targets(link))
        {
            try
            {
                if (launch(target))
                    return;
            }
            catch { /* try the next target */ }
        }
    }

    /// <summary>Desktop-app deep link first, then the original (web) link as a fallback.</summary>
    public static IEnumerable<string> Targets(string link)
    {
        var huddle = Regex.Match(link, @"app\.slack\.com/huddle/(?<team>[^/]+)/(?<channel>[^/?#]+)");
        if (huddle.Success)
            yield return $"slack://huddle?team={huddle.Groups["team"].Value}&id={huddle.Groups["channel"].Value}";

        var channel = Regex.Match(link, @"app\.slack\.com/client/(?<team>[^/]+)/(?<channel>[^/?#]+)");
        if (channel.Success)
            yield return $"slack://channel?team={channel.Groups["team"].Value}&id={channel.Groups["channel"].Value}";

        yield return link;
    }
}

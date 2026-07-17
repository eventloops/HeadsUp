using System.Diagnostics;

namespace HeadsUp.Services;

/// <summary>
/// Launches an alert's target — trying each candidate in order (a connector's deep link
/// first, the raw web link last) and stopping at the first the OS accepts. Target
/// resolution is connector-specific and lives in <see cref="AlertLinks"/>; this type only
/// performs the launch. Shared by the huddle popup and the dashboard's alert history.
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
    public static void Open(IEnumerable<string> targets, Func<string, bool> launch)
    {
        foreach (var target in targets)
        {
            try
            {
                if (launch(target))
                    return;
            }
            catch { /* try the next target */ }
        }
    }

    /// <summary>Convenience for a single raw link (no connector-specific deep links).</summary>
    public static void Open(string link, Func<string, bool> launch) =>
        Open(new[] { link }, launch);
}

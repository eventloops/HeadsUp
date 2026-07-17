using System.IO;
using System.Windows;

namespace HeadsUp.Services;

/// <summary>The app's colour themes.</summary>
public enum AppTheme { Dark, Light }

/// <summary>
/// Swaps the app's colour dictionary at runtime and remembers the choice. Colours live in a
/// dictionary merged before the control styles, so re-adding both re-resolves every
/// <c>StaticResource</c> brush against the new theme (windows opened afterwards pick it up).
/// </summary>
public static class ThemeManager
{
    private const string ControlsUri = "/Themes/Controls.xaml";

    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HeadsUp", "appearance.txt");

    public static AppTheme Current { get; private set; } = AppTheme.Dark;

    private static string ColorsUri(AppTheme theme) =>
        theme == AppTheme.Light ? "/Themes/Colors.Light.xaml" : "/Themes/Colors.Dark.xaml";

    /// <summary>Reads the saved theme and applies it. Call once at startup, before windows show.</summary>
    public static void Initialize()
    {
        Apply(ReadSaved(), persist: false);
    }

    /// <summary>Applies a theme to the running app and (by default) saves the choice.</summary>
    public static void Apply(AppTheme theme, bool persist = true)
    {
        Current = theme;

        var app = Application.Current;
        if (app is not null)
        {
            app.Resources.MergedDictionaries.Clear();
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri(ColorsUri(theme), UriKind.Relative) });
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri(ControlsUri, UriKind.Relative) });
        }

        if (persist)
            Save(theme);
    }

    private static AppTheme ReadSaved()
    {
        try
        {
            if (File.Exists(SettingsPath) &&
                Enum.TryParse<AppTheme>(File.ReadAllText(SettingsPath).Trim(), ignoreCase: true, out var saved))
                return saved;
        }
        catch { /* fall back to default */ }
        return AppTheme.Dark;
    }

    private static void Save(AppTheme theme)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, theme.ToString());
        }
        catch { /* best effort */ }
    }
}

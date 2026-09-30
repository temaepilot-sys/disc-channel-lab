using System.Windows;
using System.Windows.Media;

namespace Disc2Flac;

internal static class ThemeService
{
    private static readonly string PreferencePath = AppDataPaths.Current.PreferencePath("theme-v2.txt");

    public static bool IsDark { get; private set; }

    public static void Load()
    {
        var readPath = AppDataPaths.Current.PreferenceReadPath("theme-v2.txt");
        try { Apply(!File.Exists(readPath) || File.ReadAllText(readPath).Trim() != "light"); }
        catch (IOException) { Apply(true); }
        catch (UnauthorizedAccessException) { Apply(true); }
    }

    public static void SetDark(bool dark)
    {
        if (IsDark == dark) return;
        Apply(dark);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PreferencePath)!);
            File.WriteAllText(PreferencePath, dark ? "dark" : "light");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    internal static void Apply(bool dark)
    {
        IsDark = dark;
        var colors = dark ? new[]
        {
            "#101820", "#1B2631", "#273847", "#EDF3F8", "#A7BBCB", "#506273",
            "#59B2FF", "#0C1822", "#315673", "#344657", "#FF8B80"
        } : new[]
        {
            "#F5F7FA", "#FFFFFF", "#FFFFFF", "#26323D", "#627282", "#C4CED8",
            "#1670C5", "#FFFFFF", "#DAEFFC", "#E6EEF4", "#B3261E"
        };
        var names = new[]
        {
            "AppBackgroundBrush", "SurfaceBrush", "InputBrush", "TextBrush", "MutedBrush",
            "BorderBrush", "AccentBrush", "AccentTextBrush", "SelectionBrush",
            "MeterTrackBrush", "ErrorBrush"
        };
        for (var i = 0; i < names.Length; i++)
            Application.Current.Resources[names[i]] = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString(colors[i]));
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace Tasky.Platform;

/// <summary>
/// The native title bar colours, read from the theme files (DarkTheme.axaml / LightTheme.axaml)
/// so the title bar always matches the app header, even after the theme colours change.
/// </summary>
internal static class TitleBarColors
{
    /// <summary>Title bar background: the same colour as the header under it.</summary>
    public static Color Caption(bool dark) => Read("HeaderBackground", dark, dark ? "#121A28" : "#F4F6FA");

    /// <summary>Title text colour.</summary>
    public static Color Text(bool dark) => Read("TextPrimary", dark, dark ? "#F3F5F9" : "#141B29");

    private static Color Read(string key, bool dark, string fallback)
    {
        var variant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        if (Application.Current?.Resources.TryGetResource(key, variant, out var value) == true)
        {
            if (value is ISolidColorBrush brush) return brush.Color;
            if (value is Color color)            return color;
        }
        return Color.Parse(fallback);
    }
}

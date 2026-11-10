using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace Tasky.Helpers;

/// <summary>Theme-aware resource lookups for controls built in code.</summary>
public static class UiResources
{
    /// <summary>Binds <paramref name="property"/> to a theme brush, so it follows Dark / Light switches.</summary>
    public static T WithResource<T>(this T element, AvaloniaProperty property, string key) where T : StyledElement
    {
        element.Bind(property, element.GetResourceObservable(key));
        return element;
    }

    /// <summary>A shared control theme (e.g. "FilterChip") from the app resources.</summary>
    public static ControlTheme Theme(string key)
        => (ControlTheme)Application.Current!.FindResource(key)!;

    /// <summary>A line icon from Resources/Icons.axaml, e.g. "Icon.Bell".</summary>
    public static Geometry Geometry(string key)
        => (Geometry)Application.Current!.FindResource(key)!;

    /// <summary>A theme brush for the current Dark / Light variant (a snapshot: it does not follow later switches).</summary>
    public static IBrush Brush(string key)
    {
        var app = Application.Current!;
        return app.TryGetResource(key, app.ActualThemeVariant, out var value) && value is IBrush brush
            ? brush
            : Brushes.Transparent;
    }

    /// <summary>A line-icon control for code-built UI; its colour follows the parent's Foreground unless set.</summary>
    public static Controls.LineIcon Icon(string key, double size = 18)
        => new() { Data = Geometry(key), Size = size };
}

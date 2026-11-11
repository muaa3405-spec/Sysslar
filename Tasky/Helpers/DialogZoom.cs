using Avalonia.Controls;
using Avalonia.Media;

namespace Tasky.Helpers;

/// <summary>
/// Gives a popup window the same zoom as the main window (Settings → Appearance → Zoom),
/// so dialogs are not bigger or smaller than the rest of the app.
/// </summary>
public static class DialogZoom
{
    /// <summary>Call after InitializeComponent, before the window is shown.</summary>
    public static void Apply(Window window)
    {
        double zoom = App.Settings.Current.EffectiveZoom;
        if (Math.Abs(zoom - TextSizes.Normal) < 0.001 || window.Content is not Control content
            || content is LayoutTransformControl { Tag: "dialogZoom" }) return;

        // Detach the content first: a control can only have one parent, and wrapping it while the
        // window still holds it throws "The Control already has a parent" (every dialog at 90 % zoom,
        // the Windows default).
        window.Content = null;
        window.Content = new LayoutTransformControl
        {
            Tag             = "dialogZoom",
            LayoutTransform = new ScaleTransform(zoom, zoom),
            Child           = content,
        };
        // Fixed window sizes scale with the content; SizeToContent sizes follow it by themselves
        if (!double.IsNaN(window.Width))  window.Width  *= zoom;
        if (!double.IsNaN(window.Height)) window.Height *= zoom;
        window.MinWidth  *= zoom;
        window.MinHeight *= zoom;
    }
}

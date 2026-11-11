using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace Tasky.Helpers;

/// <summary>
/// Marks a popup that already has its own scrolling area (a scroll viewer in a row that shrinks), so
/// its buttons stay in view when the popup is capped. Other content-sized popups scroll as a whole.
/// </summary>
public interface IScrollsOwnContent;

/// <summary>
/// Places every popup the same way: opened in the centre of the Sysslar window, never bigger than it
/// (or than the screen, when the window is hidden), and scrollable when its content is longer.
/// </summary>
public static class DialogLayoutHelper
{
    /// <summary>Space kept free around a popup inside the Sysslar window.</summary>
    private const double AppWindowMargin = 16;

    /// <summary>Share of the screen a popup may use when the Sysslar window is hidden (e.g. Quick Add from the tray).</summary>
    private const double MaxWorkAreaRatio = 0.9;

    /// <summary>Call after InitializeComponent, before the popup is shown: zoom, fit, scroll and centre.</summary>
    public static void Prepare(Window window)
    {
        DialogZoom.Apply(window);

        var app = VisibleAppWindow(window);
        var limit = app is not null ? AvailableInside(app) : ScreenLimit(window);
        window.MaxWidth  = Math.Min(window.MaxWidth,  limit.Width);
        window.MaxHeight = Math.Min(window.MaxHeight, limit.Height);
        window.MinWidth  = Math.Min(window.MinWidth,  window.MaxWidth);
        window.MinHeight = Math.Min(window.MinHeight, window.MaxHeight);

        if (window is not IScrollsOwnContent && window.SizeToContent.HasFlag(SizeToContent.Height))
            ScrollWhenCapped(window);

        window.WindowStartupLocation = app is not null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;
        window.Opened += (_, _) =>
        {
            CentreAndKeepOnScreen(window, app);
            // Content that arrives after opening (details, a list, options that appear) changes the
            // height of a content-sized popup: centre it again, so it never grows off the window.
            window.SizeChanged += (_, _) => CentreAndKeepOnScreen(window, app);
        };
    }

    /// <summary>The main Sysslar window when it is on screen (not hidden in the tray or minimised).</summary>
    private static Window? VisibleAppWindow(Window popup)
    {
        var app = App.MainAppWindow;
        return app is not null && app != popup && app.IsVisible && app.WindowState != WindowState.Minimized ? app : null;
    }

    private static Size AvailableInside(Window app)
    {
        var client = app.ClientSize;
        var screen = ScreenLimit(app);
        return new Size(
            Math.Min(Math.Max(client.Width  - 2 * AppWindowMargin, 0), screen.Width),
            Math.Min(Math.Max(client.Height - 2 * AppWindowMargin, 0), screen.Height));
    }

    private static Size ScreenLimit(Window window)
    {
        var area = ScreenHelper.WorkArea(window);
        return new Size(area.Width * MaxWorkAreaRatio, area.Height * MaxWorkAreaRatio);
    }

    /// <summary>Puts the content in a scroll viewer, so a popup capped to the window can still show all of it.</summary>
    private static void ScrollWhenCapped(Window window)
    {
        if (window.Content is not Control content || content is ScrollViewer) return;
        window.Content = null;   // a control can only have one parent
        window.Content = new ScrollViewer
        {
            Content = content,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
        };
    }

    /// <summary>
    /// Centres the popup over the Sysslar window (its owner may be another popup) and keeps it fully on
    /// the screen, also when the Sysslar window itself reaches past the screen edge.
    /// </summary>
    private static void CentreAndKeepOnScreen(Window window, Window? app)
    {
        double s = window.DesktopScaling;
        var size = window.FrameSize ?? window.Bounds.Size;   // with the title bar, where there is one
        var area = ScreenHelper.WorkArea(window);

        var centre = app is not null
            ? Centre(app)
            : area.Center;
        double left = Math.Clamp(centre.X - size.Width  / 2, area.X, Math.Max(area.X, area.Right  - size.Width));
        double top  = Math.Clamp(centre.Y - size.Height / 2, area.Y, Math.Max(area.Y, area.Bottom - size.Height));

        var target = new PixelPoint((int)Math.Round(left * s), (int)Math.Round(top * s));
        if (Math.Abs(target.X - window.Position.X) > 1 || Math.Abs(target.Y - window.Position.Y) > 1)
            window.Position = target;
    }

    /// <summary>The centre of a window on the desktop (device-independent units), title bar included.</summary>
    private static Point Centre(Window window)
    {
        var size = window.FrameSize ?? window.Bounds.Size;
        return new Point(window.Position.X / window.DesktopScaling + size.Width  / 2,
                         window.Position.Y / window.DesktopScaling + size.Height / 2);
    }
}

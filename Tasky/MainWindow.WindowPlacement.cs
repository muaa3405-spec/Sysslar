using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Tasky.Helpers;

namespace Tasky;

/// <summary>
/// The main window always fits the screen it is on (so its title bar can't end up off-screen on a
/// small laptop display), and its size and position are remembered between launches.
/// </summary>
public partial class MainWindow
{
    /// <summary>Room left around the window so the title bar and borders stay on screen.</summary>
    private const double ScreenMargin = 12;

    /// <summary>Title bar and borders that Windows draws outside the client area.</summary>
    private double FrameExtraHeight => OperatingSystem.IsWindows() ? 40 : 0;

    private DispatcherTimer? _placementSaveTimer;
    private bool _placementReady;

    /// <summary>Before the window opens: last session's size (fitted to the screen once it opens).</summary>
    private void RestoreWindowPlacement()
    {
        var s = App.Settings.Current;
        if (s.WindowWidth  > 0) Width  = Math.Max(s.WindowWidth,  MinWidth);
        if (s.WindowHeight > 0) Height = Math.Max(s.WindowHeight, MinHeight);
        if (s.WindowLeft.HasValue && s.WindowTop.HasValue) WindowStartupLocation = WindowStartupLocation.Manual;

        Opened += (_, _) =>
        {
            FitToScreen(useSavedPosition: true);
            if (s.WindowMaximized) WindowState = WindowState.Maximized;
            _placementReady = true;
        };
        PositionChanged += (_, _) => SchedulePlacementSave();
        Resized         += (_, _) => SchedulePlacementSave();
        PropertyChanged += (_, e) => { if (e.Property == WindowStateProperty) SchedulePlacementSave(); };
    }

    /// <summary>
    /// Shrinks the window to the screen's usable area (without the taskbar or menu bar) and moves it
    /// fully onto the screen. Also lowers the minimum size on very small screens.
    /// </summary>
    private void FitToScreen(bool useSavedPosition = false)
    {
        if (WindowState != WindowState.Normal) return;
        var work = ScreenHelper.WorkArea(this);
        double maxWidth  = Math.Max(320, work.Width  - 2 * ScreenMargin);
        double maxHeight = Math.Max(320, work.Height - 2 * ScreenMargin - FrameExtraHeight);

        if (MinWidth  > maxWidth)  MinWidth  = maxWidth;
        if (MinHeight > maxHeight) MinHeight = maxHeight;
        Width  = Math.Min(Width,  maxWidth);
        Height = Math.Min(Height, maxHeight);

        var s = App.Settings.Current;
        double left, top;
        if (useSavedPosition && s.WindowLeft is { } savedLeft && s.WindowTop is { } savedTop)
            (left, top) = (savedLeft, savedTop);
        else if (useSavedPosition)
        {
            // First start: centre the (possibly shrunk) window
            (left, top) = (work.X + (work.Width - Width) / 2, work.Y + (work.Height - Height - FrameExtraHeight) / 2);
        }
        else
        {
            double scaling = DesktopScaling > 0 ? DesktopScaling : 1;
            (left, top) = (Position.X / scaling, Position.Y / scaling);
        }

        // Keep the whole window, title bar first, inside the usable area
        double outerHeight = Height + FrameExtraHeight;
        left = Math.Clamp(left, work.X + ScreenMargin, Math.Max(work.X + ScreenMargin, work.Right - Width - ScreenMargin));
        top  = Math.Clamp(top,  work.Y + ScreenMargin, Math.Max(work.Y + ScreenMargin, work.Bottom - outerHeight - ScreenMargin));
        ScreenHelper.MoveTo(this, new Point(left, top));
    }

    private void SchedulePlacementSave()
    {
        if (!_placementReady) return;
        _placementSaveTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _placementSaveTimer.Stop();
        _placementSaveTimer.Tick -= SavePlacement;
        _placementSaveTimer.Tick += SavePlacement;
        _placementSaveTimer.Start();
    }

    private void SavePlacement(object? sender, EventArgs e)
    {
        _placementSaveTimer?.Stop();
        var s = App.Settings.Current;
        s.WindowMaximized = WindowState == WindowState.Maximized;
        if (WindowState == WindowState.Normal)
        {
            double scaling = DesktopScaling > 0 ? DesktopScaling : 1;
            s.WindowWidth  = Width;
            s.WindowHeight = Height;
            s.WindowLeft   = Position.X / scaling;
            s.WindowTop    = Position.Y / scaling;
        }
        App.Settings.Save();
    }
}

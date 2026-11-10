namespace Tasky.Helpers;

/// <summary>
/// The zoom of the main window (Settings → Appearance → Zoom, or Ctrl/⌘ − + 0).
/// Text, icons and spacing all scale together.
/// </summary>
public static class TextSizes
{
    /// <summary>The steps the − and + buttons move through.</summary>
    public static readonly double[] Steps = [0.8, 0.85, 0.9, 1.0, 1.1, 1.2, 1.3];

    public const double Min    = 0.8;
    public const double Max    = 1.3;
    public const double Normal = 1.0;

    /// <summary>
    /// Until the user picks a zoom: Windows starts a little smaller, because Windows display scaling
    /// (125–150 % on most laptops) already makes everything large. macOS starts at 100 %.
    /// </summary>
    public static double Default => OperatingSystem.IsWindows() ? 0.9 : Normal;

    /// <summary>Keeps a hand-edited settings file from making the window unusable.</summary>
    public static double Clamp(double scale) => Math.Clamp(scale, Min, Max);

    /// <summary>The next step up (+1) or down (−1) from <paramref name="current"/>.</summary>
    public static double Step(double current, int direction)
    {
        if (direction > 0)
            return Steps.FirstOrDefault(s => s > current + 0.001, Max);
        return Steps.LastOrDefault(s => s < current - 0.001, Min);
    }

    public static string Label(double scale) => $"{Math.Round(scale * 100)} %";
}

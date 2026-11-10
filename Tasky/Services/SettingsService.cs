using System.IO;
using System.Text.Json;
using Tasky.Helpers;

namespace Tasky.Services;

public class AppSettings
{
    public string  UserName              { get; set; } = string.Empty;
    public string  Theme                 { get; set; } = ThemePreferences.Dark;
    public string  NotifTune             { get; set; } = "proximity";
    public string  NotifAccentColor      { get; set; } = "#60A5FA";
    public int     NotifAutoDismissSecs  { get; set; } = 30;
    public bool    ShowCompletionPopup      { get; set; } = true;
    public bool    SaveScreenshotToDesktop { get; set; } = true;

    /// <summary>Off by default: Tasky only goes online when the user asks it to.</summary>
    public bool     CheckForUpdates  { get; set; }
    public DateTime LastUpdateCheck  { get; set; } = DateTime.MinValue;

    /// <summary>The "Good to know" tips are shown once, on the first start.</summary>
    public bool    HasSeenWelcomeTips { get; set; }

    /// <summary>Old "Text size" setting (1, 1.15 or 1.3); only read to carry it over to <see cref="Zoom"/>.</summary>
    public double  TextScale { get; set; } = 1.0;

    /// <summary>Main-window zoom (see <see cref="Helpers.TextSizes"/>). Null until the user picks one.</summary>
    public double? Zoom { get; set; }

    /// <summary>The zoom to use: the user's choice, the old text size, or the platform default.</summary>
    public double EffectiveZoom => Helpers.TextSizes.Clamp(
        Zoom ?? (TextScale > Helpers.TextSizes.Normal ? TextScale : Helpers.TextSizes.Default));

    // ── Remembered between launches ──────────────────────────────────────

    /// <summary>Main window size and position (device-independent units); 0 / null = not saved yet.</summary>
    public double  WindowWidth  { get; set; }
    public double  WindowHeight { get; set; }
    public double? WindowLeft   { get; set; }
    public double? WindowTop    { get; set; }
    public bool    WindowMaximized { get; set; }

    /// <summary>Task list view: All, Today or ThisWeek.</summary>
    public string  TaskView         { get; set; } = "Today";
    public int?    GroupFilter      { get; set; }
    public string  CompletedSort    { get; set; } = "newest";
    public string  TopTasksFilter   { get; set; } = "overall";
    public string  MeetingFilter    { get; set; } = "week";
    public string  AppUsageFilter   { get; set; } = "today";
}

public class SettingsService
{
    private static string _path => AppPaths.SettingsFile;

    private AppSettings _settings = new();
    public AppSettings Current => _settings;

    public void Load()
    {
        try
        {
            if (File.Exists(_path))
                _settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path))
                            ?? new AppSettings();
        }
        catch { _settings = new AppSettings(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path,
                JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}

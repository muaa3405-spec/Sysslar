using Avalonia.Controls;
using Tasky.Platform.MacOS;
using Tasky.Platform.Windows;

namespace Tasky.Platform;

/// <summary>
/// Picks the Windows or macOS implementation of every OS-specific feature once,
/// so the rest of Tasky never checks which system it runs on.
/// </summary>
public static class PlatformServices
{
    public static ISoundPlayer      Sounds    { get; }
    public static IClipboardService Clipboard { get; }
    public static IAppIconProvider  AppIcons  { get; }
    public static IWindowChrome     Chrome    { get; }

    /// <summary>Windows: the hidden window that receives tray and hotkey messages. Created on first use (UI thread).</summary>
    private static Win32MessageWindow? _messageWindow;

    static PlatformServices()
    {
        if (OperatingSystem.IsWindows())
        {
            Sounds    = new WindowsSoundPlayer();
            Clipboard = new WindowsClipboard();
            AppIcons  = new WindowsAppIconProvider();
            Chrome    = new WindowsWindowChrome();
        }
        else if (OperatingSystem.IsMacOS())
        {
            Sounds    = new MacSoundPlayer();
            Clipboard = new MacClipboard();
            AppIcons  = new MacAppIconProvider();
            Chrome    = new NoWindowChrome();
        }
        else
        {
            throw new PlatformNotSupportedException("Sysslar runs on Windows and macOS.");
        }
    }

    /// <summary>Process-wide OS setup, before any window opens.</summary>
    public static void ConfigureProcess()
    {
        // Windows groups the taskbar button, pins and notifications under this id.
        if (OperatingSystem.IsWindows()) NativeMethods.SetCurrentProcessExplicitAppUserModelID(AppUserModelId);
    }

    private const string AppUserModelId = "Nordfin.Tasky.App";

    public static ISystemTray CreateTray()
    {
        if (OperatingSystem.IsWindows()) return new WindowsSystemTray(MessageWindow());
        if (OperatingSystem.IsMacOS())   return new MacMenuBarIcon();
        throw new PlatformNotSupportedException();
    }

    public static IGlobalHotkey CreateQuickAddHotkey()
    {
        if (OperatingSystem.IsWindows()) return new WindowsGlobalHotkey(MessageWindow());
        if (OperatingSystem.IsMacOS())   return new MacGlobalHotkey();
        throw new PlatformNotSupportedException();
    }

    public static IUserActivity CreateUserActivity()
    {
        if (OperatingSystem.IsWindows()) return new WindowsUserActivity();
        if (OperatingSystem.IsMacOS())   return new MacUserActivity();
        throw new PlatformNotSupportedException();
    }

    public static IForegroundAppMonitor CreateForegroundAppMonitor()
    {
        if (OperatingSystem.IsWindows()) return new WindowsForegroundAppMonitor();
        if (OperatingSystem.IsMacOS())   return new MacForegroundAppMonitor();
        throw new PlatformNotSupportedException();
    }

    /// <summary>Meeting detection reads Windows audio sessions and has no macOS counterpart.</summary>
    public static bool SupportsMeetingDetection => OperatingSystem.IsWindows();

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static Win32MessageWindow MessageWindow() => _messageWindow ??= new Win32MessageWindow();

    /// <summary>macOS draws its title bars to match the app theme on its own.</summary>
    private sealed class NoWindowChrome : IWindowChrome
    {
        public void ApplyTheme(Window window, bool dark) { }
        public void ApplyThemeWithCaption(Window window, bool dark) { }
    }
}

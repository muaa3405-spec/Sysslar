using System.Runtime.Versioning;
using Avalonia.Controls;
using static Tasky.Platform.Windows.NativeMethods;

namespace Tasky.Platform.Windows;

/// <summary>Dark title bars and a caption colour matching the app header, through DWM.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsWindowChrome : IWindowChrome
{
    public void ApplyTheme(Window window, bool dark)
    {
        if (HandleOf(window) is not { } hwnd) return;
        int value = dark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int));
    }

    public void ApplyThemeWithCaption(Window window, bool dark)
    {
        ApplyTheme(window, dark);
        if (HandleOf(window) is not { } hwnd) return;
        int caption = ToColorRef(TitleBarColors.Caption(dark));
        int text    = ToColorRef(TitleBarColors.Text(dark));
        DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref caption, sizeof(int));
        DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR,    ref text,    sizeof(int));
    }

    private static IntPtr? HandleOf(Window window)
        => window.TryGetPlatformHandle()?.Handle is { } h && h != IntPtr.Zero ? h : null;

    /// <summary>COLORREF = 0x00BBGGRR (BGR order).</summary>
    private static int ToColorRef(Avalonia.Media.Color c) => (c.B << 16) | (c.G << 8) | c.R;
}

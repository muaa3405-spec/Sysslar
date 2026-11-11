using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Controls;

namespace Tasky.Platform.MacOS;

/// <summary>
/// Colours the macOS title bar to match the app header. The title bar is made transparent,
/// so the window's own background colour (set to the header colour) shows through it,
/// while the traffic-light buttons, the title, dragging and double-click-to-zoom all stay native.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class MacWindowChrome : IWindowChrome
{
    private const string LibObjC = "/usr/lib/libobjc.A.dylib";

    [DllImport(LibObjC)] private static extern IntPtr sel_registerName(string name);
    [DllImport(LibObjC, EntryPoint = "objc_msgSend")] private static extern void   MsgBool(IntPtr receiver, IntPtr selector, byte value);
    [DllImport(LibObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr MsgRgba(IntPtr receiver, IntPtr selector,
                                                                                           double r, double g, double b, double a);

    /// <summary>Dialogs: only the light or dark look, so the title and buttons read well.</summary>
    public void ApplyTheme(Window window, bool dark)
    {
        if (NSWindowOf(window) is not { } nsWindow) return;
        ObjC.WithPool(() =>
        {
            SetAppearance(nsWindow, dark);
            return 0;
        });
    }

    /// <summary>Main window: the title bar takes the header colour, so window and app read as one.</summary>
    public void ApplyThemeWithCaption(Window window, bool dark)
    {
        if (NSWindowOf(window) is not { } nsWindow) return;
        ObjC.WithPool(() =>
        {
            SetAppearance(nsWindow, dark);
            MsgBool(nsWindow, sel_registerName("setTitlebarAppearsTransparent:"), 1);

            var c = TitleBarColors.Caption(dark);
            var color = MsgRgba(ObjC.Class("NSColor"), sel_registerName("colorWithSRGBRed:green:blue:alpha:"),
                                c.R / 255.0, c.G / 255.0, c.B / 255.0, 1.0);
            ObjC.Send(nsWindow, "setBackgroundColor:", color);
            return 0;
        });
    }

    private static void SetAppearance(IntPtr nsWindow, bool dark)
    {
        var name = ObjC.NSString(dark ? "NSAppearanceNameDarkAqua" : "NSAppearanceNameAqua");
        var appearance = ObjC.Send(ObjC.Class("NSAppearance"), "appearanceNamed:", name);
        if (appearance != IntPtr.Zero) ObjC.Send(nsWindow, "setAppearance:", appearance);
    }

    /// <summary>Avalonia hands out the window's NSView (or, on some versions, the NSWindow itself).</summary>
    private static IntPtr? NSWindowOf(Window window)
    {
        if (window.TryGetPlatformHandle() is not { Handle: var handle } platform || handle == IntPtr.Zero) return null;
        var nsWindow = platform.HandleDescriptor == "NSWindow" ? handle : ObjC.Send(handle, "window");
        return nsWindow == IntPtr.Zero ? null : nsWindow;
    }
}

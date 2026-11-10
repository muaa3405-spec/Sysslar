using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using static Tasky.Platform.Windows.NativeMethods;

namespace Tasky.Platform.Windows;

/// <summary>
/// A hidden native window that receives tray-icon and hotkey messages.
/// Created on the UI thread, whose message loop dispatches to it.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class Win32MessageWindow : IDisposable
{
    private const string ClassName = "Tasky_MessageWindow";

    // Kept in a field so the garbage collector never frees the native callback.
    private readonly WndProc _wndProc;

    /// <summary>Raised for every message; return true when it was handled.</summary>
    public event Func<uint, IntPtr, IntPtr, bool>? MessageReceived;

    public IntPtr Handle { get; }

    public Win32MessageWindow()
    {
        _wndProc = WindowProcedure;
        var wc = new WNDCLASSEX
        {
            cbSize        = Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc   = _wndProc,
            hInstance     = GetModuleHandle(null),
            lpszClassName = ClassName
        };
        RegisterClassEx(ref wc);   // fails harmlessly if the class already exists
        Handle = CreateWindowEx(0, ClassName, "Sysslar", 0, 0, 0, 0, 0,
                                IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
    }

    private IntPtr WindowProcedure(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (MessageReceived?.Invoke(msg, wParam, lParam) == true) return IntPtr.Zero;
        }
        catch (Exception ex)
        {
            // An exception must never cross back into native code.
            Services.CrashLog.Write("Error handling a window message", ex);
        }
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (Handle != IntPtr.Zero) DestroyWindow(Handle);
    }
}

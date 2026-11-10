using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Tasky.Platform.MacOS;

namespace Tasky.Helpers;

/// <summary>
/// Whether the app should animate. Follows the operating system's "reduce motion"
/// setting (macOS: Accessibility › Display › Reduce motion; Windows: "Show animations").
/// </summary>
public static class Motion
{
    private static bool? _enabled;

    public static bool Enabled => _enabled ??= !OsPrefersReducedMotion();

    /// <summary>Read the OS setting again (e.g. when the app comes back to the foreground).</summary>
    public static void Refresh() => _enabled = null;

    private static bool OsPrefersReducedMotion()
    {
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                return MacPrefersReducedMotion();
            }
            if (OperatingSystem.IsWindows())
            {
                bool animations = true;
                return SystemParametersInfo(SpiGetClientAreaAnimation, 0, ref animations, 0) && !animations;
            }
        }
        catch
        {
            // No answer from the OS: animate
        }
        return false;
    }

    [SupportedOSPlatform("macos")]
    private static bool MacPrefersReducedMotion() => ObjC.WithPool(() =>
    {
        var workspace = ObjC.Send(ObjC.Class("NSWorkspace"), "sharedWorkspace");
        return ObjC.SendBool(workspace, "accessibilityDisplayShouldReduceMotion");
    });

    private const uint SpiGetClientAreaAnimation = 0x1042;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, ref bool value, uint winIni);
}

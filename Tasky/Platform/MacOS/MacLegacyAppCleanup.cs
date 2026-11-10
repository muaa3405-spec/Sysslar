using System.IO;
using Tasky.Services;

namespace Tasky.Platform.MacOS;

/// <summary>
/// Tasky was renamed Sysslar. On macOS the app is a folder people drag to Applications, so the
/// renamed Sysslar.app lands next to the old Tasky.app instead of replacing it. Both are the same
/// app (same bundle id, same data), so once Sysslar runs, the old copy is moved to the Trash.
/// </summary>
public static class MacLegacyAppCleanup
{
    private const string LegacyBundleName = "Tasky.app";
    private const string BundleId         = "com.nordfin.tasky";

    public static void MoveOldTaskyAppToTrash()
    {
        if (!OperatingSystem.IsMacOS()) return;
        try
        {
            var current = CurrentBundle();
            if (current is null || current.EndsWith("/" + LegacyBundleName, StringComparison.Ordinal)) return;   // still running as Tasky.app

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            foreach (var folder in new[] { "/Applications", Path.Combine(home, "Applications") })
            {
                var old = Path.Combine(folder, LegacyBundleName);
                if (!IsOurApp(old) || SamePath(old, current)) continue;

                var trash = Path.Combine(home, ".Trash");
                Directory.CreateDirectory(trash);
                var target = Path.Combine(trash, LegacyBundleName);
                if (Directory.Exists(target)) target = Path.Combine(trash, $"Tasky {DateTime.Now:yyyy-MM-dd HH.mm.ss}.app");
                Directory.Move(old, target);
            }
        }
        catch (Exception ex)
        {
            CrashLog.Write("Could not move the old Tasky.app to the Trash", ex);   // harmless: both copies work
        }
    }

    /// <summary>…/Sysslar.app for …/Sysslar.app/Contents/MacOS/Tasky, or null when not run from a bundle.</summary>
    private static string? CurrentBundle()
    {
        var exe = Environment.ProcessPath;
        var macOs = exe is null ? null : Path.GetDirectoryName(exe);
        var contents = macOs is null ? null : Path.GetDirectoryName(macOs);
        var bundle = contents is null ? null : Path.GetDirectoryName(contents);
        return bundle is not null && bundle.EndsWith(".app", StringComparison.Ordinal) ? bundle : null;
    }

    private static bool IsOurApp(string bundle)
    {
        var plist = Path.Combine(bundle, "Contents", "Info.plist");
        return File.Exists(plist) && File.ReadAllText(plist).Contains($"<string>{BundleId}</string>", StringComparison.Ordinal);
    }

    private static bool SamePath(string a, string b)
        => string.Equals(Path.GetFullPath(a).TrimEnd('/'), Path.GetFullPath(b).TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
}

using System.IO;
using System.Text.Json;

namespace Tasky.Services.Sync;

/// <summary>A cloud folder found on this computer.</summary>
public sealed record CloudFolder(string Service, string Path);

/// <summary>
/// Finds the folders of Google Drive, iCloud Drive, OneDrive and Dropbox on Windows and macOS.
/// Sysslar never talks to these services: it writes files into their folder and their own app
/// moves them, so nothing needs signing in and Sysslar itself stays offline.
/// </summary>
public static class CloudFolders
{
    public const string GoogleDrive = "Google Drive";
    public const string ICloud      = "iCloud Drive";
    public const string OneDrive    = "OneDrive";
    public const string Dropbox     = "Dropbox";

    /// <summary>
    /// Sandboxed test runs only: offer this folder instead of the real cloud folders, so a test never
    /// writes into the developer's Google Drive.
    /// </summary>
    public const string TestFolderVariable = "TASKY_SYNC_TEST_FOLDER";

    public static IReadOnlyList<CloudFolder> Detect()
    {
        if (AppPaths.IsSandboxed)
        {
            var test = Environment.GetEnvironmentVariable(TestFolderVariable);
            if (string.IsNullOrEmpty(test)) return [];
            Directory.CreateDirectory(test);
            return [new CloudFolder("Test folder", test)];
        }
        var found = new List<CloudFolder>();
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (OperatingSystem.IsMacOS()) DetectMac(home, found);
            else if (OperatingSystem.IsWindows()) DetectWindows(home, found);
        }
        catch (Exception ex) { CrashLog.Write("Looking for cloud folders failed", ex); }
        return [.. found.Where(f => Directory.Exists(f.Path))
                        .GroupBy(f => System.IO.Path.GetFullPath(f.Path), StringComparer.OrdinalIgnoreCase)
                        .Select(g => g.First())];
    }

    /// <summary>The service a folder belongs to, for display ("Google Drive"), or null for any other folder.</summary>
    public static string? ServiceOf(string folder)
        => Detect().FirstOrDefault(f => string.Equals(System.IO.Path.GetFullPath(f.Path), System.IO.Path.GetFullPath(folder),
                                                      StringComparison.OrdinalIgnoreCase))?.Service
           ?? (folder.Contains("GoogleDrive", StringComparison.OrdinalIgnoreCase) || folder.Contains("Google Drive", StringComparison.OrdinalIgnoreCase) ? GoogleDrive
             : folder.Contains("OneDrive", StringComparison.OrdinalIgnoreCase) ? OneDrive
             : folder.Contains("Dropbox", StringComparison.OrdinalIgnoreCase) ? Dropbox
             : folder.Contains("CloudDocs", StringComparison.OrdinalIgnoreCase) || folder.Contains("iCloudDrive", StringComparison.OrdinalIgnoreCase) ? ICloud
             : null);

    private static void DetectMac(string home, List<CloudFolder> found)
    {
        var cloudStorage = System.IO.Path.Combine(home, "Library", "CloudStorage");
        if (Directory.Exists(cloudStorage))
        {
            foreach (var dir in Directory.EnumerateDirectories(cloudStorage, "GoogleDrive-*"))
            {
                var myDrive = Directory.EnumerateDirectories(dir).FirstOrDefault(d =>
                    System.IO.Path.GetFileName(d) is "My Drive" or "Min disk" or "Meine Ablage" or "Mon Drive" or "Mi unidad");
                found.Add(new(GoogleDrive, myDrive ?? System.IO.Path.Combine(dir, "My Drive")));
            }
            foreach (var dir in Directory.EnumerateDirectories(cloudStorage, "OneDrive*")) found.Add(new(OneDrive, dir));
            foreach (var dir in Directory.EnumerateDirectories(cloudStorage, "Dropbox*")) found.Add(new(Dropbox, dir));
        }
        found.Add(new(ICloud,  System.IO.Path.Combine(home, "Library", "Mobile Documents", "com~apple~CloudDocs")));
        found.Add(new(Dropbox, System.IO.Path.Combine(home, "Dropbox")));
        found.Add(new(GoogleDrive, System.IO.Path.Combine(home, "Google Drive", "My Drive")));
    }

    private static void DetectWindows(string home, List<CloudFolder> found)
    {
        // Google Drive for desktop: a virtual drive (usually G:) with "My Drive" in it
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady) continue;
                if (drive.VolumeLabel.Contains("Google Drive", StringComparison.OrdinalIgnoreCase))
                    found.Add(new(GoogleDrive, System.IO.Path.Combine(drive.RootDirectory.FullName, "My Drive")));
            }
            catch { /* a drive that isn't ready */ }
        }
        found.Add(new(GoogleDrive, System.IO.Path.Combine(home, "Google Drive")));   // older "mirror" installs
        found.Add(new(GoogleDrive, System.IO.Path.Combine(home, "My Drive")));

        found.Add(new(ICloud, System.IO.Path.Combine(home, "iCloudDrive")));

        foreach (var variable in new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
            if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } path) found.Add(new(OneDrive, path));

        var dropboxInfo = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Dropbox", "info.json");
        if (File.Exists(dropboxInfo))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(dropboxInfo));
                foreach (var account in doc.RootElement.EnumerateObject())
                    if (account.Value.TryGetProperty("path", out var p) && p.GetString() is { } dropboxPath)
                        found.Add(new(Dropbox, dropboxPath));
            }
            catch { /* unreadable: fall back to the usual place */ }
        }
        found.Add(new(Dropbox, System.IO.Path.Combine(home, "Dropbox")));
    }
}

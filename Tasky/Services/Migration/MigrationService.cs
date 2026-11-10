using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace Tasky.Services.Migration;

/// <summary>
/// "Migrate to another computer": packs everything Tasky stores into one
/// password-encrypted file, and restores such a file on another PC.
///
/// Import happens in two steps so no file is replaced while Tasky has it open:
///   1. <see cref="StageImport"/> decrypts and checks the file into a staging folder;
///   2. after a restart, <see cref="ApplyPendingImport"/> swaps the staged data in
///      before anything else opens the database. The previous data is kept as a
///      safety copy in %LocalAppData%\Tasky\before-import\.
/// </summary>
public sealed class MigrationService
{
    public const string FileExtension = ".taskymigration";
    public const int    MinPasswordLength = 8;

    private const string ReadyMarker = "ready";

    private readonly DatabaseService _db;

    public MigrationService(DatabaseService db) => _db = db;

    public MigrationCounts CurrentCounts() => DatabaseService.CountRecords(AppPaths.DatabaseFile);

    // ── Export ────────────────────────────────────────────────────────────

    /// <param name="recoveryKey">Optional recovery key that opens the file when the password is forgotten.</param>
    public void Export(string destinationFile, string password, string? recoveryKey = null)
    {
        var work = NewWorkFolder("export");
        try
        {
            var snapshot = Path.Combine(work, AppPaths.DatabaseFileName);
            _db.SnapshotTo(snapshot);

            var manifest = new MigrationManifest(
                MigrationManifest.CurrentFormat, DateTime.Now, AppVersion(),
                DatabaseService.CountRecords(snapshot));

            var zip = Path.Combine(work, "package.zip");
            MigrationPackage.Create(zip, manifest, snapshot, AppPaths.SettingsFile, AppPaths.AttachmentsFolder);

            // Write next to the destination first, so a failure never leaves a half-written file behind.
            var partial = destinationFile + ".partial";
            using (var input  = File.OpenRead(zip))
            using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write))
                MigrationCrypto.Encrypt(input, output, password, recoveryKey);
            File.Move(partial, destinationFile, overwrite: true);
        }
        finally
        {
            TryDeleteFolder(work);
        }
    }

    // ── Import ────────────────────────────────────────────────────────────

    /// <summary>Decrypts and checks a migration file. Nothing on this computer is changed yet.</summary>
    /// <param name="password">The password, or the file's recovery key.</param>
    /// <exception cref="MigrationFileException">Wrong password or unusable file.</exception>
    public MigrationManifest StageImport(string sourceFile, string password)
    {
        CancelStagedImport();
        var work = NewWorkFolder("import");
        try
        {
            var zip = Path.Combine(work, "package.zip");
            using (var input  = File.OpenRead(sourceFile))
            using (var output = new FileStream(zip, FileMode.Create, FileAccess.Write))
                MigrationCrypto.Decrypt(input, output, password);

            var manifest = MigrationPackage.Extract(zip, AppPaths.PendingImportFolder);
            // Make sure the database really opens before we offer to use it.
            DatabaseService.CountRecords(StagedDatabase);
            return manifest;
        }
        catch
        {
            CancelStagedImport();
            throw;
        }
        finally
        {
            TryDeleteFolder(work);
        }
    }

    /// <summary>What a workspace holds now, or null when it has no database yet (a new, never opened one).</summary>
    public static MigrationCounts? CountsFor(string workspaceId)
    {
        var file = AppPaths.DatabaseFileFor(workspaceId);
        return File.Exists(file) ? DatabaseService.CountRecords(file) : null;
    }

    /// <summary>
    /// Confirms the staged import into <paramref name="workspaceId"/>; it is applied the next time
    /// Tasky starts in that workspace. Staging happens in the open workspace, so for another
    /// workspace the staged files are moved over to it first.
    /// </summary>
    public void ConfirmStagedImport(string workspaceId)
    {
        var target = AppPaths.PendingImportFolderFor(workspaceId);
        if (workspaceId != Workspaces.Active.Id)
        {
            TryDeleteFolder(target);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            MoveFolder(AppPaths.PendingImportFolder, target);
        }
        File.WriteAllText(Path.Combine(target, ReadyMarker), DateTime.Now.ToString("o"));
    }

    public void CancelStagedImport() => TryDeleteFolder(AppPaths.PendingImportFolder);

    private static string StagedData     => Path.Combine(AppPaths.PendingImportFolder, MigrationPackage.DataFolderName);
    private static string StagedDatabase => Path.Combine(StagedData, AppPaths.DatabaseFileName);

    /// <summary>
    /// Call at startup, before the database is opened. Returns true when a confirmed
    /// import was applied. On failure the previous data is put back and the error rethrown.
    /// </summary>
    public static bool ApplyPendingImport()
    {
        if (!File.Exists(Path.Combine(AppPaths.PendingImportFolder, ReadyMarker))) return false;

        var safetyCopy = Path.Combine(AppPaths.ImportBackupsFolder, $"Tasky-{DateTime.Now:yyyyMMdd-HHmmss}");
        bool movedOld = false;
        try
        {
            if (Directory.Exists(AppPaths.DataFolder))
            {
                Directory.CreateDirectory(AppPaths.ImportBackupsFolder);
                MoveFolder(AppPaths.DataFolder, safetyCopy);
                movedOld = true;
            }
            MoveFolder(StagedData, AppPaths.DataFolder);
            TryDeleteFolder(AppPaths.PendingImportFolder);
            return true;
        }
        catch (Exception ex)
        {
            CrashLog.Write("Applying the migration import failed; restoring previous data", ex);
            if (movedOld && !Directory.Exists(AppPaths.DataFolder)) MoveFolder(safetyCopy, AppPaths.DataFolder);
            TryDeleteFolder(AppPaths.PendingImportFolder);
            throw;
        }
    }

    /// <summary>Starts a fresh Tasky that waits for this one to exit, then closes this one.</summary>
    /// <param name="extraArgs">Passed on to the new Tasky (e.g. <see cref="Workspaces.OpenImportArg"/>).</param>
    public static void RestartTasky(params string[] extraArgs)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot find the Sysslar executable");
        var start = new ProcessStartInfo(exe) { UseShellExecute = false };
        start.ArgumentList.Add(WaitForPidArg);
        start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var arg in extraArgs) start.ArgumentList.Add(arg);
        Process.Start(start);
        App.Shutdown();
    }

    public const string WaitForPidArg = "--wait-for-pid";

    /// <summary>Handles the restart argument: waits (briefly) for the previous Tasky to close.</summary>
    public static void WaitForPreviousInstance(string[] args)
    {
        int i = Array.IndexOf(args, WaitForPidArg);
        if (i < 0 || i + 1 >= args.Length || !int.TryParse(args[i + 1], out int pid)) return;
        try { Process.GetProcessById(pid).WaitForExit(15_000); }
        catch (ArgumentException) { /* already gone */ }
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static string AppVersion()
        => Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";

    private static string NewWorkFolder(string purpose)
    {
        var folder = Path.Combine(AppPaths.TempFolder, $"{purpose}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static void MoveFolder(string from, string to)
    {
        try
        {
            Directory.Move(from, to);
        }
        catch (IOException) when (!SameVolume(from, to))
        {
            CopyFolder(from, to);
            Directory.Delete(from, recursive: true);
        }
    }

    private static bool SameVolume(string a, string b)
        => string.Equals(Path.GetPathRoot(Path.GetFullPath(a)), Path.GetPathRoot(Path.GetFullPath(b)),
                         StringComparison.OrdinalIgnoreCase);

    private static void CopyFolder(string from, string to)
    {
        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)));
    }

    private static void TryDeleteFolder(string folder)
    {
        try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
        catch (Exception ex) { CrashLog.Write($"Could not delete '{folder}'", ex); }
    }
}

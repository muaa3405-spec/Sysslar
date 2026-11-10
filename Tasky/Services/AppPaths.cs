using System.IO;

namespace Tasky.Services;

/// <summary>Single source of truth for where Tasky keeps its files on this computer.</summary>
public static class AppPaths
{
    /// <summary>
    /// Developer option: set this environment variable to a folder to run Tasky
    /// against sandbox data instead of your real data (used for testing).
    /// </summary>
    public const string DataRootOverrideVariable = "TASKY_DATA_ROOT";

    private static readonly string? OverrideRoot =
        Environment.GetEnvironmentVariable(DataRootOverrideVariable) is { Length: > 0 } root ? root : null;

    /// <summary>Where the OS keeps per-user application data.</summary>
    private static string UserDataRoot => OperatingSystem.IsMacOS()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support")
        : Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    // ── Workspaces ────────────────────────────────────────────────────────

    /// <summary>
    /// Data of the first ("default") workspace: Tasky's original folder
    /// (%AppData%\Tasky on Windows, ~/Library/Application Support/Tasky on macOS).
    /// </summary>
    public static string DefaultDataFolder => OverrideRoot is not null
        ? Path.Combine(OverrideRoot, "Roaming", "Tasky")
        : Path.Combine(UserDataRoot, "Tasky");

    /// <summary>Working files of the default workspace (see <see cref="LocalFolder"/>).</summary>
    private static string DefaultLocalFolder => OverrideRoot is not null
        ? Path.Combine(OverrideRoot, "Local", "Tasky")
        : OperatingSystem.IsMacOS()
            ? Path.Combine(UserDataRoot, "Tasky Local")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tasky");

    /// <summary>
    /// Every other workspace's data, one sub-folder each, plus the workspace list
    /// (%AppData%\Tasky Workspaces on Windows, ~/Library/Application Support/Tasky Workspaces on macOS).
    /// Never inside a workspace's <see cref="DataFolder"/>, because an import swaps that whole folder.
    /// </summary>
    public static string WorkspacesFolder => OverrideRoot is not null
        ? Path.Combine(OverrideRoot, "Roaming", "Tasky Workspaces")
        : Path.Combine(UserDataRoot, "Tasky Workspaces");

    /// <summary>Working files of the other workspaces, one sub-folder each.</summary>
    private static string WorkspacesLocalFolder => OverrideRoot is not null
        ? Path.Combine(OverrideRoot, "Local", "Tasky Workspaces")
        : OperatingSystem.IsMacOS()
            ? Path.Combine(UserDataRoot, "Tasky Local Workspaces")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tasky Workspaces");

    /// <summary>The list of workspaces and which one Tasky opens.</summary>
    public static string WorkspaceRegistryFile => Path.Combine(WorkspacesFolder, "workspaces.json");

    public static string DataFolderFor(string workspaceId) => workspaceId == Workspaces.DefaultId
        ? DefaultDataFolder
        : Path.Combine(WorkspacesFolder, workspaceId);

    public static string LocalFolderFor(string workspaceId) => workspaceId == Workspaces.DefaultId
        ? DefaultLocalFolder
        : Path.Combine(WorkspacesLocalFolder, workspaceId);

    // ── The open workspace ────────────────────────────────────────────────

    /// <summary>User data of the open workspace: database, settings, attachments.</summary>
    public static string DataFolder => DataFolderFor(Workspaces.Active.Id);

    public static string DatabaseFile      => Path.Combine(DataFolder, DatabaseFileName);
    public static string SettingsFile      => Path.Combine(DataFolder, SettingsFileName);
    public static string AttachmentsFolder => Path.Combine(DataFolder, AttachmentsFolderName);

    public const string DatabaseFileName      = "tasky.db";
    public const string SettingsFileName      = "settings.json";
    public const string AttachmentsFolderName = "Attachments";

    /// <summary>
    /// Machine-local working files of the open workspace: logs, import staging, safety copies, backups
    /// (%LocalAppData%\Tasky on Windows, ~/Library/Application Support/Tasky Local on macOS, for the default one).
    /// Never inside <see cref="DataFolder"/>, because an import swaps that whole folder.
    /// </summary>
    public static string LocalFolder => LocalFolderFor(Workspaces.Active.Id);

    public static string LogsFolder          => Path.Combine(LocalFolder, "logs");
    public static string PendingImportFolder => Path.Combine(LocalFolder, "pending-import");
    public static string ImportBackupsFolder => Path.Combine(LocalFolder, "before-import");
    public static string TempFolder          => Path.Combine(LocalFolder, "temp");
    public static string BackupsFolder       => Path.Combine(LocalFolder, "backups");
    public static string PendingRestoreFile  => Path.Combine(LocalFolder, "pending-restore.txt");
}

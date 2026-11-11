using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tasky.Services.Sync;

/// <summary>One workspace's sync setting on this computer.</summary>
public sealed class WorkspaceLink
{
    /// <summary>The workspace's id in the sync folder, shared by every computer that syncs it.</summary>
    public string SyncId  { get; set; } = string.Empty;
    public bool   Enabled { get; set; }
    public DateTime? LastSync { get; set; }
    /// <summary>Hash of the content last written to this computer's file, so unchanged data isn't rewritten.</summary>
    public string? LastWrittenHash { get; set; }
}

/// <summary>
/// Sync settings of this computer, in <see cref="AppPaths.SyncFolder"/>/sync.json. The key itself is
/// never here; it's in the Windows or macOS key store (<see cref="ISyncSecretStore"/>).
/// </summary>
public sealed class SyncConfig
{
    public string DeviceId   { get; set; } = SyncIds.New();
    public string DeviceName { get; set; } = DefaultDeviceName();
    /// <summary>The folder the user picked (for example "My Drive"); sync files go in its "Sysslar Sync" folder.</summary>
    public string? Folder     { get; set; }
    /// <summary>Key fingerprint (hex) of the key in the key store. Null = sync is off.</summary>
    public string? Fingerprint { get; set; }
    /// <summary>Why syncing stopped until the user acts (for example the key was changed elsewhere), or null.</summary>
    public string? PausedReason { get; set; }
    public long LastSeq { get; set; }
    public DateTime? LastDeviceWrite { get; set; }
    public string?   LastDeviceHash  { get; set; }
    public DateTime? LastCleanup     { get; set; }
    /// <summary>Local workspace id → its sync setting.</summary>
    public Dictionary<string, WorkspaceLink> Workspaces { get; set; } = [];

    [JsonIgnore] public bool IsOn => Folder is not null && Fingerprint is not null;
    [JsonIgnore] public string? SyncRoot => Folder is null ? null : Path.Combine(Folder, SyncFolderLayout.RootName);

    /// <summary>A save number that only ever grows, even across restarts and clock changes.</summary>
    public long NextSeq()
    {
        LastSeq = Math.Max(LastSeq + 1, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        return LastSeq;
    }

    public static string DefaultFile => Path.Combine(AppPaths.SyncFolder, "sync.json");
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    [JsonIgnore] public string FilePath { get; private set; } = DefaultFile;

    public static SyncConfig Load(string? file = null)
    {
        file ??= DefaultFile;
        SyncConfig config = new();
        try
        {
            if (File.Exists(file))
                config = JsonSerializer.Deserialize<SyncConfig>(File.ReadAllText(file)) ?? new SyncConfig();
        }
        catch (Exception ex) { CrashLog.Write("Sync settings couldn't be read, so sync starts fresh", ex); }
        config.FilePath = file;
        return config;
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Options));
        File.Move(tmp, FilePath, overwrite: true);
    }

    private static string DefaultDeviceName()
    {
        var name = Environment.MachineName.Replace('-', ' ').Trim();
        if (name.EndsWith(" local", StringComparison.OrdinalIgnoreCase)) name = name[..^6];
        return name.Length == 0 ? "This computer" : name;
    }
}

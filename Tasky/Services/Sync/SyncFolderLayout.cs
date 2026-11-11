using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Tasky.Services.Sync;

/// <summary>A workspace as listed in a computer's device file.</summary>
public sealed class SharedWorkspace
{
    [JsonPropertyName("id")]    public string SyncId { get; set; } = string.Empty;
    [JsonPropertyName("name")]  public string Name   { get; set; } = string.Empty;
    [JsonPropertyName("color")] public string Color  { get; set; } = string.Empty;
}

/// <summary>A computer that syncs: its name and the workspaces it syncs (devices/&lt;id&gt;.sysslar).</summary>
public sealed class DeviceInfo
{
    [JsonPropertyName("id")]         public string DeviceId { get; set; } = string.Empty;
    [JsonPropertyName("name")]       public string Name     { get; set; } = string.Empty;
    [JsonPropertyName("os")]         public string Os       { get; set; } = string.Empty;
    [JsonPropertyName("seen")]       public DateTime LastSeen { get; set; }
    [JsonPropertyName("workspaces")] public List<SharedWorkspace> Workspaces { get; set; } = [];
}

/// <summary>Proves which key the folder uses (key.sysslar).</summary>
public sealed class KeyCheck
{
    [JsonPropertyName("by")] public string MadeBy { get; set; } = string.Empty;
    [JsonPropertyName("at")] public DateTime At { get; set; }
}

/// <summary>"The key was changed": sealed with the old key, so only computers that had it can trust it.</summary>
public sealed class KeyChangeNotice
{
    [JsonPropertyName("new")] public string NewFingerprint { get; set; } = string.Empty;
    [JsonPropertyName("by")]  public string ChangedBy { get; set; } = string.Empty;
    [JsonPropertyName("at")]  public DateTime At { get; set; }
}

/// <summary>
/// Where everything lives in the sync folder. Every computer writes only its own files, so two
/// computers never write the same file and cloud apps never make "conflicted copy" files.
///
///   Sysslar Sync/
///     key.sysslar                     which key the folder uses
///     keychange-&lt;old key id&gt;.sysslar   written when the key is changed
///     devices/&lt;device id&gt;.sysslar      each computer's name and synced workspaces
///     ws-&lt;sync id&gt;/&lt;device id&gt;.sysslarsync   each computer's copy of a workspace (replaced on every save)
///     ws-&lt;sync id&gt;/files/&lt;name&gt;.blob   attachment files, written once
///
/// Ids are random lowercase base32; no names of people, computers or workspaces appear in file names.
/// </summary>
public static class SyncFolderLayout
{
    public const string RootName = "Sysslar Sync";
    public const string SpaceScope = "space";
    public const string SnapshotExtension = ".sysslarsync";

    public static string KeyFile(string root) => Path.Combine(root, "key.sysslar");
    public static string KeyChangeFile(string root, string oldFingerprint) => Path.Combine(root, $"keychange-{oldFingerprint}.sysslar");
    public static string DevicesFolder(string root) => Path.Combine(root, "devices");
    public static string DeviceFile(string root, string deviceId) => Path.Combine(DevicesFolder(root), deviceId + ".sysslar");
    public static string WorkspaceFolder(string root, string syncId) => Path.Combine(root, "ws-" + syncId);
    public static string SnapshotFile(string root, string syncId, string deviceId) => Path.Combine(WorkspaceFolder(root, syncId), deviceId + SnapshotExtension);
    public static string BlobsFolder(string root, string syncId) => Path.Combine(WorkspaceFolder(root, syncId), "files");

    public static string BlobFile(string root, string syncId, SyncKey key, string hash)
    {
        var name = SyncIds.Base32(HMACSHA256.HashData(key.BlobNameKey(syncId), Encoding.ASCII.GetBytes(hash)))[..32].ToLowerInvariant();
        return Path.Combine(BlobsFolder(root, syncId), name + ".blob");
    }

    // Contexts bind each file to its place, so a file moved to another slot is refused.
    public const string KeyContext = "key";
    public static string KeyChangeContext(string oldFingerprint) => "keychange|" + oldFingerprint;
    public static string DeviceContext(string deviceId) => "device|" + deviceId;
    public static string SnapshotContext(string syncId, string deviceId) => $"snapshot|{syncId}|{deviceId}";
    public static string BlobContext(string syncId, string hash) => $"blob|{syncId}|{hash}";

    /// <summary>Writes a file so other computers never see half of it: a temporary file, then a rename.</summary>
    public static void WriteAtomically(string path, byte[] content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + "." + SyncIds.New()[..8] + ".tmp";
        File.WriteAllBytes(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }
}

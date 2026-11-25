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
/// The name of every sync file. Every computer writes only its own files, so two computers never
/// write the same file and cloud apps never make "conflicted copy" files.
///
///   key.sysslar                     which key the store uses
///   keychange-&lt;old key id&gt;.sysslar   written when the key is changed
///   devices/&lt;device id&gt;.sysslar      each computer's name and synced workspaces
///   ws-&lt;sync id&gt;/&lt;device id&gt;.sysslarsync   each computer's copy of a workspace (replaced on every save)
///   ws-&lt;sync id&gt;/files/&lt;name&gt;.blob   attachment files, written once
///
/// These are names, not paths: a folder store turns them into real folders, while a service with no
/// folders of its own keeps every file side by side under one name. Ids are random lowercase base32;
/// no names of people, computers or workspaces appear in file names.
/// </summary>
public static class SyncFolderLayout
{
    public const string RootName = "Sysslar Sync";
    public const string SpaceScope = "space";
    public const string SnapshotExtension = ".sysslarsync";
    public const string DevicesPrefix = "devices/";
    public const string DeviceExtension = ".sysslar";

    public const string KeyName = "key" + DeviceExtension;
    public static string KeyChangeName(string oldFingerprint) => $"keychange-{oldFingerprint}{DeviceExtension}";
    public static string DeviceName(string deviceId) => DevicesPrefix + deviceId + DeviceExtension;
    public static string WorkspacePrefix(string syncId) => "ws-" + syncId + "/";
    public static string SnapshotName(string syncId, string deviceId) => WorkspacePrefix(syncId) + deviceId + SnapshotExtension;
    public static string BlobsPrefix(string syncId) => WorkspacePrefix(syncId) + "files/";

    public static string BlobName(string syncId, SyncKey key, string hash)
    {
        var name = SyncIds.Base32(HMACSHA256.HashData(key.BlobNameKey(syncId), Encoding.ASCII.GetBytes(hash)))[..32].ToLowerInvariant();
        return BlobsPrefix(syncId) + name + ".blob";
    }

    /// <summary>The device id in "devices/&lt;id&gt;.sysslar", or null for any other name.</summary>
    public static string? DeviceIdOf(string name)
        => name.StartsWith(DevicesPrefix, StringComparison.Ordinal) && name.EndsWith(DeviceExtension, StringComparison.Ordinal)
           && name.IndexOf('/', DevicesPrefix.Length) < 0
            ? name[DevicesPrefix.Length..^DeviceExtension.Length]
            : null;

    /// <summary>The device id in "ws-&lt;sync id&gt;/&lt;id&gt;.sysslarsync" for this workspace, or null.</summary>
    public static string? SnapshotDeviceIdOf(string name, string syncId)
    {
        var prefix = WorkspacePrefix(syncId);
        if (!name.StartsWith(prefix, StringComparison.Ordinal) || !name.EndsWith(SnapshotExtension, StringComparison.Ordinal)) return null;
        var middle = name[prefix.Length..^SnapshotExtension.Length];
        return middle.Length > 0 && !middle.Contains('/') ? middle : null;
    }

    // Contexts bind each file to its place, so a file moved to another slot is refused.
    public const string KeyContext = "key";
    public static string KeyChangeContext(string oldFingerprint) => "keychange|" + oldFingerprint;
    public static string DeviceContext(string deviceId) => "device|" + deviceId;
    public static string SnapshotContext(string syncId, string deviceId) => $"snapshot|{syncId}|{deviceId}";
    public static string BlobContext(string syncId, string hash) => $"blob|{syncId}|{hash}";
}

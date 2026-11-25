using System.IO;

namespace Tasky.Services.Sync;

/// <summary>A sync file as the store sees it: its name, how big it is and when it last changed.</summary>
/// <param name="Name">The file's name in the store (<see cref="SyncFolderLayout"/>), for example "devices/abc.sysslar".</param>
/// <param name="Tag">Changes whenever the content changes, so an unchanged file isn't read again.</param>
public sealed record SyncStoreFile(string Name, string Tag, long Length, DateTime Modified);

/// <summary>The store couldn't be reached (no folder, no network, signed out).</summary>
public sealed class SyncStoreUnreachableException(string message, Exception? inner = null) : IOException(message, inner);

/// <summary>The store refused us for good: the sign-in was revoked or expired beyond repair.</summary>
public sealed class SyncStoreSignedOutException(string message, Exception? inner = null) : IOException(message, inner);

/// <summary>
/// Where the encrypted sync files are kept. A folder the cloud app syncs
/// (<see cref="FolderStore"/>), or a cloud service Sysslar talks to itself
/// (<see cref="GoogleDriveStore"/>).
///
/// Names are flat: the whole relative name, separators included, is the file's name. Services that
/// have no folders can then keep every file side by side, and two computers can never disagree
/// about which folder a file belongs in.
///
/// Every member may be called from a background thread, never from the UI thread, and throws
/// <see cref="IOException"/> (or one of its sync subclasses) when the store can't be reached.
/// </summary>
public interface ISyncStore
{
    /// <summary>What to call this place when talking to the user ("Google Drive", or a folder path).</summary>
    string Describe { get; }

    /// <summary>Makes sure sync files can be written (creates the folder on first use).</summary>
    void Prepare();

    /// <summary>Every sync file in the store. One listing serves a whole cycle.</summary>
    IReadOnlyList<SyncStoreFile> List();

    /// <summary>The file's content, or null when it isn't there (any more).</summary>
    byte[]? Read(string name);

    /// <summary>Writes the file so other computers never see half of it.</summary>
    void Write(string name, byte[] content);

    /// <summary>Removes the file; does nothing when it isn't there.</summary>
    void Delete(string name);

    /// <summary>Saves what this computer needs to find this store again after a restart.</summary>
    void SaveTo(SyncConfig config);
}

/// <summary>
/// Sync files in a folder on this computer, which a cloud app (Google Drive, iCloud Drive, OneDrive,
/// Dropbox) or a network share moves between computers. Sysslar itself stays offline.
/// </summary>
public sealed class FolderStore(string folder) : ISyncStore
{
    /// <summary>The folder the user picked; sync files live in its "Sysslar Sync" folder.</summary>
    public string Folder { get; } = folder;

    private string Root => Path.Combine(Folder, SyncFolderLayout.RootName);

    public string Describe => CloudFolders.ServiceOf(Folder) ?? Folder;

    private string PathOf(string name) => Path.Combine(Root, name.Replace('/', Path.DirectorySeparatorChar));

    public void Prepare()
    {
        if (!Directory.Exists(Folder))
            throw new SyncStoreUnreachableException($"Can't reach the sync folder ({Describe}).");
        Directory.CreateDirectory(Root);
    }

    public IReadOnlyList<SyncStoreFile> List()
    {
        if (!Directory.Exists(Folder))
            throw new SyncStoreUnreachableException($"Can't reach the sync folder ({Describe}).");
        if (!Directory.Exists(Root)) return [];
        var files = new List<SyncStoreFile>();
        foreach (var path in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
        {
            if (path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;   // still being written
            var name = Path.GetRelativePath(Root, path).Replace(Path.DirectorySeparatorChar, '/');
            var info = new FileInfo(path);
            files.Add(new SyncStoreFile(name, $"{info.LastWriteTimeUtc.Ticks}:{info.Length}", info.Length, info.LastWriteTimeUtc));
        }
        return files;
    }

    public byte[]? Read(string name)
    {
        var path = PathOf(name);
        if (!File.Exists(path)) return null;
        try { return File.ReadAllBytes(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    public void Write(string name, byte[] content)
    {
        var path = PathOf(name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + "." + SyncIds.New()[..8] + ".tmp";
        File.WriteAllBytes(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }

    public void Delete(string name)
    {
        var path = PathOf(name);
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { CrashLog.Write($"Couldn't delete the sync file '{name}'", ex); }
    }

    public void SaveTo(SyncConfig config)
    {
        config.Folder        = Folder;
        config.Transport     = SyncTransports.Folder;
        config.DriveFolderId = null;
    }
}

/// <summary>The ways sync files can travel, as saved in <see cref="SyncConfig.Transport"/>.</summary>
public static class SyncTransports
{
    /// <summary>A folder on this computer that a cloud app or a network share syncs.</summary>
    public const string Folder = "folder";
    /// <summary>Google Drive, which Sysslar talks to itself after the user signs in.</summary>
    public const string GoogleDrive = "googledrive";
}

/// <summary>Opens the store this computer's settings point at.</summary>
public static class SyncStores
{
    public static ISyncStore? Default(SyncConfig config, ISyncSecretStore secrets)
        => config.Folder is { } folder ? new FolderStore(folder) : null;
}

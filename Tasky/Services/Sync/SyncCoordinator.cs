using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Tasky.Services.Sync;

/// <summary>A workspace on this computer, as sync needs it.</summary>
public sealed record LocalWorkspace(string Id, string Name, string Color);

/// <summary>Where a workspace's files are, ready to sync.</summary>
public sealed record WorkspaceFiles(string DatabasePath, string AttachmentsRoot, string CacheFolder);

/// <summary>What sync needs from the app (the real app, or a test).</summary>
public interface ISyncHost
{
    IReadOnlyList<LocalWorkspace> Workspaces { get; }
    string ActiveWorkspaceId { get; }
    /// <summary>Opens (and if needed creates or upgrades) a workspace's database.</summary>
    WorkspaceFiles Open(string workspaceId);
    /// <summary>Before syncing a workspace: save running timers, take the daily backup.</summary>
    void BeforeSync(string workspaceId);
    /// <summary>After syncing a workspace whose data changed: refresh what's on screen.</summary>
    void AfterSync(string workspaceId, SyncChanges changes);
    /// <summary>Makes a new, empty workspace for one that so far exists only on other computers.</summary>
    string CreateWorkspace(string name, string color);
}

public enum SyncStatus { Off, UpToDate, Syncing, Paused, FolderMissing, Problem }

/// <summary>A row in the Sync settings: a local workspace, or one that so far is only on other computers.</summary>
public sealed record SyncWorkspaceRow(string? LocalId, string? SyncId, string Name, string Color, bool Enabled,
                                      DateTime? LastSync, IReadOnlyList<string> OtherComputers);

/// <summary>Outcome of checking a folder the user picked.</summary>
public sealed record FolderCheck(bool HasSyncData, string? Problem);

/// <summary>
/// Runs sync for this computer: one encrypted file per computer per workspace in a folder the user
/// picked, merged with <see cref="SyncEngine"/>. A cycle reads the other computers' files (in the
/// background), syncs each workspace's database (on the caller's thread, the UI thread in the app,
/// so nothing changes the data meanwhile), then writes this computer's files and moves attachment
/// files (in the background).
/// </summary>
public sealed class SyncCoordinator
{
    private readonly ISyncHost _host;
    private readonly ISyncSecretStore _secrets;
    private readonly SyncConfig _config;
    private SyncKey? _key;
    private HybridClock _clock;
    private bool _running, _again;

    /// <summary>Decrypted files of other computers, by path, kept while the file doesn't change.</summary>
    private readonly Dictionary<string, (DateTime Written, long Length, SyncSnapshot Snapshot)> _readCache = [];

    public SyncCoordinator(ISyncHost host, ISyncSecretStore secrets, SyncConfig config)
    {
        _host    = host;
        _secrets = secrets;
        _config  = config;
        _clock   = new HybridClock(_config.DeviceId);
        if (_config.IsOn)
        {
            try
            {
                var secret = _secrets.Read(_config.DeviceId);
                _key = secret is null ? null : SyncKey.FromSecret(secret);
            }
            catch (Exception ex) { CrashLog.Write("The sync key couldn't be read from this computer's key store", ex); }
            if (_key is null || _key.FingerprintHex != _config.Fingerprint)
            {
                _key = null;
                _config.PausedReason ??= PauseReasons.KeyMissing;
            }
        }
        Status = !_config.IsOn ? SyncStatus.Off : _config.PausedReason is not null ? SyncStatus.Paused : SyncStatus.UpToDate;
    }

    // ── State shown to the user ──────────────────────────────────────────

    public SyncStatus Status { get; private set; }
    public string? Problem { get; private set; }
    public DateTime? LastSync { get; private set; }
    /// <summary>Computers whose files are still made with the old key (after a key change).</summary>
    public int ComputersOnOldKey { get; private set; }
    public event Action? Changed;
    /// <summary>Raised on the caller's thread right after the databases were synced (before files are written).</summary>
    public event Action? DatabasesSynced;

    public bool IsOn => _config.IsOn;
    public string? Folder => _config.Folder;
    public string DeviceName => _config.DeviceName;
    public string? PausedReason => _config.PausedReason;
    public bool IsEnabled(string workspaceId) => IsOn && _config.Workspaces.TryGetValue(workspaceId, out var l) && l.Enabled;

    private void SetStatus(SyncStatus status, string? problem = null)
    {
        Status  = status;
        Problem = problem;
        Changed?.Invoke();
    }

    public static class PauseReasons
    {
        public const string KeyChangedPrefix = "changed:";
        public const string DifferentKey = "different";
        public const string KeyMissing = "missing";
    }

    // ── Turning sync on and off ──────────────────────────────────────────

    /// <summary>Whether a folder already holds Sysslar sync files (then the user enters the key they have).</summary>
    public static FolderCheck CheckFolder(string folder)
    {
        try
        {
            if (!Directory.Exists(folder)) return new(false, "This folder doesn't exist.");
            var root = Path.Combine(folder, SyncFolderLayout.RootName);
            return new(File.Exists(SyncFolderLayout.KeyFile(root)), null);
        }
        catch (Exception ex) { return new(false, ex.Message); }
    }

    /// <summary>First computer: makes a new key, sets up the folder and syncs the open workspace. Returns the key code to show.</summary>
    public string TurnOnWithNewKey(string folder)
    {
        var key  = SyncKey.Generate();
        var root = Path.Combine(folder, SyncFolderLayout.RootName);
        Directory.CreateDirectory(root);
        SyncFolderLayout.WriteAtomically(SyncFolderLayout.KeyFile(root),
            SyncCrypto.SealJson(key, SyncFolderLayout.SpaceScope, SyncFileKind.KeyCheck, SyncFolderLayout.KeyContext,
                                new KeyCheck { MadeBy = _config.DeviceName, At = DateTime.UtcNow }));
        Activate(folder, key);
        SetWorkspaceEnabled(_host.ActiveWorkspaceId, true, mergeWithSyncId: null);
        return key.ToCode();
    }

    /// <summary>Another computer: checks the typed key against the folder and turns sync on. Returns an error to show, or null.</summary>
    public string? TurnOnWithKey(string folder, string code)
    {
        var key = SyncKey.TryParseCode(code);
        if (key is null) return "That key isn't complete or has a typo. It's 54 letters and numbers.";
        var root = Path.Combine(folder, SyncFolderLayout.RootName);
        if (VerifyKeyAgainstFolder(root, key) is { } problem) return problem;
        Activate(folder, key);
        return null;
    }

    private void Activate(string folder, SyncKey key)
    {
        _secrets.Write(_config.DeviceId, key.Secret);
        _key = key;
        _config.Folder       = folder;
        _config.Fingerprint  = key.FingerprintHex;
        _config.PausedReason = null;
        _config.LastDeviceHash = null;
        foreach (var link in _config.Workspaces.Values) link.LastWrittenHash = null;
        _config.Save();
        _readCache.Clear();
        SetStatus(SyncStatus.UpToDate);
    }

    /// <summary>
    /// Moves sync to another folder that has no sync files yet, keeping the key (other computers then
    /// pick the same folder). For a folder that already has sync files, use <see cref="TurnOnWithKey"/>.
    /// </summary>
    public void MoveToEmptyFolder(string folder)
    {
        if (_key is null) throw new InvalidOperationException("Sync is off or paused.");
        var root = Path.Combine(folder, SyncFolderLayout.RootName);
        Directory.CreateDirectory(root);
        SyncFolderLayout.WriteAtomically(SyncFolderLayout.KeyFile(root),
            SyncCrypto.SealJson(_key, SyncFolderLayout.SpaceScope, SyncFileKind.KeyCheck, SyncFolderLayout.KeyContext,
                                new KeyCheck { MadeBy = _config.DeviceName, At = DateTime.UtcNow }));
        Activate(folder, _key);
    }

    /// <summary>Stops syncing on this computer and forgets the key here. Nothing is deleted, here or in the folder.</summary>
    public void TurnOff()
    {
        try { _secrets.Delete(_config.DeviceId); }
        catch (Exception ex) { CrashLog.Write("The sync key couldn't be removed from the key store", ex); }
        _key = null;
        _config.Folder = null;
        _config.Fingerprint = null;
        _config.PausedReason = null;
        foreach (var link in _config.Workspaces.Values) { link.Enabled = false; link.LastWrittenHash = null; }
        _config.Save();
        _readCache.Clear();
        SetStatus(SyncStatus.Off);
    }

    /// <summary>The key to type on another computer.</summary>
    public string? KeyCode => _key?.ToCode();

    private static string? VerifyKeyAgainstFolder(string root, SyncKey key)
    {
        var keyFile = SyncFolderLayout.KeyFile(root);
        if (!File.Exists(keyFile)) return "There are no Sysslar sync files in this folder.";
        try
        {
            var bytes = File.ReadAllBytes(keyFile);
            if (SyncCrypto.ReadFingerprint(bytes) != key.FingerprintHex)
                return "That key doesn't match the sync files in this folder. If the key was changed, use the newest one.";
            SyncCrypto.OpenJson<KeyCheck>(key, SyncFolderLayout.SpaceScope, SyncFileKind.KeyCheck, SyncFolderLayout.KeyContext, bytes);
            return null;
        }
        catch (SyncFileException ex) { return ex.Message; }
        catch (IOException ex) { return "The sync folder couldn't be read: " + ex.Message; }
    }

    // ── Changing the key ─────────────────────────────────────────────────

    /// <summary>
    /// Makes a new key. Other computers see a notice sealed with the old key (so they know it's genuine,
    /// but it doesn't contain the new key), stop syncing and ask for the new key. Returns the new code.
    /// </summary>
    public async Task<string> ChangeKeyAsync()
    {
        if (_key is null || _config.SyncRoot is not { } root) throw new InvalidOperationException("Sync is off or paused.");
        while (_running) await Task.Delay(100);
        await RunCycleAsync();   // everything this computer knows goes out under the old key first

        var oldKey = _key;
        var newKey = SyncKey.Generate();
        await Task.Run(() =>
        {
            SyncFolderLayout.WriteAtomically(SyncFolderLayout.KeyChangeFile(root, oldKey.FingerprintHex),
                SyncCrypto.SealJson(oldKey, SyncFolderLayout.SpaceScope, SyncFileKind.KeyChange,
                                    SyncFolderLayout.KeyChangeContext(oldKey.FingerprintHex),
                                    new KeyChangeNotice { NewFingerprint = newKey.FingerprintHex, ChangedBy = _config.DeviceName, At = DateTime.UtcNow }));
            SyncFolderLayout.WriteAtomically(SyncFolderLayout.KeyFile(root),
                SyncCrypto.SealJson(newKey, SyncFolderLayout.SpaceScope, SyncFileKind.KeyCheck, SyncFolderLayout.KeyContext,
                                    new KeyCheck { MadeBy = _config.DeviceName, At = DateTime.UtcNow }));
        });
        Activate(_config.Folder!, newKey);
        await RunCycleAsync();   // rewrite this computer's files with the new key
        return newKey.ToCode();
    }

    /// <summary>After the key was changed elsewhere: checks and stores the new key. Returns an error to show, or null.</summary>
    public string? EnterNewKey(string code)
    {
        if (_config.Folder is null) return "Sync is off.";
        var key = SyncKey.TryParseCode(code);
        if (key is null) return "That key isn't complete or has a typo. It's 54 letters and numbers.";
        if (VerifyKeyAgainstFolder(_config.SyncRoot!, key) is { } problem) return problem;
        Activate(_config.Folder, key);
        return null;
    }

    // ── Workspaces ───────────────────────────────────────────────────────

    /// <summary>
    /// Turns sync on or off for a local workspace. <paramref name="mergeWithSyncId"/> joins it to a workspace
    /// that already syncs from another computer (both sides keep everything); null starts a new one.
    /// Turning off never deletes anything.
    /// </summary>
    public void SetWorkspaceEnabled(string workspaceId, bool enabled, string? mergeWithSyncId)
    {
        if (!_config.Workspaces.TryGetValue(workspaceId, out var link))
            _config.Workspaces[workspaceId] = link = new WorkspaceLink { SyncId = SyncIds.New() };
        if (enabled && mergeWithSyncId is not null && link.SyncId != mergeWithSyncId)
        {
            link.SyncId = mergeWithSyncId;
            link.LastWrittenHash = null;
        }
        link.Enabled = enabled;
        _config.LastDeviceHash = null;
        _config.Save();
        Changed?.Invoke();
    }

    /// <summary>Adds a workspace that so far exists only on other computers, and syncs it here.</summary>
    public string AddWorkspaceFromOtherComputer(string syncId, string name, string color)
    {
        var id = _host.CreateWorkspace(name, color);
        _config.Workspaces[id] = new WorkspaceLink { SyncId = syncId, Enabled = true };
        _config.LastDeviceHash = null;
        _config.Save();
        Changed?.Invoke();
        return id;
    }

    /// <summary>The other computers that sync with this folder (readable with the current key).</summary>
    public async Task<IReadOnlyList<DeviceInfo>> ReadDevicesAsync()
    {
        if (_key is not { } key || _config.SyncRoot is not { } root) return [];
        return await Task.Run(() => ReadDevices(root, key));
    }

    private List<DeviceInfo> ReadDevices(string root, SyncKey key)
    {
        var list = new List<DeviceInfo>();
        var folder = SyncFolderLayout.DevicesFolder(root);
        if (!Directory.Exists(folder)) return list;
        foreach (var file in Directory.EnumerateFiles(folder, "*.sysslar"))
        {
            var id = Path.GetFileNameWithoutExtension(file);
            if (id == _config.DeviceId) continue;
            try
            {
                var bytes = File.ReadAllBytes(file);
                if (SyncCrypto.ReadFingerprint(bytes) != key.FingerprintHex) continue;
                list.Add(SyncCrypto.OpenJson<DeviceInfo>(key, SyncFolderLayout.SpaceScope, SyncFileKind.Device,
                                                         SyncFolderLayout.DeviceContext(id), bytes));
            }
            catch (Exception ex) when (ex is SyncFileException or IOException) { /* still arriving, or another key */ }
        }
        return list;
    }

    /// <summary>Rows for the Sync settings: every local workspace, then those only on other computers.</summary>
    public async Task<IReadOnlyList<SyncWorkspaceRow>> ListWorkspacesAsync()
    {
        var devices = await ReadDevicesAsync();
        var bySync = devices.SelectMany(d => d.Workspaces.Select(w => (Device: d, Workspace: w)))
                            .GroupBy(x => x.Workspace.SyncId)
                            .ToDictionary(g => g.Key, g => g.ToList());
        var rows = new List<SyncWorkspaceRow>();
        var linkedSyncIds = new HashSet<string>();
        foreach (var ws in _host.Workspaces)
        {
            _config.Workspaces.TryGetValue(ws.Id, out var link);
            bool enabled = link?.Enabled == true;
            if (enabled) linkedSyncIds.Add(link!.SyncId);
            var others = enabled && bySync.TryGetValue(link!.SyncId, out var shared)
                ? shared.Select(x => x.Device.Name).Distinct().ToList() : [];
            rows.Add(new SyncWorkspaceRow(ws.Id, link?.SyncId, ws.Name, ws.Color, enabled, link?.LastSync, others));
        }
        foreach (var (syncId, shared) in bySync.Where(kv => !linkedSyncIds.Contains(kv.Key)))
        {
            var newest = shared.OrderByDescending(x => x.Device.LastSeen).First().Workspace;
            rows.Add(new SyncWorkspaceRow(null, syncId, newest.Name, newest.Color, false, null,
                                          shared.Select(x => x.Device.Name).Distinct().ToList()));
        }
        return rows;
    }

    /// <summary>Removes another computer's files from the folder (a computer no longer used).</summary>
    public async Task RemoveComputerAsync(string deviceId)
    {
        if (_config.SyncRoot is not { } root || deviceId == _config.DeviceId) return;
        await Task.Run(() =>
        {
            TryDelete(SyncFolderLayout.DeviceFile(root, deviceId));
            foreach (var dir in Directory.EnumerateDirectories(root, "ws-*"))
                TryDelete(Path.Combine(dir, deviceId + SyncFolderLayout.SnapshotExtension));
        });
    }

    // ── The sync cycle ───────────────────────────────────────────────────

    /// <summary>One workspace's plan between the phases of a cycle.</summary>
    private sealed class WorkspaceWork
    {
        public required string LocalId;
        public required WorkspaceLink Link;
        public required LocalWorkspace Workspace;
        public List<SyncSnapshot> Remotes = [];
        public WorkspaceFiles? Files;
        public SyncRunResult? Result;
    }

    /// <summary>Runs a cycle; when one is already running, runs another right after it.</summary>
    public async Task RunCycleAsync()
    {
        if (_running) { _again = true; return; }
        _running = true;
        try
        {
            do
            {
                _again = false;
                await RunOnceAsync();
            } while (_again);
        }
        finally { _running = false; }
    }

    private async Task RunOnceAsync()
    {
        if (!_config.IsOn || _config.SyncRoot is not { } root) { SetStatus(SyncStatus.Off); return; }
        if (!Directory.Exists(_config.Folder))
        {
            SetStatus(SyncStatus.FolderMissing, $"Can't reach the sync folder ({_config.Folder}).");
            return;
        }

        var work = _host.Workspaces
            .Where(w => _config.Workspaces.TryGetValue(w.Id, out var l) && l.Enabled)
            .Select(w => new WorkspaceWork { LocalId = w.Id, Link = _config.Workspaces[w.Id], Workspace = w })
            .ToList();
        // Settings of workspaces deleted on this computer go too
        foreach (var gone in _config.Workspaces.Keys.Where(id => _host.Workspaces.All(w => w.Id != id)).ToList())
            _config.Workspaces.Remove(gone);

        // 1. Background: is the key still the folder's key? Read the other computers' files.
        string? pause;
        try
        {
            pause = await Task.Run(() => CheckKey(root));
            if (pause is null) await Task.Run(() => ReadRemotes(root, work));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus(SyncStatus.FolderMissing, "The sync folder couldn't be read: " + ex.Message);
            return;
        }
        if (pause is not null)
        {
            _config.PausedReason = pause;
            _config.Save();
            SetStatus(SyncStatus.Paused);
            return;
        }
        if (_key is null) return;

        SetStatus(SyncStatus.Syncing);
        var problems = new List<string>();

        // 2. Caller's thread: sync each workspace's database
        foreach (var w in work)
        {
            try
            {
                w.Files = _host.Open(w.LocalId);
                _host.BeforeSync(w.LocalId);
                var cache = w.Files.CacheFolder;
                var engine = new SyncEngine(w.Files.DatabasePath, w.Files.AttachmentsRoot, _config.DeviceId, _clock)
                {
                    BlobFile = hash => File.Exists(Path.Combine(cache, hash)) ? Path.Combine(cache, hash) : null,
                };
                w.Result = engine.Run(w.Remotes);
                if (w.Result.Changes.Any) _host.AfterSync(w.LocalId, w.Result.Changes);
            }
            catch (Exception ex)
            {
                CrashLog.Write($"Syncing workspace '{w.Workspace.Name}' failed", ex);
                problems.Add($"{w.Workspace.Name}: {ex.Message}");
            }
        }

        DatabasesSynced?.Invoke();

        // 3. Background: write this computer's files, share and fetch attachment files
        int fetched = 0;
        try
        {
            fetched = await Task.Run(() => WriteAndMoveFiles(root, work));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CrashLog.Write("Writing sync files failed", ex);
            problems.Add("The sync folder couldn't be written: " + ex.Message);
        }
        if (fetched > 0) _again = true;   // attachment files arrived: add them now

        var now = DateTime.Now;
        foreach (var w in work.Where(w => w.Result is not null)) w.Link.LastSync = now;
        _config.Save();
        LastSync = now;
        SetStatus(problems.Count == 0 ? SyncStatus.UpToDate : SyncStatus.Problem,
                  problems.Count == 0 ? null : string.Join("\n", problems));
    }

    /// <summary>Null when the folder still uses this computer's key, otherwise why syncing has to pause.</summary>
    private string? CheckKey(string root)
    {
        if (_key is null) return _config.PausedReason ?? PauseReasons.KeyMissing;
        var keyFile = SyncFolderLayout.KeyFile(root);
        if (!File.Exists(keyFile))
        {
            // The folder was emptied or is new on this computer: it uses our key from now on.
            SyncFolderLayout.WriteAtomically(keyFile, SyncCrypto.SealJson(_key, SyncFolderLayout.SpaceScope, SyncFileKind.KeyCheck,
                SyncFolderLayout.KeyContext, new KeyCheck { MadeBy = _config.DeviceName, At = DateTime.UtcNow }));
            return null;
        }
        var fingerprint = SyncCrypto.ReadFingerprint(File.ReadAllBytes(keyFile));
        if (fingerprint == _key.FingerprintHex) return null;

        var notice = SyncFolderLayout.KeyChangeFile(root, _key.FingerprintHex);
        if (File.Exists(notice))
        {
            try
            {
                var change = SyncCrypto.OpenJson<KeyChangeNotice>(_key, SyncFolderLayout.SpaceScope, SyncFileKind.KeyChange,
                    SyncFolderLayout.KeyChangeContext(_key.FingerprintHex), File.ReadAllBytes(notice));
                return PauseReasons.KeyChangedPrefix + change.ChangedBy;
            }
            catch (SyncFileException) { /* not genuine: treat as an unknown key */ }
        }
        return PauseReasons.DifferentKey;
    }

    private void ReadRemotes(string root, List<WorkspaceWork> work)
    {
        int onOldKey = 0;
        foreach (var w in work)
        {
            var folder = SyncFolderLayout.WorkspaceFolder(root, w.Link.SyncId);
            if (!Directory.Exists(folder)) continue;
            foreach (var file in Directory.EnumerateFiles(folder, "*" + SyncFolderLayout.SnapshotExtension))
            {
                var deviceId = Path.GetFileNameWithoutExtension(file);
                if (deviceId == _config.DeviceId) continue;
                try
                {
                    var info = new FileInfo(file);
                    if (_readCache.TryGetValue(file, out var cached) && cached.Written == info.LastWriteTimeUtc && cached.Length == info.Length)
                    {
                        w.Remotes.Add(cached.Snapshot);
                        continue;
                    }
                    var bytes = File.ReadAllBytes(file);
                    if (SyncCrypto.ReadFingerprint(bytes) != _key!.FingerprintHex) { onOldKey++; continue; }
                    var snapshot = SyncCrypto.OpenJson<SyncSnapshot>(_key, w.Link.SyncId, SyncFileKind.Snapshot,
                        SyncFolderLayout.SnapshotContext(w.Link.SyncId, deviceId), bytes);
                    if (snapshot.DeviceId != deviceId) continue;
                    _readCache[file] = (info.LastWriteTimeUtc, info.Length, snapshot);
                    w.Remotes.Add(snapshot);
                }
                catch (Exception ex) when (ex is SyncFileException or IOException)
                {
                    // Probably still being written or downloaded by the cloud app: read it next time.
                }
            }
        }
        ComputersOnOldKey = onOldKey;
    }

    private int WriteAndMoveFiles(string root, List<WorkspaceWork> work)
    {
        var key = _key!;
        int fetched = 0;
        foreach (var w in work.Where(w => w.Result is not null))
        {
            var syncId = w.Link.SyncId;

            // This computer's copy of the workspace, only when it changed
            var snapshot = new SyncSnapshot
            {
                App = AppVersion, DeviceId = _config.DeviceId, DeviceName = _config.DeviceName,
                WorkspaceName = w.Workspace.Name, WorkspaceColor = w.Workspace.Color, State = w.Result!.State,
            };
            var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
                key.FingerprintHex + JsonSerializer.Serialize(snapshot))));
            if (hash != w.Link.LastWrittenHash)
            {
                snapshot.Seq = _config.NextSeq();
                SyncFolderLayout.WriteAtomically(SyncFolderLayout.SnapshotFile(root, syncId, _config.DeviceId),
                    SyncCrypto.SealJson(key, syncId, SyncFileKind.Snapshot, SyncFolderLayout.SnapshotContext(syncId, _config.DeviceId), snapshot));
                w.Link.LastWrittenHash = hash;
            }

            // Attachment files this computer has
            foreach (var (blobHash, path) in w.Result.BlobsToShare)
            {
                var blobFile = SyncFolderLayout.BlobFile(root, syncId, key, blobHash);
                if (File.Exists(blobFile)) continue;
                try
                {
                    SyncFolderLayout.WriteAtomically(blobFile,
                        SyncCrypto.Seal(key, syncId, SyncFileKind.Blob, SyncFolderLayout.BlobContext(syncId, blobHash), File.ReadAllBytes(path)));
                }
                catch (IOException ex) { CrashLog.Write("An attachment couldn't be shared", ex); }
            }

            // Attachment files this computer is waiting for
            var cache = w.Files!.CacheFolder;
            foreach (var needed in w.Result.BlobsNeeded)
            {
                var target = Path.Combine(cache, needed);
                if (File.Exists(target)) continue;
                var blobFile = SyncFolderLayout.BlobFile(root, syncId, key, needed);
                if (!File.Exists(blobFile)) continue;
                try
                {
                    var plain = SyncCrypto.Open(key, syncId, SyncFileKind.Blob, SyncFolderLayout.BlobContext(syncId, needed), File.ReadAllBytes(blobFile));
                    if (Convert.ToHexStringLower(SHA256.HashData(plain)) != needed) continue;
                    SyncFolderLayout.WriteAtomically(target, plain);
                    fetched++;
                }
                catch (Exception ex) when (ex is SyncFileException or IOException) { /* still arriving */ }
            }
            // Files already added to the workspace aren't needed in the cache any more
            if (Directory.Exists(cache))
                foreach (var file in Directory.EnumerateFiles(cache))
                    if (!w.Result.BlobsNeeded.Contains(Path.GetFileName(file))) TryDelete(file);
        }

        WriteDeviceFile(root, key, work);
        if (_config.LastCleanup is null || DateTime.UtcNow - _config.LastCleanup > TimeSpan.FromDays(1))
        {
            CleanUpBlobs(root, key, work);
            _config.LastCleanup = DateTime.UtcNow;
        }
        return fetched;
    }

    /// <summary>This computer's name and synced workspaces, rewritten when they change and once a day ("last seen").</summary>
    private void WriteDeviceFile(string root, SyncKey key, List<WorkspaceWork> work)
    {
        var info = new DeviceInfo
        {
            DeviceId = _config.DeviceId, Name = _config.DeviceName,
            Os = OperatingSystem.IsMacOS() ? "macOS" : OperatingSystem.IsWindows() ? "Windows" : "other",
            Workspaces = [.. work.Select(w => new SharedWorkspace { SyncId = w.Link.SyncId, Name = w.Workspace.Name, Color = w.Workspace.Color })],
        };
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key.FingerprintHex + JsonSerializer.Serialize(info))));
        if (hash == _config.LastDeviceHash && _config.LastDeviceWrite is { } last && DateTime.UtcNow - last < TimeSpan.FromHours(12)) return;
        info.LastSeen = DateTime.UtcNow;
        SyncFolderLayout.WriteAtomically(SyncFolderLayout.DeviceFile(root, _config.DeviceId),
            SyncCrypto.SealJson(key, SyncFolderLayout.SpaceScope, SyncFileKind.Device, SyncFolderLayout.DeviceContext(_config.DeviceId), info));
        _config.LastDeviceHash  = hash;
        _config.LastDeviceWrite = DateTime.UtcNow;
    }

    /// <summary>Attachment files no computer refers to any more (or left from an old key) go after a week.</summary>
    private void CleanUpBlobs(string root, SyncKey key, List<WorkspaceWork> work)
    {
        foreach (var w in work.Where(w => w.Result is not null))
        {
            var folder = SyncFolderLayout.BlobsFolder(root, w.Link.SyncId);
            if (!Directory.Exists(folder)) continue;
            var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var state in w.Remotes.Select(r => r.State).Append(w.Result!.State))
                foreach (var table in SyncSchema.Tables.Where(t => t.HasBlob))
                    if (state.Tables.TryGetValue(table.Name, out var t))
                        foreach (var r in t.Records.Values)
                            if (r.Get(SyncSchema.BlobField) is { Length: > 0 } h)
                                referenced.Add(SyncFolderLayout.BlobFile(root, w.Link.SyncId, key, h));
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                if (referenced.Contains(file)) continue;
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > TimeSpan.FromDays(7)) TryDelete(file);
            }
        }
    }

    private static string AppVersion => Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { CrashLog.Write($"Couldn't delete '{path}'", ex); }
    }
}

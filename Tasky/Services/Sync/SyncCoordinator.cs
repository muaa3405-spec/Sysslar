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

public enum SyncStatus { Off, UpToDate, Syncing, Paused, FolderMissing, SignedOut, Problem }

/// <summary>A row in the Sync settings: a local workspace, or one that so far is only on other computers.</summary>
public sealed record SyncWorkspaceRow(string? LocalId, string? SyncId, string Name, string Color, bool Enabled,
                                      DateTime? LastSync, IReadOnlyList<string> OtherComputers);

/// <summary>Outcome of checking a place the user picked for sync files.</summary>
public sealed record FolderCheck(bool HasSyncData, string? Problem);

/// <summary>
/// Runs sync for this computer: one encrypted file per computer per workspace in a store the user
/// picked (a folder a cloud app syncs, or Google Drive), merged with <see cref="SyncEngine"/>.
/// A cycle reads the other computers' files (in the background), syncs each workspace's database
/// (on the caller's thread, the UI thread in the app, so nothing changes the data meanwhile), then
/// writes this computer's files and moves attachment files (in the background).
/// </summary>
public sealed class SyncCoordinator
{
    private readonly ISyncHost _host;
    private readonly ISyncSecretStore _secrets;
    private readonly SyncConfig _config;
    private readonly Func<SyncConfig, ISyncSecretStore, ISyncStore?> _storeFactory;
    private ISyncStore? _store;
    private SyncKey? _key;
    private HybridClock _clock;
    private bool _running, _again;

    /// <summary>Every sync file in the store, read once at the start of a cycle.</summary>
    private Dictionary<string, SyncStoreFile> _listing = [];

    /// <summary>Decrypted files of other computers, by name, kept while the file doesn't change.</summary>
    private readonly Dictionary<string, (string Tag, SyncSnapshot Snapshot)> _readCache = [];

    public SyncCoordinator(ISyncHost host, ISyncSecretStore secrets, SyncConfig config,
                           Func<SyncConfig, ISyncSecretStore, ISyncStore?>? storeFactory = null)
    {
        _host    = host;
        _secrets = secrets;
        _config  = config;
        _storeFactory = storeFactory ?? SyncStores.Default;
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
            try { _store = _storeFactory(_config, _secrets); }
            catch (Exception ex) { CrashLog.Write("The sync store couldn't be opened", ex); }
        }
        Status = !_config.IsOn ? SyncStatus.Off
               : _config.PausedReason is not null ? SyncStatus.Paused
               : _store is null ? SyncStatus.Problem
               : SyncStatus.UpToDate;
        if (_config.IsOn && _store is null)
            Problem = "This copy of Sysslar can't sync with Google Drive. Install the Sysslar release build, or pick a sync folder instead.";
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
    /// <summary>The folder sync files go to, or null when they go to a service instead.</summary>
    public string? Folder => _config.IsGoogleDrive ? null : _config.Folder;
    public bool IsGoogleDrive => _config.IsGoogleDrive;
    /// <summary>Where the sync files are kept, as the user should see it ("Google Drive", a folder path).</summary>
    public string StorageName => _store?.Describe ?? (_config.IsGoogleDrive ? CloudFolders.GoogleDrive : _config.Folder ?? "your sync folder");
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

    /// <summary>Whether a store already holds Sysslar sync files (then the user enters the key they have).</summary>
    public static FolderCheck Check(ISyncStore store)
    {
        try
        {
            store.Prepare();
            return new(store.Read(SyncFolderLayout.KeyName) is not null, null);
        }
        catch (SyncStoreSignedOutException ex) { return new(false, ex.Message); }
        catch (SyncStoreUnreachableException ex) { return new(false, ex.Message); }
        catch (Exception ex) { return new(false, ex.Message); }
    }

    /// <summary>Whether a folder already holds Sysslar sync files.</summary>
    public static FolderCheck CheckFolder(string folder)
        => Directory.Exists(folder) ? Check(new FolderStore(folder)) : new(false, "This folder doesn't exist.");

    public static Task<FolderCheck> CheckAsync(ISyncStore store) => Task.Run(() => Check(store));

    /// <summary>First computer: makes a new key, sets up the store and syncs the open workspace. Returns the key code to show.</summary>
    public async Task<string> TurnOnWithNewKeyAsync(ISyncStore store)
    {
        var key = SyncKey.Generate();
        await Task.Run(() =>
        {
            store.Prepare();
            store.Write(SyncFolderLayout.KeyName,
                SyncCrypto.SealJson(key, SyncFolderLayout.SpaceScope, SyncFileKind.KeyCheck, SyncFolderLayout.KeyContext,
                                    new KeyCheck { MadeBy = _config.DeviceName, At = DateTime.UtcNow }));
        });
        Activate(store, key);
        SetWorkspaceEnabled(_host.ActiveWorkspaceId, true, mergeWithSyncId: null);
        return key.ToCode();
    }

    /// <summary>Another computer: checks the typed key against the store and turns sync on. Returns an error to show, or null.</summary>
    public async Task<string?> TurnOnWithKeyAsync(ISyncStore store, string code)
    {
        var key = SyncKey.TryParseCode(code);
        if (key is null) return "That key isn't complete or has a typo. It's 54 letters and numbers.";
        if (await Task.Run(() => VerifyKeyAgainstStore(store, key)) is { } problem) return problem;
        Activate(store, key);
        return null;
    }

    private void Activate(ISyncStore store, SyncKey key)
    {
        _secrets.Write(_config.DeviceId, key.Secret);
        _key   = key;
        _store = store;
        store.SaveTo(_config);
        _config.Fingerprint  = key.FingerprintHex;
        _config.PausedReason = null;
        _config.LastDeviceHash = null;
        foreach (var link in _config.Workspaces.Values) link.LastWrittenHash = null;
        _config.Save();
        _readCache.Clear();
        _listing.Clear();
        SetStatus(SyncStatus.UpToDate);
    }

    /// <summary>
    /// Moves sync to another store that has no sync files yet, keeping the key (other computers then
    /// pick the same place). For a store that already has sync files, use <see cref="TurnOnWithKeyAsync"/>.
    /// </summary>
    public async Task MoveToEmptyStoreAsync(ISyncStore store)
    {
        if (_key is not { } key) throw new InvalidOperationException("Sync is off or paused.");
        await Task.Run(() =>
        {
            store.Prepare();
            store.Write(SyncFolderLayout.KeyName,
                SyncCrypto.SealJson(key, SyncFolderLayout.SpaceScope, SyncFileKind.KeyCheck, SyncFolderLayout.KeyContext,
                                    new KeyCheck { MadeBy = _config.DeviceName, At = DateTime.UtcNow }));
        });
        Activate(store, key);
    }

    /// <summary>Stops syncing on this computer and forgets the key here. Nothing is deleted, here or in the store.</summary>
    public void TurnOff()
    {
        try { _secrets.Delete(_config.DeviceId); }
        catch (Exception ex) { CrashLog.Write("The sync key couldn't be removed from the key store", ex); }
        // A Google sign-in is no use once sync is off here, so it goes too.
        try { (_store as GoogleDriveStore)?.Disconnect(); }
        catch (Exception ex) { CrashLog.Write("The Google sign-in couldn't be ended", ex); }
        try { (_store as IDisposable)?.Dispose(); }
        catch (Exception ex) { CrashLog.Write("Closing the sync store failed", ex); }
        _key   = null;
        _store = null;
        _config.Folder = null;
        _config.Transport = null;
        _config.DriveFolderId = null;
        _config.Fingerprint = null;
        _config.PausedReason = null;
        foreach (var link in _config.Workspaces.Values) { link.Enabled = false; link.LastWrittenHash = null; }
        _config.Save();
        _readCache.Clear();
        _listing.Clear();
        SetStatus(SyncStatus.Off);
    }

    /// <summary>The key to type on another computer.</summary>
    public string? KeyCode => _key?.ToCode();

    /// <summary>
    /// A Google Drive store for this computer, ready to be connected and then passed to
    /// <see cref="TurnOnWithNewKeyAsync"/>. Null when this build has no Google client, in which
    /// case the option isn't offered.
    /// </summary>
    public GoogleDriveStore? NewGoogleDriveStore()
        => GoogleClientApp.Current is { } app ? new GoogleDriveStore(app, _config, _secrets) : null;

    /// <summary>
    /// Signs in to Google again on this computer after the sign-in lapsed or was taken back, keeping
    /// the key, the workspaces and everything waiting to be synced. Returns an error to show, or null.
    /// </summary>
    public async Task<string?> SignInAgainAsync(Action<string>? showUrl = null, CancellationToken cancel = default)
    {
        if (_store is not GoogleDriveStore drive) return "This computer doesn't sync through Google Drive.";
        try
        {
            await drive.ConnectAsync(showUrl, cancel);
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            CrashLog.Write("Signing in to Google again failed", ex);
            return "Sysslar couldn't sign in to Google: " + ex.Message;
        }
        SetStatus(SyncStatus.UpToDate);
        await RunCycleAsync();
        return null;
    }

    private static string? VerifyKeyAgainstStore(ISyncStore store, SyncKey key)
    {
        try
        {
            store.Prepare();
            if (store.Read(SyncFolderLayout.KeyName) is not { } bytes) return "There are no Sysslar sync files here.";
            if (SyncCrypto.ReadFingerprint(bytes) != key.FingerprintHex)
                return "That key doesn't match the sync files here. If the key was changed, use the newest one.";
            SyncCrypto.OpenJson<KeyCheck>(key, SyncFolderLayout.SpaceScope, SyncFileKind.KeyCheck, SyncFolderLayout.KeyContext, bytes);
            return null;
        }
        catch (SyncFileException ex) { return ex.Message; }
        catch (IOException ex) { return "The sync files couldn't be read: " + ex.Message; }
    }

    // ── Changing the key ─────────────────────────────────────────────────

    /// <summary>
    /// Makes a new key. Other computers see a notice sealed with the old key (so they know it's genuine,
    /// but it doesn't contain the new key), stop syncing and ask for the new key. Returns the new code.
    /// </summary>
    public async Task<string> ChangeKeyAsync()
    {
        if (_key is null || _store is not { } store) throw new InvalidOperationException("Sync is off or paused.");
        while (_running) await Task.Delay(100);
        await RunCycleAsync();   // everything this computer knows goes out under the old key first

        var oldKey = _key;
        var newKey = SyncKey.Generate();
        await Task.Run(() =>
        {
            store.Write(SyncFolderLayout.KeyChangeName(oldKey.FingerprintHex),
                SyncCrypto.SealJson(oldKey, SyncFolderLayout.SpaceScope, SyncFileKind.KeyChange,
                                    SyncFolderLayout.KeyChangeContext(oldKey.FingerprintHex),
                                    new KeyChangeNotice { NewFingerprint = newKey.FingerprintHex, ChangedBy = _config.DeviceName, At = DateTime.UtcNow }));
            store.Write(SyncFolderLayout.KeyName,
                SyncCrypto.SealJson(newKey, SyncFolderLayout.SpaceScope, SyncFileKind.KeyCheck, SyncFolderLayout.KeyContext,
                                    new KeyCheck { MadeBy = _config.DeviceName, At = DateTime.UtcNow }));
        });
        Activate(store, newKey);
        await RunCycleAsync();   // rewrite this computer's files with the new key
        return newKey.ToCode();
    }

    /// <summary>After the key was changed elsewhere: checks and stores the new key. Returns an error to show, or null.</summary>
    public async Task<string?> EnterNewKeyAsync(string code)
    {
        if (_store is not { } store) return "Sync is off.";
        var key = SyncKey.TryParseCode(code);
        if (key is null) return "That key isn't complete or has a typo. It's 54 letters and numbers.";
        if (await Task.Run(() => VerifyKeyAgainstStore(store, key)) is { } problem) return problem;
        Activate(store, key);
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

    /// <summary>The other computers that sync with this store (readable with the current key).</summary>
    public async Task<IReadOnlyList<DeviceInfo>> ReadDevicesAsync()
    {
        if (_key is not { } key || _store is not { } store) return [];
        return await Task.Run(() =>
        {
            try { return ReadDevices(store, store.List(), key); }
            catch (IOException ex)
            {
                CrashLog.Write("The other computers couldn't be listed", ex);
                return [];
            }
        });
    }

    private List<DeviceInfo> ReadDevices(ISyncStore store, IReadOnlyList<SyncStoreFile> listing, SyncKey key)
    {
        var list = new List<DeviceInfo>();
        foreach (var file in listing)
        {
            if (SyncFolderLayout.DeviceIdOf(file.Name) is not { } id || id == _config.DeviceId) continue;
            try
            {
                if (store.Read(file.Name) is not { } bytes) continue;
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

    /// <summary>Removes another computer's files from the store (a computer no longer used).</summary>
    public async Task RemoveComputerAsync(string deviceId)
    {
        if (_store is not { } store || deviceId == _config.DeviceId) return;
        await Task.Run(() =>
        {
            try
            {
                store.Delete(SyncFolderLayout.DeviceName(deviceId));
                foreach (var file in store.List())
                    if (file.Name.EndsWith("/" + deviceId + SyncFolderLayout.SnapshotExtension, StringComparison.Ordinal))
                        store.Delete(file.Name);
            }
            catch (IOException ex) { CrashLog.Write("A computer's sync files couldn't be removed", ex); }
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
        if (!_config.IsOn) { SetStatus(SyncStatus.Off); return; }
        if (_store is not { } store)
        {
            SetStatus(SyncStatus.Problem, Problem ?? "Sysslar has nowhere to put the sync files.");
            return;
        }

        var work = _host.Workspaces
            .Where(w => _config.Workspaces.TryGetValue(w.Id, out var l) && l.Enabled)
            .Select(w => new WorkspaceWork { LocalId = w.Id, Link = _config.Workspaces[w.Id], Workspace = w })
            .ToList();
        // Settings of workspaces deleted on this computer go too
        foreach (var gone in _config.Workspaces.Keys.Where(id => _host.Workspaces.All(w => w.Id != id)).ToList())
            _config.Workspaces.Remove(gone);

        // 1. Background: list the store, check the key is still its key, read the other computers' files.
        string? pause;
        try
        {
            pause = await Task.Run(() =>
            {
                store.Prepare();
                _listing = store.List().ToDictionary(f => f.Name, StringComparer.Ordinal);
                var reason = CheckKey(store);
                if (reason is null) ReadRemotes(store, work);
                return reason;
            });
        }
        catch (SyncStoreSignedOutException ex)
        {
            SetStatus(SyncStatus.SignedOut, ex.Message);
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus(SyncStatus.FolderMissing, ex is SyncStoreUnreachableException
                ? ex.Message
                : "The sync files couldn't be read: " + ex.Message);
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
            fetched = await Task.Run(() => WriteAndMoveFiles(store, work));
        }
        catch (SyncStoreSignedOutException ex)
        {
            SetStatus(SyncStatus.SignedOut, ex.Message);
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CrashLog.Write("Writing sync files failed", ex);
            problems.Add("The sync files couldn't be written: " + ex.Message);
        }
        if (fetched > 0) _again = true;   // attachment files arrived: add them now

        var now = DateTime.Now;
        foreach (var w in work.Where(w => w.Result is not null)) w.Link.LastSync = now;
        _config.Save();
        LastSync = now;
        SetStatus(problems.Count == 0 ? SyncStatus.UpToDate : SyncStatus.Problem,
                  problems.Count == 0 ? null : string.Join("\n", problems));
    }

    /// <summary>Null when the store still uses this computer's key, otherwise why syncing has to pause.</summary>
    private string? CheckKey(ISyncStore store)
    {
        if (_key is null) return _config.PausedReason ?? PauseReasons.KeyMissing;
        if (!_listing.ContainsKey(SyncFolderLayout.KeyName) || store.Read(SyncFolderLayout.KeyName) is not { } keyBytes)
        {
            // The store was emptied or is new on this computer: it uses our key from now on.
            store.Write(SyncFolderLayout.KeyName, SyncCrypto.SealJson(_key, SyncFolderLayout.SpaceScope, SyncFileKind.KeyCheck,
                SyncFolderLayout.KeyContext, new KeyCheck { MadeBy = _config.DeviceName, At = DateTime.UtcNow }));
            return null;
        }
        var fingerprint = SyncCrypto.ReadFingerprint(keyBytes);
        if (fingerprint == _key.FingerprintHex) return null;

        var notice = SyncFolderLayout.KeyChangeName(_key.FingerprintHex);
        if (_listing.ContainsKey(notice) && store.Read(notice) is { } noticeBytes)
        {
            try
            {
                var change = SyncCrypto.OpenJson<KeyChangeNotice>(_key, SyncFolderLayout.SpaceScope, SyncFileKind.KeyChange,
                    SyncFolderLayout.KeyChangeContext(_key.FingerprintHex), noticeBytes);
                return PauseReasons.KeyChangedPrefix + change.ChangedBy;
            }
            catch (SyncFileException) { /* not genuine: treat as an unknown key */ }
        }
        return PauseReasons.DifferentKey;
    }

    private void ReadRemotes(ISyncStore store, List<WorkspaceWork> work)
    {
        int onOldKey = 0;
        foreach (var w in work)
        {
            foreach (var file in _listing.Values)
            {
                if (SyncFolderLayout.SnapshotDeviceIdOf(file.Name, w.Link.SyncId) is not { } deviceId) continue;
                if (deviceId == _config.DeviceId) continue;
                try
                {
                    if (_readCache.TryGetValue(file.Name, out var cached) && cached.Tag == file.Tag)
                    {
                        w.Remotes.Add(cached.Snapshot);
                        continue;
                    }
                    if (store.Read(file.Name) is not { } bytes) continue;
                    if (SyncCrypto.ReadFingerprint(bytes) != _key!.FingerprintHex) { onOldKey++; continue; }
                    var snapshot = SyncCrypto.OpenJson<SyncSnapshot>(_key, w.Link.SyncId, SyncFileKind.Snapshot,
                        SyncFolderLayout.SnapshotContext(w.Link.SyncId, deviceId), bytes);
                    if (snapshot.DeviceId != deviceId) continue;
                    _readCache[file.Name] = (file.Tag, snapshot);
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

    private int WriteAndMoveFiles(ISyncStore store, List<WorkspaceWork> work)
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
                store.Write(SyncFolderLayout.SnapshotName(syncId, _config.DeviceId),
                    SyncCrypto.SealJson(key, syncId, SyncFileKind.Snapshot, SyncFolderLayout.SnapshotContext(syncId, _config.DeviceId), snapshot));
                w.Link.LastWrittenHash = hash;
            }

            // Attachment files this computer has
            foreach (var (blobHash, path) in w.Result.BlobsToShare)
            {
                var blobName = SyncFolderLayout.BlobName(syncId, key, blobHash);
                if (_listing.ContainsKey(blobName)) continue;
                try
                {
                    store.Write(blobName,
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
                var blobName = SyncFolderLayout.BlobName(syncId, key, needed);
                if (!_listing.ContainsKey(blobName)) continue;
                try
                {
                    if (store.Read(blobName) is not { } sealedBytes) continue;
                    var plain = SyncCrypto.Open(key, syncId, SyncFileKind.Blob, SyncFolderLayout.BlobContext(syncId, needed), sealedBytes);
                    if (Convert.ToHexStringLower(SHA256.HashData(plain)) != needed) continue;
                    Directory.CreateDirectory(cache);
                    var tmp = target + ".tmp";
                    File.WriteAllBytes(tmp, plain);
                    File.Move(tmp, target, overwrite: true);
                    fetched++;
                }
                catch (Exception ex) when (ex is SyncFileException or IOException) { /* still arriving */ }
            }
            // Files already added to the workspace aren't needed in the cache any more
            if (Directory.Exists(cache))
                foreach (var file in Directory.EnumerateFiles(cache))
                    if (!w.Result.BlobsNeeded.Contains(Path.GetFileName(file))) TryDeleteLocal(file);
        }

        WriteDeviceFile(store, key, work);
        if (_config.LastCleanup is null || DateTime.UtcNow - _config.LastCleanup > TimeSpan.FromDays(1))
        {
            CleanUpBlobs(store, key, work);
            _config.LastCleanup = DateTime.UtcNow;
        }
        return fetched;
    }

    /// <summary>This computer's name and synced workspaces, rewritten when they change and once a day ("last seen").</summary>
    private void WriteDeviceFile(ISyncStore store, SyncKey key, List<WorkspaceWork> work)
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
        store.Write(SyncFolderLayout.DeviceName(_config.DeviceId),
            SyncCrypto.SealJson(key, SyncFolderLayout.SpaceScope, SyncFileKind.Device, SyncFolderLayout.DeviceContext(_config.DeviceId), info));
        _config.LastDeviceHash  = hash;
        _config.LastDeviceWrite = DateTime.UtcNow;
    }

    /// <summary>Attachment files no computer refers to any more (or left from an old key) go after a week.</summary>
    private void CleanUpBlobs(ISyncStore store, SyncKey key, List<WorkspaceWork> work)
    {
        foreach (var w in work.Where(w => w.Result is not null))
        {
            var prefix = SyncFolderLayout.BlobsPrefix(w.Link.SyncId);
            var referenced = new HashSet<string>(StringComparer.Ordinal);
            foreach (var state in w.Remotes.Select(r => r.State).Append(w.Result!.State))
                foreach (var table in SyncSchema.Tables.Where(t => t.HasBlob))
                    if (state.Tables.TryGetValue(table.Name, out var t))
                        foreach (var r in t.Records.Values)
                            if (r.Get(SyncSchema.BlobField) is { Length: > 0 } h)
                                referenced.Add(SyncFolderLayout.BlobName(w.Link.SyncId, key, h));
            foreach (var file in _listing.Values)
            {
                if (!file.Name.StartsWith(prefix, StringComparison.Ordinal)) continue;
                if (referenced.Contains(file.Name)) continue;
                if (DateTime.UtcNow - file.Modified > TimeSpan.FromDays(7)) store.Delete(file.Name);
            }
        }
    }

    private static string AppVersion => Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";

    private static void TryDeleteLocal(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { CrashLog.Write($"Couldn't delete '{path}'", ex); }
    }
}

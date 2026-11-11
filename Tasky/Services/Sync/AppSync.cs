using System.IO;
using Avalonia.Threading;
using Microsoft.Data.Sqlite;

namespace Tasky.Services.Sync;

/// <summary>What sync needs from the running app: its workspaces, the open one's data and window.</summary>
internal sealed class AppSyncHost : ISyncHost
{
    private readonly HashSet<string> _prepared = [];

    /// <summary>Set while Sysslar quits: the last sync then doesn't touch the window.</summary>
    public bool Exiting { get; set; }

    public IReadOnlyList<LocalWorkspace> Workspaces
        => [.. Services.Workspaces.All.Select(w => new LocalWorkspace(w.Id, w.Name, w.Color))];

    public string ActiveWorkspaceId => Services.Workspaces.Active.Id;

    public WorkspaceFiles Open(string workspaceId)
    {
        var cache = AppPaths.SyncCacheFolderFor(workspaceId);
        if (workspaceId == ActiveWorkspaceId && App.Database is { } open)
            return new(open.DatabasePath, open.AttachmentsRoot, cache);

        // A workspace that isn't open: make sure its database exists and is up to date (once per run)
        var db = new DatabaseService(AppPaths.DataFolderFor(workspaceId));
        if (_prepared.Add(workspaceId)) db.Initialize();
        return new(db.DatabasePath, db.AttachmentsRoot, cache);
    }

    public void BeforeSync(string workspaceId)
    {
        if (workspaceId != ActiveWorkspaceId || Exiting) return;
        App.ViewModel?.CheckpointRunningTasks();   // the database has the running timer's time so far
        try { App.Backups?.BackUpIfDue(); }        // the daily safety copy, before anything is merged in
        catch (Exception ex) { CrashLog.Write("The daily backup before syncing failed", ex); }
    }

    public void AfterSync(string workspaceId, SyncChanges changes)
    {
        if (workspaceId != ActiveWorkspaceId || Exiting) return;
        App.ViewModel?.ApplySyncedChanges();
        App.MainAppWindow?.RefreshAfterSync();
        if (changes.Total == 0) return;
        var message = changes.Total == 1 ? "1 change arrived from your other computers." : $"{changes.Total} changes arrived from your other computers.";
        if (changes.Renumbered > 0) message += " Tasks that got the same number on two computers were renumbered.";
        InAppNotificationService.Info(message);
    }

    public string CreateWorkspace(string name, string color)
    {
        var clean = name.Trim().Length == 0 ? "Synced" : name.Trim();
        if (Services.Workspaces.NameIsTaken(clean))
        {
            var suffixed = clean.Length > Services.Workspaces.MaxNameLength - 9 ? clean[..(Services.Workspaces.MaxNameLength - 9)] : clean;
            clean = suffixed + " (synced)";
        }
        return Services.Workspaces.Create(clean, color).Id;
    }
}

/// <summary>
/// Sync in the running app: one <see cref="SyncCoordinator"/>, and when it runs:
/// 15 seconds after a change on this computer, 5 seconds after another computer's file changes in the
/// sync folder, right after a workspace switch, every 5 minutes as a fallback (file watchers miss
/// changes on some cloud and network drives), and once more when Sysslar quits.
/// </summary>
public static class AppSync
{
    private static readonly TimeSpan AfterLocalChange  = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan AfterFolderChange = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Fallback          = TimeSpan.FromMinutes(5);

    private static AppSyncHost? _host;
    private static DispatcherTimer? _timer;
    private static FileSystemWatcher? _watcher;
    private static string? _watchedRoot;
    private static DateTime? _dueAt;
    private static DateTime _lastRun = DateTime.MinValue;
    private static SqliteConnection? _versionConnection;
    private static string? _versionDatabase;
    private static long _lastVersion;
    private static int _ticks;

    public static SyncCoordinator? Coordinator { get; private set; }

    /// <summary>Raised on the UI thread whenever the sync status changes.</summary>
    public static event Action? StatusChanged;

    public static void Start()
    {
        try
        {
            _host = new AppSyncHost();
            var config = SyncConfig.Load();
            Coordinator = new SyncCoordinator(_host, SyncSecretStores.ForThisComputer(AppPaths.SyncFolder), config);
            Coordinator.Changed += () => Dispatcher.UIThread.Post(() => { UpdateWatcher(); StatusChanged?.Invoke(); });
            Coordinator.DatabasesSynced += ResetLocalChangeBaseline;

            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += (_, _) => Tick();
            _timer.Start();
            UpdateWatcher();
            Request(TimeSpan.FromSeconds(3));
        }
        catch (Exception ex) { CrashLog.Write("Sync couldn't start", ex); }
    }

    /// <summary>Asks for a sync within <paramref name="delay"/> (an earlier request wins).</summary>
    public static void Request(TimeSpan delay)
    {
        var at = DateTime.Now + delay;
        if (_dueAt is null || at < _dueAt) _dueAt = at;
    }

    public static Task SyncNowAsync()
    {
        _dueAt = null;
        return RunAsync();
    }

    /// <summary>After a workspace switch: watch the new database for changes, and sync it now.</summary>
    public static void OnWorkspaceSwitched()
    {
        CloseVersionConnection();
        Request(TimeSpan.FromSeconds(1));
    }

    /// <summary>Last sync on quit, so changes made just before don't wait for the next start.</summary>
    public static void FlushOnExit()
    {
        _timer?.Stop();
        _watcher?.Dispose();
        CloseVersionConnection();
        if (Coordinator is not { IsOn: true } c || _host is null) return;
        _host.Exiting = true;
        try { Task.Run(c.RunCycleAsync).Wait(TimeSpan.FromSeconds(8)); }
        catch (Exception ex) { CrashLog.Write("The last sync before quitting failed", ex); }
    }

    private static void Tick()
    {
        if (Coordinator is not { IsOn: true }) return;
        if (++_ticks % 5 == 0 && LocalDataChanged() && _dueAt is null) Request(AfterLocalChange);
        if (_dueAt is null && DateTime.Now - _lastRun > Fallback) _dueAt = DateTime.Now;
        if (_dueAt is { } due && DateTime.Now >= due)
        {
            _dueAt = null;
            _ = RunAsync();
        }
    }

    private static async Task RunAsync()
    {
        if (Coordinator is null) return;
        _lastRun = DateTime.Now;
        try { await Coordinator.RunCycleAsync(); }
        catch (Exception ex) { CrashLog.Write("Sync failed", ex); }
    }

    // ── Noticing changes on this computer ────────────────────────────────
    // SQLite's data_version, read on a connection kept open, changes whenever any other connection
    // (the app saving a task, a note…) commits. So no save code in the app has to know about sync.

    private static bool LocalDataChanged()
    {
        if (App.Database is not { } db || !Coordinator!.IsEnabled(Workspaces.Active.Id)) return false;
        try
        {
            if (_versionConnection is null || _versionDatabase != db.DatabasePath)
            {
                CloseVersionConnection();
                _versionConnection = new SqliteConnection($"Data Source={db.DatabasePath};Mode=ReadOnly;Pooling=False");
                _versionConnection.Open();
                _versionDatabase = db.DatabasePath;
                _lastVersion = ReadVersion();
                return false;
            }
            var version = ReadVersion();
            if (version == _lastVersion) return false;
            _lastVersion = version;
            return true;
        }
        catch (Exception ex)
        {
            CrashLog.Write("Watching for changes to sync failed", ex);
            CloseVersionConnection();
            return false;
        }
    }

    /// <summary>The sync itself just wrote the database: that isn't a change to sync again.</summary>
    private static void ResetLocalChangeBaseline()
    {
        try { if (_versionConnection is not null) _lastVersion = ReadVersion(); }
        catch { CloseVersionConnection(); }
    }

    private static long ReadVersion()
    {
        using var cmd = _versionConnection!.CreateCommand();
        cmd.CommandText = "PRAGMA data_version";
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static void CloseVersionConnection()
    {
        _versionConnection?.Dispose();
        _versionConnection = null;
        _versionDatabase = null;
    }

    // ── Noticing changes from other computers ────────────────────────────

    private static void UpdateWatcher()
    {
        var root = Coordinator is { IsOn: true, Folder: { } folder } ? Path.Combine(folder, SyncFolderLayout.RootName) : null;
        if (root == _watchedRoot && (_watcher is not null || root is null)) return;
        _watcher?.Dispose();
        _watcher = null;
        _watchedRoot = root;
        if (root is null || !Directory.Exists(root)) return;
        try
        {
            _watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            FileSystemEventHandler onChange = (_, e) =>
            {
                if (e.Name?.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) == true) return;
                Dispatcher.UIThread.Post(() => Request(AfterFolderChange));
            };
            _watcher.Changed += onChange;
            _watcher.Created += onChange;
            _watcher.Renamed += (s, e) => onChange(s, e);
            _watcher.Error   += (_, _) => Dispatcher.UIThread.Post(() => { _watcher?.Dispose(); _watcher = null; _watchedRoot = null; });
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            CrashLog.Write("Watching the sync folder failed; checking every 5 minutes instead", ex);
            _watcher = null;
        }
    }
}

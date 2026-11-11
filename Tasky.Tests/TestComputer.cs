using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tasky.Models;
using Tasky.Services;
using Tasky.Services.Sync;

namespace Tasky.Tests;

internal static class TestSetup
{
    /// <summary>Points every Sysslar path at a throw-away folder before any of it is used.</summary>
    [ModuleInitializer]
    internal static void SandboxData()
    {
        var root = Path.Combine(Path.GetTempPath(), "sysslar-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Environment.SetEnvironmentVariable(AppPaths.DataRootOverrideVariable, root);
    }
}

/// <summary>
/// One computer in a sync test: its own workspace database and attachments, a clock the test controls,
/// and the sync file it last wrote. Blobs (attachment files) are shared through a dictionary,
/// standing in for the sync folder.
/// </summary>
internal sealed class TestComputer : IDisposable
{
    private static readonly JsonSerializerOptions Json = new();

    public string Name { get; }
    public string DeviceId { get; }
    public string Folder { get; }
    public DatabaseService Db { get; }
    public long NowMs { get; set; } = 1_000_000;
    public SyncSnapshot? LastFile { get; private set; }
    public SyncChanges LastChanges { get; private set; } = new();

    private readonly HybridClock _clock;
    private readonly SyncEngine _engine;
    private readonly Dictionary<string, byte[]> _sharedBlobs;
    private long _seq;

    public TestComputer(string name, Dictionary<string, byte[]> sharedBlobs)
    {
        Name     = name;
        DeviceId = SyncIds.New();
        Folder   = Path.Combine(Path.GetTempPath(), "sysslar-sync-" + name + "-" + Guid.NewGuid().ToString("N")[..8]);
        Db       = new DatabaseService(Folder);
        Db.Initialize();
        _sharedBlobs = sharedBlobs;
        _clock  = new HybridClock(DeviceId, () => NowMs);
        _engine = new SyncEngine(Db.DatabasePath, Db.AttachmentsRoot, DeviceId, _clock)
        {
            BlobFile = hash =>
            {
                if (!_sharedBlobs.TryGetValue(hash, out var bytes)) return null;
                var path = Path.Combine(Folder, "cache-" + hash);
                File.WriteAllBytes(path, bytes);
                return path;
            }
        };
    }

    /// <summary>Syncs with the latest files of <paramref name="others"/>, then writes this computer's file.</summary>
    public SyncChanges SyncWith(params TestComputer[] others)
    {
        NowMs += 1000;
        var remotes = others.Where(o => o.LastFile is not null).Select(o => Clone(o.LastFile!)).ToList();
        var result = _engine.Run(remotes);
        foreach (var (hash, path) in result.BlobsToShare) _sharedBlobs[hash] = File.ReadAllBytes(path);
        LastFile = new SyncSnapshot { DeviceId = DeviceId, DeviceName = Name, Seq = ++_seq, State = Clone(result.State) };
        LastChanges = result.Changes;
        return result.Changes;
    }

    /// <summary>Puts an older file back in place of the latest one (an attacker or a confused cloud app).</summary>
    public void RollBackFileTo(SyncSnapshot older) => LastFile = older;

    private static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Json), Json)!;

    // ── Data helpers ─────────────────────────────────────────────────────

    public TaskItem AddTask(string title, int number = 0, DateTime? created = null)
        => Db.InsertTask(new TaskItem { Title = title, TaskNumber = number, CreatedAt = created ?? DateTime.Now });

    public List<TaskItem> Tasks() => Db.GetAllTasks();
    public List<TaskItem> Bin() => Db.GetDeletedTasks();
    public TaskItem Task(string title) => Db.GetAllTasks().Concat(Db.GetDeletedTasks()).Single(t => t.Title == title);
    public List<Note> Notes() => Db.GetAllNotes();
    public List<TaskGroup> Groups() => Db.GetAllGroups();

    /// <summary>Every synced value in the database, to check that two computers ended up identical.</summary>
    public string Fingerprint()
    {
        using var conn = new SqliteConnection($"Data Source={Db.DatabasePath};Pooling=False");
        conn.Open();
        var lines = new List<string>();
        foreach (var table in SyncSchema.Tables)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT SyncId, {string.Join(",", table.Fields.Where(f => f.Kind != FieldKind.Ref).Select(f => f.Column))}"
                            + (table.Name == SyncSchema.Tasks ? ", AccumulatedSeconds" : "")
                            + $" FROM {table.Name} ORDER BY SyncId";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var values = Enumerable.Range(0, r.FieldCount).Select(i => r.IsDBNull(i) ? "∅" : Convert.ToString(r.GetValue(i)));
                lines.Add(table.Name + ": " + string.Join(" | ", values));
            }
        }
        return string.Join("\n", lines);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(Folder, recursive: true); } catch { }
    }
}

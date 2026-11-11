using Microsoft.Data.Sqlite;
using Tasky.Models;
using Tasky.Services;
using Tasky.Services.Sync;
using Xunit;

namespace Tasky.Tests;

/// <summary>A computer as the coordinator sees it: workspaces in a temp folder, a key store in memory.</summary>
internal sealed class FakeHost : ISyncHost, IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "sysslar-host-" + Guid.NewGuid().ToString("N")[..8]);
    public List<LocalWorkspace> List { get; } = [];
    public IReadOnlyList<LocalWorkspace> Workspaces => List;
    public string ActiveWorkspaceId { get; set; } = "default";
    public List<(string Id, SyncChanges Changes)> Synced { get; } = [];
    public Dictionary<string, DatabaseService> Dbs { get; } = [];

    public FakeHost() => List.Add(new LocalWorkspace("default", "Personal", "#3B82F6"));

    public DatabaseService Db(string id = "default")
    {
        if (!Dbs.TryGetValue(id, out var db))
        {
            db = new DatabaseService(Path.Combine(Root, id));
            db.Initialize();
            Dbs[id] = db;
        }
        return db;
    }

    public WorkspaceFiles Open(string id) => new(Db(id).DatabasePath, Db(id).AttachmentsRoot, Path.Combine(Root, id + "-cache"));
    public void BeforeSync(string id) { }
    public void AfterSync(string id, SyncChanges changes) => Synced.Add((id, changes));

    public string CreateWorkspace(string name, string color)
    {
        var id = "ws-" + Guid.NewGuid().ToString("N")[..6];
        List.Add(new LocalWorkspace(id, name, color));
        return id;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(Root, recursive: true); } catch { }
    }
}

internal sealed class MemorySecrets : ISyncSecretStore
{
    private readonly Dictionary<string, byte[]> _items = [];
    public byte[]? Read(string account) => _items.TryGetValue(account, out var v) ? v : null;
    public void Write(string account, byte[] secret) => _items[account] = secret;
    public void Delete(string account) => _items.Remove(account);
}

/// <summary>Two computers, one shared folder, the real encrypted files.</summary>
public sealed class SyncCoordinatorTests : IDisposable
{
    private readonly string _cloud = Path.Combine(Path.GetTempPath(), "sysslar-cloud-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly FakeHost _hostA = new(), _hostB = new();
    private readonly SyncCoordinator _a, _b;

    public SyncCoordinatorTests()
    {
        Directory.CreateDirectory(_cloud);
        _a = new SyncCoordinator(_hostA, new MemorySecrets(), SyncConfig.Load(Path.Combine(_hostA.Root, "sync.json")));
        _b = new SyncCoordinator(_hostB, new MemorySecrets(), SyncConfig.Load(Path.Combine(_hostB.Root, "sync.json")));
    }

    public void Dispose()
    {
        _hostA.Dispose();
        _hostB.Dispose();
        try { Directory.Delete(_cloud, recursive: true); } catch { }
    }

    private string Root => Path.Combine(_cloud, SyncFolderLayout.RootName);

    private async Task JoinBAsync()
    {
        var code = _a.TurnOnWithNewKey(_cloud);
        await _a.RunCycleAsync();
        Assert.True(SyncCoordinator.CheckFolder(_cloud).HasSyncData);
        Assert.Null(_b.TurnOnWithKey(_cloud, code));
        _b.SetWorkspaceEnabled("default", true, (await _b.ListWorkspacesAsync()).Single(r => r.LocalId is null).SyncId);
    }

    private async Task SyncAllAsync()
    {
        for (int i = 0; i < 2; i++) { await _a.RunCycleAsync(); await _b.RunCycleAsync(); }
    }

    [Fact]
    public async Task Tasks_travel_through_encrypted_files_in_the_folder()
    {
        _hostA.Db().InsertTask(new TaskItem { Title = "Secret plan" });
        await JoinBAsync();
        await SyncAllAsync();

        Assert.Equal("Secret plan", _hostB.Db().GetAllTasks().Single().Title);
        foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
            Assert.DoesNotContain("Secret plan", File.ReadAllText(file));
        Assert.Equal(SyncStatus.UpToDate, _b.Status);
    }

    [Fact]
    public void A_wrong_key_is_refused()
    {
        _a.TurnOnWithNewKey(_cloud);
        Assert.NotNull(_b.TurnOnWithKey(_cloud, SyncKey.Generate().ToCode()));
        Assert.NotNull(_b.TurnOnWithKey(_cloud, "not a key"));
        Assert.False(_b.IsOn);
    }

    [Fact]
    public async Task Attachment_files_arrive_through_the_folder()
    {
        var task = _hostA.Db().InsertTask(new TaskItem { Title = "Has file" });
        var dir = Path.Combine(_hostA.Db().AttachmentsRoot, task.Id.ToString());
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "a.txt"), "attached");
        _hostA.Db().InsertAttachment(new TaskAttachment { TaskId = task.Id, FileName = "a.txt", FilePath = Path.Combine(dir, "a.txt") });

        await JoinBAsync();
        await SyncAllAsync();

        var copy = _hostB.Db().GetAllTasks().Single();
        var att = _hostB.Db().GetAttachments(copy.Id).Single();
        Assert.Equal("attached", File.ReadAllText(att.FilePath));
    }

    [Fact]
    public async Task Changing_the_key_pauses_the_other_computer_until_it_enters_the_new_key()
    {
        await JoinBAsync();
        await SyncAllAsync();

        var newCode = await _a.ChangeKeyAsync();
        _hostA.Db().InsertTask(new TaskItem { Title = "After key change" });
        await _a.RunCycleAsync();

        await _b.RunCycleAsync();
        Assert.Equal(SyncStatus.Paused, _b.Status);
        Assert.StartsWith(SyncCoordinator.PauseReasons.KeyChangedPrefix, _b.PausedReason);
        Assert.Empty(_hostB.Db().GetAllTasks());

        _hostB.Db().InsertTask(new TaskItem { Title = "Made on B while paused" });
        Assert.NotNull(_b.EnterNewKey(SyncKey.Generate().ToCode()));   // a wrong key is refused
        Assert.Null(_b.EnterNewKey(newCode));
        await SyncAllAsync();

        Assert.Equal(2, _hostA.Db().GetAllTasks().Count);
        Assert.Equal(2, _hostB.Db().GetAllTasks().Count);
        Assert.Equal(SyncStatus.UpToDate, _a.Status);
        Assert.Equal(SyncStatus.UpToDate, _b.Status);
    }

    [Fact]
    public async Task A_workspace_from_the_other_computer_can_be_added_and_fills_in()
    {
        await JoinBAsync();
        var officeOnA = _hostA.CreateWorkspace("Office", "#FF0000");
        _hostA.Db(officeOnA).InsertTask(new TaskItem { Title = "Office task" });
        _a.SetWorkspaceEnabled(officeOnA, true, null);
        await SyncAllAsync();

        var row = (await _b.ListWorkspacesAsync()).Single(r => r.Name == "Office");
        Assert.Null(row.LocalId);
        var officeOnB = _b.AddWorkspaceFromOtherComputer(row.SyncId!, row.Name, row.Color);
        await _b.RunCycleAsync();

        Assert.Equal("Office task", _hostB.Db(officeOnB).GetAllTasks().Single().Title);
        Assert.DoesNotContain(_hostB.Db().GetAllTasks(), t => t.Title == "Office task");
    }

    [Fact]
    public async Task Turning_a_workspace_off_stops_it_syncing_and_deletes_nothing()
    {
        await JoinBAsync();
        _hostA.Db().InsertTask(new TaskItem { Title = "Before" });
        await SyncAllAsync();

        _b.SetWorkspaceEnabled("default", false, null);
        _hostA.Db().InsertTask(new TaskItem { Title = "After" });
        await SyncAllAsync();

        Assert.Equal(["Before"], _hostB.Db().GetAllTasks().Select(t => t.Title));
        Assert.Equal(2, _hostA.Db().GetAllTasks().Count);
    }

    [Fact]
    public async Task A_missing_folder_is_reported_not_treated_as_empty()
    {
        await JoinBAsync();
        _hostB.Db().InsertTask(new TaskItem { Title = "Keep" });
        await SyncAllAsync();

        Directory.Delete(_cloud, recursive: true);
        await _b.RunCycleAsync();

        Assert.Equal(SyncStatus.FolderMissing, _b.Status);
        Assert.Single(_hostB.Db().GetAllTasks());
    }
}

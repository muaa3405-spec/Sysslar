using Microsoft.Data.Sqlite;
using Tasky.Models;
using Tasky.Services;
using Tasky.Services.Sync;
using Xunit;

namespace Tasky.Tests;

/// <summary>
/// A store with no folders at all, standing in for Google Drive: names are plain labels in one flat
/// place, nothing nests, and every write replaces the whole file at once. Sync has to work the same
/// way here as it does in a folder.
/// </summary>
internal sealed class MemoryStore : ISyncStore
{
    private readonly Dictionary<string, (byte[] Content, DateTime Modified, long Version)> _files = new(StringComparer.Ordinal);
    private long _version;

    /// <summary>How often the store was listed, to show a cycle doesn't list it once per question.</summary>
    public int Listings { get; private set; }

    /// <summary>Set to make every call fail, as a store that can't be reached does.</summary>
    public bool Unreachable { get; set; }

    /// <summary>Set to make every call fail the way a sign-in that was taken back does.</summary>
    public bool SignedOut { get; set; }

    public string Describe => "Test store";

    public void Prepare() => Check();

    public IReadOnlyList<SyncStoreFile> List()
    {
        Check();
        Listings++;
        return [.. _files.Select(f => new SyncStoreFile(f.Key, f.Value.Version.ToString(), f.Value.Content.Length, f.Value.Modified))];
    }

    public byte[]? Read(string name)
    {
        Check();
        return _files.TryGetValue(name, out var file) ? file.Content : null;
    }

    public void Write(string name, byte[] content)
    {
        Check();
        Assert.DoesNotContain('\\', name);        // names are flat: the layout must not use real paths
        _files[name] = (content, DateTime.UtcNow, ++_version);
    }

    public void Delete(string name)
    {
        Check();
        _files.Remove(name);
    }

    public void SaveTo(SyncConfig config) => config.Folder = "memory";

    public IReadOnlyCollection<string> Names => _files.Keys;
    public byte[] Content(string name) => _files[name].Content;

    /// <summary>Puts an older copy of a file back, as a confused cloud app or an attacker might.</summary>
    public void RollBack(string name, byte[] older) => _files[name] = (older, DateTime.UtcNow, ++_version);

    private void Check()
    {
        if (SignedOut) throw new SyncStoreSignedOutException("The test store says you are signed out.");
        if (Unreachable) throw new SyncStoreUnreachableException("The test store can't be reached.");
    }
}

/// <summary>Two computers syncing through a store that has no folders, the way Google Drive does.</summary>
public sealed class SyncStoreTests : IDisposable
{
    private readonly FakeHost _hostA = new(), _hostB = new();
    private readonly SyncCoordinator _a, _b;
    private readonly MemoryStore _store = new();

    public SyncStoreTests()
    {
        _a = new SyncCoordinator(_hostA, new MemorySecrets(), SyncConfig.Load(Path.Combine(_hostA.Root, "sync.json")),
                                 (_, _) => _store);
        _b = new SyncCoordinator(_hostB, new MemorySecrets(), SyncConfig.Load(Path.Combine(_hostB.Root, "sync.json")),
                                 (_, _) => _store);
    }

    public void Dispose()
    {
        _hostA.Dispose();
        _hostB.Dispose();
    }

    private async Task JoinBAsync()
    {
        var code = await _a.TurnOnWithNewKeyAsync(_store);
        await _a.RunCycleAsync();
        Assert.Null(await _b.TurnOnWithKeyAsync(_store, code));
        _b.SetWorkspaceEnabled("default", true, (await _b.ListWorkspacesAsync()).Single(r => r.LocalId is null).SyncId);
    }

    private async Task SyncAllAsync()
    {
        for (int i = 0; i < 2; i++) { await _a.RunCycleAsync(); await _b.RunCycleAsync(); }
    }

    [Fact]
    public async Task Tasks_travel_through_a_store_without_folders()
    {
        _hostA.Db().InsertTask(new TaskItem { Title = "Secret plan" });
        await JoinBAsync();
        await SyncAllAsync();

        Assert.Equal("Secret plan", _hostB.Db().GetAllTasks().Single().Title);
        foreach (var name in _store.Names)
            Assert.DoesNotContain("Secret plan", System.Text.Encoding.UTF8.GetString(_store.Content(name)));
        Assert.Equal(SyncStatus.UpToDate, _b.Status);
    }

    [Fact]
    public async Task File_names_say_nothing_about_the_user()
    {
        _hostA.Db().InsertTask(new TaskItem { Title = "Dentist" });
        await JoinBAsync();
        await SyncAllAsync();

        Assert.Contains(SyncFolderLayout.KeyName, _store.Names);
        foreach (var name in _store.Names)
        {
            Assert.DoesNotContain("Dentist", name);
            Assert.DoesNotContain("Personal", name);     // the workspace's name
            Assert.DoesNotContain("default", name);      // its id on this computer
        }
    }

    [Fact]
    public async Task Attachment_files_arrive_through_a_store_without_folders()
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
    public async Task A_cycle_lists_the_store_once()
    {
        await JoinBAsync();
        await SyncAllAsync();

        var before = _store.Listings;
        _hostA.Db().InsertTask(new TaskItem { Title = "One more" });
        await _a.RunCycleAsync();

        // One listing for the cycle. Anything per file would be a request each on a real service.
        Assert.Equal(1, _store.Listings - before);
    }

    [Fact]
    public async Task A_store_that_cant_be_reached_keeps_the_data_and_says_so()
    {
        await JoinBAsync();
        await SyncAllAsync();

        _store.Unreachable = true;
        _hostA.Db().InsertTask(new TaskItem { Title = "Made while offline" });
        await _a.RunCycleAsync();
        Assert.Equal(SyncStatus.FolderMissing, _a.Status);

        _store.Unreachable = false;
        await SyncAllAsync();
        Assert.Equal(SyncStatus.UpToDate, _a.Status);
        Assert.Contains(_hostB.Db().GetAllTasks(), t => t.Title == "Made while offline");
    }

    [Fact]
    public async Task A_sign_in_that_was_taken_back_shows_as_signed_out()
    {
        await JoinBAsync();
        await SyncAllAsync();

        _store.SignedOut = true;
        _hostA.Db().InsertTask(new TaskItem { Title = "Made after signing out" });
        await _a.RunCycleAsync();

        Assert.Equal(SyncStatus.SignedOut, _a.Status);
        Assert.True(_a.IsOn);   // nothing is lost: sync is still set up, it just needs the user

        _store.SignedOut = false;
        await SyncAllAsync();
        Assert.Contains(_hostB.Db().GetAllTasks(), t => t.Title == "Made after signing out");
        Assert.Equal(SyncStatus.UpToDate, _a.Status);
    }

    [Fact]
    public async Task An_old_file_put_back_is_refused()
    {
        await JoinBAsync();
        _hostA.Db().InsertTask(new TaskItem { Title = "First" });
        await _a.RunCycleAsync();

        // Only A has written a copy of the workspace so far, so this is A's.
        var snapshot = _store.Names.Single(n => n.EndsWith(SyncFolderLayout.SnapshotExtension, StringComparison.Ordinal));
        var older = _store.Content(snapshot);

        await SyncAllAsync();
        _hostA.Db().InsertTask(new TaskItem { Title = "Second" });
        await SyncAllAsync();
        Assert.Equal(2, _hostB.Db().GetAllTasks().Count);

        _store.RollBack(snapshot, older);
        await _b.RunCycleAsync();

        // The older file is behind the one B already merged, so nothing it knows is undone.
        Assert.Equal(2, _hostB.Db().GetAllTasks().Count);
    }
}

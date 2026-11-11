using Tasky.Models;
using Tasky.Services.Sync;
using Xunit;

namespace Tasky.Tests;

/// <summary>The merge rules: add, update (newest wins), remove only when removed somewhere, true sync.</summary>
public sealed class SyncEngineTests : IDisposable
{
    private readonly Dictionary<string, byte[]> _blobs = [];
    private readonly TestComputer _a;
    private readonly TestComputer _b;

    public SyncEngineTests()
    {
        _a = new TestComputer("A", _blobs);
        _b = new TestComputer("B", _blobs);
    }

    public void Dispose() { _a.Dispose(); _b.Dispose(); }

    /// <summary>Both computers sync until neither has anything new (two rounds is always enough).</summary>
    private void SyncBoth()
    {
        _a.SyncWith(_b);
        _b.SyncWith(_a);
        _a.SyncWith(_b);
        _b.SyncWith(_a);
    }

    private void AssertIdentical() => Assert.Equal(_a.Fingerprint(), _b.Fingerprint());

    [Fact]
    public void New_tasks_notes_and_groups_appear_on_the_other_computer()
    {
        var group = _a.Db.InsertGroup(new TaskGroup { Name = "Work", Color = "#FF0000" });
        var task  = _a.AddTask("Write report");
        task.GroupId = group.Id;
        task.Description = "Quarterly";
        _a.Db.UpdateTask(task);
        _a.Db.InsertNote(new Note { Title = "Ideas", Description = "Sync everything" });

        SyncBoth();

        var copy = _b.Task("Write report");
        Assert.Equal("Quarterly", copy.Description);
        Assert.Equal("Work", _b.Groups().Single(g => g.Id == copy.GroupId).Name);
        Assert.Equal("Sync everything", _b.Notes().Single().Description);
        AssertIdentical();
    }

    [Fact]
    public void Both_sides_keep_their_new_items()
    {
        _a.AddTask("From A");
        _b.AddTask("From B");

        SyncBoth();

        Assert.Equal(["From A", "From B"], _a.Tasks().Select(t => t.Title).Order());
        Assert.Equal(["From A", "From B"], _b.Tasks().Select(t => t.Title).Order());
        AssertIdentical();
    }

    [Fact]
    public void A_new_empty_computer_never_removes_anything()
    {
        _a.AddTask("Keep me");
        _a.Db.InsertNote(new Note { Title = "Me too" });
        _a.SyncWith();

        _b.SyncWith(_a);      // B starts empty
        _a.SyncWith(_b);

        Assert.Single(_a.Tasks());
        Assert.Single(_a.Notes());
        Assert.Single(_b.Tasks());
        AssertIdentical();
    }

    [Fact]
    public void An_edit_on_one_computer_updates_the_other()
    {
        _a.AddTask("Draft");
        SyncBoth();

        var task = _b.Task("Draft");
        task.Title = "Final";
        task.IsCompleted = true;
        task.CompletedAt = DateTime.Now;
        _b.Db.UpdateTask(task);
        SyncBoth();

        var onA = _a.Task("Final");
        Assert.True(onA.IsCompleted);
        Assert.NotNull(onA.CompletedAt);
        AssertIdentical();
    }

    [Fact]
    public void Same_field_changed_on_both_the_newest_change_wins_everywhere()
    {
        _a.Db.InsertNote(new Note { Title = "Plan", Description = "v1" });
        SyncBoth();

        var onA = _a.Notes().Single(); onA.Description = "older edit from A"; _a.Db.UpdateNote(onA);
        var onB = _b.Notes().Single(); onB.Description = "newer edit from B"; _b.Db.UpdateNote(onB);
        _a.NowMs = 5_000_000;
        _a.SyncWith(_b);           // A's change is detected at 5,000,000
        _b.NowMs = 9_000_000;
        _b.SyncWith(_a);           // B's change is detected later, so it wins
        _a.SyncWith(_b);

        Assert.Equal("newer edit from B", _a.Notes().Single().Description);
        Assert.Equal("newer edit from B", _b.Notes().Single().Description);
        AssertIdentical();
    }

    [Fact]
    public void Different_fields_changed_on_each_computer_both_survive()
    {
        _a.AddTask("Shared");
        SyncBoth();

        var onA = _a.Task("Shared"); onA.Description = "details from A"; _a.Db.UpdateTask(onA);
        var onB = _b.Task("Shared"); onB.DueDate = new DateTime(2030, 1, 2); _b.Db.UpdateTask(onB);
        SyncBoth();

        foreach (var c in new[] { _a, _b })
        {
            var t = c.Task("Shared");
            Assert.Equal("details from A", t.Description);
            Assert.Equal(new DateTime(2030, 1, 2), t.DueDate);
        }
        AssertIdentical();
    }

    [Fact]
    public void Deleting_a_task_moves_it_to_the_recycle_bin_everywhere_and_restore_brings_it_back()
    {
        var task = _a.AddTask("Old idea");
        SyncBoth();

        _b.Db.SoftDeleteTask(_b.Task("Old idea").Id);
        SyncBoth();
        Assert.Empty(_a.Tasks());
        Assert.Single(_a.Bin());

        _a.Db.RestoreTask(task.Id);
        SyncBoth();
        Assert.Single(_b.Tasks());
        Assert.Empty(_b.Bin());
        AssertIdentical();
    }

    [Fact]
    public void Removing_on_one_computer_removes_on_the_other()
    {
        var note = _a.Db.InsertNote(new Note { Title = "Temp" });
        var group = _a.Db.InsertGroup(new TaskGroup { Name = "Old group" });
        var task = _a.AddTask("Purge me");
        SyncBoth();

        _a.Db.DeleteNote(note.Id);
        _a.Db.DeleteGroup(group.Id);
        _a.Db.SoftDeleteTask(task.Id);
        _a.Db.EmptyTrash();
        SyncBoth();

        Assert.Empty(_b.Notes());
        Assert.Empty(_b.Groups());
        Assert.Empty(_b.Tasks());
        Assert.Empty(_b.Bin());
        AssertIdentical();
    }

    [Fact]
    public void A_removal_wins_over_an_edit_made_while_apart()
    {
        _a.Db.InsertNote(new Note { Title = "Contested" });
        SyncBoth();

        _a.Db.DeleteNote(_a.Notes().Single().Id);
        var onB = _b.Notes().Single(); onB.Description = "edited later on B"; _b.Db.UpdateNote(onB);
        _b.NowMs = 50_000_000;   // even a later edit doesn't bring it back
        SyncBoth();

        Assert.Empty(_a.Notes());
        Assert.Empty(_b.Notes());
    }

    [Fact]
    public void Tracked_time_from_both_computers_adds_up()
    {
        _a.AddTask("Timed");
        SyncBoth();

        var onA = _a.Task("Timed"); onA.AccumulatedSeconds += 600; _a.Db.UpdateTask(onA);
        var onB = _b.Task("Timed"); onB.AccumulatedSeconds += 300; _b.Db.UpdateTask(onB);
        SyncBoth();

        Assert.Equal(900, _a.Task("Timed").AccumulatedSeconds);
        Assert.Equal(900, _b.Task("Timed").AccumulatedSeconds);

        onA = _a.Task("Timed"); onA.AccumulatedSeconds += 100; _a.Db.UpdateTask(onA);
        SyncBoth();
        Assert.Equal(1000, _b.Task("Timed").AccumulatedSeconds);
        AssertIdentical();
    }

    [Fact]
    public void A_running_timer_is_not_overwritten_until_it_stops()
    {
        _a.AddTask("Running");
        SyncBoth();

        var onB = _b.Task("Running"); onB.IsActive = true; onB.IsPaused = false; onB.AccumulatedSeconds = 60; _b.Db.UpdateTask(onB);
        var onA = _a.Task("Running"); onA.AccumulatedSeconds = 500; _a.Db.UpdateTask(onA);
        SyncBoth();

        Assert.Equal(60, _b.Task("Running").AccumulatedSeconds);    // B's timer is running: untouched
        Assert.Equal(560, _a.Task("Running").AccumulatedSeconds);   // A has both

        onB = _b.Task("Running"); onB.IsActive = false; _b.Db.UpdateTask(onB);
        SyncBoth();
        Assert.Equal(560, _b.Task("Running").AccumulatedSeconds);
    }

    [Fact]
    public void The_same_group_made_on_both_computers_is_not_doubled()
    {
        _a.Db.InsertGroup(new TaskGroup { Name = "Home" });
        _b.Db.InsertGroup(new TaskGroup { Name = "home" });

        SyncBoth();

        Assert.Single(_a.Groups());
        Assert.Single(_b.Groups());
    }

    [Fact]
    public void Two_copies_of_the_same_workspace_merge_without_doubles()
    {
        var created = new DateTime(2026, 3, 4, 10, 11, 12, 123);
        _a.AddTask("Copied task", 1, created);
        _b.AddTask("Copied task", 1, created);
        _b.AddTask("Only on B", 2);

        SyncBoth();

        Assert.Equal(2, _a.Tasks().Count);
        Assert.Equal(2, _b.Tasks().Count);
        AssertIdentical();
    }

    [Fact]
    public void Clashing_task_numbers_are_fixed_the_same_way_on_both()
    {
        _a.AddTask("A's first", 1, new DateTime(2026, 1, 1));
        _b.AddTask("B's first", 1, new DateTime(2026, 1, 2));

        SyncBoth();

        Assert.Equal(1, _a.Task("A's first").TaskNumber);
        Assert.Equal(2, _a.Task("B's first").TaskNumber);
        AssertIdentical();
    }

    [Fact]
    public void Completing_the_same_repeating_task_on_both_makes_one_next_occurrence()
    {
        var due = new DateTime(2030, 6, 1);
        _a.Db.InsertTask(new TaskItem { Title = "Standup", DueDate = due, Recurrence = RecurrenceType.Daily });
        _b.Db.InsertTask(new TaskItem { Title = "Standup", DueDate = due, Recurrence = RecurrenceType.Daily });

        SyncBoth();

        Assert.Single(_a.Tasks());
        Assert.Single(_b.Tasks());
    }

    [Fact]
    public void Attachments_arrive_with_their_file()
    {
        var task = _a.AddTask("With file");
        var folder = Path.Combine(_a.Db.AttachmentsRoot, task.Id.ToString());
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "notes.txt");
        File.WriteAllText(file, "hello from A");
        _a.Db.InsertAttachment(new TaskAttachment { TaskId = task.Id, FileName = "notes.txt", FilePath = file });

        SyncBoth();

        var copy = _b.Task("With file");
        var attachment = _b.Db.GetAttachments(copy.Id).Single();
        Assert.Equal("notes.txt", attachment.FileName);
        Assert.Equal("hello from A", File.ReadAllText(attachment.FilePath));
        Assert.StartsWith(_b.Db.AttachmentsRoot, attachment.FilePath);
    }

    [Fact]
    public void An_attachment_waits_for_its_file_and_is_not_deleted_meanwhile()
    {
        var task = _a.AddTask("Big file");
        var folder = Path.Combine(_a.Db.AttachmentsRoot, task.Id.ToString());
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "video.bin");
        File.WriteAllBytes(file, [1, 2, 3]);
        _a.Db.InsertAttachment(new TaskAttachment { TaskId = task.Id, FileName = "video.bin", FilePath = file });

        _a.SyncWith();
        _blobs.Clear();               // the file hasn't reached B's folder yet
        _b.SyncWith(_a);
        _b.SyncWith(_a);
        Assert.Empty(_b.Db.GetAttachments(_b.Task("Big file").Id));
        _a.SyncWith(_b);
        Assert.Single(_a.Db.GetAttachments(task.Id));   // and A's copy is untouched

        _a.SyncWith(_b);              // A shares the file again
        _b.SyncWith(_a);
        Assert.Single(_b.Db.GetAttachments(_b.Task("Big file").Id));
    }

    [Fact]
    public void Reminders_follow_their_task()
    {
        var task = _a.AddTask("Call");
        _a.Db.InsertReminder(new ReminderItem { TaskId = task.Id, NextFireTime = new DateTime(2030, 1, 1, 9, 0, 0), TaskTitle = "Call" });
        SyncBoth();
        Assert.Single(_b.Db.GetRemindersForTask(_b.Task("Call").Id));

        _b.Db.DeleteTask(_b.Task("Call").Id);
        SyncBoth();
        Assert.Empty(_a.Tasks());
        Assert.Empty(_a.Db.GetAllReminders());
    }

    [Fact]
    public void An_older_file_put_back_is_ignored()
    {
        _a.AddTask("Version 1");
        _a.SyncWith();
        var old = _a.LastFile!;
        _b.SyncWith(_a);

        var t = _a.Task("Version 1"); t.Title = "Version 2"; _a.Db.UpdateTask(t);
        _a.SyncWith();
        _b.SyncWith(_a);
        Assert.Equal("Version 2", _b.Tasks().Single().Title);

        _a.RollBackFileTo(old);
        _b.SyncWith(_a);
        Assert.Equal("Version 2", _b.Tasks().Single().Title);
    }

    [Fact]
    public void Syncing_again_with_nothing_new_changes_nothing()
    {
        _a.AddTask("Stable");
        SyncBoth();
        Assert.False(_b.SyncWith(_a).Any);
        Assert.False(_a.SyncWith(_b).Any);
    }

    [Fact]
    public void Three_computers_end_up_identical()
    {
        using var c = new TestComputer("C", _blobs);
        _a.AddTask("A1");
        _b.AddTask("B1");
        c.AddTask("C1");
        c.Db.InsertNote(new Note { Title = "C note" });

        for (int round = 0; round < 3; round++)
        {
            _a.SyncWith(_b, c);
            _b.SyncWith(_a, c);
            c.SyncWith(_a, _b);
        }

        Assert.Equal(_a.Fingerprint(), _b.Fingerprint());
        Assert.Equal(_a.Fingerprint(), c.Fingerprint());
        Assert.Equal(3, c.Tasks().Count);
    }
}

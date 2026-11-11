namespace Tasky.Services.Sync;

/// <summary>
/// Merges one computer's synced data into another's. Pure functions, no files or database.
///
/// Rules (the same on every computer, in any order, any number of times, so all computers end up
/// identical):
/// - A row only one side has is added to the other. A row that is merely missing is never removed.
/// - For every field, the value with the newest clock wins.
/// - Tracked time: each computer's own counter, newest clock wins per computer; the total is the sum.
/// - A delete marker removes the row everywhere, and a removed row never comes back.
/// </summary>
public static class SyncMerge
{
    /// <summary>Merges <paramref name="incoming"/> into <paramref name="into"/>. Returns true when <paramref name="into"/> changed.</summary>
    public static bool Merge(WorkspaceState into, WorkspaceState incoming, HybridClock? clock = null)
    {
        bool changed = false;
        foreach (var (name, table) in incoming.Tables)
            changed |= MergeTable(into.Table(name), table, clock);
        return changed;
    }

    public static bool MergeTable(TableState into, TableState incoming, HybridClock? clock = null)
    {
        bool changed = false;

        foreach (var (id, deletedAt) in incoming.Tombstones)
        {
            clock?.Observe(deletedAt);
            if (!into.Tombstones.TryGetValue(id, out var existing) || HybridClock.Compare(deletedAt, existing) > 0)
            {
                into.Tombstones[id] = deletedAt;
                changed = true;
            }
        }

        foreach (var (id, record) in incoming.Records)
        {
            if (into.Tombstones.ContainsKey(id)) continue;
            if (into.Records.TryGetValue(id, out var mine))
                changed |= MergeRecord(mine, record, clock);
            else
            {
                into.Records[id] = record.Clone();
                foreach (var f in record.Fields.Values) clock?.Observe(f.Clock);
                changed = true;
            }
        }

        foreach (var id in into.Tombstones.Keys)
            changed |= into.Records.Remove(id);

        return changed;
    }

    public static bool MergeRecord(RecordState into, RecordState incoming, HybridClock? clock = null)
    {
        bool changed = false;
        foreach (var (name, value) in incoming.Fields)
        {
            clock?.Observe(value.Clock);
            if (!into.Fields.TryGetValue(name, out var mine) || HybridClock.Compare(value.Clock, mine.Clock) > 0)
            {
                if (mine is null || mine.Value != value.Value || mine.Clock != value.Clock) changed = true;
                into.Fields[name] = value.Clone();
            }
        }

        if (incoming.Time is { } time)
        {
            into.Time ??= [];
            foreach (var (device, entry) in time)
            {
                if (!into.Time.TryGetValue(device, out var mine) || HybridClock.Compare(entry.Clock, mine.Clock) > 0)
                {
                    into.Time[device] = entry.Clone();
                    changed = true;
                }
            }
        }
        return changed;
    }
}

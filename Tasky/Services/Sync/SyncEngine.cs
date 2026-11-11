using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace Tasky.Services.Sync;

/// <summary>What one sync changed in a workspace's database.</summary>
public sealed class SyncChanges
{
    public int Added      { get; set; }
    public int Updated    { get; set; }
    public int Removed    { get; set; }
    public int Renumbered { get; set; }
    /// <summary>Rows where only tracked time changed (the open window still has to pick it up).</summary>
    public int TimeUpdated { get; set; }
    public bool Any => Added + Updated + Removed + Renumbered + TimeUpdated > 0;
    public int Total => Added + Updated + Removed;
}

/// <summary>Result of <see cref="SyncEngine.Run"/>.</summary>
public sealed class SyncRunResult
{
    /// <summary>Changes made to this computer's data by other computers.</summary>
    public SyncChanges Changes { get; init; } = new();
    /// <summary>The merged state: what this computer's sync file should now hold.</summary>
    public WorkspaceState State { get; init; } = new();
    /// <summary>Attachment files this computer has (content hash → local file) that others may need.</summary>
    public Dictionary<string, string> BlobsToShare { get; init; } = [];
    /// <summary>Content hashes of attachments waiting for their file to arrive.</summary>
    public HashSet<string> BlobsNeeded { get; init; } = [];
}

/// <summary>
/// Syncs one workspace database with the sync files of other computers. Everything happens in one
/// transaction on the database, so the app never sees half a sync:
///
/// 1. Detect: compare every synced row with the copy kept from the last sync (SyncRecords). A changed
///    field gets a new clock; a new row joins; a row that was synced before and is gone now gets a
///    delete marker. No save code in the app has to know about sync.
/// 2. Merge: fold in the other computers' files (<see cref="SyncMerge"/>).
/// 3. Match twins: a row made here and never synced that matches a row from another computer
///    (for example the same group name, or the same task from an earlier migration file) becomes
///    that row, instead of being doubled.
/// 4. Apply: write the merged result back. Rows are added and updated; a row is removed only when a
///    delete marker says so. A running timer's time and attachment paths stay local.
/// </summary>
public sealed class SyncEngine
{
    private readonly string _dbPath;
    private readonly string _attachmentsRoot;
    private readonly string _deviceId;
    private readonly HybridClock _clock;

    /// <summary>Plain file for a content hash, when this computer has it (downloaded and decrypted), or null.</summary>
    public Func<string, string?> BlobFile { get; set; } = _ => null;

    public SyncEngine(string dbPath, string attachmentsRoot, string deviceId, HybridClock clock)
    {
        _dbPath          = dbPath;
        _attachmentsRoot = attachmentsRoot;
        _deviceId        = deviceId;
        _clock           = clock;
    }

    // ── Local bookkeeping kept next to each record ───────────────────────

    private sealed class Extra
    {
        /// <summary>Tasks: AccumulatedSeconds in the database as of the last sync.</summary>
        [JsonPropertyName("lt")] public long? LocalTime { get; set; }
        /// <summary>In the synced data but not in the database yet (its parent or file hasn't arrived).</summary>
        [JsonPropertyName("p")]  public bool Pending { get; set; }
    }

    private sealed class StoredRecord
    {
        [JsonPropertyName("r")] public RecordState Record { get; set; } = new();
        [JsonPropertyName("x")] public Extra       Extra  { get; set; } = new();
    }

    private sealed class Store
    {
        public WorkspaceState State { get; } = new();
        public Dictionary<string, Dictionary<string, Extra>> Extras { get; } = [];

        public Extra ExtraFor(string table, string id)
        {
            if (!Extras.TryGetValue(table, out var map)) Extras[table] = map = [];
            if (!map.TryGetValue(id, out var extra)) map[id] = extra = new Extra();
            return extra;
        }
    }

    /// <summary>A database row: its id, SyncId and the synced values (references already as SyncIds).</summary>
    private sealed class Row
    {
        public long Id;
        public string SyncId = string.Empty;
        public Dictionary<string, string?> Values = [];
        public long AccumulatedSeconds;
        public bool IsRunning;
        public string? FilePath;
    }

    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    // ── Run ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Detects this computer's changes, merges <paramref name="remotes"/> (other computers' files whose
    /// save number is new) and applies the result to the database.
    /// </summary>
    public SyncRunResult Run(IReadOnlyList<SyncSnapshot> remotes)
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath};Pooling=False");
        conn.Open();
        SyncSchema.Ensure(conn);
        using var tx = conn.BeginTransaction();

        var store = Load(conn, tx);
        var before = Serialize(store);
        foreach (var table in store.State.Tables.Values)
            foreach (var record in table.Records.Values)
                foreach (var f in record.Fields.Values) _clock.Observe(f.Clock);

        Detect(conn, tx, store);

        var fresh = remotes.Where(r => r.DeviceId != _deviceId && r.Seq > LastSeq(conn, tx, r.DeviceId)).ToList();
        foreach (var remote in fresh) SyncMerge.Merge(store.State, remote.State, _clock);

        MatchTwins(conn, tx, store, remotes);
        var changes = Apply(conn, tx, store);

        foreach (var remote in fresh) SetLastSeq(conn, tx, remote.DeviceId, remote.Seq);
        if (Serialize(store) != before) Save(conn, tx, store);   // nothing new: leave the database alone
        tx.Commit();

        var result = new SyncRunResult { Changes = changes, State = store.State };
        CollectBlobs(conn, store, result);
        return result;
    }

    // ── 1. Detect ────────────────────────────────────────────────────────

    private void Detect(SqliteConnection conn, SqliteTransaction tx, Store store)
    {
        var sidsByTable = new Dictionary<string, Dictionary<long, string>>();
        foreach (var table in SyncSchema.Tables)
        {
            var state = store.State.Table(table.Name);
            AssignSyncIds(conn, tx, table, state, sidsByTable);
            var rows = ReadRows(conn, tx, table, sidsByTable);
            sidsByTable[table.Name] = rows.ToDictionary(r => r.Id, r => r.SyncId);

            var seen = new HashSet<string>();
            foreach (var row in rows)
            {
                seen.Add(row.SyncId);
                if (state.Tombstones.ContainsKey(row.SyncId)) continue;   // removed elsewhere: Apply removes it here

                bool isNew = !state.Records.TryGetValue(row.SyncId, out var record);
                record ??= new RecordState();
                foreach (var field in table.Fields)
                {
                    var value = row.Values[field.Column];
                    if (!record.Fields.TryGetValue(field.Column, out var current) || current.Value != value)
                        record.Fields[field.Column] = new FieldValue { Value = value, Clock = _clock.Tick() };
                }

                if (table.HasBlob && !record.Fields.ContainsKey(SyncSchema.BlobField))
                    record.Fields[SyncSchema.BlobField] = new FieldValue { Value = HashFile(row.FilePath), Clock = _clock.Tick() };

                var extra = store.ExtraFor(table.Name, row.SyncId);
                extra.Pending = false;
                if (table.Name == SyncSchema.Tasks) DetectTime(record, extra, row, isNew);

                state.Records[row.SyncId] = record;
            }

            // Synced before and gone now: removed on this computer.
            foreach (var id in state.Records.Keys.Where(id => !seen.Contains(id)).ToList())
            {
                if (store.ExtraFor(table.Name, id).Pending) continue;   // never got here yet: not a delete
                state.Records.Remove(id);
                state.Tombstones[id] = _clock.Tick();
            }
        }
    }

    /// <summary>Tracked time: this computer's counter grows by however much the task's time grew here.</summary>
    private void DetectTime(RecordState record, Extra extra, Row row, bool isNew)
    {
        record.Time ??= [];
        long last = extra.LocalTime ?? 0;
        if (isNew && extra.LocalTime is null)
        {
            if (row.AccumulatedSeconds != 0 || !record.Time.ContainsKey(_deviceId))
                record.Time[_deviceId] = new TimeEntry { Seconds = row.AccumulatedSeconds, Clock = _clock.Tick() };
        }
        else if (row.AccumulatedSeconds != last)
        {
            record.Time.TryGetValue(_deviceId, out var mine);
            record.Time[_deviceId] = new TimeEntry
            {
                Seconds = (mine?.Seconds ?? 0) + (row.AccumulatedSeconds - last),
                Clock   = _clock.Tick(),
            };
        }
        extra.LocalTime = row.AccumulatedSeconds;
    }

    /// <summary>
    /// Gives rows without one a SyncId. A repeating task's next occurrence gets one made from its
    /// title, due date and repeat, so two computers completing the same task make the same next one.
    /// </summary>
    private static void AssignSyncIds(SqliteConnection conn, SqliteTransaction tx, SyncTable table, TableState state,
                                      Dictionary<string, Dictionary<long, string>> sidsByTable)
    {
        var missing = new List<(long Id, string Seed)>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = table.Name == SyncSchema.Tasks
                ? "SELECT Id, Title, DueDate, Recurrence, GroupId FROM Tasks WHERE SyncId IS NULL"
                : $"SELECT Id FROM {table.Name} WHERE SyncId IS NULL";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                string seed = string.Empty;
                if (table.Name == SyncSchema.Tasks && !r.IsDBNull(3) && r.GetInt64(3) != 0 && !r.IsDBNull(2))
                {
                    string group = !r.IsDBNull(4) && sidsByTable.TryGetValue(SyncSchema.Groups, out var groups)
                                   && groups.TryGetValue(r.GetInt64(4), out var g) ? g : string.Empty;
                    seed = $"repeat|{r.GetString(1)}|{r.GetString(2)}|{r.GetInt64(3)}|{group}";
                }
                missing.Add((r.GetInt64(0), seed));
            }
        }
        if (missing.Count == 0) return;

        var taken = ExistingSyncIds(conn, tx, table.Name);
        using var update = conn.CreateCommand();
        update.Transaction = tx;
        update.CommandText = $"UPDATE {table.Name} SET SyncId = $sid WHERE Id = $id";
        var pSid = update.Parameters.Add("$sid", SqliteType.Text);
        var pId  = update.Parameters.Add("$id",  SqliteType.Integer);
        foreach (var (id, seed) in missing)
        {
            string sid = seed.Length > 0 ? "r" + SyncIds.FromText(seed)[1..] : SyncIds.New();
            if (taken.Contains(sid) || state.Tombstones.ContainsKey(sid) || state.Records.ContainsKey(sid)) sid = SyncIds.New();
            taken.Add(sid);
            pSid.Value = sid;
            pId.Value  = id;
            update.ExecuteNonQuery();
        }
    }

    private static HashSet<string> ExistingSyncIds(SqliteConnection conn, SqliteTransaction tx, string table)
    {
        var set = new HashSet<string>();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"SELECT SyncId FROM {table} WHERE SyncId IS NOT NULL";
        using var r = cmd.ExecuteReader();
        while (r.Read()) set.Add(r.GetString(0));
        return set;
    }

    private static List<Row> ReadRows(SqliteConnection conn, SqliteTransaction tx, SyncTable table,
                                      Dictionary<string, Dictionary<long, string>> sidsByTable)
    {
        var rows = new List<Row>();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"SELECT * FROM {table.Name} WHERE SyncId IS NOT NULL";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var row = new Row { Id = r.GetInt64(r.GetOrdinal("Id")), SyncId = r.GetString(r.GetOrdinal("SyncId")) };
            foreach (var field in table.Fields)
            {
                var raw = r.GetValue(r.GetOrdinal(field.Column));
                row.Values[field.Column] = field.Kind == FieldKind.Ref
                    ? raw is long refId && sidsByTable.TryGetValue(field.RefTable!, out var map) && map.TryGetValue(refId, out var sid) ? sid : null
                    : ToText(raw);
            }
            if (table.Name == SyncSchema.Tasks)
            {
                row.AccumulatedSeconds = r.GetInt64(r.GetOrdinal("AccumulatedSeconds"));
                row.IsRunning = r.GetInt64(r.GetOrdinal("IsActive")) == 1 && r.GetInt64(r.GetOrdinal("IsPaused")) == 0;
            }
            if (table.HasBlob) row.FilePath = r.GetString(r.GetOrdinal("FilePath"));
            rows.Add(row);
        }
        return rows;
    }

    private static string? ToText(object raw) => raw switch
    {
        DBNull   => null,
        long l   => l.ToString(CultureInfo.InvariantCulture),
        double d => ((long)d).ToString(CultureInfo.InvariantCulture),
        string s => s,
        _        => Convert.ToString(raw, CultureInfo.InvariantCulture),
    };

    private static string HashFile(string? path)
    {
        try
        {
            if (path is null || !File.Exists(path)) return string.Empty;
            using var stream = File.OpenRead(path);
            return Convert.ToHexStringLower(SHA256.HashData(stream));
        }
        catch { return string.Empty; }
    }

    // ── 3. Twins ─────────────────────────────────────────────────────────

    /// <summary>
    /// A row made here and not yet known anywhere else that matches a row from another computer becomes
    /// that row (it gets the other row's SyncId), so the first sync of two copies of the same workspace,
    /// or the same group made on two computers, doesn't double everything.
    /// </summary>
    private void MatchTwins(SqliteConnection conn, SqliteTransaction tx, Store store, IReadOnlyList<SyncSnapshot> remotes)
    {
        var knownElsewhere = new HashSet<string>();
        foreach (var remote in remotes.Where(r => r.DeviceId != _deviceId))
            foreach (var table in remote.State.Tables.Values)
            {
                knownElsewhere.UnionWith(table.Records.Keys);
                knownElsewhere.UnionWith(table.Tombstones.Keys);
            }

        foreach (var table in SyncSchema.Tables)
        {
            var state   = store.State.Table(table.Name);
            var inDb    = ExistingSyncIds(conn, tx, table.Name);
            var localOnly = state.Records.Where(kv => inDb.Contains(kv.Key) && !knownElsewhere.Contains(kv.Key))
                                         .GroupBy(kv => TwinKey(table, kv.Value))
                                         .Where(g => g.Key is not null)
                                         .ToDictionary(g => g.Key!, g => g.First().Key);
            if (localOnly.Count == 0) continue;

            foreach (var (remoteId, record) in state.Records.Where(kv => !inDb.Contains(kv.Key)).ToList())
            {
                if (TwinKey(table, record) is not { } key || !localOnly.Remove(key, out var localId)) continue;

                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = $"UPDATE {table.Name} SET SyncId = $new WHERE SyncId = $old";
                    cmd.Parameters.AddWithValue("$new", remoteId);
                    cmd.Parameters.AddWithValue("$old", localId);
                    cmd.ExecuteNonQuery();
                }
                SyncMerge.MergeRecord(record, state.Records[localId]);
                state.Records.Remove(localId);
                if (store.Extras.TryGetValue(table.Name, out var extras) && extras.Remove(localId, out var extra))
                    extras[remoteId] = extra;

                // Rows pointing at the local twin now point at the shared one.
                foreach (var other in SyncSchema.Tables)
                    foreach (var field in other.Fields.Where(f => f.Kind == FieldKind.Ref && f.RefTable == table.Name))
                        foreach (var r in store.State.Table(other.Name).Records.Values)
                            if (r.Fields.TryGetValue(field.Column, out var v) && v.Value == localId) v.Value = remoteId;
            }
        }
    }

    private static string? TwinKey(SyncTable table, RecordState r) => table.Name switch
    {
        SyncSchema.Groups => r.Get("Name")?.Trim().ToLowerInvariant(),
        SyncSchema.Tasks or SyncSchema.Notes => r.Get("CreatedAt") is { } created ? $"{r.Get("Title")}|{created}" : null,
        SyncSchema.Reminders => $"{r.Get("TaskId")}|{r.Get("NextFireTime")}|{r.Get("Frequency")}",
        _ => $"{r.Get(table.ParentColumn!)}|{r.Get("FileName")}|{r.Get(SyncSchema.BlobField)}",
    };

    // ── 4. Apply ─────────────────────────────────────────────────────────

    private SyncChanges Apply(SqliteConnection conn, SqliteTransaction tx, Store store)
    {
        var changes = new SyncChanges();
        var sidsByTable = new Dictionary<string, Dictionary<long, string>>();
        var idsByTable  = new Dictionary<string, Dictionary<string, long>>();

        foreach (var table in SyncSchema.Tables)
        {
            var state = store.State.Table(table.Name);
            var rows  = ReadRows(conn, tx, table, sidsByTable).ToDictionary(r => r.SyncId);

            // Removed on another computer
            foreach (var row in rows.Values.Where(r => state.Tombstones.ContainsKey(r.SyncId)).ToList())
            {
                DeleteRow(conn, tx, table, row);
                rows.Remove(row.SyncId);
                changes.Removed++;
            }

            var ids = rows.Values.ToDictionary(r => r.SyncId, r => r.Id);
            foreach (var (sid, record) in state.Records.ToList())
            {
                var extra = store.ExtraFor(table.Name, sid);

                // A child whose parent was removed goes too.
                if (table.ParentColumn is { } parentColumn && record.Get(parentColumn) is { } parentSid
                    && store.State.Table(table.Field(parentColumn).RefTable!).Tombstones.ContainsKey(parentSid))
                {
                    state.Records.Remove(sid);
                    state.Tombstones[sid] = _clock.Tick();
                    if (rows.TryGetValue(sid, out var orphan)) { DeleteRow(conn, tx, table, orphan); changes.Removed++; }
                    continue;
                }

                var values = Resolve(table, record, idsByTable, out bool parentMissing);
                if (rows.TryGetValue(sid, out var row))
                {
                    switch (Update(conn, tx, table, row, record, values, extra))
                    {
                        case UpdateKind.Fields:   changes.Updated++;     break;
                        case UpdateKind.TimeOnly: changes.TimeUpdated++; break;
                    }
                }
                else if (parentMissing || !TryInsert(conn, tx, table, record, values, extra, out long newId, sid))
                {
                    extra.Pending = true;
                }
                else
                {
                    ids[sid] = newId;
                    extra.Pending = false;
                    changes.Added++;
                }
            }
            idsByTable[table.Name]  = ids;
            sidsByTable[table.Name] = ids.ToDictionary(kv => kv.Value, kv => kv.Key);

            if (table.Name == SyncSchema.Tasks) changes.Renumbered += FixTaskNumberClashes(conn, tx, state);
        }
        return changes;
    }

    /// <summary>Database values for a record: references turned into local ids.</summary>
    private static Dictionary<string, object> Resolve(SyncTable table, RecordState record,
                                                      Dictionary<string, Dictionary<string, long>> idsByTable,
                                                      out bool parentMissing)
    {
        parentMissing = false;
        var values = new Dictionary<string, object>();
        foreach (var field in table.Fields)
        {
            var text = record.Get(field.Column);
            object value;
            if (field.Kind == FieldKind.Ref)
            {
                value = text is not null && idsByTable.TryGetValue(field.RefTable!, out var ids) && ids.TryGetValue(text, out var id)
                    ? id : DBNull.Value;
                if (field.Column == table.ParentColumn && value is DBNull) parentMissing = true;
            }
            else if (text is null) value = DBNull.Value;
            else if (field.Kind == FieldKind.Int)
                value = long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0L;
            else value = text;
            values[field.Column] = value;
        }
        return values;
    }

    private enum UpdateKind { None, Fields, TimeOnly }

    private UpdateKind Update(SqliteConnection conn, SqliteTransaction tx, SyncTable table, Row row, RecordState record,
                        Dictionary<string, object> values, Extra extra)
    {
        var sets = new List<string>();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        foreach (var field in table.Fields)
        {
            var wanted = record.Get(field.Column);
            if (row.Values[field.Column] == wanted) continue;
            if (field.Kind == FieldKind.Ref && values[field.Column] is DBNull && wanted is not null) continue;   // target not here yet
            if (field.Column == table.ParentColumn && values[field.Column] is DBNull) continue;
            sets.Add($"{field.Column} = ${field.Column}");
            cmd.Parameters.AddWithValue("$" + field.Column, values[field.Column]);
        }

        // Tracked time: the sum of every computer's counter, unless the timer is running here right now
        // (then it's done on the next sync, after the timer stops or saves).
        if (table.Name == SyncSchema.Tasks && !row.IsRunning)
        {
            long total = record.TotalSeconds;
            if (total != row.AccumulatedSeconds)
            {
                sets.Add("AccumulatedSeconds = $acc");
                cmd.Parameters.AddWithValue("$acc", total);
            }
            extra.LocalTime = total;
        }

        if (sets.Count == 0) return UpdateKind.None;
        cmd.CommandText = $"UPDATE {table.Name} SET {string.Join(", ", sets)} WHERE Id = $id";
        cmd.Parameters.AddWithValue("$id", row.Id);
        cmd.ExecuteNonQuery();
        return sets.Count == 1 && sets[0].StartsWith("AccumulatedSeconds", StringComparison.Ordinal)
            ? UpdateKind.TimeOnly : UpdateKind.Fields;
    }

    private bool TryInsert(SqliteConnection conn, SqliteTransaction tx, SyncTable table, RecordState record,
                           Dictionary<string, object> values, Extra extra, out long newId, string sid)
    {
        newId = 0;
        string? sourceFile = null;
        if (table.HasBlob)
        {
            var hash = record.Get(SyncSchema.BlobField) ?? string.Empty;
            if (hash.Length > 0)
            {
                sourceFile = BlobFile(hash);
                if (sourceFile is null) return false;   // the file hasn't arrived yet
            }
        }

        var columns = new List<string>(table.Fields.Select(f => f.Column)) { "SyncId" };
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        foreach (var field in table.Fields) cmd.Parameters.AddWithValue("$" + field.Column, values[field.Column]);
        cmd.Parameters.AddWithValue("$SyncId", sid);

        if (table.Name == SyncSchema.Tasks)
        {
            columns.Add("AccumulatedSeconds");
            cmd.Parameters.AddWithValue("$AccumulatedSeconds", record.TotalSeconds);
            extra.LocalTime = record.TotalSeconds;
        }
        if (table.HasBlob)
        {
            columns.Add("FilePath");
            cmd.Parameters.AddWithValue("$FilePath", string.Empty);   // set below, once the row id is known
        }
        foreach (var notNull in new[] { "Title", "Description", "Name", "Color", "Tags", "FileName", "TaskTitle" })
            if (cmd.Parameters.Contains("$" + notNull) && cmd.Parameters["$" + notNull].Value is DBNull)
                cmd.Parameters["$" + notNull].Value = string.Empty;
        foreach (var dated in new[] { "CreatedAt", "UpdatedAt", "NextFireTime" })
            if (cmd.Parameters.Contains("$" + dated) && cmd.Parameters["$" + dated].Value is DBNull)
                cmd.Parameters["$" + dated].Value = DateTime.Now.ToString("o");

        cmd.CommandText = $"INSERT INTO {table.Name} ({string.Join(",", columns)}) VALUES ({string.Join(",", columns.Select(c => "$" + c))}); SELECT last_insert_rowid();";
        newId = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);

        if (table.HasBlob)
        {
            long ownerId = (long)values[table.ParentColumn!];
            var folder = table.Name == SyncSchema.Attachments
                ? AttachmentFolders.ForTask((int)ownerId)
                : AttachmentFolders.ForNote((int)ownerId);
            var fileName = record.Get("FileName") ?? "attachment";
            var path = sourceFile is null
                ? Path.Combine(_attachmentsRoot, folder, AttachmentStorage.SafeFileName(fileName))
                : new AttachmentStorage(_attachmentsRoot).SaveCopyAs(folder, sourceFile, fileName);
            using var setPath = conn.CreateCommand();
            setPath.Transaction = tx;
            setPath.CommandText = $"UPDATE {table.Name} SET FilePath = $p WHERE Id = $id";
            setPath.Parameters.AddWithValue("$p", path);
            setPath.Parameters.AddWithValue("$id", newId);
            setPath.ExecuteNonQuery();
        }
        return true;
    }

    private void DeleteRow(SqliteConnection conn, SqliteTransaction tx, SyncTable table, Row row)
    {
        var storage = new AttachmentStorage(_attachmentsRoot);
        var sql = table.Name switch
        {
            SyncSchema.Tasks  => ["DELETE FROM Attachments WHERE TaskId = $id", "DELETE FROM Reminders WHERE TaskId = $id", "DELETE FROM Tasks WHERE Id = $id"],
            SyncSchema.Notes  => ["DELETE FROM NoteAttachments WHERE NoteId = $id", "DELETE FROM Notes WHERE Id = $id"],
            SyncSchema.Groups => ["UPDATE Tasks SET GroupId = NULL WHERE GroupId = $id", "DELETE FROM Groups WHERE Id = $id"],
            _                 => new[] { $"DELETE FROM {table.Name} WHERE Id = $id" },
        };
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.Parameters.AddWithValue("$id", row.Id);
        foreach (var s in sql) { cmd.CommandText = s; cmd.ExecuteNonQuery(); }

        if (table.Name == SyncSchema.Tasks) storage.DeleteFolder(AttachmentFolders.ForTask((int)row.Id));
        else if (table.Name == SyncSchema.Notes) storage.DeleteFolder(AttachmentFolders.ForNote((int)row.Id));
        else if (table.HasBlob && !string.IsNullOrEmpty(row.FilePath)) storage.DeleteFile(row.FilePath);
    }

    /// <summary>
    /// Two computers can each give a new task the same number while apart. The task made later gets
    /// the next free number (the same choice on every computer). Returns how many were renumbered.
    /// </summary>
    private int FixTaskNumberClashes(SqliteConnection conn, SqliteTransaction tx, TableState state)
    {
        var tasks = new List<(long Id, string Sid, long Number, DateTime Created)>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT Id, SyncId, TaskNumber, CreatedAt FROM Tasks WHERE SyncId IS NOT NULL";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                tasks.Add((r.GetInt64(0), r.GetString(1), r.GetInt64(2),
                           DateTime.TryParse(r.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var c) ? c : DateTime.MinValue));
        }
        long next = tasks.Count == 0 ? 1 : tasks.Max(t => t.Number) + 1;
        int renumbered = 0;
        foreach (var clash in tasks.GroupBy(t => t.Number).Where(g => g.Count() > 1))
        {
            foreach (var task in clash.OrderBy(t => t.Created).ThenBy(t => t.Sid, StringComparer.Ordinal).Skip(1))
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "UPDATE Tasks SET TaskNumber = $n WHERE Id = $id";
                cmd.Parameters.AddWithValue("$n", next);
                cmd.Parameters.AddWithValue("$id", task.Id);
                cmd.ExecuteNonQuery();
                if (state.Records.TryGetValue(task.Sid, out var record))
                    record.Fields["TaskNumber"] = new FieldValue { Value = next.ToString(CultureInfo.InvariantCulture), Clock = _clock.Tick() };
                next++;
                renumbered++;
            }
        }
        return renumbered;
    }

    // ── Blobs ────────────────────────────────────────────────────────────

    private static void CollectBlobs(SqliteConnection conn, Store store, SyncRunResult result)
    {
        foreach (var table in SyncSchema.Tables.Where(t => t.HasBlob))
        {
            var paths = new Dictionary<string, string>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $"SELECT SyncId, FilePath FROM {table.Name} WHERE SyncId IS NOT NULL";
                using var r = cmd.ExecuteReader();
                while (r.Read()) paths[r.GetString(0)] = r.GetString(1);
            }
            foreach (var (sid, record) in store.State.Table(table.Name).Records)
            {
                var hash = record.Get(SyncSchema.BlobField);
                if (string.IsNullOrEmpty(hash)) continue;
                if (paths.TryGetValue(sid, out var path) && File.Exists(path)) result.BlobsToShare[hash] = path;
                else if (store.ExtraFor(table.Name, sid).Pending) result.BlobsNeeded.Add(hash);
            }
        }
        result.BlobsNeeded.ExceptWith(result.BlobsToShare.Keys);
    }

    // ── Storage of the last-synced copy ──────────────────────────────────

    private static Store Load(SqliteConnection conn, SqliteTransaction tx)
    {
        var store = new Store();
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT TableName, SyncId, Data FROM SyncRecords";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var stored = JsonSerializer.Deserialize<StoredRecord>(r.GetString(2), Json);
                if (stored is null) continue;
                store.State.Table(r.GetString(0)).Records[r.GetString(1)] = stored.Record;
                store.ExtraFor(r.GetString(0), r.GetString(1)).LocalTime = stored.Extra.LocalTime;
                store.ExtraFor(r.GetString(0), r.GetString(1)).Pending   = stored.Extra.Pending;
            }
        }
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT TableName, SyncId, Clock FROM SyncTombstones";
            using var r = cmd.ExecuteReader();
            while (r.Read()) store.State.Table(r.GetString(0)).Tombstones[r.GetString(1)] = r.GetString(2);
        }
        return store;
    }

    private static string Serialize(Store store)
        => JsonSerializer.Serialize(store.State, Json) + JsonSerializer.Serialize(store.Extras, Json);

    private static void Save(SqliteConnection conn, SqliteTransaction tx, Store store)
    {
        using var clear = conn.CreateCommand();
        clear.Transaction = tx;
        clear.CommandText = "DELETE FROM SyncRecords; DELETE FROM SyncTombstones;";
        clear.ExecuteNonQuery();

        using var rec = conn.CreateCommand();
        rec.Transaction = tx;
        rec.CommandText = "INSERT INTO SyncRecords (TableName, SyncId, Data) VALUES ($t, $s, $d)";
        var pT = rec.Parameters.Add("$t", SqliteType.Text);
        var pS = rec.Parameters.Add("$s", SqliteType.Text);
        var pD = rec.Parameters.Add("$d", SqliteType.Text);

        using var tomb = conn.CreateCommand();
        tomb.Transaction = tx;
        tomb.CommandText = "INSERT INTO SyncTombstones (TableName, SyncId, Clock) VALUES ($t, $s, $c)";
        var qT = tomb.Parameters.Add("$t", SqliteType.Text);
        var qS = tomb.Parameters.Add("$s", SqliteType.Text);
        var qC = tomb.Parameters.Add("$c", SqliteType.Text);

        foreach (var (name, table) in store.State.Tables)
        {
            foreach (var (sid, record) in table.Records)
            {
                pT.Value = name; pS.Value = sid;
                pD.Value = JsonSerializer.Serialize(new StoredRecord { Record = record, Extra = store.ExtraFor(name, sid) }, Json);
                rec.ExecuteNonQuery();
            }
            foreach (var (sid, clock) in table.Tombstones)
            {
                qT.Value = name; qS.Value = sid; qC.Value = clock;
                tomb.ExecuteNonQuery();
            }
        }
    }

    private static long LastSeq(SqliteConnection conn, SqliteTransaction tx, string deviceId)
        => long.TryParse(GetMeta(conn, tx, "seq:" + deviceId), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;

    private static void SetLastSeq(SqliteConnection conn, SqliteTransaction tx, string deviceId, long seq)
        => SetMeta(conn, tx, "seq:" + deviceId, seq.ToString(CultureInfo.InvariantCulture));

    private static string? GetMeta(SqliteConnection conn, SqliteTransaction tx, string key)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT Value FROM SyncMeta WHERE Key = $k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    private static void SetMeta(SqliteConnection conn, SqliteTransaction tx, string key, string value)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO SyncMeta (Key, Value) VALUES ($k, $v) ON CONFLICT(Key) DO UPDATE SET Value = $v";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }
}

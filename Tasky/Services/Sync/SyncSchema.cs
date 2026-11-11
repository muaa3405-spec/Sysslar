using Microsoft.Data.Sqlite;

namespace Tasky.Services.Sync;

/// <summary>How a synced column is stored.</summary>
public enum FieldKind
{
    /// <summary>TEXT column, synced as is.</summary>
    Text,
    /// <summary>INTEGER column, synced as an invariant number string.</summary>
    Int,
    /// <summary>INTEGER id of a row in another synced table, synced as that row's SyncId.</summary>
    Ref,
}

public sealed record SyncField(string Column, FieldKind Kind, string? RefTable = null);

/// <summary>A table that syncs, and which of its columns do.</summary>
/// <param name="ParentColumn">The column that must point at an existing row (a reminder's task), or null.</param>
/// <param name="HasBlob">Rows carry an attachment file, synced as an encrypted blob.</param>
public sealed record SyncTable(string Name, SyncField[] Fields, string? ParentColumn = null, bool HasBlob = false)
{
    public SyncField Field(string column) => Fields.First(f => f.Column == column);
}

/// <summary>
/// What syncs. Columns not listed stay on each computer: a running timer (IsActive, IsPaused,
/// ActiveStartTime), attachment paths (each computer keeps its own copy of the file), and the
/// app-usage, meeting and notification logs. Tracked time (AccumulatedSeconds) syncs as one
/// counter per computer, see <see cref="SyncEngine"/>.
/// </summary>
public static class SyncSchema
{
    public const string Groups          = "Groups";
    public const string Notes           = "Notes";
    public const string Tasks           = "Tasks";
    public const string Reminders       = "Reminders";
    public const string Attachments     = "Attachments";
    public const string NoteAttachments = "NoteAttachments";

    /// <summary>Virtual field holding an attachment's content hash (there is no such column).</summary>
    public const string BlobField = "Blob";

    /// <summary>Parents before children, so a row's references exist when it is added.</summary>
    public static readonly SyncTable[] Tables =
    [
        new(Groups,
        [
            new("Name",  FieldKind.Text),
            new("Color", FieldKind.Text),
        ]),
        new(Notes,
        [
            new("Title",       FieldKind.Text),
            new("Description", FieldKind.Text),
            new("Tags",        FieldKind.Text),
            new("CreatedAt",   FieldKind.Text),
            new("UpdatedAt",   FieldKind.Text),
        ]),
        new(Tasks,
        [
            new("TaskNumber",        FieldKind.Int),
            new("Title",             FieldKind.Text),
            new("Description",       FieldKind.Text),
            new("IsCompleted",       FieldKind.Int),
            new("SortOrder",         FieldKind.Int),
            new("CreatedAt",         FieldKind.Text),
            new("ManualTimeMinutes", FieldKind.Int),
            new("DueDate",           FieldKind.Text),
            new("CompletedAt",       FieldKind.Text),
            new("GroupId",           FieldKind.Ref, Groups),
            new("Recurrence",        FieldKind.Int),
            new("RecurrenceEndDate", FieldKind.Text),
            new("DeletedAt",         FieldKind.Text),
        ]),
        new(Reminders,
        [
            new("TaskId",          FieldKind.Ref, Tasks),
            new("NextFireTime",    FieldKind.Text),
            new("Frequency",       FieldKind.Int),
            new("EndDate",         FieldKind.Text),
            new("IsEnabled",       FieldKind.Int),
            new("TaskTitle",       FieldKind.Text),
            new("TargetDayOfWeek", FieldKind.Int),
            new("DayOfMonth",      FieldKind.Int),
        ], ParentColumn: "TaskId"),
        new(Attachments,
        [
            new("TaskId",    FieldKind.Ref, Tasks),
            new("FileName",  FieldKind.Text),
            new("CreatedAt", FieldKind.Text),
        ], ParentColumn: "TaskId", HasBlob: true),
        new(NoteAttachments,
        [
            new("NoteId",    FieldKind.Ref, Notes),
            new("FileName",  FieldKind.Text),
            new("CreatedAt", FieldKind.Text),
        ], ParentColumn: "NoteId", HasBlob: true),
    ];

    public static SyncTable Table(string name) => Tables.First(t => t.Name == name);

    /// <summary>
    /// Adds what sync needs to a workspace database. Safe to run on every start.
    /// SyncId: the row's identity on every computer (random, filled in on the first sync).
    /// SyncRecords: this computer's view of every synced row as of the last sync, with a clock per field.
    /// SyncTombstones: rows removed on some computer, so they are removed everywhere.
    /// These live in the database itself, so a backup restore or an import swaps them together with the data.
    /// </summary>
    public static void Ensure(SqliteConnection conn)
    {
        foreach (var table in Tables)
        {
            AddColumnIfMissing(conn, table.Name, "SyncId", "TEXT");
            Execute(conn, $"CREATE UNIQUE INDEX IF NOT EXISTS IX_{table.Name}_SyncId ON {table.Name}(SyncId);");
        }
        Execute(conn, @"
            CREATE TABLE IF NOT EXISTS SyncRecords (
                TableName TEXT NOT NULL,
                SyncId    TEXT NOT NULL,
                Data      TEXT NOT NULL,
                PRIMARY KEY (TableName, SyncId)
            );
            CREATE TABLE IF NOT EXISTS SyncTombstones (
                TableName TEXT NOT NULL,
                SyncId    TEXT NOT NULL,
                Clock     TEXT NOT NULL,
                PRIMARY KEY (TableName, SyncId)
            );
            CREATE TABLE IF NOT EXISTS SyncMeta (
                Key   TEXT PRIMARY KEY,
                Value TEXT NOT NULL
            );");
    }

    private static void AddColumnIfMissing(SqliteConnection conn, string table, string column, string definition)
    {
        using (var check = conn.CreateCommand())
        {
            check.CommandText = $"PRAGMA table_info({table})";
            using var reader = check.ExecuteReader();
            while (reader.Read())
                if (reader.GetString(1).Equals(column, StringComparison.OrdinalIgnoreCase)) return;
        }
        Execute(conn, $"ALTER TABLE {table} ADD COLUMN {column} {definition}");
    }

    private static void Execute(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}

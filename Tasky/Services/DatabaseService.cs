using System.IO;
using Microsoft.Data.Sqlite;
using Tasky.Models;
using Tasky.Services.Migration;

namespace Tasky.Services;

public class DatabaseService
{
    private readonly string _dbPath;
    private readonly string _connectionString;
    public readonly string AttachmentsRoot;

    public DatabaseService() : this(AppPaths.DataFolder) { }

    /// <summary>The database of the workspace whose data lives in <paramref name="dataFolder"/>
    /// (sync opens workspaces that aren't the open one this way).</summary>
    public DatabaseService(string dataFolder)
    {
        Directory.CreateDirectory(dataFolder);
        _dbPath = Path.Combine(dataFolder, AppPaths.DatabaseFileName);
        _connectionString = $"Data Source={_dbPath}";
        AttachmentsRoot = Path.Combine(dataFolder, AppPaths.AttachmentsFolderName);
        Directory.CreateDirectory(AttachmentsRoot);
    }

    /// <summary>The database file (sync reads and writes it directly).</summary>
    public string DatabasePath => _dbPath;

    public void Initialize()
    {
        using var conn = OpenConnection();

        Execute(conn, @"
            PRAGMA journal_mode=WAL;

            CREATE TABLE IF NOT EXISTS Groups (
                Id    INTEGER PRIMARY KEY AUTOINCREMENT,
                Name  TEXT    NOT NULL DEFAULT '',
                Color TEXT    NOT NULL DEFAULT '📁'
            );

            CREATE TABLE IF NOT EXISTS Notes (
                Id          INTEGER PRIMARY KEY AUTOINCREMENT,
                Title       TEXT NOT NULL DEFAULT '',
                Description TEXT NOT NULL DEFAULT '',
                CreatedAt   TEXT NOT NULL DEFAULT (datetime('now')),
                UpdatedAt   TEXT NOT NULL DEFAULT (datetime('now'))
            );

            CREATE TABLE IF NOT EXISTS Tasks (
                Id                INTEGER PRIMARY KEY AUTOINCREMENT,
                Title             TEXT    NOT NULL DEFAULT '',
                Description       TEXT    NOT NULL DEFAULT '',
                IsCompleted       INTEGER NOT NULL DEFAULT 0,
                IsActive          INTEGER NOT NULL DEFAULT 0,
                SortOrder         INTEGER NOT NULL DEFAULT 0,
                CreatedAt         TEXT    NOT NULL DEFAULT (datetime('now')),
                ActiveStartTime   TEXT
            );

            CREATE TABLE IF NOT EXISTS Reminders (
                Id           INTEGER PRIMARY KEY AUTOINCREMENT,
                TaskId       INTEGER NOT NULL REFERENCES Tasks(Id) ON DELETE CASCADE,
                NextFireTime TEXT    NOT NULL,
                Frequency    INTEGER NOT NULL DEFAULT 0,
                EndDate      TEXT,
                IsEnabled    INTEGER NOT NULL DEFAULT 1,
                TaskTitle    TEXT    NOT NULL DEFAULT ''
            );

            CREATE TABLE IF NOT EXISTS Attachments (
                Id        INTEGER PRIMARY KEY AUTOINCREMENT,
                TaskId    INTEGER NOT NULL REFERENCES Tasks(Id) ON DELETE CASCADE,
                FileName  TEXT    NOT NULL DEFAULT '',
                FilePath  TEXT    NOT NULL DEFAULT '',
                CreatedAt TEXT    NOT NULL DEFAULT (datetime('now'))
            );
        ");

        MigrateColumn(conn, "Tasks", "IsPaused",           "INTEGER NOT NULL DEFAULT 0");
        MigrateColumn(conn, "Tasks", "AccumulatedSeconds",  "INTEGER NOT NULL DEFAULT 0");
        MigrateColumn(conn, "Tasks", "ManualTimeMinutes",   "INTEGER");
        MigrateColumn(conn, "Tasks", "TaskNumber",          "INTEGER NOT NULL DEFAULT 0");
        MigrateColumn(conn, "Tasks", "DueDate",             "TEXT");
        MigrateColumn(conn, "Tasks", "CompletedAt",         "TEXT");
        MigrateColumn(conn, "Tasks", "GroupId",             "INTEGER");
        MigrateColumn(conn, "Tasks",    "Recurrence",       "INTEGER NOT NULL DEFAULT 0");
        MigrateColumn(conn, "Tasks",    "RecurrenceEndDate","TEXT");
        MigrateColumn(conn, "Tasks",    "DeletedAt",        "TEXT");
        MigrateColumn(conn, "Reminders","TargetDayOfWeek","INTEGER");
        MigrateColumn(conn, "Reminders","DayOfMonth",     "INTEGER");

        Execute(conn, "UPDATE Tasks SET TaskNumber = Id WHERE TaskNumber = 0;");

        // Missed notifications inbox — CREATE first, then migrate
        Execute(conn, @"
            CREATE TABLE IF NOT EXISTS MissedNotifications (
                Id        INTEGER PRIMARY KEY AUTOINCREMENT,
                TaskId    INTEGER NOT NULL DEFAULT 0,
                TaskTitle TEXT    NOT NULL DEFAULT '',
                FiredAt   TEXT    NOT NULL DEFAULT (datetime('now')),
                IsRead    INTEGER NOT NULL DEFAULT 0
            );");
        MigrateColumn(conn, "MissedNotifications", "IsRead", "INTEGER NOT NULL DEFAULT 0");;

        // Meeting sessions — auto-detected from mic/camera usage
        Execute(conn, @"
            CREATE TABLE IF NOT EXISTS MeetingSessions (
                Id        INTEGER PRIMARY KEY AUTOINCREMENT,
                StartTime TEXT NOT NULL,
                EndTime   TEXT NOT NULL
            );");

        // App usage sessions — foreground window time tracked by AppUsageService
        Execute(conn, @"
            CREATE TABLE IF NOT EXISTS AppUsageSessions (
                Id        INTEGER PRIMARY KEY AUTOINCREMENT,
                AppName   TEXT NOT NULL,
                ExeName   TEXT NOT NULL DEFAULT '',
                StartTime TEXT NOT NULL,
                EndTime   TEXT NOT NULL
            );");

        // Files attached to notes (files live in Attachments\notes\<note id>\)
        Execute(conn, @"
            CREATE TABLE IF NOT EXISTS NoteAttachments (
                Id        INTEGER PRIMARY KEY AUTOINCREMENT,
                NoteId    INTEGER NOT NULL,
                FileName  TEXT    NOT NULL DEFAULT '',
                FilePath  TEXT    NOT NULL DEFAULT '',
                CreatedAt TEXT    NOT NULL DEFAULT (datetime('now'))
            );");

        // Schema migrations (idempotent — errors mean column already exists)
        try { Execute(conn, "ALTER TABLE Notes ADD COLUMN Tags TEXT NOT NULL DEFAULT ''"); } catch { }

        DeleteOrphanedChildRows(conn);
        RepairAttachmentPaths(conn);
        Sync.SyncSchema.Ensure(conn);
    }

    // ── MissedNotifications ────────────────────────────────────────────────
    public void InsertMissedNotification(int taskId, string taskTitle)
    {
        using var conn = OpenConnection();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO MissedNotifications (TaskId,TaskTitle,FiredAt) VALUES ($tid,$tt,$fa)";
        cmd.Parameters.AddWithValue("$tid", taskId);
        cmd.Parameters.AddWithValue("$tt",  taskTitle);
        cmd.Parameters.AddWithValue("$fa",  DateTime.Now.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    public List<(int Id, int TaskId, string TaskTitle, DateTime FiredAt, bool IsRead)> GetMissedNotifications()
    {
        using var conn = OpenConnection();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "SELECT Id,TaskId,TaskTitle,FiredAt,IsRead FROM MissedNotifications ORDER BY IsRead ASC, FiredAt DESC";
        using var r = cmd.ExecuteReader();
        var list = new List<(int, int, string, DateTime, bool)>();
        while (r.Read())
            list.Add((r.GetInt32(0), r.GetInt32(1), r.GetString(2),
                      DateTime.Parse(r.GetString(3)), r.GetInt32(4) == 1));
        return list;
    }

    public void MarkMissedNotificationRead(int id)
    {
        using var conn = OpenConnection();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "UPDATE MissedNotifications SET IsRead=1 WHERE Id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public int GetMissedNotificationCount()
    {
        using var conn = OpenConnection();
        using var cmd  = conn.CreateCommand();
        // Badge only counts UNREAD
        cmd.CommandText = "SELECT COUNT(*) FROM MissedNotifications WHERE IsRead=0";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public void DeleteMissedNotification(int id)
    {
        using var conn = OpenConnection();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM MissedNotifications WHERE Id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public void ClearMissedNotifications()
    {
        using var conn = OpenConnection();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM MissedNotifications";
        cmd.ExecuteNonQuery();
    }

    // ── MeetingSessions ───────────────────────────────────────────────────
    public void InsertMeetingSession(DateTime start, DateTime end)
    {
        using var conn = OpenConnection();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO MeetingSessions (StartTime,EndTime) VALUES ($s,$e)";
        cmd.Parameters.AddWithValue("$s", start.ToString("o"));
        cmd.Parameters.AddWithValue("$e", end.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    public void ClearMeetingSessions()
    {
        using var conn = OpenConnection();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM MeetingSessions";
        cmd.ExecuteNonQuery();
    }

    public double GetMeetingHours(DateTime from, DateTime to)
    {
        using var conn = OpenConnection();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT StartTime, EndTime FROM MeetingSessions
            WHERE StartTime >= $from AND StartTime < $to";
        cmd.Parameters.AddWithValue("$from", from.ToString("o"));
        cmd.Parameters.AddWithValue("$to",   to.ToString("o"));
        using var r = cmd.ExecuteReader();
        double total = 0;
        while (r.Read())
        {
            var s = DateTime.Parse(r.GetString(0));
            var e = DateTime.Parse(r.GetString(1));
            total += (e - s).TotalHours;
        }
        return total;
    }

    // ── AppUsageSessions ──────────────────────────────────────────────────────

    public void InsertAppUsageSession(string appName, string exeName, DateTime start, DateTime end)
    {
        using var conn = OpenConnection();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO AppUsageSessions (AppName,ExeName,StartTime,EndTime) VALUES ($a,$x,$s,$e)";
        cmd.Parameters.AddWithValue("$a", appName);
        cmd.Parameters.AddWithValue("$x", exeName);
        cmd.Parameters.AddWithValue("$s", start.ToString("o"));
        cmd.Parameters.AddWithValue("$e", end.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    public void ClearAppUsageSessions()
    {
        using var conn = OpenConnection();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM AppUsageSessions";
        cmd.ExecuteNonQuery();
    }

    public List<(string AppName, int TotalSeconds, string ExePath)> GetTopAppsByTime(DateTime from, DateTime to)
    {
        using var conn = OpenConnection();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT AppName,
                   CAST(SUM((julianday(EndTime) - julianday(StartTime)) * 86400) AS INTEGER) AS TotalSecs,
                   MAX(ExeName) AS ExePath
            FROM AppUsageSessions
            WHERE StartTime >= $from AND StartTime < $to
            GROUP BY AppName
            ORDER BY TotalSecs DESC
            LIMIT 10";
        cmd.Parameters.AddWithValue("$from", from.ToString("o"));
        cmd.Parameters.AddWithValue("$to",   to.ToString("o"));
        using var r = cmd.ExecuteReader();
        var list = new List<(string, int, string)>();
        while (r.Read())
            list.Add((r.GetString(0), r.GetInt32(1), r.IsDBNull(2) ? string.Empty : r.GetString(2)));
        return list;
    }

    private static void MigrateColumn(SqliteConnection conn, string table, string column, string definition)
    {
        using var check = conn.CreateCommand();
        check.CommandText = $"PRAGMA table_info({table})";
        using var reader = check.ExecuteReader();
        while (reader.Read())
        {
            if (reader.GetString(1).Equals(column, StringComparison.OrdinalIgnoreCase))
                return;
        }
        Execute(conn, $"ALTER TABLE {table} ADD COLUMN {column} {definition}");
    }

    private static void Execute(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    /// <summary>
    /// Closes the pooled connections that keep database files open after use. Windows can't delete or
    /// replace an open file (macOS can), so call this when a workspace closes, before its files may be
    /// deleted or swapped.
    /// </summary>
    public static void ReleaseOpenFiles() => SqliteConnection.ClearAllPools();

    // Tasks

    public List<TaskItem> GetAllTasks()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT * FROM Tasks
            WHERE DeletedAt IS NULL
            ORDER BY
                IsCompleted ASC,
                IsActive    DESC,
                SortOrder   ASC";
        using var reader = cmd.ExecuteReader();
        var list = new List<TaskItem>();
        while (reader.Read()) list.Add(ReadTask(reader));
        return list;
    }

    /// <summary>Tasks currently in the recycle bin, most recently deleted first.</summary>
    public List<TaskItem> GetDeletedTasks()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM Tasks WHERE DeletedAt IS NOT NULL ORDER BY DeletedAt DESC";
        using var reader = cmd.ExecuteReader();
        var list = new List<TaskItem>();
        while (reader.Read()) list.Add(ReadTask(reader));
        return list;
    }

    /// <summary>Move a task to the recycle bin (soft delete).</summary>
    public void SoftDeleteTask(int id)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE Tasks SET DeletedAt = $now WHERE Id = $id";
        cmd.Parameters.AddWithValue("$now", DateTime.Now.ToString("o"));
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Restore a task from the recycle bin back to its list.</summary>
    public void RestoreTask(int id)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE Tasks SET DeletedAt = NULL WHERE Id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Permanently delete every task currently in the recycle bin. Returns the deleted task ids.</summary>
    public List<int> EmptyTrash()
    {
        var ids = QueryTaskIds("SELECT Id FROM Tasks WHERE DeletedAt IS NOT NULL", null);
        DeleteTasksWithChildren(ids);
        return ids;
    }

    /// <summary>Permanently delete recycle-bin tasks older than the given cutoff. Returns the deleted task ids.</summary>
    public List<int> PurgeDeletedOlderThan(DateTime cutoff)
    {
        var ids = QueryTaskIds(
            "SELECT Id FROM Tasks WHERE DeletedAt IS NOT NULL AND DeletedAt < $cutoff",
            cmd => cmd.Parameters.AddWithValue("$cutoff", cutoff.ToString("o")));
        DeleteTasksWithChildren(ids);
        return ids;
    }

    /// <summary>Ids of every task row, including ones in the recycle bin.</summary>
    public HashSet<int> GetAllTaskIds() => QueryTaskIds("SELECT Id FROM Tasks", null).ToHashSet();

    private List<int> QueryTaskIds(string sql, Action<SqliteCommand>? bind)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        bind?.Invoke(cmd);
        using var reader = cmd.ExecuteReader();
        var ids = new List<int>();
        while (reader.Read()) ids.Add(reader.GetInt32(0));
        return ids;
    }

    /// <summary>
    /// Deletes tasks together with their attachment and reminder rows in one transaction.
    /// (SQLite does not enforce the ON DELETE CASCADE clauses unless foreign keys are
    /// switched on per connection, so children are removed explicitly.)
    /// </summary>
    private void DeleteTasksWithChildren(IReadOnlyCollection<int> taskIds)
    {
        if (taskIds.Count == 0) return;
        using var conn = OpenConnection();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        var pId = cmd.Parameters.Add("$id", SqliteType.Integer);
        foreach (var id in taskIds)
        {
            pId.Value = id;
            foreach (var sql in new[]
            {
                "DELETE FROM Attachments WHERE TaskId = $id",
                "DELETE FROM Reminders   WHERE TaskId = $id",
                "DELETE FROM Tasks       WHERE Id     = $id",
            })
            {
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
            }
        }
        tx.Commit();
    }

    /// <summary>Removes attachment and reminder rows left behind by tasks deleted in older versions.</summary>
    private static void DeleteOrphanedChildRows(SqliteConnection conn)
    {
        Execute(conn, "DELETE FROM Attachments WHERE TaskId NOT IN (SELECT Id FROM Tasks);");
        Execute(conn, "DELETE FROM Reminders   WHERE TaskId NOT IN (SELECT Id FROM Tasks);");
        Execute(conn, "DELETE FROM NoteAttachments WHERE NoteId NOT IN (SELECT Id FROM Notes);");
    }

    public int GetNextTaskNumber()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(TaskNumber),0)+1 FROM Tasks";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public TaskItem InsertTask(TaskItem task)
    {
        if (task.TaskNumber == 0) task.TaskNumber = GetNextTaskNumber();
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO Tasks (TaskNumber,Title,Description,IsCompleted,IsActive,IsPaused,
                               SortOrder,CreatedAt,ActiveStartTime,AccumulatedSeconds,ManualTimeMinutes,
                               DueDate,CompletedAt,GroupId,Recurrence,RecurrenceEndDate)
            VALUES ($num,$title,$desc,$comp,$active,$paused,
                    $sort,$created,$ast,$acc,$manual,$due,$cat,$grp,$rec,$recEnd);
            SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$num",     task.TaskNumber);
        cmd.Parameters.AddWithValue("$title",   task.Title);
        cmd.Parameters.AddWithValue("$desc",    task.Description);
        cmd.Parameters.AddWithValue("$comp",    task.IsCompleted ? 1 : 0);
        cmd.Parameters.AddWithValue("$active",  task.IsActive ? 1 : 0);
        cmd.Parameters.AddWithValue("$paused",  task.IsPaused ? 1 : 0);
        cmd.Parameters.AddWithValue("$sort",    task.SortOrder);
        cmd.Parameters.AddWithValue("$created", task.CreatedAt.ToString("o"));
        cmd.Parameters.AddWithValue("$ast",     task.ActiveStartTime.HasValue
            ? (object)task.ActiveStartTime.Value.ToString("o") : DBNull.Value);
        cmd.Parameters.AddWithValue("$acc",     task.AccumulatedSeconds);
        cmd.Parameters.AddWithValue("$manual",  task.ManualTimeMinutes.HasValue
            ? (object)task.ManualTimeMinutes.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("$due",     task.DueDate.HasValue
            ? (object)task.DueDate.Value.ToString("yyyy-MM-dd") : DBNull.Value);
        cmd.Parameters.AddWithValue("$cat",     task.CompletedAt.HasValue
            ? (object)task.CompletedAt.Value.ToString("o") : DBNull.Value);
        cmd.Parameters.AddWithValue("$grp",     task.GroupId.HasValue
            ? (object)task.GroupId.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("$rec",     (int)task.Recurrence);
        AddRecurrenceEndDate(cmd, task);
        task.Id = Convert.ToInt32(cmd.ExecuteScalar());
        return task;
    }

    public void UpdateTask(TaskItem task)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE Tasks SET
                Title             = $title,
                Description       = $desc,
                IsCompleted       = $comp,
                IsActive          = $active,
                IsPaused          = $paused,
                SortOrder         = $sort,
                ActiveStartTime   = $ast,
                AccumulatedSeconds= $acc,
                ManualTimeMinutes = $manual,
                DueDate           = $due,
                CompletedAt       = $cat,
                GroupId           = $grp,
                Recurrence        = $rec,
                RecurrenceEndDate = $recEnd
            WHERE Id = $id";
        cmd.Parameters.AddWithValue("$title",   task.Title);
        cmd.Parameters.AddWithValue("$desc",    task.Description);
        cmd.Parameters.AddWithValue("$comp",    task.IsCompleted ? 1 : 0);
        cmd.Parameters.AddWithValue("$active",  task.IsActive ? 1 : 0);
        cmd.Parameters.AddWithValue("$paused",  task.IsPaused ? 1 : 0);
        cmd.Parameters.AddWithValue("$sort",    task.SortOrder);
        cmd.Parameters.AddWithValue("$ast",     task.ActiveStartTime.HasValue
            ? (object)task.ActiveStartTime.Value.ToString("o") : DBNull.Value);
        cmd.Parameters.AddWithValue("$acc",     task.AccumulatedSeconds);
        cmd.Parameters.AddWithValue("$manual",  task.ManualTimeMinutes.HasValue
            ? (object)task.ManualTimeMinutes.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("$due",     task.DueDate.HasValue
            ? (object)task.DueDate.Value.ToString("yyyy-MM-dd") : DBNull.Value);
        cmd.Parameters.AddWithValue("$cat",     task.CompletedAt.HasValue
            ? (object)task.CompletedAt.Value.ToString("o") : DBNull.Value);
        cmd.Parameters.AddWithValue("$grp",     task.GroupId.HasValue
            ? (object)task.GroupId.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("$rec",     (int)task.Recurrence);
        AddRecurrenceEndDate(cmd, task);
        cmd.Parameters.AddWithValue("$id",      task.Id);
        cmd.ExecuteNonQuery();
    }

    private static void AddRecurrenceEndDate(SqliteCommand cmd, TaskItem task)
        => cmd.Parameters.AddWithValue("$recEnd", task.RecurrenceEndDate.HasValue
            ? (object)task.RecurrenceEndDate.Value.ToString("yyyy-MM-dd") : DBNull.Value);

    public void DeleteTask(int id) => DeleteTasksWithChildren(new[] { id });

    public void UpdateSortOrders(IEnumerable<(int Id, int SortOrder)> orders)
    {
        using var conn = OpenConnection();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE Tasks SET SortOrder = $sort WHERE Id = $id";
        var pId   = cmd.Parameters.Add("$id",   SqliteType.Integer);
        var pSort = cmd.Parameters.Add("$sort",  SqliteType.Integer);
        foreach (var (id, sort) in orders) { pId.Value = id; pSort.Value = sort; cmd.ExecuteNonQuery(); }
        tx.Commit();
    }

    // Reminders

    public List<ReminderItem> GetAllReminders()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM Reminders WHERE IsEnabled = 1";
        using var reader = cmd.ExecuteReader();
        var list = new List<ReminderItem>();
        while (reader.Read()) list.Add(ReadReminder(reader));
        return list;
    }

    public List<ReminderItem> GetRemindersForTask(int taskId)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM Reminders WHERE TaskId = $taskId";
        cmd.Parameters.AddWithValue("$taskId", taskId);
        using var reader = cmd.ExecuteReader();
        var list = new List<ReminderItem>();
        while (reader.Read()) list.Add(ReadReminder(reader));
        return list;
    }

    public int GetReminderCountForTask(int taskId)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM Reminders WHERE TaskId = $id AND IsEnabled = 1";
        cmd.Parameters.AddWithValue("$id", taskId);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public ReminderItem InsertReminder(ReminderItem reminder)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO Reminders (TaskId,NextFireTime,Frequency,EndDate,IsEnabled,TaskTitle,TargetDayOfWeek,DayOfMonth)
            VALUES ($taskId,$next,$freq,$end,$enabled,$title,$dow,$dom);
            SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$taskId",  reminder.TaskId);
        cmd.Parameters.AddWithValue("$next",    reminder.NextFireTime.ToString("o"));
        cmd.Parameters.AddWithValue("$freq",    (int)reminder.Frequency);
        cmd.Parameters.AddWithValue("$end",     reminder.EndDate.HasValue ? (object)reminder.EndDate.Value.ToString("o") : DBNull.Value);
        cmd.Parameters.AddWithValue("$enabled", reminder.IsEnabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$title",   reminder.TaskTitle);
        cmd.Parameters.AddWithValue("$dow",     reminder.TargetDayOfWeek.HasValue ? (object)reminder.TargetDayOfWeek.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("$dom",     reminder.DayOfMonth.HasValue ? (object)reminder.DayOfMonth.Value : DBNull.Value);
        reminder.Id = Convert.ToInt32(cmd.ExecuteScalar());
        return reminder;
    }

    public void UpdateReminder(ReminderItem reminder)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE Reminders SET NextFireTime=$next,Frequency=$freq,
                EndDate=$end,IsEnabled=$enabled,TaskTitle=$title,
                TargetDayOfWeek=$dow,DayOfMonth=$dom WHERE Id=$id";
        cmd.Parameters.AddWithValue("$next",    reminder.NextFireTime.ToString("o"));
        cmd.Parameters.AddWithValue("$freq",    (int)reminder.Frequency);
        cmd.Parameters.AddWithValue("$end",     reminder.EndDate.HasValue ? (object)reminder.EndDate.Value.ToString("o") : DBNull.Value);
        cmd.Parameters.AddWithValue("$enabled", reminder.IsEnabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$title",   reminder.TaskTitle);
        cmd.Parameters.AddWithValue("$dow",     reminder.TargetDayOfWeek.HasValue ? (object)reminder.TargetDayOfWeek.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("$dom",     reminder.DayOfMonth.HasValue ? (object)reminder.DayOfMonth.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("$id",      reminder.Id);
        cmd.ExecuteNonQuery();
    }

    public void DeleteReminder(int reminderId)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM Reminders WHERE Id = $id";
        cmd.Parameters.AddWithValue("$id", reminderId);
        cmd.ExecuteNonQuery();
    }

    public void DeleteRemindersForTask(int taskId)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM Reminders WHERE TaskId = $taskId";
        cmd.Parameters.AddWithValue("$taskId", taskId);
        cmd.ExecuteNonQuery();
    }

    // Attachments

    /// <summary>
    /// Attachment rows store absolute paths. When data arrives from another computer
    /// (or the Windows user name changed) those paths point nowhere; re-point each one
    /// at the same file inside this computer's Attachments folder.
    /// </summary>
    private void RepairAttachmentPaths(SqliteConnection conn)
    {
        RepairAttachmentPaths(conn, "SELECT Id, TaskId, FilePath FROM Attachments",
            "UPDATE Attachments SET FilePath = $path WHERE Id = $id", AttachmentFolders.ForTask);
        RepairAttachmentPaths(conn, "SELECT Id, NoteId, FilePath FROM NoteAttachments",
            "UPDATE NoteAttachments SET FilePath = $path WHERE Id = $id", AttachmentFolders.ForNote);
    }

    /// <param name="selectSql">Must return (Id, owner id, FilePath).</param>
    private void RepairAttachmentPaths(SqliteConnection conn, string selectSql, string updateSql,
                                       Func<int, string> ownerFolder)
    {
        var fixes = new List<(int Id, string Path)>();
        using (var read = conn.CreateCommand())
        {
            read.CommandText = selectSql;
            using var r = read.ExecuteReader();
            while (r.Read())
            {
                var stored = r.GetString(2);
                var local  = Path.Combine(AttachmentsRoot, ownerFolder(r.GetInt32(1)), FileNameOf(stored));
                bool alreadyLocal = string.Equals(Path.GetFullPath(stored), Path.GetFullPath(local),
                                                  StringComparison.OrdinalIgnoreCase);
                if (!alreadyLocal && File.Exists(local)) fixes.Add((r.GetInt32(0), local));
            }
        }
        if (fixes.Count == 0) return;

        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = updateSql;
        var pPath = cmd.Parameters.Add("$path", SqliteType.Text);
        var pId   = cmd.Parameters.Add("$id",   SqliteType.Integer);
        foreach (var (id, path) in fixes) { pId.Value = id; pPath.Value = path; cmd.ExecuteNonQuery(); }
        tx.Commit();
    }

    /// <summary>
    /// File name of a path stored on any OS. A Windows path ("C:\…\a.png") read on
    /// macOS still yields "a.png", although '\' is not a separator there.
    /// </summary>
    private static string FileNameOf(string storedPath)
        => storedPath[(storedPath.LastIndexOfAny(['\\', '/']) + 1)..];

    /// <summary>
    /// Writes a consistent copy of the whole database to <paramref name="destinationFile"/>
    /// while Tasky keeps running (SQLite VACUUM INTO also folds in the WAL journal).
    /// </summary>
    public void SnapshotTo(string destinationFile)
    {
        if (File.Exists(destinationFile)) File.Delete(destinationFile);
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "VACUUM INTO $dest";
        cmd.Parameters.AddWithValue("$dest", destinationFile);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Row counts shown to the user before exporting or importing.</summary>
    public static MigrationCounts CountRecords(string databaseFile)
    {
        using var conn = new SqliteConnection($"Data Source={databaseFile};Mode=ReadOnly;Pooling=False");
        conn.Open();
        int Count(string sql)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
        return new MigrationCounts(
            OpenTasks:      Count("SELECT COUNT(*) FROM Tasks WHERE IsCompleted = 0 AND DeletedAt IS NULL"),
            CompletedTasks: Count("SELECT COUNT(*) FROM Tasks WHERE IsCompleted = 1 AND DeletedAt IS NULL"),
            Notes:          Count("SELECT COUNT(*) FROM Notes"),
            Groups:         Count("SELECT COUNT(*) FROM Groups"),
            Reminders:      Count("SELECT COUNT(*) FROM Reminders"),
            Attachments:    Count("SELECT COUNT(*) FROM Attachments")
                          // Files made before note attachments existed have no NoteAttachments table.
                          + (Count("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'NoteAttachments'") > 0
                                ? Count("SELECT COUNT(*) FROM NoteAttachments") : 0));
    }

    public List<TaskAttachment> GetAttachments(int taskId)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM Attachments WHERE TaskId=$taskId ORDER BY Id";
        cmd.Parameters.AddWithValue("$taskId", taskId);
        using var reader = cmd.ExecuteReader();
        var list = new List<TaskAttachment>();
        while (reader.Read()) list.Add(ReadAttachment(reader));
        return list;
    }

    public TaskAttachment InsertAttachment(TaskAttachment att)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO Attachments (TaskId,FileName,FilePath,CreatedAt)
            VALUES ($taskId,$name,$path,$created);
            SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$taskId",  att.TaskId);
        cmd.Parameters.AddWithValue("$name",    att.FileName);
        cmd.Parameters.AddWithValue("$path",    att.FilePath);
        cmd.Parameters.AddWithValue("$created", att.CreatedAt.ToString("o"));
        att.Id = Convert.ToInt32(cmd.ExecuteScalar());
        return att;
    }

    public void DeleteAttachment(int id)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM Attachments WHERE Id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public int GetAttachmentCount(int taskId)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM Attachments WHERE TaskId=$taskId";
        cmd.Parameters.AddWithValue("$taskId", taskId);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    // Groups

    public List<Models.TaskGroup> GetAllGroups()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM Groups ORDER BY Name ASC";
        using var reader = cmd.ExecuteReader();
        var list = new List<Models.TaskGroup>();
        while (reader.Read())
            list.Add(new Models.TaskGroup { Id = reader.GetInt32(0), Name = reader.GetString(1), Color = reader.GetString(2) });
        return list;
    }

    public Models.TaskGroup InsertGroup(Models.TaskGroup g)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO Groups (Name,Color) VALUES ($n,$c); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$n", g.Name);
        cmd.Parameters.AddWithValue("$c", g.Color);
        g.Id = Convert.ToInt32(cmd.ExecuteScalar());
        return g;
    }

    public void UpdateGroup(Models.TaskGroup g)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE Groups SET Name=$n, Color=$c WHERE Id=$id";
        cmd.Parameters.AddWithValue("$n",  g.Name);
        cmd.Parameters.AddWithValue("$c",  g.Color);
        cmd.Parameters.AddWithValue("$id", g.Id);
        cmd.ExecuteNonQuery();
    }

    public void DeleteGroup(int id)
    {
        using var conn = OpenConnection();
        var u = conn.CreateCommand();
        u.CommandText = "UPDATE Tasks SET GroupId=NULL WHERE GroupId=$id";
        u.Parameters.AddWithValue("$id", id);
        u.ExecuteNonQuery();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM Groups WHERE Id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    // Notes

    public List<Models.Note> GetAllNotes()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM Notes ORDER BY UpdatedAt DESC";
        using var reader = cmd.ExecuteReader();
        var list = new List<Models.Note>();
        while (reader.Read())
            list.Add(new Models.Note
            {
                Id          = reader.GetInt32(0),
                Title       = reader.GetString(1),
                Description = reader.GetString(2),
                CreatedAt   = DateTime.Parse(reader.GetString(3)),
                UpdatedAt   = DateTime.Parse(reader.GetString(4)),
                Tags        = reader["Tags"]?.ToString() ?? string.Empty,
            });
        return list;
    }

    public Models.Note InsertNote(Models.Note n)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO Notes (Title,Description,Tags,CreatedAt,UpdatedAt) VALUES ($t,$d,$tags,$c,$u); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$t",    n.Title);
        cmd.Parameters.AddWithValue("$d",    n.Description);
        cmd.Parameters.AddWithValue("$tags", n.Tags);
        cmd.Parameters.AddWithValue("$c",    n.CreatedAt.ToString("o"));
        cmd.Parameters.AddWithValue("$u",    n.UpdatedAt.ToString("o"));
        n.Id = Convert.ToInt32(cmd.ExecuteScalar());
        return n;
    }

    public void UpdateNote(Models.Note n)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        n.UpdatedAt = DateTime.Now;
        cmd.CommandText = "UPDATE Notes SET Title=$t, Description=$d, Tags=$tags, UpdatedAt=$u WHERE Id=$id";
        cmd.Parameters.AddWithValue("$t",    n.Title);
        cmd.Parameters.AddWithValue("$d",    n.Description);
        cmd.Parameters.AddWithValue("$tags", n.Tags);
        cmd.Parameters.AddWithValue("$u",    n.UpdatedAt.ToString("o"));
        cmd.Parameters.AddWithValue("$id",   n.Id);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Deletes a note together with its attachment rows.</summary>
    public void DeleteNote(int id)
    {
        using var conn = OpenConnection();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.CommandText = "DELETE FROM NoteAttachments WHERE NoteId=$id";
        cmd.ExecuteNonQuery();
        cmd.CommandText = "DELETE FROM Notes WHERE Id=$id";
        cmd.ExecuteNonQuery();
        tx.Commit();
    }

    // Note attachments

    public List<NoteAttachment> GetNoteAttachments(int noteId)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, NoteId, FileName, FilePath, CreatedAt FROM NoteAttachments WHERE NoteId=$noteId ORDER BY Id";
        cmd.Parameters.AddWithValue("$noteId", noteId);
        using var r = cmd.ExecuteReader();
        var list = new List<NoteAttachment>();
        while (r.Read())
            list.Add(new NoteAttachment
            {
                Id        = r.GetInt32(0),
                NoteId    = r.GetInt32(1),
                FileName  = r.GetString(2),
                FilePath  = r.GetString(3),
                CreatedAt = DateTime.Parse(r.GetString(4))
            });
        return list;
    }

    /// <summary>Attachment count per note id (notes without attachments are absent).</summary>
    public Dictionary<int, int> GetNoteAttachmentCounts()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT NoteId, COUNT(*) FROM NoteAttachments GROUP BY NoteId";
        using var r = cmd.ExecuteReader();
        var counts = new Dictionary<int, int>();
        while (r.Read()) counts[r.GetInt32(0)] = r.GetInt32(1);
        return counts;
    }

    public NoteAttachment InsertNoteAttachment(NoteAttachment att)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO NoteAttachments (NoteId,FileName,FilePath,CreatedAt)
            VALUES ($noteId,$name,$path,$created);
            SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$noteId",  att.NoteId);
        cmd.Parameters.AddWithValue("$name",    att.FileName);
        cmd.Parameters.AddWithValue("$path",    att.FilePath);
        cmd.Parameters.AddWithValue("$created", att.CreatedAt.ToString("o"));
        att.Id = Convert.ToInt32(cmd.ExecuteScalar());
        return att;
    }

    public void DeleteNoteAttachment(int id)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM NoteAttachments WHERE Id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Ids of every note.</summary>
    public HashSet<int> GetAllNoteIds()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id FROM Notes";
        using var r = cmd.ExecuteReader();
        var ids = new HashSet<int>();
        while (r.Read()) ids.Add(r.GetInt32(0));
        return ids;
    }

    // Readers

    private static TaskItem ReadTask(SqliteDataReader r)
    {
        var item = new TaskItem
        {
            Id          = r.GetInt32(r.GetOrdinal("Id")),
            Title       = r.GetString(r.GetOrdinal("Title")),
            Description = r.GetString(r.GetOrdinal("Description")),
            IsCompleted = r.GetInt32(r.GetOrdinal("IsCompleted")) == 1,
            IsActive    = r.GetInt32(r.GetOrdinal("IsActive")) == 1,
            SortOrder   = r.GetInt32(r.GetOrdinal("SortOrder")),
            CreatedAt   = DateTime.Parse(r.GetString(r.GetOrdinal("CreatedAt"))),
        };
        var astOrd = r.GetOrdinal("ActiveStartTime");
        item.ActiveStartTime   = r.IsDBNull(astOrd) ? null : DateTime.Parse(r.GetString(astOrd));
        item.IsPaused          = GetIntCol(r, "IsPaused") == 1;
        item.AccumulatedSeconds= GetIntCol(r, "AccumulatedSeconds");
        item.TaskNumber        = GetIntCol(r, "TaskNumber", item.Id);
        var manOrd = r.GetOrdinal("ManualTimeMinutes");
        item.ManualTimeMinutes = r.IsDBNull(manOrd) ? null : r.GetInt32(manOrd);
        try { var o = r.GetOrdinal("DueDate");     item.DueDate     = r.IsDBNull(o) ? null : DateTime.Parse(r.GetString(o)); } catch { item.DueDate = DateTime.Today; }
        try { var o = r.GetOrdinal("CompletedAt"); item.CompletedAt = r.IsDBNull(o) ? null : DateTime.Parse(r.GetString(o)); } catch { }
        try { var o = r.GetOrdinal("GroupId");     item.GroupId     = r.IsDBNull(o) ? null : r.GetInt32(o); } catch { }
        try { var o = r.GetOrdinal("Recurrence");  item.Recurrence  = (Models.RecurrenceType)GetIntCol(r, "Recurrence"); } catch { }
        try { var o = r.GetOrdinal("RecurrenceEndDate"); item.RecurrenceEndDate = r.IsDBNull(o) ? null : DateTime.Parse(r.GetString(o)); } catch { }
        try { var o = r.GetOrdinal("DeletedAt");    item.DeletedAt   = r.IsDBNull(o) ? null : DateTime.Parse(r.GetString(o)); } catch { }
        return item;
    }

    private static int GetIntCol(SqliteDataReader r, string col, int fallback = 0)
    {
        try { var o = r.GetOrdinal(col); return r.IsDBNull(o) ? fallback : r.GetInt32(o); }
        catch { return fallback; }
    }

    private static ReminderItem ReadReminder(SqliteDataReader r)
    {
        var item = new ReminderItem
        {
            Id           = r.GetInt32(0),
            TaskId       = r.GetInt32(1),
            NextFireTime = DateTime.Parse(r.GetString(2)),
            Frequency    = (ReminderFrequency)r.GetInt32(3),
            EndDate      = r.IsDBNull(4) ? null : DateTime.Parse(r.GetString(4)),
            IsEnabled    = r.GetInt32(5) == 1,
            TaskTitle    = r.GetString(6)
        };
        try { var o = r.GetOrdinal("TargetDayOfWeek"); item.TargetDayOfWeek = r.IsDBNull(o) ? null : r.GetInt32(o); } catch { }
        try { var o = r.GetOrdinal("DayOfMonth");      item.DayOfMonth      = r.IsDBNull(o) ? null : r.GetInt32(o); } catch { }
        return item;
    }

    private static TaskAttachment ReadAttachment(SqliteDataReader r) => new()
    {
        Id        = r.GetInt32(0),
        TaskId    = r.GetInt32(1),
        FileName  = r.GetString(2),
        FilePath  = r.GetString(3),
        CreatedAt = DateTime.Parse(r.GetString(4))
    };
}

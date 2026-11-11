using System.Text.Json.Serialization;

namespace Tasky.Services.Sync;

/// <summary>One synced value and the clock of the change that set it.</summary>
public sealed class FieldValue
{
    [JsonPropertyName("v")] public string? Value { get; set; }
    [JsonPropertyName("c")] public string  Clock { get; set; } = string.Empty;

    public FieldValue Clone() => new() { Value = Value, Clock = Clock };
}

/// <summary>Seconds tracked on one computer (only that computer ever changes its own entry).</summary>
public sealed class TimeEntry
{
    [JsonPropertyName("s")] public long   Seconds { get; set; }
    [JsonPropertyName("c")] public string Clock   { get; set; } = string.Empty;

    public TimeEntry Clone() => new() { Seconds = Seconds, Clock = Clock };
}

/// <summary>A synced row: every field with its own clock, so edits to different fields on different
/// computers both survive, and the newest edit of the same field wins everywhere.</summary>
public sealed class RecordState
{
    [JsonPropertyName("f")] public Dictionary<string, FieldValue> Fields { get; set; } = [];

    /// <summary>Tasks only: tracked seconds per computer. The task's time is the sum.</summary>
    [JsonPropertyName("t")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, TimeEntry>? Time { get; set; }

    public string? Get(string field) => Fields.TryGetValue(field, out var f) ? f.Value : null;

    public long TotalSeconds => Time?.Values.Sum(t => t.Seconds) ?? 0;

    public RecordState Clone() => new()
    {
        Fields = Fields.ToDictionary(kv => kv.Key, kv => kv.Value.Clone()),
        Time   = Time?.ToDictionary(kv => kv.Key, kv => kv.Value.Clone()),
    };
}

/// <summary>All synced rows of one table, plus the rows removed somewhere (delete markers).</summary>
public sealed class TableState
{
    [JsonPropertyName("r")] public Dictionary<string, RecordState> Records    { get; set; } = [];
    [JsonPropertyName("d")] public Dictionary<string, string>      Tombstones { get; set; } = [];
}

/// <summary>A workspace's synced data as one computer knows it. This is what a sync file holds.</summary>
public sealed class WorkspaceState
{
    [JsonPropertyName("tables")] public Dictionary<string, TableState> Tables { get; set; } = [];

    public TableState Table(string name)
    {
        if (!Tables.TryGetValue(name, out var table)) Tables[name] = table = new TableState();
        return table;
    }
}

/// <summary>The decrypted content of one computer's sync file for one workspace.</summary>
public sealed class SyncSnapshot
{
    [JsonPropertyName("schema")]  public int    Schema     { get; set; } = 1;
    [JsonPropertyName("app")]     public string App        { get; set; } = string.Empty;
    [JsonPropertyName("device")]  public string DeviceId   { get; set; } = string.Empty;
    [JsonPropertyName("name")]    public string DeviceName { get; set; } = string.Empty;
    /// <summary>Grows with every save, so an older copy put back in its place is ignored.</summary>
    [JsonPropertyName("seq")]     public long   Seq        { get; set; }
    [JsonPropertyName("wsName")]  public string WorkspaceName  { get; set; } = string.Empty;
    [JsonPropertyName("wsColor")] public string WorkspaceColor { get; set; } = string.Empty;
    [JsonPropertyName("state")]   public WorkspaceState State { get; set; } = new();
}

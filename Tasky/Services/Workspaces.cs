using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Tasky.Services;

/// <summary>One separate set of Tasky data (e.g. "Personal" and "Office").</summary>
public sealed class Workspace
{
    public string Id    { get; set; } = string.Empty;
    public string Name  { get; set; } = string.Empty;
    public string Color { get; set; } = Workspaces.DefaultColor;

    public bool IsDefault => Id == Workspaces.DefaultId;
    public bool IsActive  => Id == Workspaces.Active.Id;
}

/// <summary>
/// Workspaces keep completely separate data: each has its own database, settings,
/// attachments, backups and logs, so nothing mixes between them.
///
/// The first ("default") workspace uses Tasky's original folders, so existing data stays where it is.
/// Every other workspace gets its own folders (see <see cref="AppPaths.DataFolderFor"/>).
/// The list of workspaces and which one is open lives in
/// <see cref="AppPaths.WorkspaceRegistryFile"/>, outside every workspace's data.
///
/// One workspace is open per run: switching saves the choice and restarts Tasky,
/// so every service (database, timers, reminders, backups) starts cleanly on the new data.
/// </summary>
public static class Workspaces
{
    public const string DefaultId    = "default";
    public const string DefaultName  = "Personal";
    public const string DefaultColor = "#3B82F6";
    public const int    MaxNameLength = 30;

    /// <summary>Restart argument: open the migration import as soon as the new workspace is up.</summary>
    public const string OpenImportArg = "--open-import";

    /// <summary>Colours offered for a workspace (shown as the dot next to its name).</summary>
    public static readonly (string Hex, string Name)[] Palette =
    [
        ("#3B82F6", "Blue"),  ("#22C55E", "Green"),  ("#F97316", "Orange"), ("#8B5CF6", "Violet"),
        ("#EC4899", "Pink"),  ("#EF4444", "Red"),    ("#14B8A6", "Teal"),   ("#EAB308", "Yellow"),
        ("#64748B", "Slate"),
    ];

    private static readonly Regex SafeId = new("^[a-z0-9-]{1,40}$", RegexOptions.Compiled);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly Lock Sync = new();

    private sealed class Registry
    {
        public string          Active     { get; set; } = DefaultId;
        public List<Workspace> Workspaces { get; set; } = [];
    }

    private static Registry _registry = Load();

    /// <summary>The workspace Sysslar has open (changes when the user switches, see <see cref="Activate"/>).</summary>
    public static Workspace Active { get; private set; } = Find(_registry.Active) ?? Find(DefaultId)!;

    /// <summary>
    /// Makes <paramref name="id"/> the open workspace right away (and the one to start with next time).
    /// Every path in <see cref="AppPaths"/> follows it, so the caller must have stopped everything that
    /// uses the old workspace's files first (see App.SwitchWorkspaceAsync).
    /// </summary>
    public static void Activate(string id)
    {
        lock (Sync)
        {
            var workspace = _registry.Workspaces.FirstOrDefault(w => w.Id == id)
                            ?? throw new InvalidOperationException($"No workspace '{id}'");
            _registry.Active = id;
            Save();
            Active = workspace;
        }
    }

    /// <summary>All workspaces, the default one first.</summary>
    public static IReadOnlyList<Workspace> All
    {
        get { lock (Sync) return [.. _registry.Workspaces]; }
    }

    /// <summary>True when the user has made more than the one built-in workspace.</summary>
    public static bool HasSeveral => All.Count > 1;

    public static Workspace? Find(string id)
    {
        lock (Sync) return _registry.Workspaces.FirstOrDefault(w => w.Id == id);
    }

    // ── Changes ───────────────────────────────────────────────────────────

    /// <summary>
    /// Adds an empty workspace. Its settings start as a copy of the open workspace's
    /// (theme, text size, name…), so it looks the same; its tasks, notes and history start empty.
    /// </summary>
    public static Workspace Create(string name, string color)
    {
        var workspace = new Workspace { Id = NewId(), Name = CleanName(name), Color = CleanColor(color) };

        var data = AppPaths.DataFolderFor(workspace.Id);
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(AppPaths.LocalFolderFor(workspace.Id));
        if (File.Exists(AppPaths.SettingsFile))
            File.Copy(AppPaths.SettingsFile, Path.Combine(data, AppPaths.SettingsFileName), overwrite: true);

        lock (Sync)
        {
            _registry.Workspaces.Add(workspace);
            Save();
        }
        return workspace;
    }

    public static void Update(string id, string name, string color)
    {
        lock (Sync)
        {
            var workspace = _registry.Workspaces.FirstOrDefault(w => w.Id == id)
                            ?? throw new InvalidOperationException($"No workspace '{id}'");
            workspace.Name  = CleanName(name);
            workspace.Color = CleanColor(color);   // Active is this same object, so it updates too
            Save();
        }
    }

    /// <summary>
    /// Permanently deletes a workspace and all of its data. The default workspace and
    /// the open one can't be deleted.
    /// </summary>
    public static void Delete(string id)
    {
        if (id == DefaultId)  throw new InvalidOperationException("The first workspace can't be deleted");
        if (id == Active.Id)  throw new InvalidOperationException("The open workspace can't be deleted");   // re-read: Active can change

        lock (Sync)
        {
            _registry.Workspaces.RemoveAll(w => w.Id == id);
            if (_registry.Active == id) _registry.Active = Active.Id;
            Save();
        }
        DeleteFolder(AppPaths.DataFolderFor(id));
        DeleteFolder(AppPaths.LocalFolderFor(id));
    }

    /// <summary>Makes <paramref name="id"/> the workspace Tasky opens next time it starts.</summary>
    public static void OpenOnNextStart(string id)
    {
        lock (Sync)
        {
            if (_registry.Workspaces.All(w => w.Id != id)) throw new InvalidOperationException($"No workspace '{id}'");
            _registry.Active = id;
            Save();
        }
    }

    /// <summary>Puts the open workspace back as the one to start with (when a switch could not restart).</summary>
    public static void KeepActiveOnNextStart()
    {
        lock (Sync)
        {
            _registry.Active = Active.Id;
            Save();
        }
    }

    public static string CleanName(string name)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0) throw new ArgumentException("A workspace needs a name");
        return trimmed.Length > MaxNameLength ? trimmed[..MaxNameLength] : trimmed;
    }

    /// <summary>True when another workspace already uses this name (ignoring case).</summary>
    public static bool NameIsTaken(string name, string? exceptId = null)
        => All.Any(w => w.Id != exceptId && string.Equals(w.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    // ── Storage ───────────────────────────────────────────────────────────

    /// <summary>
    /// Reads the workspace list. Never throws, and never logs (the log folder depends on the
    /// open workspace, which is being worked out here): a missing or damaged file means
    /// "just the default workspace", which is exactly how Tasky worked before workspaces.
    /// </summary>
    private static Registry Load()
    {
        Registry registry;
        try
        {
            var file = AppPaths.WorkspaceRegistryFile;
            registry = File.Exists(file)
                ? JsonSerializer.Deserialize<Registry>(File.ReadAllText(file)) ?? new Registry()
                : new Registry();
        }
        catch
        {
            registry = new Registry();
        }

        // Drop anything that could point outside Tasky's folders, and duplicates.
        registry.Workspaces = [.. registry.Workspaces
            .Where(w => w is not null && SafeId.IsMatch(w.Id ?? string.Empty) && !string.IsNullOrWhiteSpace(w.Name))
            .GroupBy(w => w.Id).Select(g => g.First())];
        foreach (var w in registry.Workspaces)
        {
            w.Name  = w.Name.Trim().Length > MaxNameLength ? w.Name.Trim()[..MaxNameLength] : w.Name.Trim();
            w.Color = CleanColor(w.Color);
        }

        // The default workspace (Tasky's original data) is always there, and always first.
        var first = registry.Workspaces.FirstOrDefault(w => w.Id == DefaultId)
                    ?? new Workspace { Id = DefaultId, Name = DefaultName, Color = DefaultColor };
        registry.Workspaces.Remove(first);
        registry.Workspaces.Insert(0, first);

        if (registry.Workspaces.All(w => w.Id != registry.Active)) registry.Active = DefaultId;
        return registry;
    }

    private static void Save()
    {
        var file = AppPaths.WorkspaceRegistryFile;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var partial = file + ".partial";
        File.WriteAllText(partial, JsonSerializer.Serialize(_registry, JsonOptions));
        File.Move(partial, file, overwrite: true);
    }

    private static string NewId() => "ws-" + Guid.NewGuid().ToString("N")[..10];

    private static string CleanColor(string? color)
        => color is { Length: 7 } && color[0] == '#' && color.Skip(1).All(Uri.IsHexDigit) ? color.ToUpperInvariant() : DefaultColor;

    private static void DeleteFolder(string folder)
    {
        try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
        catch (Exception ex) { CrashLog.Write($"Could not delete workspace folder '{folder}'", ex); }
    }
}

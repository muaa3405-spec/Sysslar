using System.Text;
using Tasky.Models;

namespace Tasky.Services.DataExchange;

/// <summary>
/// Writes tasks and notes as a Markdown checklist, readable as plain text and in apps such as
/// Obsidian, Notion, GitHub or VS Code.
/// </summary>
public static class MarkdownExporter
{
    private const string NoGroup = "No group";

    public static string ToMarkdown(IReadOnlyList<TaskExportRow> tasks, IReadOnlyList<Note> notes, DateTime exportedAt)
    {
        var md = new StringBuilder();
        md.AppendLine($"# Sysslar export, {exportedAt:d MMMM yyyy}").AppendLine();

        AppendTaskSection(md, "Open tasks",
            tasks.Where(t => !t.IsCompleted).OrderBy(t => t.DueDate ?? DateTime.MaxValue).ThenBy(t => t.Number));
        AppendTaskSection(md, "Completed tasks",
            tasks.Where(t => t.IsCompleted).OrderByDescending(t => t.CompletedAt));

        if (notes.Count > 0)
        {
            md.AppendLine("## Notes").AppendLine();
            foreach (var note in notes)
            {
                md.AppendLine($"### {note.Title}");
                if (!string.IsNullOrWhiteSpace(note.Tags)) md.AppendLine($"*Tags: {note.Tags}*");
                md.AppendLine();
                if (!string.IsNullOrWhiteSpace(note.Description)) md.AppendLine(note.Description.Trim()).AppendLine();
            }
        }
        return md.ToString();
    }

    private static void AppendTaskSection(StringBuilder md, string heading, IEnumerable<TaskExportRow> tasks)
    {
        var byGroup = tasks.GroupBy(t => string.IsNullOrWhiteSpace(t.Group) ? NoGroup : t.Group)
                           .OrderBy(g => g.Key == NoGroup).ThenBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase)
                           .ToList();
        if (byGroup.Count == 0) return;

        md.AppendLine($"## {heading}").AppendLine();
        foreach (var group in byGroup)
        {
            md.AppendLine($"### {group.Key}").AppendLine();
            foreach (var task in group) AppendTask(md, task);
            md.AppendLine();
        }
    }

    private static void AppendTask(StringBuilder md, TaskExportRow task)
    {
        var details = new List<string>();
        if (task.DueDate is { } due)            details.Add($"due {due:ddd d MMM yyyy}");
        if (task.RepeatDisplay.Length > 0)      details.Add($"repeats {task.RepeatDisplay.ToLowerInvariant()}");
        if (task.CompletedAt is { } done)       details.Add($"done {done:d MMM yyyy}");
        if (task.TimeSpentDisplay.Length > 0)   details.Add(task.TimeSpentDisplay);

        md.Append(task.IsCompleted ? "- [x] " : "- [ ] ")
          .Append(task.NumberDisplay).Append(' ').Append(task.Title);
        if (details.Count > 0) md.Append(" (").Append(string.Join(", ", details)).Append(')');
        md.AppendLine();

        // Indented under the checklist item, so it stays part of it.
        foreach (var line in task.Description.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries))
            md.Append("  ").AppendLine(line.TrimEnd('\r'));
    }
}

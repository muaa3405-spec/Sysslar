using System.Net;
using System.Text;

namespace Tasky.Services.DataExchange;

/// <summary>
/// Builds a print-ready page of open tasks. It opens in the default browser, which shows its
/// print dialog straight away; "Save as PDF" there makes a PDF. This works the same on Windows
/// and macOS without Tasky needing its own printing code.
/// </summary>
public static class PrintableTaskList
{
    public static string ToHtml(string listName, IReadOnlyList<TaskExportRow> tasks, DateTime printedAt)
    {
        var html = new StringBuilder();
        html.Append($$"""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8">
            <title>Tasky · {{Encode(listName)}}</title>
            <style>
              body { font: 14px/1.45 -apple-system, "Segoe UI", sans-serif; color: #111; margin: 32px; }
              h1 { font-size: 22px; margin: 0 0 2px; }
              .meta { color: #666; margin-bottom: 20px; }
              h2 { font-size: 15px; margin: 22px 0 6px; border-bottom: 1px solid #ccc; padding-bottom: 3px; }
              ul { list-style: none; padding: 0; margin: 0; }
              li { display: flex; gap: 10px; padding: 6px 0; border-bottom: 1px solid #eee; page-break-inside: avoid; }
              .box { width: 14px; height: 14px; border: 1.5px solid #333; border-radius: 3px; flex: none; margin-top: 3px; }
              .num { color: #666; font-family: Consolas, Menlo, monospace; font-size: 12px; }
              .title { font-weight: 600; }
              .details { color: #555; font-size: 12px; }
              .desc { color: #333; font-size: 12px; white-space: pre-wrap; margin-top: 2px; }
              .empty { color: #666; font-style: italic; }
              @media print { body { margin: 12mm; } }
            </style></head>
            <body onload="window.print()">
            <h1>{{Encode(listName)}}</h1>
            <div class="meta">Printed from Sysslar on {{printedAt:dddd d MMMM yyyy, HH:mm}} · {{tasks.Count}} task(s)</div>
            """);

        if (tasks.Count == 0) html.Append("<p class=\"empty\">Nothing to do here.</p>");

        foreach (var day in tasks.GroupBy(t => t.DueDate?.Date).OrderBy(g => g.Key ?? DateTime.MaxValue))
        {
            html.Append($"<h2>{Encode(DayHeading(day.Key, printedAt.Date))}</h2><ul>");
            foreach (var task in day) AppendTask(html, task);
            html.Append("</ul>");
        }
        html.Append("</body></html>");
        return html.ToString();
    }

    private static void AppendTask(StringBuilder html, TaskExportRow task)
    {
        var details = new[] { task.Group, task.RepeatDisplay.Length > 0 ? $"repeats {task.RepeatDisplay.ToLowerInvariant()}" : "" }
            .Where(d => d.Length > 0);
        html.Append("<li><span class=\"box\"></span><div>")
            .Append($"<span class=\"num\">{Encode(task.NumberDisplay)}</span> <span class=\"title\">{Encode(task.Title)}</span>")
            .Append($"<div class=\"details\">{Encode(string.Join(" · ", details))}</div>");
        if (!string.IsNullOrWhiteSpace(task.Description))
            html.Append($"<div class=\"desc\">{Encode(task.Description.Trim())}</div>");
        html.Append("</div></li>");
    }

    private static string DayHeading(DateTime? day, DateTime today) => day switch
    {
        null                           => "No date",
        var d when d < today           => $"Overdue · {d:ddd d MMM}",
        var d when d == today          => $"Today · {d:ddd d MMM}",
        var d when d == today.AddDays(1) => $"Tomorrow · {d:ddd d MMM}",
        var d                          => $"{d:dddd d MMM}"
    };

    private static string Encode(string text) => WebUtility.HtmlEncode(text);
}

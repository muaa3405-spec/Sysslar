using System.IO;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Tasky.Models;

namespace Tasky.Services;

/// <summary>
/// Hooks for the CI smoke test only; each needs a sandboxed <see cref="AppPaths.DataRootOverrideVariable"/>.
/// <list type="bullet">
/// <item><see cref="ShowReminderVariable"/>: shows a sample reminder and logs its position, so the
/// macOS screenshot shows where reminders appear.</item>
/// <item><see cref="FullRunVariable"/>: fills the empty sandbox with sample tasks and notes (long
/// descriptions, groups, repeats, completed work), opens every page once, then writes
/// <see cref="PassedMarkerFile"/>. A start-up or layout crash on real-looking data fails the build.</item>
/// </list>
/// </summary>
public static class SmokeTest
{
    public const string ShowReminderVariable = "TASKY_SMOKE_REMINDER";
    public const string FullRunVariable      = "TASKY_SMOKE_FULL";

    /// <summary>Optional: this many extra open tasks in the sample data, to try a long list.</summary>
    public const string ExtraTasksVariable   = "TASKY_SMOKE_EXTRA_TASKS";

    /// <summary>Written when every page has been shown; CI checks for it.</summary>
    public static string PassedMarkerFile => Path.Combine(AppPaths.LocalFolder, "smoke-test-passed");

    private static readonly TimeSpan LogDelay = TimeSpan.FromSeconds(3);

    private const string LongText =
        "This description is deliberately long so the task card has to cut it off and show the " +
        "\"Show more\" link. It goes on for a few lines with ordinary words, a link https://example.com " +
        "and some punctuation: commas, full stops; and a final sentence that wraps once more.";

    private static bool IsRequested(string variable)
        => Environment.GetEnvironmentVariable(variable) is { Length: > 0 }
        && Environment.GetEnvironmentVariable(AppPaths.DataRootOverrideVariable) is { Length: > 0 };

    public static bool IsFullRun => IsRequested(FullRunVariable);

    // ── Sample data ───────────────────────────────────────────────────────

    /// <summary>Fills an empty sandbox database with data that exercises every kind of task card.</summary>
    public static void SeedSampleDataIfRequested(DatabaseService db)
    {
        if (!IsFullRun || db.GetAllTasks().Count > 0) return;

        var work     = db.InsertGroup(new TaskGroup { Name = "Work",     Color = "#3B82F6" });
        var personal = db.InsertGroup(new TaskGroup { Name = "Personal", Color = "#22C55E" });
        var today    = DateTime.Today;

        var plan = db.InsertTask(new TaskItem { Title = "Plan the week", Description = LongText, GroupId = work.Id, DueDate = today });
        // One attachment and one reminder, so the cards show their count chips
        db.InsertAttachment(new TaskAttachment { TaskId = plan.Id, FileName = "brief.pdf", FilePath = Path.Combine(AppPaths.LocalFolder, "brief.pdf") });
        db.InsertReminder(new ReminderItem { TaskId = plan.Id, TaskTitle = plan.Title, NextFireTime = DateTime.Now.AddDays(1) });
        db.InsertTask(new TaskItem { Title = "Overdue invoice", Description = "Short note", GroupId = work.Id, DueDate = today.AddDays(-3) });
        // No description, but a reminder: its count chip sits on the line under the title
        var plants = db.InsertTask(new TaskItem { Title = "Water the plants", GroupId = personal.Id, DueDate = today,
                                     Recurrence = RecurrenceType.Daily, RecurrenceEndDate = today.AddDays(30) });
        db.InsertReminder(new ReminderItem { TaskId = plants.Id, TaskTitle = plants.Title, NextFireTime = DateTime.Now.AddDays(1) });
        db.InsertTask(new TaskItem { Title = "A very long task title that keeps going so it has to wrap onto a second line in the card",
                                     DueDate = today.AddDays(2), Recurrence = RecurrenceType.Weekly });
        db.InsertTask(new TaskItem { Title = "No due date", DueDate = null });
        db.InsertTask(new TaskItem { Title = "Finished report", Description = LongText, GroupId = work.Id, DueDate = today.AddDays(-1),
                                     IsCompleted = true, CompletedAt = DateTime.Now.AddHours(-2), AccumulatedSeconds = 5400 });

        if (int.TryParse(Environment.GetEnvironmentVariable(ExtraTasksVariable), out int extra))
            for (int i = 1; i <= Math.Min(extra, 5000); i++)
                db.InsertTask(new TaskItem
                {
                    Title       = $"Sample task {i}",
                    Description = i % 3 == 0 ? LongText : string.Empty,
                    GroupId     = i % 2 == 0 ? work.Id : personal.Id,
                    DueDate     = today.AddDays(i % 5 - 2),
                });

        db.InsertNote(new Note { Title = "Long note", Description = LongText + "\n\n" + LongText, Tags = "ideas, work" });
        db.InsertNote(new Note { Title = "Short note", Description = "One line." });
    }

    // ── Page walk ─────────────────────────────────────────────────────────

    /// <summary>Opens every page of the main window once, then records that the run passed.</summary>
    public static void VisitAllPagesIfRequested(MainWindow window)
    {
        if (!IsFullRun) return;
        window.ShowEveryPageForSmokeTest(() =>
        {
            Directory.CreateDirectory(AppPaths.LocalFolder);
            File.WriteAllText(PassedMarkerFile, DateTime.Now.ToString("o"));
        });
    }

    // ── Reminder position ─────────────────────────────────────────────────

    public static void ShowReminderIfRequested()
    {
        if (!IsRequested(ShowReminderVariable)) return;
        App.Notifications?.SendReminder("Smoke test reminder");

        var timer = new DispatcherTimer { Interval = LogDelay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            LogReminderPosition();
        };
        timer.Start();
    }

    private static void LogReminderPosition()
    {
        if (App.Instance.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return;
        foreach (var toast in desktop.Windows.OfType<Dialogs.ReminderToastWindow>())
        {
            var screen = toast.Screens.ScreenFromWindow(toast) ?? toast.Screens.Primary;
            CrashLog.Write("Smoke test: reminder position",
                $"position={toast.Position} size={toast.Bounds.Size} desktopScaling={toast.DesktopScaling} " +
                $"renderScaling={toast.RenderScaling} screenScaling={screen?.Scaling} workingArea={screen?.WorkingArea}");
        }
    }
}

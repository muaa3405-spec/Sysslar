using Avalonia.Controls;
using Tasky.Dialogs;
using Tasky.Helpers;
using Tasky.Services;
using Tasky.Services.Migration;

namespace Tasky;

/// <summary>
/// Smoke test: opens every popup window once at each zoom it is likely to run at, then closes it.
/// Dialogs wrap their content for zoom only when the zoom is not 100 % (Windows starts at 90 %,
/// macOS at 100 %), so testing both catches a dialog that only breaks on one of the two systems.
/// </summary>
public partial class MainWindow
{
    private static readonly double[] SmokeDialogZooms = [0.9, TextSizes.Normal, 1.2];
    private static readonly TimeSpan SmokeDialogDelay = TimeSpan.FromMilliseconds(400);

    private async Task SmokeTestDialogsAsync()
    {
        var settings = App.Settings.Current;
        double? savedZoom = settings.Zoom;
        try
        {
            foreach (double zoom in SmokeDialogZooms)
            {
                settings.Zoom = zoom;
                foreach (var (name, create) in SmokeDialogs())
                    await OpenAndCloseForSmokeTestAsync($"{name} at {TextSizes.Label(zoom)}", create);
            }
        }
        finally
        {
            settings.Zoom = savedZoom;
        }
    }

    /// <summary>Every popup window, with sample data where it needs a task.</summary>
    private IEnumerable<(string Name, Func<Window> Create)> SmokeDialogs()
    {
        yield return ("Workspaces", () => new WorkspacesDialog(startWithNew: false));
        yield return ("New workspace", () => new WorkspacesDialog(startWithNew: true));
        yield return ("Manage groups", () => new ManageGroupsDialog(Vm));
        yield return ("Confirm", () => new ConfirmDialog("Smoke test", "Sample message", "OK", ConfirmType.Info));
        yield return ("Good to know", () => new GoodToKnowDialog(showTips: true));
        yield return ("Shortcuts", () => new GoodToKnowDialog(showTips: false));
        yield return ("Manual time", () => new ManualTimeDialog());
        yield return ("Quick add", () => new Windows.QuickAddWindow(Vm));
        if (App.Database is { } db)
            yield return ("Migrate", () => new MigrateDialog(new MigrationService(db)));

        if (Vm.VisibleTasks.FirstOrDefault() is not { } task) yield break;
        yield return ("Task details", () => new TaskDetailDialog(task, Vm));
        yield return ("Task completed", () => new TaskCompletedDialog(task));
        yield return ("Attachments", () => new AttachmentDialog(task.Id, task.Title, Vm));
        if (App.Database is { } database)
            yield return ("Reminder", () => new ReminderDialog(task.Id, task.Title, database));
    }

    /// <summary>A failure is logged as a fatal error, which fails the smoke test.</summary>
    private async Task OpenAndCloseForSmokeTestAsync(string name, Func<Window> create)
    {
        try
        {
            var dialog = create();
            dialog.Show(this);
            await Task.Delay(SmokeDialogDelay);
            dialog.Close();
        }
        catch (Exception ex)
        {
            CrashLog.Write($"Fatal error: the {name} dialog failed to open in the smoke test", ex);
        }
    }
}

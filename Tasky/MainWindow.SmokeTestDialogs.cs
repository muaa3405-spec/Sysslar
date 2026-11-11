using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Tasky.Dialogs;
using Tasky.Helpers;
using Tasky.Services;
using Tasky.Services.Migration;

namespace Tasky;

/// <summary>
/// Smoke test: opens every popup window at each zoom it is likely to run at, with the main window at its
/// usual and at its smallest size, checks that it fits inside the main window and sits in its centre,
/// then closes it. Dialogs wrap their content for zoom only when the zoom is not 100 % (Windows starts
/// at 90 %, macOS at 100 %), so testing several zooms catches a dialog that only breaks on one system.
/// </summary>
public partial class MainWindow
{
    private static readonly double[] SmokeDialogZooms = [0.9, TextSizes.Normal, 1.2];
    private static readonly TimeSpan SmokeDialogDelay = TimeSpan.FromMilliseconds(400);

    /// <summary>How far (device-independent units) a popup's centre may be from the main window's.</summary>
    private const double SmokeCentreTolerance = 3;

    private async Task SmokeTestDialogsAsync(string? snapshotDir)
    {
        var settings = App.Settings.Current;
        double? savedZoom = settings.Zoom;
        var savedSize = new Size(Width, Height);
        try
        {
            foreach (var (label, size) in new[] { ("usual", savedSize), ("smallest", new Size(MinWidth, MinHeight)) })
            {
                Width = size.Width;
                Height = size.Height;
                await Task.Delay(SmokeDialogDelay);
                foreach (double zoom in SmokeDialogZooms)
                {
                    settings.Zoom = zoom;
                    foreach (var (name, create) in SmokeDialogs())
                        await OpenAndCloseForSmokeTestAsync($"{name} at {TextSizes.Label(zoom)}, {label} window", create, snapshotDir);
                }
            }
        }
        finally
        {
            settings.Zoom = savedZoom;
            Width = savedSize.Width;
            Height = savedSize.Height;
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
        {
            yield return ("Migrate", () => new MigrateDialog(new MigrationService(db)));
            yield return ("Migrate export", () =>
            {
                var migrate = new MigrateDialog(new MigrationService(db));
                migrate.ShowExportStep();
                return migrate;
            });
        }

        if (Vm.VisibleTasks.FirstOrDefault() is not { } task) yield break;
        yield return ("Task details", () => new TaskDetailDialog(task, Vm));
        yield return ("Task completed", () => new TaskCompletedDialog(task));
        yield return ("Attachments", () => new AttachmentDialog(task.Id, task.Title, Vm));
        if (App.Database is { } database)
            yield return ("Reminder", () => new ReminderDialog(task.Id, task.Title, database));
    }

    /// <summary>A failure is logged as a fatal error, which fails the smoke test.</summary>
    private async Task OpenAndCloseForSmokeTestAsync(string name, Func<Window> create, string? snapshotDir)
    {
        try
        {
            var dialog = create();
            dialog.Show(this);
            await Task.Delay(SmokeDialogDelay);
            string? problem = PopupPlacementProblem(dialog);
            if (snapshotDir is not null) SavePopupSnapshot(dialog, snapshotDir, name);
            dialog.Close();
            if (problem is not null)
                CrashLog.Write($"Fatal error: the {name} dialog is misplaced in the smoke test", problem);
        }
        catch (Exception ex)
        {
            CrashLog.Write($"Fatal error: the {name} dialog failed to open in the smoke test", ex);
        }
    }

    /// <summary>Null when the popup lies inside the main window and in its centre; otherwise what is wrong.</summary>
    private string? PopupPlacementProblem(Window popup)
    {
        var app = ScreenRect(this);
        var box = ScreenRect(popup);
        if (!app.Contains(box))
            return $"popup {box} reaches outside the main window {app}";
        if (Math.Abs(box.Center.X - app.Center.X) > SmokeCentreTolerance || Math.Abs(box.Center.Y - app.Center.Y) > SmokeCentreTolerance)
            return $"popup {box} is not centred on the main window {app}";
        return null;
    }

    /// <summary>A window's place on the desktop in device-independent units (rounded to whole units).</summary>
    private static Rect ScreenRect(Window window)
    {
        double s = window.DesktopScaling;
        var size = window.FrameSize ?? window.Bounds.Size;   // with the title bar, where there is one
        return new Rect(Math.Round(window.Position.X / s), Math.Round(window.Position.Y / s),
                        Math.Round(size.Width), Math.Round(size.Height));
    }

    private static void SavePopupSnapshot(Window popup, string folder, string name)
    {
        try
        {
            Directory.CreateDirectory(folder);
            var size = new PixelSize((int)popup.Bounds.Width, (int)popup.Bounds.Height);
            using var bitmap = new RenderTargetBitmap(size);
            bitmap.Render(popup);
            bitmap.Save(Path.Combine(folder, "Popup-" + string.Concat(name.Split(Path.GetInvalidFileNameChars())).Replace(' ', '-') + ".png"));
        }
        catch (Exception ex)
        {
            CrashLog.Write($"Popup snapshot {name}", ex);
        }
    }
}

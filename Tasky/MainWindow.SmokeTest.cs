using System.IO;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Tasky;

/// <summary>CI smoke test hook (see <see cref="Services.SmokeTest"/>).</summary>
public partial class MainWindow
{
    private static readonly TimeSpan SmokePageDelay = TimeSpan.FromSeconds(1.5);

    /// <summary>
    /// Optional folder for design snapshots: when set (with the smoke test), every page is saved as a
    /// PNG at the default and the minimum window width, so layout can be reviewed without clicking around.
    /// </summary>
    private const string SnapshotDirVariable = "TASKY_SNAPSHOT_DIR";

    /// <summary>Opens every page in turn (so each one is laid out with real data), then the task list again.</summary>
    public void ShowEveryPageForSmokeTest(Action onFinished)
    {
        string? snapshotDir = Environment.GetEnvironmentVariable(SnapshotDirVariable) is { Length: > 0 } dir ? dir : null;
        double[] widths = snapshotDir is null ? [Width] : [Width, MinWidth];

        var steps = new List<(double Width, AppPage Page, bool Dark)>();
        bool startDark = App.Instance.IsDarkTheme;
        foreach (bool dark in snapshotDir is null ? [startDark] : new[] { true, false })
            foreach (double width in widths)
                foreach (var page in Enum.GetValues<AppPage>().Append(AppPage.Main))
                    steps.Add((width, page, dark));

        int next = 0;
        (double Width, AppPage Page, bool Dark)? shown = null;
        var timer = new DispatcherTimer { Interval = SmokePageDelay };
        timer.Tick += (_, _) =>
        {
            if (snapshotDir is not null && shown is { } last)
            {
                string name = $"{last.Page}-{(int)last.Width}-{(last.Dark ? "dark" : "light")}";
                SaveSnapshot(snapshotDir, name);
                // Long pages: also the bottom
                if (last.Page == AppPage.Settings &&
                    SettingsPage.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault() is { } scroller)
                {
                    scroller.ScrollToEnd();
                    scroller.UpdateLayout();
                    SaveSnapshot(snapshotDir, name + "-bottom");
                    scroller.ScrollToHome();
                }
            }

            if (next < steps.Count)
            {
                var step = steps[next++];
                if (Math.Abs(Width - step.Width) > 0.5) Width = step.Width;
                if (step.Dark != App.Instance.IsDarkTheme) App.Instance.ApplyTheme(step.Dark);
                ShowPageForSmokeTest(step.Page);
                shown = step;
                return;
            }
            timer.Stop();
            if (App.Instance.IsDarkTheme != startDark) App.Instance.ApplyTheme(startDark);
            _ = SmokeTestDialogsThenWorkspacesAsync(snapshotDir, onFinished);
        };
        timer.Start();
    }

    private async Task SmokeTestDialogsThenWorkspacesAsync(string? snapshotDir, Action onFinished)
    {
        await SmokeTestDialogsAsync();
        await SmokeTestWorkspaceSwitchAsync(snapshotDir, onFinished);
    }

    /// <summary>
    /// Switches to a new, empty workspace and back without restarting, then removes it again.
    /// A failure is logged as a fatal error, which fails the smoke test.
    /// </summary>
    private async Task SmokeTestWorkspaceSwitchAsync(string? snapshotDir, Action onFinished)
    {
        string home = Services.Workspaces.Active.Id;
        int homeTasks = Vm.Tasks.Count;
        try
        {
            var temp = Services.Workspaces.Create("Smoke switch", "#8B5CF6");
            await SwitchWorkspaceInPlaceAsync(temp);
            if (Services.Workspaces.Active.Id != temp.Id || Vm.Tasks.Count != 0)
                throw new InvalidOperationException($"Expected the empty workspace, got {Services.Workspaces.Active.Id} with {Vm.Tasks.Count} tasks");
            if (snapshotDir is not null) SaveSnapshot(snapshotDir, "Switch-empty");

            await SwitchWorkspaceInPlaceAsync(Services.Workspaces.Find(home)!);
            if (Services.Workspaces.Active.Id != home || Vm.Tasks.Count != homeTasks)
                throw new InvalidOperationException($"Expected {home} with {homeTasks} tasks back, got {Services.Workspaces.Active.Id} with {Vm.Tasks.Count}");
            if (snapshotDir is not null) SaveSnapshot(snapshotDir, "Switch-back");
            Services.Workspaces.Delete(temp.Id);
            if (Directory.Exists(Services.AppPaths.DataFolderFor(temp.Id)))
                throw new InvalidOperationException("The deleted workspace's data folder is still there (a file was left open)");
        }
        catch (Exception ex)
        {
            Services.CrashLog.Write("Fatal error: switching workspace without a restart failed in the smoke test", ex);
        }
        onFinished();
    }

    private void ShowPageForSmokeTest(AppPage page)
    {
        // The edit page needs a task: open the first one (and leave without saving on the next step)
        if (page == AppPage.EditTask)
        {
            if (Vm.VisibleTasks.FirstOrDefault() is { } task) _ = EditTaskAsync(task);
            return;
        }
        if (page == AppPage.NoteDetail)
        {
            Vm.LoadNotes();
            if (Vm.FilteredNotes.FirstOrDefault() is { } note) OpenNotePage(note);
            return;
        }
        NavigateTo(page);
    }

    private void SaveSnapshot(string folder, string name)
    {
        try
        {
            Directory.CreateDirectory(folder);
            const double scale = 2;
            var size = new PixelSize((int)(Bounds.Width * scale), (int)(Bounds.Height * scale));
            using var bitmap = new RenderTargetBitmap(size, new Vector(96 * scale, 96 * scale));
            bitmap.Render(this);
            bitmap.Save(Path.Combine(folder, name + ".png"));
        }
        catch (Exception ex)
        {
            Services.CrashLog.Write($"Snapshot {name}", ex);
        }
    }
}

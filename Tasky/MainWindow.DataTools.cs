using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Tasky.Dialogs;
using Tasky.Helpers;
using Tasky.Services;
using Tasky.Services.DataExchange;
using Tasky.Services.Migration;
using Tasky.ViewModels;

namespace Tasky;

/// <summary>Settings → text size, backups, export / import, printing and update checks.</summary>
public partial class MainWindow
{
    private static readonly FilePickerFileType CsvFiles = new("CSV file")
    {
        Patterns = ["*.csv"], AppleUniformTypeIdentifiers = ["public.comma-separated-values-text"], MimeTypes = ["text/csv"]
    };

    private static readonly FilePickerFileType MarkdownFiles = new("Markdown file")
    {
        Patterns = ["*.md"], AppleUniformTypeIdentifiers = ["net.daringfireball.markdown"], MimeTypes = ["text/markdown"]
    };

    private UpdateCheckService.UpdateInfo? _availableUpdate;

    /// <summary>Called by <see cref="LoadSettingsPage"/>.</summary>
    private void LoadDataToolsSettings()
    {
        var scale = TextSizes.Clamp(App.Settings.Current.TextScale);
        TextSizeNormal.IsChecked = scale == TextSizes.Normal;
        TextSizeLarge.IsChecked  = scale == TextSizes.Large;
        TextSizeLarger.IsChecked = scale == TextSizes.Larger;

        CheckUpdatesToggle.IsChecked = App.Settings.Current.CheckForUpdates;
        ShowUpdateStatus();
        RefreshBackupList();
    }

    // ── Text size ────────────────────────────────────────────────────────

    private const double BaseMinWidth  = 444;
    private const double BaseMinHeight = 421;

    private void TextSize_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as RadioButton)?.Tag is not double scale) return;
        App.Settings.Current.TextScale = scale;
        App.Settings.Save();
        ApplyTextScale(scale);
    }

    /// <summary>Zooms the whole window so text, icons and spacing grow together.</summary>
    private void ApplyTextScale(double scale)
    {
        scale = TextSizes.Clamp(scale);
        ZoomHost.LayoutTransform = scale == TextSizes.Normal ? null : new ScaleTransform(scale, scale);
        MinWidth  = BaseMinWidth  * scale;
        MinHeight = BaseMinHeight * scale;
        if (Width  < MinWidth)  Width  = MinWidth;
        if (Height < MinHeight) Height = MinHeight;
    }

    private async void ShowWelcomeTips_Click(object? sender, RoutedEventArgs e)
        => await ShowModalDialog(new GoodToKnowDialog(showTips: true));

    // ── Backups ──────────────────────────────────────────────────────────

    private void RefreshBackupList()
    {
        var backups = BackupService.List();
        BackupPicker.ItemsSource       = backups;
        BackupPicker.DisplayMemberBinding = new Avalonia.Data.Binding(nameof(BackupService.BackupInfo.Label));
        BackupPicker.SelectedIndex     = backups.Count > 0 ? 0 : -1;
        BackupStatusText.Text = backups.Count == 0
            ? $"Sysslar saves a copy of your tasks and settings every day and keeps the last {BackupService.KeepCount}."
            : $"Every day, the last {BackupService.KeepCount} are kept. Newest: {backups[0].Label}. " +
              "Attachment files stay in place and are not copied.";
    }

    private async void BackUpNow_Click(object? sender, RoutedEventArgs e)
    {
        if (App.Backups is not { } backups) return;
        try
        {
            await Task.Run(backups.BackUpNow);
            RefreshBackupList();
            InAppNotificationService.Success("Backup saved.");
        }
        catch (Exception ex)
        {
            CrashLog.Write("Manual backup failed", ex);
            InAppNotificationService.Error("The backup couldn't be saved.");
        }
    }

    private void OpenBackupFolder_Click(object? sender, RoutedEventArgs e) => FileLauncher.OpenFolder(AppPaths.BackupsFolder);

    private async void RestoreBackup_Click(object? sender, RoutedEventArgs e)
    {
        if (BackupPicker.SelectedItem is not BackupService.BackupInfo backup) return;
        var confirm = new ConfirmDialog("Restore Backup",
            $"Put back your tasks, notes and settings as they were on {backup.Label}?\n\n" +
            "Sysslar restarts to do this. Your current data is kept as a safety copy first.",
            "Restore and restart", ConfirmType.Warning);
        if (await ShowModalDialog(confirm) != true) return;

        try
        {
            BackupService.StageRestore(backup);
            MigrationService.RestartTasky();
        }
        catch (Exception ex)
        {
            CrashLog.Write("Staging a backup restore failed", ex);
            InAppNotificationService.Error("That backup couldn't be opened, so nothing was changed.");
        }
    }

    // ── Export / import ──────────────────────────────────────────────────

    private async void ExportCsv_Click(object? sender, RoutedEventArgs e)
        => await ExportAsync("Export tasks", $"Sysslar-tasks-{DateTime.Today:yyyy-MM-dd}.csv", CsvFiles, ".csv",
                             () => TaskCsvExporter.ToCsv(Vm.ExportAllTasks()));

    private async void ExportMarkdown_Click(object? sender, RoutedEventArgs e)
        => await ExportAsync("Export tasks and notes", $"Sysslar-{DateTime.Today:yyyy-MM-dd}.md", MarkdownFiles, ".md",
                             () => MarkdownExporter.ToMarkdown(Vm.ExportAllTasks(), [.. Vm.Notes], DateTime.Now));

    private async Task ExportAsync(string title, string suggestedName, FilePickerFileType type, string extension, Func<string> build)
    {
        var path = await FileDialogs.SaveFileAsync(this, title, suggestedName, type, extension);
        if (path is null) return;
        try
        {
            await File.WriteAllTextAsync(path, build(), CsvFormat.FileEncoding);
            InAppNotificationService.Success($"Saved {Path.GetFileName(path)}.");
        }
        catch (Exception ex)
        {
            CrashLog.Write($"Export to '{path}' failed", ex);
            InAppNotificationService.Error($"Couldn't save {Path.GetFileName(path)}.");
        }
    }

    private async void ImportCsv_Click(object? sender, RoutedEventArgs e)
    {
        var files = await FileDialogs.PickFilesAsync(this, "Import tasks from a CSV file", [CsvFiles, FileDialogs.AllFiles], allowMultiple: false);
        if (files.Count == 0) return;

        TaskCsvImporter.Result parsed;
        try
        {
            parsed = TaskCsvImporter.Parse(await File.ReadAllTextAsync(files[0]));
        }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
        {
            InAppNotificationService.Error(ex is FormatException ? ex.Message : "That file couldn't be read.");
            return;
        }
        if (parsed.Tasks.Count == 0)
        {
            InAppNotificationService.Warning("No tasks were found in that file.");
            return;
        }

        int done = parsed.Tasks.Count(t => t.IsCompleted);
        var confirm = new ConfirmDialog("Import Tasks",
            $"Add {parsed.Tasks.Count} task(s) from {Path.GetFileName(files[0])}?" +
            (done > 0 ? $"\n\n{done} of them are already done and go to Completed Tasks." : string.Empty) +
            (parsed.SkippedRows > 0 ? $"\n{parsed.SkippedRows} row(s) without a title are skipped." : string.Empty) +
            "\n\nYour existing tasks are kept.",
            "Import", ConfirmType.Info);
        if (await ShowModalDialog(confirm) != true) return;

        int newGroups = Vm.ImportTasks(parsed.Tasks);
        BuildGroupChips();
        PopulateNewTaskGroupPicker();
        InAppNotificationService.Success($"Imported {parsed.Tasks.Count} task(s)" +
                                         (newGroups > 0 ? $" and {newGroups} new group(s)." : "."));
    }

    // ── Print ────────────────────────────────────────────────────────────

    private void Print_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not TaskFilter filter) return;
        var listName = TaskDateFilters.Label(filter);
        try
        {
            Directory.CreateDirectory(AppPaths.TempFolder);
            var page = Path.Combine(AppPaths.TempFolder, $"Sysslar-print-{DateTime.Now:yyyyMMdd-HHmmss}.html");
            File.WriteAllText(page, PrintableTaskList.ToHtml(listName, Vm.OpenTasksIn(filter), DateTime.Now));
            FileLauncher.Open(page);
        }
        catch (Exception ex)
        {
            CrashLog.Write("Creating the printable list failed", ex);
            InAppNotificationService.Error("The printable list couldn't be created.");
        }
    }

    // ── Updates ──────────────────────────────────────────────────────────

    private void CheckUpdatesToggle_Click(object? sender, RoutedEventArgs e)
    {
        App.Settings.Current.CheckForUpdates = CheckUpdatesToggle.IsChecked == true;
        App.Settings.Save();
        if (App.Settings.Current.CheckForUpdates) _ = CheckForUpdatesAsync(userAsked: false);
    }

    private async void CheckUpdatesNow_Click(object? sender, RoutedEventArgs e) => await CheckForUpdatesAsync(userAsked: true);

    private void DownloadUpdate_Click(object? sender, RoutedEventArgs e)
        => FileLauncher.OpenUrl(_availableUpdate?.PageUrl ?? AppInfo.ReleasesPageUrl);

    /// <summary>At start-up: checks once a day, and only if the user turned checks on.</summary>
    public async Task CheckForUpdatesIfDueAsync()
    {
        if (UpdateCheckService.IsAutomaticCheckDue(App.Settings.Current)) await CheckForUpdatesAsync(userAsked: false);
    }

    private async Task CheckForUpdatesAsync(bool userAsked)
    {
        UpdateStatusText.Text = "Checking…";
        try
        {
            _availableUpdate = await new UpdateCheckService().CheckAsync();
            App.Settings.Current.LastUpdateCheck = DateTime.Now;
            App.Settings.Save();
            ShowUpdateStatus();
            if (_availableUpdate is { } update)
                InAppNotificationService.Info($"Sysslar {update.Version.ToString(3)} is available. Settings → Updates → Download.");
            else if (userAsked)
                InAppNotificationService.Success($"You have the latest version ({AppInfo.VersionText}).");
        }
        catch (Exception ex)
        {
            CrashLog.Write("Update check failed", ex);
            UpdateStatusText.Text = "Couldn't reach GitHub. Check your internet connection.";
            if (userAsked) InAppNotificationService.Warning("Couldn't check for updates right now.");
        }
    }

    private void ShowUpdateStatus()
    {
        DownloadUpdateBtn.IsVisible = _availableUpdate is not null;
        var lastCheck = App.Settings.Current.LastUpdateCheck;
        UpdateStatusText.Text = _availableUpdate is { } update
            ? $"Sysslar {update.Version.ToString(3)} is available (you have {AppInfo.VersionText})."
            : lastCheck == DateTime.MinValue
                ? $"You have version {AppInfo.VersionText}."
                : $"You have version {AppInfo.VersionText}. Last checked {lastCheck:d MMM, HH:mm}.";
    }
}

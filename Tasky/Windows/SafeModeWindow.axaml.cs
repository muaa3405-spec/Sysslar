using Avalonia.Controls;
using Avalonia.Interactivity;
using Tasky.Helpers;
using Tasky.Services;
using Tasky.Services.Migration;

namespace Tasky.Windows;

/// <summary>
/// Explains a failed start instead of Tasky silently closing, and offers the ways out:
/// restore the newest automatic backup, look at the files, or fetch a fixed version.
/// </summary>
public partial class SafeModeWindow : Window
{
    private readonly BackupService.BackupInfo? _latestBackup;

    /// <summary>For the XAML loader / previewer.</summary>
    public SafeModeWindow() : this(new Exception("Preview")) { }

    public SafeModeWindow(Exception error)
    {
        InitializeComponent();
        _latestBackup = SafeLatestBackup();

        DataFolderText.Text = AppPaths.DataFolder;
        ErrorText.Text      = error.GetBaseException().Message;

        RestoreButton.IsEnabled = _latestBackup is not null;
        RestoreButton.Content   = _latestBackup is { } backup
            ? $"Restore the backup from {backup.Label} and restart"
            : "No automatic backup yet";
        BackupHint.Text = _latestBackup is null
            ? "If this keeps happening, install the latest version of Sysslar. Your data stays where it is."
            : "Restoring puts back your tasks and settings as they were at that time. Today's data is kept as a safety copy.";
    }

    private static BackupService.BackupInfo? SafeLatestBackup()
    {
        try { return BackupService.Latest; }
        catch { return null; }
    }

    private void Restore_Click(object? sender, RoutedEventArgs e)
    {
        if (_latestBackup is null) return;
        try
        {
            BackupService.StageRestore(_latestBackup);
            MigrationService.RestartTasky();
        }
        catch (Exception ex)
        {
            CrashLog.Write("Safe mode: restoring the latest backup failed", ex);
            BackupHint.Text = $"That backup couldn't be used ({ex.GetBaseException().Message}). Try opening the data folder instead.";
        }
    }

    private void OpenDataFolder_Click(object? sender, RoutedEventArgs e) => FileLauncher.OpenFolder(AppPaths.DataFolder);
    private void OpenLog_Click(object? sender, RoutedEventArgs e)        => FileLauncher.Open(CrashLog.CurrentFile);
    private void Download_Click(object? sender, RoutedEventArgs e)       => FileLauncher.OpenUrl(AppInfo.ReleasesPageUrl);
    private void Quit_Click(object? sender, RoutedEventArgs e)           => App.Shutdown();
}

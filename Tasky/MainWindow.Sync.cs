using Avalonia.Interactivity;
using Tasky.Dialogs;
using Tasky.Services.Sync;

namespace Tasky;

/// <summary>Settings → Sync, and the banner shown when sync needs the user.</summary>
public partial class MainWindow
{
    /// <summary>The banner about an unreachable folder waits this long: short offline gaps don't matter.</summary>
    private static readonly TimeSpan FolderMissingGrace = TimeSpan.FromHours(1);
    private DateTime? _folderMissingSince;

    private async void Sync_Click(object? sender, RoutedEventArgs e)
    {
        await ShowModalDialog(new SyncDialog());
        UpdateSyncIndicators();
    }

    private void UpdateSyncIndicators()
    {
        if (AppSync.Coordinator is not { } sync) return;

        SyncSubtitle.Text = sync.IsOn
            ? $"{SyncStatusText.Short(sync)} · {CloudFolders.ServiceOf(sync.Folder ?? string.Empty) ?? "your sync folder"}"
            : "Same tasks on all your computers";

        _folderMissingSince = sync.Status == SyncStatus.FolderMissing ? _folderMissingSince ?? DateTime.Now : null;
        if (sync.Status == SyncStatus.Paused)
        {
            var reason = sync.PausedReason ?? string.Empty;
            SyncBannerText.Text = reason.StartsWith(SyncCoordinator.PauseReasons.KeyChangedPrefix, StringComparison.Ordinal)
                ? $"Sync is paused: the encryption key was changed on {reason[SyncCoordinator.PauseReasons.KeyChangedPrefix.Length..]}. Your changes here are kept."
                : "Sync is paused: it needs your encryption key. Your changes here are kept.";
            SyncBannerButton.Content = "Enter new key";
            SyncBanner.IsVisible = true;
        }
        else if (_folderMissingSince is { } since && DateTime.Now - since > FolderMissingGrace)
        {
            SyncBannerText.Text = $"Can't reach the sync folder ({CloudFolders.ServiceOf(sync.Folder ?? string.Empty) ?? sync.Folder}). Your changes are saved here and will sync later.";
            SyncBannerButton.Content = "Open Sync";
            SyncBanner.IsVisible = true;
        }
        else
        {
            SyncBanner.IsVisible = false;
        }
    }
}

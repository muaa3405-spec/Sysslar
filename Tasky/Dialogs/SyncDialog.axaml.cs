using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Tasky.Helpers;
using Tasky.Platform;
using Tasky.Services;
using Tasky.Services.Sync;

namespace Tasky.Dialogs;

/// <summary>
/// Settings → Sync. Off: one button. Turning on: pick a folder; Sysslar then either shows a new key
/// (no sync files there yet) or asks for the key (another computer set it up). On: the folder, a
/// switch per workspace, the key, and the other computers.
/// </summary>
public partial class SyncDialog : Window
{
    private enum KeyMode { Join, NewKeyAfterChange, ChangeFolder }

    private readonly SyncCoordinator? _sync = AppSync.Coordinator;
    private string? _pickedFolder;
    private bool _changingFolder;
    private KeyMode _keyMode;
    private bool _busy;

    public SyncDialog()
    {
        InitializeComponent();
        ThemeHelper.Attach(this);
        OffSecurity.Child = SecurityNote();
        OnSecurity.Child  = SecurityNote();
        if (_sync is { IsOn: true }) _ = ShowOnAsync();
        else ShowStep(OffStep);
        AppSync.StatusChanged += OnStatusChanged;
        Closed += (_, _) => AppSync.StatusChanged -= OnStatusChanged;
    }

    private void ShowStep(Control step)
    {
        foreach (var panel in new Control[] { OffStep, FolderStep, NewKeyStep, EnterKeyStep, OnStep })
            panel.IsVisible = panel == step;
    }

    private void OnStatusChanged()
    {
        if (OnStep.IsVisible) RefreshStatus();
    }

    // ── What the user is told about security ─────────────────────────────

    private static Control SecurityNote()
    {
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(new TextBlock
        {
            Text = "Your synced data is locked before it leaves this computer.",
            FontSize = 13, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap,
            [!TextBlock.ForegroundProperty] = new DynamicResourceExtension("TextPrimary"),
        });
        foreach (var text in new[]
                 {
                     "Sysslar encrypts every sync file (AES-256) before saving it to the folder. If someone gets the files, " +
                     "even Google, Apple, Microsoft or Dropbox, they can't read your tasks or notes.",
                     "The encryption key is kept only on the computers where you turn on sync, protected by your account there. " +
                     "You can change it at any time: your other computers then pause syncing until you type the new key, and nothing is lost meanwhile.",
                 })
        {
            panel.Children.Add(new TextBlock
            {
                Text = text, TextWrapping = TextWrapping.Wrap,
                Theme = (Avalonia.Styling.ControlTheme)Application.Current!.FindResource("SubText")!,
            });
        }
        return panel;
    }

    // ── Turning on: where ────────────────────────────────────────────────

    private void TurnOn_Click(object? sender, RoutedEventArgs e)
    {
        _changingFolder = false;
        ShowFolderStep();
    }

    private void ChangeFolder_Click(object? sender, RoutedEventArgs e)
    {
        _changingFolder = true;
        ShowFolderStep();
    }

    private void ShowFolderStep()
    {
        FolderOptions.Children.Clear();
        _pickedFolder = null;
        FolderContinue.IsEnabled = false;
        FolderError.IsVisible = false;
        var found = CloudFolders.Detect();
        foreach (var folder in found) AddFolderOption(folder.Service, folder.Path, check: false);
        NoCloudFolders.IsVisible = found.Count == 0;
        if (FolderOptions.Children.FirstOrDefault() is RadioButton first) first.IsChecked = true;
        ShowStep(FolderStep);
    }

    private void AddFolderOption(string title, string path, bool check)
    {
        var option = new RadioButton
        {
            GroupName = "SyncFolder",
            Theme = (Avalonia.Styling.ControlTheme)this.FindResource("FolderOption")!,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Tag = path,
            Content = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeight.SemiBold,
                                    [!TextBlock.ForegroundProperty] = new DynamicResourceExtension("TextPrimary") },
                    new TextBlock { Text = path, TextTrimming = TextTrimming.CharacterEllipsis,
                                    Theme = (Avalonia.Styling.ControlTheme)Application.Current!.FindResource("SubText")! },
                },
            },
        };
        option.IsCheckedChanged += (_, _) =>
        {
            if (option.IsChecked != true) return;
            _pickedFolder = path;
            FolderContinue.IsEnabled = true;
            FolderError.IsVisible = false;
        };
        FolderOptions.Children.Add(option);
        if (check) option.IsChecked = true;
    }

    private async void ChooseFolder_Click(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose the folder for sync files",
            AllowMultiple = false,
        });
        if (folders.FirstOrDefault()?.TryGetLocalPath() is not { } path) return;
        AddFolderOption(CloudFolders.ServiceOf(path) ?? System.IO.Path.GetFileName(path.TrimEnd(System.IO.Path.DirectorySeparatorChar)), path, check: true);
        NoCloudFolders.IsVisible = false;
    }

    private void FolderContinue_Click(object? sender, RoutedEventArgs e)
    {
        if (_sync is null || _pickedFolder is not { } folder) return;
        var check = SyncCoordinator.CheckFolder(folder);
        if (check.Problem is { } problem) { ShowFolderError(problem); return; }
        try
        {
            if (check.HasSyncData)
            {
                ShowEnterKey(_changingFolder ? KeyMode.ChangeFolder : KeyMode.Join);
                return;
            }
            if (_changingFolder)
            {
                _sync.MoveToEmptyFolder(folder);
                AfterSetupChange();
                return;
            }
            var code = _sync.TurnOnWithNewKey(folder);
            ShowNewKey(code, "Your encryption key",
                "Sync is on. Type this key on your other computers to sync with this one. Save it in a password manager: " +
                "without it a new computer can't join, and nobody can recover it.");
            AppSync.Request(TimeSpan.FromSeconds(1));
        }
        catch (Exception ex)
        {
            CrashLog.Write("Turning on sync failed", ex);
            ShowFolderError("Sysslar couldn't write to this folder: " + ex.Message);
        }
    }

    private void ShowFolderError(string message)
    {
        FolderError.Text = message;
        FolderError.IsVisible = true;
    }

    private async void CancelSetup_Click(object? sender, RoutedEventArgs e)
    {
        if (_sync is { IsOn: true }) await ShowOnAsync();
        else ShowStep(OffStep);
    }

    // ── Keys ─────────────────────────────────────────────────────────────

    private void ShowNewKey(string code, string title, string intro)
    {
        NewKeyTitle.Text = title;
        NewKeyIntro.Text = intro;
        NewKeyText.Text  = code;
        CopiedText.IsVisible = false;
        ShowStep(NewKeyStep);
    }

    private void CopyKey_Click(object? sender, RoutedEventArgs e)
    {
        if (NewKeyText.Text is { } code && PlatformServices.Clipboard.SetText(code)) CopiedText.IsVisible = true;
    }

    private async void NewKeyDone_Click(object? sender, RoutedEventArgs e) => await ShowOnAsync();

    private void ShowEnterKey(KeyMode mode)
    {
        _keyMode = mode;
        KeyInput.Text = string.Empty;
        KeyError.IsVisible = false;
        if (mode == KeyMode.NewKeyAfterChange)
        {
            EnterKeyTitle.Text = "Enter the new encryption key";
            EnterKeyIntro.Text = PausedExplanation() + " Find the new key on that computer: Settings → Sync → Show key.";
        }
        else
        {
            EnterKeyTitle.Text = "Enter your encryption key";
            EnterKeyIntro.Text = "This folder already has Sysslar sync files. Find the key on any computer that syncs: Settings → Sync → Show key.";
        }
        ShowStep(EnterKeyStep);
        KeyInput.Focus();
    }

    private async void KeyContinue_Click(object? sender, RoutedEventArgs e)
    {
        if (_sync is null) return;
        string? error;
        try
        {
            error = _keyMode switch
            {
                KeyMode.NewKeyAfterChange => _sync.EnterNewKey(KeyInput.Text ?? string.Empty),
                _                         => _sync.TurnOnWithKey(_pickedFolder!, KeyInput.Text ?? string.Empty),
            };
        }
        catch (Exception ex)
        {
            CrashLog.Write("Entering the sync key failed", ex);
            error = "Sysslar couldn't save the key: " + ex.Message;
        }
        if (error is not null)
        {
            KeyError.Text = error;
            KeyError.IsVisible = true;
            return;
        }
        AfterSetupChange();
        await Task.CompletedTask;
    }

    private async void CancelKey_Click(object? sender, RoutedEventArgs e)
    {
        if (_keyMode == KeyMode.NewKeyAfterChange || _sync is { IsOn: true }) await ShowOnAsync();
        else ShowFolderStep();
    }

    private async void AfterSetupChange()
    {
        await ShowOnAsync();
        await SyncNowAndRefreshAsync();
    }

    private void ShowKey_Click(object? sender, RoutedEventArgs e)
    {
        if (_sync?.KeyCode is not { } code) return;
        ShowNewKey(code, "Your encryption key",
            "Type this key on another computer to sync it with this one (Settings → Sync → Turn on sync, then pick the same folder). " +
            "Anyone with this key and the sync files can read them, so keep it private.");
    }

    private async void ChangeKey_Click(object? sender, RoutedEventArgs e)
    {
        if (_sync is null || _busy) return;
        var confirm = new ConfirmDialog("Change the encryption key?",
            "Sysslar makes a new key and re-encrypts this computer's sync files with it.\n\n" +
            "Your other computers pause syncing until you type the new key on them. Nothing is lost meanwhile.\n\n" +
            "Use this if a computer was lost or the key was shared by mistake: the old key can't read anything after the change.",
            "Change key", ConfirmType.Warning);
        if (await confirm.ShowDialog<bool?>(this) != true) return;
        _busy = true;
        try
        {
            var code = await _sync.ChangeKeyAsync();
            ShowNewKey(code, "Your new encryption key",
                "Type it on each of your other computers: they show \"Sync paused\" until you do. Save it in a password manager.");
        }
        catch (Exception ex)
        {
            CrashLog.Write("Changing the sync key failed", ex);
            InAppNotificationService.Error("The key couldn't be changed: " + ex.Message);
        }
        finally { _busy = false; }
    }

    private void EnterNewKey_Click(object? sender, RoutedEventArgs e) => ShowEnterKey(KeyMode.NewKeyAfterChange);

    private string PausedExplanation()
    {
        var reason = _sync?.PausedReason ?? string.Empty;
        if (reason.StartsWith(SyncCoordinator.PauseReasons.KeyChangedPrefix, StringComparison.Ordinal))
            return $"The encryption key was changed on {reason[SyncCoordinator.PauseReasons.KeyChangedPrefix.Length..]}.";
        if (reason == SyncCoordinator.PauseReasons.KeyMissing)
            return "This computer's sync key is missing from its key store.";
        return "The sync folder now uses a different encryption key.";
    }

    // ── On ───────────────────────────────────────────────────────────────

    private async Task ShowOnAsync()
    {
        ShowStep(OnStep);
        RefreshStatus();
        await RefreshListsAsync();
    }

    private void RefreshStatus()
    {
        if (_sync is null) return;
        if (!_sync.IsOn) { ShowStep(OffStep); return; }
        StatusText.Text = SyncStatusText.Short(_sync);
        PausedCard.IsVisible = _sync.Status == SyncStatus.Paused;
        PausedText.Text = PausedExplanation() + " Sync is paused here until you enter the new key. Your changes on this computer are kept.";
        var folder = _sync.Folder ?? string.Empty;
        FolderName.Text = CloudFolders.ServiceOf(folder) ?? System.IO.Path.GetFileName(folder.TrimEnd(System.IO.Path.DirectorySeparatorChar));
        FolderPath.Text = folder;
    }

    private async Task RefreshListsAsync()
    {
        if (_sync is null) return;
        var rows = await _sync.ListWorkspacesAsync();
        WorkspaceRows.Children.Clear();
        foreach (var row in rows) WorkspaceRows.Children.Add(WorkspaceRow(row, rows));

        var devices = await _sync.ReadDevicesAsync();
        ComputerRows.Children.Clear();
        foreach (var device in devices.OrderByDescending(d => d.LastSeen)) ComputerRows.Children.Add(ComputerRow(device));
        ComputersSection.IsVisible = devices.Count > 0;
    }

    private Control WorkspaceRow(SyncWorkspaceRow row, IReadOnlyList<SyncWorkspaceRow> all)
    {
        string detail = row.LocalId is null
            ? $"On {string.Join(", ", row.OtherComputers)} only. Switch on to add it here."
            : row.Enabled
                ? (row.LastSync is { } last ? $"Synced {SyncStatusText.Ago(last)}" : "Waiting for the first sync")
                  + (row.OtherComputers.Count > 0 ? $" · also on {string.Join(", ", row.OtherComputers)}" : string.Empty)
                : "Not synced";

        var toggle = new ToggleButton
        {
            IsChecked = row.Enabled,
            Theme = (Avalonia.Styling.ControlTheme)Application.Current!.FindResource("ActiveToggleSwitch")!,
            VerticalAlignment = VerticalAlignment.Center,
            [ToolTip.TipProperty] = row.Enabled ? "Stop syncing this workspace" : "Sync this workspace",
            [Avalonia.Automation.AutomationProperties.NameProperty] = $"Sync {row.Name}",
        };
        toggle.Click += async (_, _) => await ToggleWorkspaceAsync(row, toggle.IsChecked == true, all);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(0, 6) };
        var dot = new Ellipse { Width = 10, Height = 10, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center,
                                Fill = Color.TryParse(row.Color, out var c) ? new SolidColorBrush(c) : Brushes.Gray };
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = row.Name, FontSize = 13, [!TextBlock.ForegroundProperty] = new DynamicResourceExtension("TextPrimary") });
        text.Children.Add(new TextBlock { Text = detail, TextWrapping = TextWrapping.Wrap,
                                          Theme = (Avalonia.Styling.ControlTheme)Application.Current!.FindResource("SubText")! });
        Grid.SetColumn(text, 1);
        Grid.SetColumn(toggle, 2);
        grid.Children.Add(dot);
        grid.Children.Add(text);
        grid.Children.Add(toggle);
        return grid;
    }

    private async Task ToggleWorkspaceAsync(SyncWorkspaceRow row, bool on, IReadOnlyList<SyncWorkspaceRow> all)
    {
        if (_sync is null) return;
        try
        {
            if (row.LocalId is null)
            {
                if (!on) return;
                _sync.AddWorkspaceFromOtherComputer(row.SyncId!, row.Name, row.Color);
                InAppNotificationService.Success($"\"{row.Name}\" was added. It fills in now; open it from the workspace menu next to the logo.");
            }
            else if (on)
            {
                // The same workspace name on another computer: offer to make them one
                var twin = all.FirstOrDefault(r => r.LocalId is null && string.Equals(r.Name, row.Name, StringComparison.OrdinalIgnoreCase));
                string? mergeWith = null;
                if (twin is not null)
                {
                    var ask = new ConfirmDialog($"Merge with \"{twin.Name}\" on {string.Join(", ", twin.OtherComputers)}?",
                        $"\"{twin.Name}\" is also synced from {string.Join(", ", twin.OtherComputers)}.\n\n" +
                        "Merge them: both keep everything, and from now on they stay the same.\n" +
                        "Not now: this workspace syncs on its own, next to the other one.",
                        "Merge them", ConfirmType.Info);
                    if (await ask.ShowDialog<bool?>(this) == true) mergeWith = twin.SyncId;
                }
                _sync.SetWorkspaceEnabled(row.LocalId, true, mergeWith);
            }
            else
            {
                _sync.SetWorkspaceEnabled(row.LocalId, false, null);
            }
        }
        catch (Exception ex)
        {
            CrashLog.Write("Changing which workspaces sync failed", ex);
            InAppNotificationService.Error("That didn't work: " + ex.Message);
        }
        await SyncNowAndRefreshAsync();
    }

    private Control ComputerRow(DeviceInfo device)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 6) };
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = device.Name, FontSize = 13, [!TextBlock.ForegroundProperty] = new DynamicResourceExtension("TextPrimary") });
        text.Children.Add(new TextBlock { Text = $"{device.Os} · last seen {SyncStatusText.Ago(device.LastSeen.ToLocalTime())}",
                                          Theme = (Avalonia.Styling.ControlTheme)Application.Current!.FindResource("SubText")! });
        var remove = new Button { Content = "Remove", Classes = { "small" }, VerticalAlignment = VerticalAlignment.Center,
                                  Theme = (Avalonia.Styling.ControlTheme)Application.Current!.FindResource("SecondaryButton")! };
        remove.Click += async (_, _) =>
        {
            var ask = new ConfirmDialog($"Remove {device.Name}?",
                $"{device.Name}'s sync files are removed from the folder. Nothing is deleted on this computer or on {device.Name}.\n\n" +
                "If it syncs again, it comes back. To lock a lost computer out for good, change the key instead.",
                "Remove", ConfirmType.Warning);
            if (await ask.ShowDialog<bool?>(this) != true || _sync is null) return;
            await _sync.RemoveComputerAsync(device.DeviceId);
            await RefreshListsAsync();
        };
        Grid.SetColumn(remove, 1);
        grid.Children.Add(text);
        grid.Children.Add(remove);
        return grid;
    }

    private async void SyncNow_Click(object? sender, RoutedEventArgs e) => await SyncNowAndRefreshAsync();

    private async Task SyncNowAndRefreshAsync()
    {
        SyncNowButton.IsEnabled = false;
        try { await AppSync.SyncNowAsync(); }
        finally { SyncNowButton.IsEnabled = true; }
        RefreshStatus();
        await RefreshListsAsync();
    }

    private async void TurnOff_Click(object? sender, RoutedEventArgs e)
    {
        if (_sync is null) return;
        var ask = new ConfirmDialog("Turn off sync on this computer?",
            "Nothing is deleted: your data stays here and on your other computers, and the sync files stay in the folder.\n\n" +
            "This computer forgets the key. To sync again later, turn it on and enter the key.",
            "Turn off", ConfirmType.Warning);
        if (await ask.ShowDialog<bool?>(this) != true) return;
        _sync.TurnOff();
        ShowStep(OffStep);
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close(true);

    /// <summary>Smoke test: every step of the dialog, and a real sync into the test folder when there is one.</summary>
    internal async Task RunSmokeTestAsync(Func<string, Task> snapshot)
    {
        if (_sync is null) throw new InvalidOperationException("Sync didn't start");
        await snapshot("Sync-off");
        ShowFolderStep();
        await snapshot("Sync-folder");
        if (_pickedFolder is null) return;   // no test folder: the steps above are all that can be shown

        FolderContinue_Click(this, new RoutedEventArgs());
        if (!_sync.IsOn) throw new InvalidOperationException("Turning sync on failed: " + FolderError.Text);
        if (SyncKey.TryParseCode(NewKeyText.Text) is null) throw new InvalidOperationException("The key shown can't be read back");
        await snapshot("Sync-key");
        await ShowOnAsync();
        await SyncNowAndRefreshAsync();
        if (_sync.Status != SyncStatus.UpToDate) throw new InvalidOperationException($"Sync ended as {_sync.Status}: {_sync.Problem}");
        await snapshot("Sync-on");
        _sync.TurnOff();
    }
}

/// <summary>Short sync status texts, shared by the Sync dialog, the Settings row and the banner.</summary>
public static class SyncStatusText
{
    public static string Short(SyncCoordinator sync) => sync.Status switch
    {
        SyncStatus.Off           => "Off",
        SyncStatus.Syncing       => "Syncing…",
        SyncStatus.Paused        => "Paused",
        SyncStatus.FolderMissing => "Can't reach the folder",
        SyncStatus.Problem       => "Couldn't sync everything",
        _ => sync.LastSync is { } last ? $"Synced {Ago(last)}" : "On",
    };

    public static string Ago(DateTime when)
    {
        var span = DateTime.Now - when;
        if (span < TimeSpan.FromMinutes(1)) return "just now";
        if (span < TimeSpan.FromHours(1))   return $"{(int)span.TotalMinutes} min ago";
        if (span < TimeSpan.FromDays(1))    return $"{(int)span.TotalHours} h ago";
        if (span < TimeSpan.FromDays(2))    return "yesterday";
        return when.ToString("MMM d");
    }
}

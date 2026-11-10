using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Tasky.Helpers;
using Tasky.Platform;
using Tasky.Services;
using Tasky.Services.Migration;
using Ellipse = Avalonia.Controls.Shapes.Ellipse;

namespace Tasky.Dialogs;

/// <summary>
/// "Migrate to another computer" wizard.
/// Export: password (+ optional recovery key) → choose location → encrypted file.
/// Import: choose file → password → review and pick the workspace → Tasky restarts in that
/// workspace with the imported data.
/// </summary>
public partial class MigrateDialog : Window
{
    private static readonly FilePickerFileType TextFileType = new("Text file") { Patterns = ["*.txt"] };

    private static readonly FilePickerFileType MigrationFileType = new("Sysslar migration file")
    {
        Patterns = ["*" + MigrationService.FileExtension]
    };

    /// <summary>The intro without "PC", shown on macOS.</summary>
    private const string MacIntroText =
        "Move everything to a new computer in one password-protected file: tasks, completed history, " +
        "groups, notes, reminders, attachments, reports and settings.";

    private readonly MigrationService _migration;
    private string? _importFile;
    private string? _exportedFile;
    /// <summary>The recovery key shown on the export step; only used when the box is ticked.</summary>
    private string? _recoveryKey;
    private bool _busy;
    private bool _importStaged;
    private bool _importConfirmed;

    /// <summary>A workspace in the "Import into" picker; a null Id is "New workspace…".</summary>
    private sealed record WorkspaceOption(string? Id, string Name, string Color);

    [Obsolete("XAML previewer only")]
    public MigrateDialog()
    {
        _migration = null!;
        InitializeComponent();
    }

    /// <param name="startWithImport">Skip the export / import choice and open the import step.</param>
    public MigrateDialog(MigrationService migration, bool startWithImport = false)
    {
        _migration = migration;
        InitializeComponent();
        // The confirm field must be typed, so a pasted typo can't slip through twice.
        ExportPasswordConfirm.AddHandler(TextBox.PastingFromClipboardEvent, BlockPaste, RoutingStrategies.Bubble);
        ThemeHelper.Attach(this);
        DialogLayoutHelper.FitToWorkArea(this);
        if (OperatingSystem.IsMacOS()) IntroText.Text = MacIntroText;
        if (startWithImport) ChooseImport_Click(this, new RoutedEventArgs());
    }

    // ── Navigation ────────────────────────────────────────────────────────

    private void ShowStep(Control step)
    {
        foreach (var panel in new Control[]
                 { ChooseStep, ExportStep, ImportStep, ConfirmImportStep, WorkingStep, DoneStep })
            panel.IsVisible = panel == step;
    }

    private void ShowWorking(string message)
    {
        WorkingLabel.Text = message;
        ShowStep(WorkingStep);
    }

    private void ChooseExport_Click(object? sender, RoutedEventArgs e)
    {
        try { ExportSummary.Text = _migration.CurrentCounts().Summary; }
        catch (Exception ex) { CrashLog.Write("Could not count records for export", ex); ExportSummary.Text = "All your Sysslar data"; }
        ExportPassword.Clear();
        ExportPasswordConfirm.Clear();
        ExportShowPassword.IsChecked = false;
        _recoveryKey = null;
        RecoveryKeyCheck.IsChecked   = false;
        ExportAcknowledge.IsChecked  = false;
        HideError(ExportError);
        UpdateExportPasswordState();
        ShowStep(ExportStep);
        ExportPassword.Focus();
    }

    private void ChooseImport_Click(object? sender, RoutedEventArgs e)
    {
        HideError(ImportError);
        ImportPassword.Clear();
        ImportShowPassword.IsChecked = false;
        UpdateImportState();
        ShowStep(ImportStep);
    }

    private void Back_Click(object? sender, RoutedEventArgs e) => ShowStep(ChooseStep);

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    // ── Export ────────────────────────────────────────────────────────────

    private void ExportPassword_Changed(object? sender, TextChangedEventArgs e) => UpdateExportPasswordState();

    private bool UsesRecoveryKey => RecoveryKeyCheck.IsChecked == true && _recoveryKey is not null;

    private void UpdateExportPasswordState()
    {
        // TextChanged fires while the XAML is still loading, before every field exists
        if (ExportButton is null || ExportPasswordHint is null || ExportAcknowledge is null
            || RecoveryKeyCheck is null || ExportWarning is null) return;
        var (ok, hint) = CheckNewPassword(ExportPassword.Text ?? string.Empty, ExportPasswordConfirm.Text ?? string.Empty);
        ExportPasswordHint.Text = hint;

        bool recovery = UsesRecoveryKey;
        RecoveryKeyPanel.IsVisible = recovery;
        RecoveryKeyText.Text       = recovery ? _recoveryKey : string.Empty;
        ExportWarning.Text = recovery
            ? "⚠  The file opens with the password or the recovery key. If both are lost, it can't be opened."
            : "⚠  Remember this password. The file is encrypted with it and cannot be opened without it — there is no way to recover a forgotten password.";
        ExportAcknowledgeText.Text = recovery
            ? "I've saved the recovery key somewhere safe."
            : "I've saved this password somewhere safe, and I understand it can't be recovered.";

        ExportButton.IsEnabled = ok && ExportAcknowledge.IsChecked == true;
    }

    private void ExportAcknowledge_Changed(object? sender, RoutedEventArgs e) => UpdateExportPasswordState();

    private void ExportShowPassword_Changed(object? sender, RoutedEventArgs e)
    {
        bool show = ExportShowPassword.IsChecked == true;
        ExportPassword.RevealPassword        = show;
        ExportPasswordConfirm.RevealPassword = show;
    }

    private void ImportShowPassword_Changed(object? sender, RoutedEventArgs e)
        => ImportPassword.RevealPassword = ImportShowPassword.IsChecked == true;

    private void BlockPaste(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        ExportPasswordHint.Text = "Type the password again here; pasting is turned off so a typo can't slip through.";
    }

    private void RecoveryKeyCheck_Changed(object? sender, RoutedEventArgs e)
    {
        // A new key each time the box is ticked, so a key that was shown and then turned off is never reused.
        _recoveryKey = RecoveryKeyCheck.IsChecked == true ? RecoveryKey.Generate() : null;
        // What the user acknowledged has changed.
        ExportAcknowledge.IsChecked = false;
        UpdateExportPasswordState();
    }

    private void CopyRecoveryKey_Click(object? sender, RoutedEventArgs e)
    {
        if (_recoveryKey is null) return;
        if (PlatformServices.Clipboard.SetText(_recoveryKey))
            InAppNotificationService.Success("Recovery key copied. Paste it somewhere safe.");
        else
            InAppNotificationService.Error("Couldn't copy the recovery key. Please try again.");
    }

    private async void SaveRecoveryKey_Click(object? sender, RoutedEventArgs e)
    {
        if (_recoveryKey is not { } key) return;
        var fileName = await FileDialogs.SaveFileAsync(this, "Save recovery key",
            $"Sysslar-recovery-key-{DateTime.Now:yyyy-MM-dd}.txt", TextFileType, ".txt");
        if (fileName is null) return;
        try
        {
            await File.WriteAllTextAsync(fileName,
                "Sysslar migration recovery key" + Environment.NewLine + Environment.NewLine +
                key + Environment.NewLine + Environment.NewLine +
                $"Created {DateTime.Now:MMMM d, yyyy 'at' HH:mm}." + Environment.NewLine +
                "If you forget the password of the migration file made at this time, type this key " +
                "in the password field when you import it." + Environment.NewLine +
                "Keep this file away from the migration file: anyone with both can open it." + Environment.NewLine);
            InAppNotificationService.Success("Recovery key saved.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CrashLog.Write("Could not save the recovery key", ex);
            InAppNotificationService.Error($"Couldn't save the recovery key: {ex.Message}");
        }
    }

    /// <summary>Rules for a new migration password, with a hint to show under the fields.</summary>
    private static (bool Ok, string Hint) CheckNewPassword(string password, string confirm)
    {
        if (password.Length < MigrationService.MinPasswordLength)
            return (false, $"Use at least {MigrationService.MinPasswordLength} characters.");
        if (confirm.Length == 0)
            return (false, "Type the password again to confirm it.");
        if (password != confirm)
            return (false, "The passwords don't match.");
        if (password != password.Trim())
            return (true, "⚠ Passwords match, but it starts or ends with a space. Make sure that's on purpose.");
        return (true, password.Length >= 14 ? "✔ Strong password." : "✔ Passwords match. Longer is stronger.");
    }

    private async void Export_Click(object? sender, RoutedEventArgs e)
    {
        var fileName = await FileDialogs.SaveFileAsync(this, "Save migration file",
            $"Sysslar-migration-{DateTime.Now:yyyy-MM-dd}{MigrationService.FileExtension}",
            MigrationFileType, MigrationService.FileExtension);
        if (fileName is null) return;

        var password    = ExportPassword.Text ?? string.Empty;
        var recoveryKey = UsesRecoveryKey ? _recoveryKey : null;
        App.ViewModel?.CheckpointRunningTasks();   // include the latest tracked time
        await RunBusy("Encrypting your data…", () => _migration.Export(fileName, password, recoveryKey),
            onSuccess: () =>
            {
                _exportedFile  = fileName;
                DoneLabel.Text = $"Saved to {fileName}";
                DoneRecoveryNote.IsVisible = recoveryKey is not null;
                ShowStep(DoneStep);
            },
            onError: message => { ShowStep(ExportStep); ShowError(ExportError, message); });
    }

    private void ShowInFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (_exportedFile is null) return;
        try { RevealInFileManager(_exportedFile); }
        catch (Exception ex) { CrashLog.Write(OperatingSystem.IsMacOS() ? "Could not open Finder" : "Could not open Explorer", ex); }
    }

    /// <summary>Opens Explorer (Windows) or Finder (macOS) with the file selected.</summary>
    private static void RevealInFileManager(string file)
    {
        if (OperatingSystem.IsMacOS())
            Process.Start(new ProcessStartInfo("open") { ArgumentList = { "-R", file } });
        else
            Process.Start("explorer.exe", $"/select,\"{file}\"");
    }

    // ── Import ────────────────────────────────────────────────────────────

    private async void BrowseImport_Click(object? sender, RoutedEventArgs e)
    {
        var files = await FileDialogs.PickFilesAsync(this, "Choose your migration file",
                                                     [MigrationFileType], allowMultiple: false);
        if (files.Count == 0) return;
        _importFile = files[0];
        ImportFileLabel.Text = Path.GetFileName(_importFile);
        ToolTip.SetTip(ImportFileLabel, _importFile);
        HideError(ImportError);
        UpdateImportState();
        ImportPassword.Focus();
    }

    private void ImportInput_Changed(object? sender, TextChangedEventArgs e) => UpdateImportState();

    private void UpdateImportState()
    {
        // TextChanged fires while the XAML is still loading, before every field exists
        if (UnlockButton is null) return;
        UnlockButton.IsEnabled = _importFile is not null && !string.IsNullOrEmpty(ImportPassword.Text);
    }

    private void ImportPassword_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && UnlockButton.IsEnabled) Unlock_Click(sender, e);
    }

    private async void Unlock_Click(object? sender, RoutedEventArgs e)
    {
        if (_importFile is null) return;
        var file = _importFile;
        var password = ImportPassword.Text ?? string.Empty;
        MigrationManifest? manifest = null;

        await RunBusy("Unlocking and checking the file…", () => manifest = _migration.StageImport(file, password),
            onSuccess: () =>
            {
                _importStaged = true;
                ShowImportReview(manifest!);
            },
            onError: message =>
            {
                ShowStep(ImportStep);
                ShowError(ImportError, message);
                ImportPassword.SelectAll();
                ImportPassword.Focus();
            });
    }

    private void ShowImportReview(MigrationManifest manifest)
    {
        ImportCreatedLabel.Text = $"Exported on {manifest.CreatedAt:MMMM d, yyyy 'at' HH:mm}";
        ImportSummary.Text      = manifest.Counts.Summary;
        PopulateWorkspacePicker();
        ShowStep(ConfirmImportStep);
    }

    // ── Which workspace receives the import ───────────────────────────────

    private void PopulateWorkspacePicker()
    {
        var options = Workspaces.All
            .Select(w => new WorkspaceOption(w.Id, w.IsActive ? $"{w.Name} (open now)" : w.Name, w.Color))
            .Append(new WorkspaceOption(null, "＋  New workspace…", NextFreeColor()))
            .ToList();
        WorkspacePicker.ItemTemplate = new FuncDataTemplate<WorkspaceOption>((option, _) => BuildWorkspaceContent(option));
        WorkspacePicker.ItemsSource  = options;
        WorkspacePicker.SelectedIndex = options.FindIndex(o => o.Id == Workspaces.Active.Id);
        NewWorkspaceName.Text = string.Empty;
        UpdateImportTarget();
    }

    private static Control BuildWorkspaceContent(WorkspaceOption? option)
    {
        if (option is null) return new TextBlock();
        var label = new TextBlock { Text = option.Name, VerticalAlignment = VerticalAlignment.Center };
        if (option.Id is null) return label;
        var dot = new Ellipse
        {
            Width = 10, Height = 10, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center,
            Fill = new SolidColorBrush(Color.Parse(option.Color)),
        };
        return new StackPanel { Orientation = Orientation.Horizontal, Children = { dot, label } };
    }

    /// <summary>A colour for a new workspace that no other workspace uses yet, if there is one.</summary>
    private static string NextFreeColor()
    {
        var used = Workspaces.All.Select(w => w.Color).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Workspaces.Palette.Select(p => p.Hex).FirstOrDefault(hex => !used.Contains(hex)) ?? Workspaces.Palette[1].Hex;
    }

    private WorkspaceOption? SelectedTarget => WorkspacePicker.SelectedItem as WorkspaceOption;

    private void WorkspacePicker_Changed(object? sender, SelectionChangedEventArgs e)
    {
        UpdateImportTarget();
        if (SelectedTarget is { Id: null }) NewWorkspaceName.Focus();
    }

    private void NewWorkspaceName_Changed(object? sender, TextChangedEventArgs e) => UpdateImportTarget();

    /// <summary>Shows what happens to the chosen workspace, and whether the import can go ahead.</summary>
    private void UpdateImportTarget()
    {
        // Events fire while the XAML is still loading, before every field exists
        if (ConfirmImportButton is null || NewWorkspacePanel is null || ReplaceWarning is null) return;
        if (SelectedTarget is not { } target) { ConfirmImportButton.IsEnabled = false; return; }

        if (target.Id is null)
        {
            NewWorkspacePanel.IsVisible = true;
            var name = (NewWorkspaceName.Text ?? string.Empty).Trim();
            string? problem = name.Length == 0 ? null
                : Workspaces.NameIsTaken(name) ? "Another workspace already has this name." : null;
            NewWorkspaceHint.Text = problem ?? string.Empty;
            NewWorkspaceHint.IsVisible = problem is not null;
            ConfirmImportButton.IsEnabled = name.Length > 0 && problem is null;
            ReplaceWarning.Text = "A new workspace is created with this data, and Sysslar restarts in it. " +
                                  "Your other workspaces are not changed.";
            return;
        }

        NewWorkspacePanel.IsVisible = false;
        ConfirmImportButton.IsEnabled = true;

        var workspace = Workspaces.Find(target.Id)!;
        MigrationCounts? current = null;
        try { current = MigrationService.CountsFor(workspace.Id); } catch { /* treat as empty */ }
        bool hasData = current is { } c && (c.OpenTasks + c.CompletedTasks + c.Notes) > 0;
        bool others  = Workspaces.HasSeveral;
        var where = others ? $"the \"{workspace.Name}\" workspace" : "this computer";
        ReplaceWarning.Text = hasData
            ? $"⚠  This replaces the Sysslar data in {where} ({current!.Summary}). " +
              $"A safety copy of it is kept in {AppPaths.ImportBackupsFolderFor(workspace.Id)}." +
              (others ? " Your other workspaces are not changed." : string.Empty)
            : others
                ? $"Sysslar will restart and open \"{workspace.Name}\" with this data. Your other workspaces are not changed."
                : "Sysslar will restart and open with this data.";
    }

    private void ConfirmImport_Click(object? sender, RoutedEventArgs e)
    {
        if (SelectedTarget is not { } target) return;

        string targetId;
        bool createdNew = false;
        try
        {
            if (target.Id is null)
            {
                var name = (NewWorkspaceName.Text ?? string.Empty).Trim();
                if (name.Length == 0 || Workspaces.NameIsTaken(name)) { UpdateImportTarget(); return; }
                targetId = Workspaces.Create(name, target.Color).Id;
                createdNew = true;
            }
            else targetId = target.Id;
        }
        catch (Exception ex)
        {
            CrashLog.Write("Could not create a workspace for the import", ex);
            InAppNotificationService.Error("Sysslar couldn't create the workspace. Details were written to the Sysslar log.");
            return;
        }

        bool otherWorkspace = targetId != Workspaces.Active.Id;
        try
        {
            _migration.ConfirmStagedImport(targetId);
            _importConfirmed = true;
            if (otherWorkspace) Workspaces.OpenOnNextStart(targetId);
            ShowWorking("Restarting Sysslar…");
            MigrationService.RestartTasky();
        }
        catch (Exception ex)
        {
            CrashLog.Write("Could not restart after import", ex);
            if (otherWorkspace)
            {
                // Don't leave the import waiting in a workspace the user isn't taken to.
                _importConfirmed = false;
                _importStaged = false;
                try { Workspaces.KeepActiveOnNextStart(); } catch { /* the list file is unwritable */ }
                try
                {
                    var pending = AppPaths.PendingImportFolderFor(targetId);
                    if (Directory.Exists(pending)) Directory.Delete(pending, recursive: true);
                    if (createdNew) Workspaces.Delete(targetId);
                }
                catch (Exception cleanup) { CrashLog.Write("Could not undo the import into another workspace", cleanup); }
                ShowStep(ChooseStep);
                InAppNotificationService.Error("Sysslar couldn't restart to finish the import. Please try again.");
                return;
            }
            ShowStep(ConfirmImportStep);
            InAppNotificationService.Warning("Close and reopen Sysslar to finish the import.");
        }
    }

    private void CancelImport_Click(object? sender, RoutedEventArgs e)
    {
        _migration.CancelStagedImport();
        _importStaged = false;
        ShowStep(ChooseStep);
    }

    // ── Shared ────────────────────────────────────────────────────────────

    /// <summary>Runs slow work off the UI thread with a progress screen; reports a friendly error.</summary>
    private async Task RunBusy(string message, Action work, Action onSuccess, Action<string> onError)
    {
        _busy = true;
        ShowWorking(message);
        try
        {
            await Task.Run(work);
            onSuccess();
        }
        catch (MigrationFileException ex)
        {
            onError(ex.Message);
        }
        catch (Exception ex)
        {
            CrashLog.Write("Migration failed", ex);
            onError(ex is IOException or UnauthorizedAccessException
                ? $"Couldn't read or write the file: {ex.Message}"
                : "Something went wrong. Details were written to the Sysslar log.");
        }
        finally
        {
            _busy = false;
        }
    }

    private static void ShowError(TextBlock target, string message)
    {
        target.Text = message;
        target.IsVisible = true;
    }

    private static void HideError(TextBlock target) => target.IsVisible = false;

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (_busy) { e.Cancel = true; return; }   // never abandon a half-written file
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        // Unlocked but not confirmed: throw away the decrypted copy.
        if (_importStaged && !_importConfirmed) _migration.CancelStagedImport();
        base.OnClosed(e);
    }
}

using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Tasky.Helpers;
using Tasky.Services;
using Tasky.Services.Migration;

namespace Tasky.Dialogs;

/// <summary>
/// "Migrate to another computer" wizard.
/// Export: password → choose location → encrypted file.
/// Import: choose file → password → review → Tasky restarts with the imported data.
/// </summary>
public partial class MigrateDialog : Window
{
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
    private bool _busy;
    private bool _importStaged;
    private bool _importConfirmed;

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
        HideError(ExportError);
        UpdateExportPasswordState();
        ShowStep(ExportStep);
        ExportPassword.Focus();
    }

    private void ChooseImport_Click(object? sender, RoutedEventArgs e)
    {
        HideError(ImportError);
        ImportPassword.Clear();
        UpdateImportState();
        ShowStep(ImportStep);
    }

    private void Back_Click(object? sender, RoutedEventArgs e) => ShowStep(ChooseStep);

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    // ── Export ────────────────────────────────────────────────────────────

    private void ExportPassword_Changed(object? sender, TextChangedEventArgs e) => UpdateExportPasswordState();

    private void UpdateExportPasswordState()
    {
        // TextChanged fires while the XAML is still loading, before every field exists
        if (ExportButton is null || ExportPasswordHint is null) return;
        var (ok, hint) = CheckNewPassword(ExportPassword.Text ?? string.Empty, ExportPasswordConfirm.Text ?? string.Empty);
        ExportButton.IsEnabled = ok;
        ExportPasswordHint.Text = hint;
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
        return (true, password.Length >= 14 ? "✔ Strong password." : "✔ Passwords match. Longer is stronger.");
    }

    private async void Export_Click(object? sender, RoutedEventArgs e)
    {
        var fileName = await FileDialogs.SaveFileAsync(this, "Save migration file",
            $"Sysslar-migration-{DateTime.Now:yyyy-MM-dd}{MigrationService.FileExtension}",
            MigrationFileType, MigrationService.FileExtension);
        if (fileName is null) return;

        var password = ExportPassword.Text ?? string.Empty;
        App.ViewModel?.CheckpointRunningTasks();   // include the latest tracked time
        await RunBusy("Encrypting your data…", () => _migration.Export(fileName, password),
            onSuccess: () =>
            {
                _exportedFile  = fileName;
                DoneLabel.Text = $"Saved to {fileName}";
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

        MigrationCounts? current = null;
        try { current = _migration.CurrentCounts(); } catch { /* treat as empty */ }
        bool hasData = current is { } c && (c.OpenTasks + c.CompletedTasks + c.Notes) > 0;
        // With several workspaces, say which one receives the data; the others are not touched.
        var where = Workspaces.HasSeveral ? $"the \"{Workspaces.Active.Name}\" workspace" : "this computer";
        ReplaceWarning.Text = hasData
            ? $"⚠  This replaces the Sysslar data in {where} ({current!.Summary}). " +
              $"A safety copy of it is kept in {AppPaths.ImportBackupsFolder}."
            : Workspaces.HasSeveral
                ? $"Sysslar will restart and open \"{Workspaces.Active.Name}\" with this data. Your other workspaces are not changed."
                : "Sysslar will restart and open with this data.";
        ShowStep(ConfirmImportStep);
    }

    private void ConfirmImport_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            _migration.ConfirmStagedImport();
            _importConfirmed = true;
            ShowWorking("Restarting Sysslar…");
            MigrationService.RestartTasky();
        }
        catch (Exception ex)
        {
            CrashLog.Write("Could not restart after import", ex);
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

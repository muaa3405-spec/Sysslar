using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Tasky.Behaviors;
using Tasky.Helpers;
using Tasky.Platform;
using Tasky.Services;
using Tasky.Services.Migration;
using Tasky.ViewModels;

namespace Tasky;

public partial class App : Application
{
    private static SingleInstanceService? _singleInstance;
    private static TrackingCheckpointService? _checkpoints;
    private static IUserActivity? _userActivity;

    public static App Instance => (App)Current!;

    public static MainViewModel? ViewModel { get; private set; }
    public static DatabaseService? Database { get; private set; }
    public static IGlobalHotkey? Hotkey { get; private set; }
    public static ReminderSchedulerService? ReminderScheduler { get; private set; }
    public static NotificationService? Notifications { get; private set; }
    public static IdleMonitorService? IdleMonitor { get; private set; }
    public static MeetingMonitorService? MeetingMonitor { get; private set; }
    public static AppUsageService? AppUsage { get; private set; }
    public static BackupService? Backups { get; private set; }
    public static SettingsService  Settings  { get; } = new();
    public static MainWindow? MainAppWindow { get; private set; }

    private IClassicDesktopStyleApplicationLifetime Desktop
        => (IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        var args = Desktop.Args ?? [];

        // Errors are logged to the Tasky logs folder and Tasky keeps running.
        GlobalErrorHandler.Register(PauseRunningTimers);

        // After "Migrate to another computer" → Import, Tasky restarts itself; let the old copy exit first.
        MigrationService.WaitForPreviousInstance(args);

        _singleInstance = new SingleInstanceService();
        if (!_singleInstance.IsPrimaryInstance)
        {
            // Tasky is already running (possibly hidden in the tray) — bring it forward.
            _singleInstance.SignalPrimaryInstance();
            Desktop.Shutdown();
            return;
        }
        _singleInstance.ListenForActivation(() =>
            Dispatcher.UIThread.Post(() => MainAppWindow?.ShowFromTray()));

        try
        {
            StartTasky(args);
        }
        catch (Exception ex)
        {
            // Instead of closing silently, explain what happened and offer a way out.
            CrashLog.Write("Sysslar could not start", ex);
            ShowSafeMode(ex);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void StartTasky(string[] args)
    {
        // Both must run before anything opens the data files.
        var importResult  = ApplyPendingImport();
        var restoreResult = ApplyPendingRestore();

        // Idle / lock / sleep detection is shared by every workspace
        _userActivity = PlatformServices.CreateUserActivity();
        _userActivity.SessionEnding += PauseRunningTimers;   // log-off / shutdown: stop the clock first

        StartWorkspaceServices(isAppStart: true);
        _userActivity.Start();

        AccessibleNames.Register();
        ScrollPaddingWheel.Register();

        var mainWindow = new MainWindow { DataContext = ViewModel };
        MainAppWindow = mainWindow;
        Desktop.MainWindow = mainWindow;
        Desktop.ShutdownRequested += (_, _) => PauseRunningTimers();   // e.g. macOS log-off or Quit
        Desktop.Exit += (_, _) => OnExit();
        WatchDockReopen();

        void OnFirstOpened(object? sender, EventArgs e)
        {
            mainWindow.Opened -= OnFirstOpened;   // Opened repeats each time the window is shown from the tray
            if (ViewModel!.Tasks.Any(t => t.WasInterrupted))
                InAppNotificationService.Info("Your timer was paused because Sysslar closed while it was running.");
            importResult?.Notify();
            restoreResult?.Notify();

            Hotkey = PlatformServices.CreateQuickAddHotkey();
            Hotkey.Register(ShowQuickAdd);
            SmokeTest.ShowReminderIfRequested();
            SmokeTest.VisitAllPagesIfRequested(mainWindow);
            if (args.Contains(Workspaces.OpenImportArg)) mainWindow.OpenImportForNewWorkspace();
            _ = mainWindow.CheckForUpdatesIfDueAsync();
            Task.Run(Platform.MacOS.MacLegacyAppCleanup.MoveOldTaskyAppToTrash);   // renamed app: drop the old Tasky.app
        }
        mainWindow.Opened += OnFirstOpened;
        Services.Sync.AppSync.Start();

        // Workspace switch: open on the "Opening …" screen, where the old window was
        mainWindow.BeginArrival(args);

        // Before Show, so the window never flashes the wrong theme
        InitializeTheme();
        mainWindow.Show();
    }

    // ─── The open workspace's data and services ──────────────────────────

    /// <summary>Opens the active workspace's settings and database and starts everything that uses them.</summary>
    private static void StartWorkspaceServices(bool isAppStart = false)
    {
        Settings.Load();

        Database = new DatabaseService();
        Database.Initialize();
        if (isAppStart) SmokeTest.SeedSampleDataIfRequested(Database);   // not into workspaces opened later

        Backups = new BackupService(Database);
        Backups.BackUpIfDueInBackground();
        StartBackupTimer();

        Notifications = new NotificationService(Database);

        ViewModel = new MainViewModel(Database);
        ViewModel.PurgeExpiredDeletedTasks();   // drop recycle-bin items older than 30 days
        ViewModel.DeleteOrphanAttachmentFolders();
        ViewModel.LoadTasks();
        ViewModel.LoadDeletedTasks();
        ViewModel.LoadGroups();
        ViewModel.LoadNotes();

        ReminderScheduler = new ReminderSchedulerService(Database, Notifications);
        ReminderScheduler.Start();

        // Idle / lock / sleep monitor — auto-pauses the running task
        IdleMonitor = new IdleMonitorService(_userActivity!, ViewModel.PauseRunningTasks, ViewModel.ResumeActiveTask);
        IdleMonitor.Start();

        // Saves running timers every minute so a crash or power cut can't inflate them
        _checkpoints = new TrackingCheckpointService(ViewModel.CheckpointRunningTasks);
        _checkpoints.Start();

        // Meeting monitor — detects mic/camera usage and records meeting sessions (Windows only)
        if (PlatformServices.SupportsMeetingDetection && OperatingSystem.IsWindows())
        {
            MeetingMonitor = new MeetingMonitorService(Database);
            MeetingMonitor.Start();
        }

        // App usage monitor — tracks foreground window time
        AppUsage = new AppUsageService(Database, PlatformServices.CreateForegroundAppMonitor(), _userActivity!);
        AppUsage.Start();
    }

    /// <summary>Saves running timers and stops everything that uses the open workspace's files.</summary>
    private static void StopWorkspaceServices()
    {
        PauseRunningTimers();
        _backupTimer?.Stop();
        _checkpoints?.Dispose();
        AppUsage?.Dispose();
        if (OperatingSystem.IsWindows()) MeetingMonitor?.Dispose();
        MeetingMonitor = null;
        IdleMonitor?.Dispose();
        ReminderScheduler?.Stop();
        if (ViewModel is { } old)
            foreach (var task in old.Tasks) task.Detach();   // stop their one-second display timers
        DatabaseService.ReleaseOpenFiles();   // so the closed workspace can be deleted (Windows)
    }

    /// <summary>
    /// Switches to another workspace without restarting: saves and stops the open workspace's services,
    /// opens the other one's settings and database, and starts its services. The window, tray icon and
    /// quick-add shortcut stay. Returns false (and keeps the old workspace) when anything fails.
    /// </summary>
    public static bool SwitchWorkspace(string id)
    {
        string previous = Workspaces.Active.Id;
        StopWorkspaceServices();
        try
        {
            Workspaces.Activate(id);
            ApplyPendingImport()?.Notify();
            ApplyPendingRestore()?.Notify();
            StartWorkspaceServices();
            Services.Sync.AppSync.OnWorkspaceSwitched();
            return true;
        }
        catch (Exception ex)
        {
            CrashLog.Write("Switching workspace failed", ex);
            try
            {
                Workspaces.Activate(previous);
                StartWorkspaceServices();
            }
            catch (Exception again) { CrashLog.Write("Reopening the previous workspace failed", again); }
            return false;
        }
    }

    /// <summary>Applies the open workspace's theme choice (after a switch).</summary>
    public void ReapplyTheme() => ApplyThemePreference();

    /// <summary>Ends the app (tray "Exit Tasky", restart after an import).</summary>
    public static void Shutdown() => Instance.Desktop.Shutdown();

    // ─── Start-up failure ─────────────────────────────────────────────────

    private void ShowSafeMode(Exception error)
    {
        StopServices();
        if (MainAppWindow is { } broken)
        {
            MainAppWindow = null;
            try { broken.DisposeTray(); broken.Close(); } catch { /* it is already broken */ }
        }

        var safeMode = new Windows.SafeModeWindow(error);
        safeMode.Closed += (_, _) => Shutdown();
        Desktop.MainWindow = safeMode;
        safeMode.Show();
    }

    // ─── Backups ──────────────────────────────────────────────────────────

    /// <summary>How often a running Tasky checks whether a daily backup is due.</summary>
    private static readonly TimeSpan BackupCheckInterval = TimeSpan.FromHours(1);

    private static DispatcherTimer? _backupTimer;

    private static void StartBackupTimer()
    {
        _backupTimer = new DispatcherTimer { Interval = BackupCheckInterval };
        _backupTimer.Tick += (_, _) => Backups?.BackUpIfDueInBackground();
        _backupTimer.Start();
    }

    // ─── Data swapped in at start-up (migration import, backup restore) ───

    /// <summary>Outcome of an import or restore applied at startup, reported once the window is up.</summary>
    private sealed record StartupSwapOutcome(bool Succeeded, string SuccessMessage, string FailureMessage)
    {
        public void Notify()
        {
            if (Succeeded) InAppNotificationService.Success(SuccessMessage);
            else           InAppNotificationService.Error($"{FailureMessage} Details: {CrashLog.CurrentFile}");
        }
    }

    private static StartupSwapOutcome? ApplyPendingImport()
        => ApplyStartupSwap(MigrationService.ApplyPendingImport,
            "Your Sysslar data has been imported. Welcome back!",
            "Import failed, so your existing data was kept.");

    private static StartupSwapOutcome? ApplyPendingRestore()
        => ApplyStartupSwap(BackupService.ApplyPendingRestore,
            "Your backup has been restored.",
            "Restoring the backup failed, so your existing data was kept.");

    private static StartupSwapOutcome? ApplyStartupSwap(Func<bool> apply, string success, string failure)
    {
        try
        {
            return apply() ? new StartupSwapOutcome(true, success, failure) : null;
        }
        catch
        {
            return new StartupSwapOutcome(false, success, failure);   // already logged; current data restored
        }
    }

    public static void ShowQuickAdd()
    {
        var qa = new Windows.QuickAddWindow(ViewModel!);
        qa.Show();
        qa.Activate();
    }

    /// <summary>macOS: clicking the Dock icon while the window is hidden brings it back.</summary>
    private void WatchDockReopen()
    {
        if (this.TryGetFeature<IActivatableLifetime>() is not { } activatable) return;
        activatable.Activated += (_, e) =>
        {
            if (e.Kind == ActivationKind.Reopen) MainAppWindow?.ShowFromTray();
        };
    }

    // ─── Theme ────────────────────────────────────────────────────────────

    private bool _isDark = true;

    public bool IsDarkTheme => _isDark;

    /// <summary>Saves the Settings → Theme choice (dark, light or auto) and applies it.</summary>
    public void SetThemePreference(string theme)
    {
        Settings.Current.Theme = theme;
        Settings.Save();
        ApplyThemePreference();
    }

    /// <summary>Applies the saved theme choice; "auto" follows the operating system.</summary>
    private void ApplyThemePreference()
    {
        bool dark = Settings.Current.Theme switch
        {
            ThemePreferences.Light => false,
            ThemePreferences.Auto  => ReadSystemIsDark(),
            _                      => true,
        };
        ApplyTheme(dark);
        MainAppWindow?.SyncThemeIcons(dark);
        if (MainAppWindow is { IsVisible: true } window)
            window.ApplyTitleBarThemePublic(dark);   // a window that isn't open yet does this when it opens
    }

    internal void ApplyTheme(bool dark)
    {
        _isDark = dark;
        RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        if (ViewModel is not null) ViewModel.IsDarkTheme = dark;
    }

    private void InitializeTheme()
    {
        ApplyThemePreference();
        if (PlatformSettings is { } settings) settings.ColorValuesChanged += OnSystemColorsChanged;
    }

    private void OnSystemColorsChanged(object? sender, PlatformColorValues e)
    {
        if (Settings.Current.Theme != ThemePreferences.Auto) return;
        Dispatcher.UIThread.Post(ApplyThemePreference);
    }

    /// <summary>The OS app-mode setting (Windows "Choose your app mode", macOS Appearance).</summary>
    private bool ReadSystemIsDark()
        => PlatformSettings?.GetColorValues().ThemeVariant == PlatformThemeVariant.Dark;

    // ─── Shutdown ─────────────────────────────────────────────────────────

    /// <summary>Pauses and saves every running timer (used on exit, log-off, shutdown and fatal errors).</summary>
    private static void PauseRunningTimers() => ViewModel?.PauseRunningTasks(DateTime.Now);

    private static void OnExit()
    {
        StopServices();
        Services.Sync.AppSync.FlushOnExit();
        MainAppWindow?.DisposeTray();
        _singleInstance?.Dispose();
    }

    /// <summary>Saves timers and stops every background service (on exit, or when start-up fails).</summary>
    private static void StopServices()
    {
        StopWorkspaceServices();
        _userActivity?.Dispose();
        Hotkey?.Dispose();
    }
}

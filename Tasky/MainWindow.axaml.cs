using System.IO;
using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tasky.Behaviors;
using Tasky.Controls;
using Tasky.Dialogs;
using Tasky.Helpers;
using Tasky.Models;
using Tasky.Platform;
using Tasky.Services;
using Tasky.ViewModels;

namespace Tasky;

public partial class MainWindow : Window
{
    private ISystemTray? _trayIcon;
    private bool _exiting;
    private bool _addPanelOpen;
    private string _completedGroupFilter = "All";
    private string _topTasksFilter = "overall";
    private bool _addNotePanelOpen;
    private int _statsWeekOffset = 0;
    private bool _weeklyChartShowTime = true;
    private string _meetingFilter = "week";
    private string _appUsageFilter = "today";

    private MainViewModel Vm => (MainViewModel)DataContext!;

    private static readonly TimeSpan PageAnimation = TimeSpan.FromMilliseconds(220);
    private const double PageSlide = 16;

    // Navigation

    private enum AppPage { Main, Completed, Notes, Stats, Reminders, Settings, Trash, EditTask, NoteDetail }

    /// <summary>The Edit Task page while it is open (it is rebuilt for each task).</summary>
    private Views.EditTaskPage? _editPage;

    /// <summary>The note page while it is open (it is rebuilt for each note).</summary>
    private Views.NotePage? _notePage;

    private void NavigateTo(AppPage page)
    {
        TaskListPage.IsVisible   = page == AppPage.Main;
        CompletedPage.IsVisible  = page == AppPage.Completed;
        NotesPage.IsVisible      = page == AppPage.Notes;
        StatsPage.IsVisible      = page == AppPage.Stats;
        RemindersPage.IsVisible  = page == AppPage.Reminders;
        SettingsPage.IsVisible   = page == AppPage.Settings;
        TrashPage.IsVisible      = page == AppPage.Trash;
        EditTaskPageHost.IsVisible = page == AppPage.EditTask;
        NotePageHost.IsVisible     = page == AppPage.NoteDetail;
        AddPanelBorder.IsVisible = page == AppPage.Main;
        MainHeader.IsVisible     = page == AppPage.Main;
        SubHeader.IsVisible      = page is not (AppPage.Main or AppPage.EditTask or AppPage.NoteDetail);   // these pages have their own header
        if (page != AppPage.EditTask)   CloseEditPage();
        if (page != AppPage.NoteDetail) CloseNotePage();

        EmptyBinBtn.IsVisible    = page == AppPage.Trash;
        NewNoteBtn.IsVisible     = page == AppPage.Notes;

        SubPageTitle.Text = page switch
        {
            AppPage.Completed => "Completed Tasks",
            AppPage.Notes     => "Notes",
            AppPage.Stats     => "Stats & Reports",
            AppPage.Reminders => "Reminders",
            AppPage.Settings  => "Settings",
            AppPage.Trash     => "Recycle Bin",
            _                 => ""
        };

        if (page == AppPage.Trash)     RefreshTrashList();
        if (page == AppPage.Completed) RefreshCompletedList();
        if (page == AppPage.Notes)     { Vm.LoadNotes(); RefreshNotesEmpty(); }
        if (page == AppPage.Stats)     { Vm.RefreshStats(_statsWeekOffset); BuildStatsUI(); }
        if (page == AppPage.Reminders)
        {
            NotifPanel.IsVisible     = true;
            UpcomingPanel.IsVisible  = false;
            NotifTabBtn.IsChecked    = true;
            UpcomingTabBtn.IsChecked = false;
            RefreshRemindersPage();
        }
        if (page == AppPage.Settings) LoadSettingsPage();

        AnimatePageIn(page);
    }

    private void AnimatePageIn(AppPage page)
    {
        Control? target = page switch
        {
            AppPage.Completed => CompletedPage,
            AppPage.Notes     => NotesPage,
            AppPage.Stats     => StatsPage,
            AppPage.Reminders => RemindersPage,
            AppPage.Settings  => SettingsPage,
            AppPage.Trash     => TrashPage,
            AppPage.EditTask  => EditTaskPageHost,
            AppPage.NoteDetail => NotePageHost,
            AppPage.Main      => TaskListPage,
            _                 => null
        };
        if (target is null || !target.IsVisible) return;

        // Sub-pages slide in from the right; going back to the list slides in from the left
        double from = page == AppPage.Main ? -PageSlide : PageSlide;
        var tt = new TranslateTransform(from, 0);
        target.RenderTransform = tt;

        var ease = new CubicEaseOut();
        _ = Animator.Animate(tt, TranslateTransform.XProperty, from, 0, PageAnimation, ease);
        _ = Animator.Fade(target, 0, 1, PageAnimation, ease);
    }

    private void Back_Click(object? sender, RoutedEventArgs e)
    {
        Vm.CompletedSearch = string.Empty;
        Vm.NoteSearch      = string.Empty;
        Vm.TaskSearch      = string.Empty;
        _addNotePanelOpen  = false;
        AddNotePanel.IsVisible = false;
        NavigateTo(AppPage.Main);
    }

    // Title bar theme (Windows draws it to match the header; macOS follows the app theme)

    private void ApplyTitleBarTheme(bool dark) => PlatformServices.Chrome.ApplyThemeWithCaption(this, dark);

    public void ApplyTitleBarThemePublic(bool dark) => ApplyTitleBarTheme(dark);

    public void SyncThemeIcons(bool dark)
    {
        SubThemeIcon.Data = UiResources.Geometry(dark ? "Icon.Sun" : "Icon.Moon");
    }

    public MainWindow()
    {
        InitializeComponent();
        Icon = new WindowIcon(AssetLoader.Open(new Uri(OperatingSystem.IsWindows()
            ? "avares://Tasky/Assets/icon.ico"
            : "avares://Tasky/Assets/sysslar-logo.png")));
        ApplyPlatformTexts();
        RefreshWorkspaceHeader();
        PopulateSoundPicker();
        InitTrayIcon();
        RegisterKeyboardShortcuts();
        ApplyTextScale(App.Settings.Current.EffectiveZoom);
        RestoreWindowPlacement();
        SetUpResponsiveLayout();
        SetUpMotion();

        NewTaskAttachments.AcceptAttachmentsAnywhereIn(AddFormPanel);
        NewNoteAttachments.AcceptAttachmentsAnywhereIn(AddNotePanel);
        WeeklyChartScroll.AddHandler(PointerWheelChangedEvent, InnerScroll_PreviewMouseWheel, RoutingStrategies.Tunnel);
        // A drag-to-reorder swallows the release, so the card would stay pressed in: always let it pop back up.
        TaskListControl.AddHandler(PointerReleasedEvent, (_, _) => ReleasePressedCard(), RoutingStrategies.Bubble, handledEventsToo: true);
        Closing += Window_Closing;

        Opened += (_, _) =>
        {
            bool dark = App.Instance.IsDarkTheme;
            ApplyTitleBarTheme(dark);
            SyncThemeIcons(dark);
        };
        Loaded += (_, _) =>
        {
            if (DataContext is MainViewModel vm) AttachViewModel(vm);

            // Auto-refresh every 10 minutes so due-date labels (Today/Tomorrow/Yesterday)
            // and sort order stay accurate without needing to restart the app.
            var autoRefresh = new DispatcherTimer { Interval = TimeSpan.FromMinutes(10) };
            autoRefresh.Tick += (_, _) => AutoRefresh();
            autoRefresh.Start();
        };
    }

    /// <summary>Wires the window to a view model: at start-up, and again after a workspace switch.</summary>
    private void AttachViewModel(MainViewModel vm)
    {
        vm.PropertyChanged += (_, e) =>
        {
            if (!ReferenceEquals(vm, DataContext)) return;   // a previous workspace's view model
            if (e.PropertyName == nameof(MainViewModel.CompletedSearch) && CompletedPage.IsVisible)
                RefreshCompletedList();
            if (e.PropertyName == nameof(MainViewModel.NoteSearch) && NotesPage.IsVisible)
                RefreshNotesEmpty();
        };
        vm.LoadGroups();
        RestoreViewChoices(vm);
        BuildGroupChips();
        PopulateNewTaskGroupPicker();
        vm.RefreshMissedNotifCount();
        AttachMotion(vm);
    }

    /// <summary>The few texts that name the operating system.</summary>
    private void ApplyPlatformTexts()
    {
        // Meeting detection reads Windows audio sessions; macOS has no equivalent, so its cards are hidden there.
        MeetingHoursCard.IsVisible   = PlatformServices.SupportsMeetingDetection;
        MeetingHistoryCard.IsVisible = PlatformServices.SupportsMeetingDetection;
        if (!OperatingSystem.IsMacOS()) return;
        AppUsageSubtitle.Text = "Time spent in each Mac application  ·  top 10";
    }

    private void PopulateSoundPicker()
    {
        foreach (var sound in PlatformServices.Sounds.Options)
            SettingTune.Items.Add(new ComboBoxItem { Content = sound.Label, Tag = sound.Tag });
        SettingTune.SelectedIndex = PlatformServices.Sounds.Options.ToList()
            .FindIndex(s => s.Tag == SoundTags.Default);
    }

    // ─── Auto-refresh (every 10 min) ─────────────────────────────────────────
    // Keeps due-date labels (Today/Tomorrow/Yesterday) and the date filters
    // current. Tasks are refreshed in place — never reloaded — so running
    // timers, the widget and any open dialog keep working on the same objects.
    private void AutoRefresh()
    {
        if (DataContext is not MainViewModel vm) return;

        vm.PurgeExpiredDeletedTasks();   // honour the 30-day recycle-bin retention
        vm.RefreshTimeSensitiveLabels();
        vm.RefreshMissedNotifCount();

        // Keep the new-task date picker current across midnight
        vm.NewDueDate = DateTime.Today;

        // If a page that shows time-sensitive data is open, refresh it too
        if (TrashPage.IsVisible)     { vm.LoadDeletedTasks(); RefreshTrashList(); }
        if (CompletedPage.IsVisible) RefreshCompletedList();
        if (StatsPage.IsVisible)     { vm.RefreshStats(_statsWeekOffset); BuildStatsUI(); }
        if (RemindersPage.IsVisible) RefreshRemindersPage();
    }

    // Group chips

    private static Color GroupDotColor(TaskGroup g)
        => Color.TryParse(g.Color, out var c) ? c : Color.FromRgb(107, 114, 128);

    private void BuildGroupChips()
    {
        GroupChipsPanel.Children.Clear();

        var allChip = new RadioButton
        {
            Content   = "All",
            Theme     = UiResources.Theme("FilterChip"),
            GroupName = "GF",
            IsChecked = Vm.SelectedGroupFilter == null,
            Margin    = new Thickness(0, 0, 5, 0)
        };
        allChip.Click += (_, _) => Vm.SelectedGroupFilter = null;
        GroupChipsPanel.Children.Add(allChip);

        foreach (var g in Vm.Groups)
        {
            // Build chip content: coloured 8px dot + group name
            var dot = new Avalonia.Controls.Shapes.Ellipse
            {
                Width  = 8, Height = 8,
                Fill   = new SolidColorBrush(GroupDotColor(g)),
                Margin = new Thickness(0, 0, 5, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            var label = new TextBlock
            {
                Text = g.Name,
                VerticalAlignment = VerticalAlignment.Center
            };
            var content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children = { dot, label }
            };

            var chip = new RadioButton
            {
                Content   = content,
                Theme     = UiResources.Theme("FilterChip"),
                GroupName = "GF",
                IsChecked = Vm.SelectedGroupFilter == g.Id,
                Margin    = new Thickness(0, 0, 5, 0),
                Tag       = g.Id
            };
            int gid = g.Id;
            chip.Click += (_, _) => Vm.SelectedGroupFilter = gid;
            GroupChipsPanel.Children.Add(chip);
        }

        GroupFilterScroll.IsVisible = Vm.Groups.Count > 0;
    }

    private void PopulateNewTaskGroupPicker()
    {
        NewTaskGroupPicker.Items.Clear();

        int selectIndex = 0;
        int i = 0;
        foreach (var g in Vm.Groups)
        {
            var dot = new Avalonia.Controls.Shapes.Ellipse
            {
                Width  = 8, Height = 8,
                Fill   = new SolidColorBrush(GroupDotColor(g)),
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            var label = new TextBlock { Text = g.Name, VerticalAlignment = VerticalAlignment.Center };
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(dot);
            panel.Children.Add(label);

            NewTaskGroupPicker.Items.Add(new ComboBoxItem { Content = panel, Tag = (int?)g.Id });

            // Pre-select whichever group matches the VM default (set by LoadGroups)
            if ((int?)g.Id == Vm.NewTaskGroupId) selectIndex = i;
            i++;
        }
        NewTaskGroupPicker.SelectedIndex = selectIndex;
    }

    /// <summary>
    /// Shows the view model's new-task group and repeat values in their pickers. Without this the pickers
    /// keep the previous task's choice on screen while the view model has already reset to the defaults,
    /// so the next task silently gets "Other" / no repeat.
    /// </summary>
    private void SyncNewTaskPickersToViewModel()
    {
        NewTaskGroupPicker.SelectedItem = NewTaskGroupPicker.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(i => i.Tag as int? == Vm.NewTaskGroupId);

        string recurrenceTag = ((int)Vm.NewTaskRecurrence).ToString();
        NewTaskRecurrencePicker.SelectedItem = NewTaskRecurrencePicker.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(i => i.Tag as string == recurrenceTag);
    }

    private void NewTaskGroup_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not MainViewModel) return;
        if (sender is ComboBox cb && cb.SelectedItem is ComboBoxItem ci)
            Vm.NewTaskGroupId = ci.Tag as int?;
    }

    private void NewTaskRecurrence_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not MainViewModel) return;
        if (sender is ComboBox cb && cb.SelectedItem is ComboBoxItem ci
            && ci.Tag is string s && int.TryParse(s, out int v))
            Vm.NewTaskRecurrence = (RecurrenceType)v;
    }

    // System Tray

    private void InitTrayIcon()
    {
        _trayIcon = PlatformServices.CreateTray();
        _trayIcon.OpenRequested += ShowFromTray;
        _trayIcon.Show(Workspaces.HasSeveral ? $"Sysslar - {Workspaces.Active.Name}" : "Sysslar - Task Manager",
        [
            new TrayMenuItem("Open Sysslar", ShowFromTray),
            new TrayMenuItem("Quick Add",  OpenQuickAdd),
            TrayMenuItem.Separator,
            new TrayMenuItem("Exit Sysslar", ExitApp)
        ]);
    }

    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
    }

    public void ShowBalloonReminder(string title, string message)
        => _trayIcon?.ShowBalloon(title, message, TimeSpan.FromSeconds(8));

    public void DisposeTray()
    {
        _trayIcon?.Dispose();
        _trayIcon = null;
    }

    // ─── Dim + blur overlay for modal dialogs ─────────────────────────────

    /// <summary>
    /// Blurs the window content and darkens it. Call before showing a dialog,
    /// then HideDimOverlay() when it closes.
    /// </summary>
    private static readonly BlurEffect _blur = new() { Radius = 6 };

    private void ShowDimOverlay()
    {
        // Blur only the content panels — NOT RootGrid, so InAppNotifBorder stays crisp on top
        MainHeader.Effect      = _blur;
        AddPanelBorder.Effect  = _blur;
        PageContainer.Effect   = _blur;
        DimOverlay.IsVisible   = true;
    }

    private void HideDimOverlay()
    {
        DimOverlay.IsVisible   = false;
        MainHeader.Effect      = null;
        AddPanelBorder.Effect  = null;
        PageContainer.Effect   = null;
    }

    /// <summary>Shows a dialog modally with blur/dim so every dialog gets the same backdrop.</summary>
    private async Task<bool?> ShowModalDialog(Window dlg)
    {
        ShowDimOverlay();
        AnimateDialogIn(dlg);
        try   { return await dlg.ShowDialog<bool?>(this); }
        finally { HideDimOverlay(); }
    }

    // ─── In-app notification overlay ─────────────────────────────────────

    private DispatcherTimer? _notifTimer;
    private NotificationAction? _notifAction;

    private static readonly TimeSpan NotificationDuration       = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ActionNotificationDuration = TimeSpan.FromSeconds(7);   // time to reach "Undo"

    public void ShowInAppNotification(string message, NotificationKind kind, NotificationAction? action = null)
    {
        // Icon and its colour say what kind of message it is; the card itself stays neutral
        var (icon, colour) = kind switch
        {
            NotificationKind.Success => ("Icon.Done",  "Success"),
            NotificationKind.Warning => ("Icon.Info",  "Warning"),
            NotificationKind.Error   => ("Icon.Close", "Danger"),
            NotificationKind.Undo    => ("Icon.Undo",  "PrimaryBlue"),
            _                        => ("Icon.Info",  "PrimaryBlue"),
        };
        NotifIcon.Data = UiResources.Geometry(icon);
        NotifIcon.WithResource(LineIcon.ForegroundProperty, colour);
        NotifMessage.Text = message;

        _notifAction = action;
        NotifActionBtn.Content   = action?.Label;
        NotifActionBtn.IsVisible = action is not null;
        InAppNotifBorder.IsHitTestVisible = action is not null;   // otherwise clicks pass through to the page

        // Rise in from below
        InAppNotifBorder.IsVisible = true;
        var rise = new TranslateTransform(0, 12);
        InAppNotifBorder.RenderTransform = rise;
        var ease = new CubicEaseOut();
        _ = Animator.Animate(rise, TranslateTransform.YProperty, 12, 0, TimeSpan.FromMilliseconds(240), ease);
        _ = Animator.Fade(InAppNotifBorder, 0, 1, TimeSpan.FromMilliseconds(200), ease);

        _notifTimer?.Stop();
        _notifTimer = new DispatcherTimer { Interval = action is null ? NotificationDuration : ActionNotificationDuration };
        _notifTimer.Tick += (_, _) => _ = HideInAppNotification();
        _notifTimer.Start();
    }

    private async Task HideInAppNotification()
    {
        _notifTimer?.Stop();
        _notifAction = null;
        InAppNotifBorder.IsHitTestVisible = false;
        if (await Animator.Fade(InAppNotifBorder, 1, 0, TimeSpan.FromMilliseconds(250)))
            InAppNotifBorder.IsVisible = false;
    }

    private void NotifAction_Click(object? sender, RoutedEventArgs e)
    {
        var action = _notifAction;
        _ = HideInAppNotification();
        action?.Run();
    }

    private void OpenQuickAdd() => App.ShowQuickAdd();

    private void ExitApp()
    {
        _exiting = true;
        DisposeTray();
        App.Shutdown();
    }

    // Window events

    private void Window_Closing(object? sender, WindowClosingEventArgs e)
    {
        // Quitting (tray Exit, macOS Quit, log-off) really closes; the close button only hides.
        if (_exiting || e.CloseReason != WindowCloseReason.WindowClosing) return;
        e.Cancel = true;
        Hide();
        _trayIcon?.ShowBalloon("Sysslar", "Running in background. Double-click to restore.", TimeSpan.FromSeconds(2));
    }

    // Header buttons

    private void ThemeToggle_Click(object? sender, RoutedEventArgs e)
    {
        var app = App.Instance;
        app.SetThemePreference(app.IsDarkTheme ? ThemePreferences.Light : ThemePreferences.Dark);
    }

    private void Completed_Click(object? sender, RoutedEventArgs e)  => NavigateTo(AppPage.Completed);
    private void Notes_Click(object? sender, RoutedEventArgs e)      => NavigateTo(AppPage.Notes);
    private void Stats_Click(object? sender, RoutedEventArgs e)      { Vm.RefreshStats(_statsWeekOffset); NavigateTo(AppPage.Stats); }
    private void Reminders_Click(object? sender, RoutedEventArgs e)  => NavigateTo(AppPage.Reminders);

    // ─── Reminders page ───────────────────────────────────────────────────

    /// <summary>Flat view-model used by the reminders ItemsControl.</summary>
    private sealed class ReminderRow
    {
        public int    Id               { get; init; }
        public string TaskTitle        { get; init; } = string.Empty;
        public string TaskIdDisplay    { get; init; } = string.Empty;
        public string FireDisplay      { get; init; } = string.Empty;
        public string FrequencyDisplay { get; init; } = string.Empty;
    }

    private sealed class MissedNotifRow
    {
        public int    Id             { get; init; }
        public int    TaskId         { get; init; }
        public string TaskTitle      { get; init; } = string.Empty;
        public string TaskIdDisplay  { get; init; } = string.Empty;
        public string FiredAtDisplay { get; init; } = string.Empty;
        public bool   IsRead         { get; init; }
    }

    private void RefreshRemindersPage()
    {
        // All notifications (unread first, then read/faded)
        var all = App.Database!.GetMissedNotifications()
            .Select(m => new MissedNotifRow
            {
                Id             = m.Id,
                TaskId         = m.TaskId,
                TaskTitle      = m.TaskTitle,
                TaskIdDisplay  = $"#{m.TaskId:D3}",
                FiredAtDisplay = m.FiredAt.Date == DateTime.Today
                    ? $"Today {m.FiredAt:HH:mm}"
                    : $"{m.FiredAt:MMM d, HH:mm}",
                IsRead         = m.IsRead
            })
            .ToList();

        var unread = all.Where(r => !r.IsRead).ToList();
        var read   = all.Where(r =>  r.IsRead).ToList();

        MissedList.ItemsSource       = unread;
        ReadList.ItemsSource         = read;
        MissedSection.IsVisible      = unread.Count > 0;
        ReadSection.IsVisible        = read.Count   > 0;
        NotifEmptyLabel.IsVisible    = unread.Count == 0 && read.Count == 0;
        Vm.MissedNotifCount          = unread.Count;

        // Upcoming reminders — nearest first
        var reminders = App.Database!.GetAllReminders()
            .Where(r => r.NextFireTime > DateTime.Now)
            .OrderBy(r => r.NextFireTime)
            .Select(r => new ReminderRow
            {
                Id               = r.Id,
                TaskTitle        = r.TaskTitle,
                TaskIdDisplay    = $"#{r.TaskId:D3}",
                FireDisplay      = r.NextFireTime.ToString("MMM d, yyyy  HH:mm"),
                FrequencyDisplay = BuildFreqLabel(r)
            })
            .ToList();

        RemindersList.ItemsSource     = reminders;
        RemindersEmptyLabel.IsVisible = reminders.Count == 0;
    }

    private void NotifTab_Click(object? sender, RoutedEventArgs e)
    {
        NotifPanel.IsVisible    = true;
        UpcomingPanel.IsVisible = false;
    }

    private void UpcomingTab_Click(object? sender, RoutedEventArgs e)
    {
        NotifPanel.IsVisible    = false;
        UpcomingPanel.IsVisible = true;
    }

    private async void MissedNotif_Click(object? sender, PointerReleasedEventArgs e)
    {
        if (!IsLeftClick(e) || sender is not Control { DataContext: MissedNotifRow row }) return;
        // Mark as read (moves to faded "Read" section) instead of deleting
        App.Database!.MarkMissedNotificationRead(row.Id);
        RefreshRemindersPage();
        // Open task detail
        if (FindTask(row.TaskId) is { } vm) await OpenTaskDetail(vm);
    }

    private async void ReadNotif_Click(object? sender, PointerReleasedEventArgs e)
    {
        if (!IsLeftClick(e) || sender is not Control { DataContext: MissedNotifRow row }) return;
        if (FindTask(row.TaskId) is { } vm) await OpenTaskDetail(vm);
    }

    private TaskItemViewModel? FindTask(int taskId)
        => Vm.Tasks.FirstOrDefault(t => t.Id == taskId)
        ?? Vm.CompletedTasks.FirstOrDefault(t => t.Id == taskId);

    private void DismissMissedNotif_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int id }) return;
        App.Database!.DeleteMissedNotification(id);
        RefreshRemindersPage();
    }

    private static string BuildFreqLabel(ReminderItem r)
    {
        if (r.Frequency == ReminderFrequency.Weekly && r.TargetDayOfWeek.HasValue)
        {
            var day = System.Globalization.CultureInfo.CurrentCulture
                .DateTimeFormat.GetDayName((DayOfWeek)r.TargetDayOfWeek.Value);
            return $"Every {day}";
        }
        if (r.Frequency == ReminderFrequency.Monthly && r.DayOfMonth.HasValue)
        {
            string suffix = (r.DayOfMonth.Value % 100) switch
            {
                11 or 12 or 13 => "th",
                _ => (r.DayOfMonth.Value % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" }
            };
            return $"{r.DayOfMonth}{suffix} of month";
        }
        return r.Frequency switch
        {
            ReminderFrequency.Daily   => "Daily",
            ReminderFrequency.Weekly  => "Weekly",
            ReminderFrequency.Monthly => "Monthly",
            _                         => "Once"
        };
    }

    private async void DeleteReminder_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int id }) return;
        var dlg = new ConfirmDialog("Delete Reminder",
            "Delete this reminder? The task will not be deleted.",
            "Delete", ConfirmType.Danger);
        if (await ShowModalDialog(dlg) == true)
        {
            App.Database!.DeleteReminder(id);
            RefreshRemindersPage();
            InAppNotificationService.Success("Reminder deleted.");
        }
    }

    private async void Groups_Click(object? sender, RoutedEventArgs e)
    {
        await ShowModalDialog(new ManageGroupsDialog(Vm));
        BuildGroupChips();
        PopulateNewTaskGroupPicker();
    }

    private void Settings_Click(object? sender, RoutedEventArgs e) => NavigateTo(AppPage.Settings);

    // ── Settings page ────────────────────────────────────────────────────────
    private void LoadSettingsPage()
    {
        // Keyboard shortcuts start folded each time Settings opens
        ShortcutsToggle.IsChecked = false;
        ShortcutsBody.IsVisible   = false;
        var s = App.Settings.Current;
        SettingUserName.Text = s.UserName;

        // Theme
        ThemeDark.IsChecked  = s.Theme == ThemePreferences.Dark;
        ThemeLight.IsChecked = s.Theme == ThemePreferences.Light;
        ThemeAuto.IsChecked  = s.Theme == ThemePreferences.Auto;

        // Tune
        foreach (var item in SettingTune.Items.OfType<ComboBoxItem>())
            if (item.Tag?.ToString() == s.NotifTune) { SettingTune.SelectedItem = item; break; }

        // Accent color
        AccentBlue.IsChecked   = s.NotifAccentColor == "#60A5FA";
        AccentGreen.IsChecked  = s.NotifAccentColor == "#22C55E";
        AccentOrange.IsChecked = s.NotifAccentColor == "#FB923C";
        AccentPurple.IsChecked = s.NotifAccentColor == "#A78BFA";

        // Dismiss
        DismissPerm.IsChecked = s.NotifAutoDismissSecs == 0;
        Dismiss15.IsChecked   = s.NotifAutoDismissSecs == 15;
        Dismiss30.IsChecked   = s.NotifAutoDismissSecs == 30;
        Dismiss60.IsChecked   = s.NotifAutoDismissSecs == 60;

        // Task completion
        CompletionPopupToggle.IsChecked = s.ShowCompletionPopup;
        SaveScreenshotToggle.IsChecked  = s.SaveScreenshotToDesktop;

        LoadDataToolsSettings();
    }

    private void SettingUserName_Changed(object? sender, TextChangedEventArgs e)
    {
        App.Settings.Current.UserName = SettingUserName.Text ?? string.Empty;
        App.Settings.Save();
    }

    private void ThemeSetting_Click(object? sender, RoutedEventArgs e)
    {
        var tag = (sender as RadioButton)?.Tag?.ToString() ?? ThemePreferences.Dark;
        App.Instance.SetThemePreference(tag);
    }

    private string SelectedTuneTag => (SettingTune.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "notify";

    private void SettingTune_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;   // the list is being filled
        App.Settings.Current.NotifTune = SelectedTuneTag;
        App.Settings.Save();
    }

    private void PreviewTune_Click(object? sender, RoutedEventArgs e)
        => PlatformServices.Sounds.Play(SelectedTuneTag);

    private void TestNotification_Click(object? sender, RoutedEventArgs e)
    {
        // Fire a real reminder toast so the user can verify their notification
        // settings (sound, accent color, auto-dismiss) are working end-to-end.
        if (App.Notifications is { } notifications)
            notifications.SendReminder("This is a test reminder — your notifications are working!");
        else
            InAppNotificationService.Error("Notification service is not available.");
    }

    private void AccentColor_Click(object? sender, RoutedEventArgs e)
    {
        var tag = (sender as RadioButton)?.Tag?.ToString() ?? "#60A5FA";
        App.Settings.Current.NotifAccentColor = tag;
        App.Settings.Save();
    }

    private void DismissTime_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as RadioButton)?.Tag?.ToString() is string t && int.TryParse(t, out int secs))
        {
            App.Settings.Current.NotifAutoDismissSecs = secs;
            App.Settings.Save();
        }
    }

    private void CompletionPopupToggle_Click(object? sender, RoutedEventArgs e)
    {
        App.Settings.Current.ShowCompletionPopup = CompletionPopupToggle.IsChecked == true;
        App.Settings.Save();
    }

    private void SaveScreenshotToggle_Click(object? sender, RoutedEventArgs e)
    {
        App.Settings.Current.SaveScreenshotToDesktop = SaveScreenshotToggle.IsChecked == true;
        App.Settings.Save();
    }

    // Add panel

    private void AddToggle_Click(object? sender, RoutedEventArgs e)
    {
        SetAddFormOpen(!_addPanelOpen);
        if (_addPanelOpen)
        {
            Vm.NewDueDate = DateTime.Today;  // always default to today when form opens
            TitleInputBox.Focus();
        }
        else
        {
            NewTaskAttachments.Clear();   // cancelled
        }
    }

    private void AddTask_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(Vm.NewTitle)) return;

        Vm.AddTask();   // creates and sorts the task
        SyncNewTaskPickersToViewModel();   // AddTask resets the group/repeat defaults

        // The newest task IS the one just added — find it by CreatedAt (most recent)
        var newTask = Vm.Tasks.OrderByDescending(t => t.Model.CreatedAt).FirstOrDefault();
        if (newTask != null && NewTaskAttachments.Count > 0)
        {
            int saved = NewTaskAttachments.SaveAll(
                (png, ext) => Vm.SaveAttachmentFromBytes(newTask.Id, png, ext),
                path => Vm.SaveAttachment(newTask.Id, path));
            Vm.RefreshAttachmentCount(newTask);
            if (saved > 0) InAppNotificationService.Success($"Task created with {saved} attachment(s).");
        }

        SetAddFormOpen(false);
    }

    /// <summary>Shows or hides the New Task form (with a short drop-in) and flips the button to Cancel.</summary>
    private void SetAddFormOpen(bool open)
    {
        _addPanelOpen          = open;
        AddFormPanel.IsVisible = open;
        AddToggleText.Text     = open ? "Cancel" : "New Task";
        AddToggleIcon.Data     = UiResources.Geometry(open ? "Icon.Close" : "Icon.Plus");
        if (!open) return;

        var tt = new TranslateTransform(0, -8);
        AddFormPanel.RenderTransform = tt;
        var ease = new CubicEaseOut();
        _ = Animator.Animate(tt, TranslateTransform.YProperty, -8, 0, PageAnimation, ease);
        _ = Animator.Fade(AddFormPanel, 0, 1, PageAnimation, ease);
    }

    private void TitleBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) AddTask_Click(this, new RoutedEventArgs());
    }

    // Filter chips

    private void Filter_All_Click(object? sender, RoutedEventArgs e)      => Vm.ActiveFilter = TaskFilter.All;
    private void Filter_Today_Click(object? sender, RoutedEventArgs e)    => Vm.ActiveFilter = TaskFilter.Today;
    private void Filter_ThisWeek_Click(object? sender, RoutedEventArgs e) => Vm.ActiveFilter = TaskFilter.ThisWeek;

    // Task event handlers

    private async void Checkbox_Click(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (GetTaskVm(sender) is { } vm) await ConfirmAndCompleteAsync(vm, sender as CheckBox);
    }

    /// <summary>Asks, then completes the task (from its checkbox or the Space shortcut).</summary>
    private async Task ConfirmAndCompleteAsync(TaskItemViewModel vm, CheckBox? box = null)
    {
        // The checkbox binding is one-way: clicking only ticks the box on screen and
        // the task itself is untouched until the user confirms. On cancel, untick it.
        var confirm = new ConfirmDialog(
            "Mark as Done",
            $"Mark \"{vm.Title}\" as complete?",
            "Mark Done", ConfirmType.Info);
        if (await ShowModalDialog(confirm) != true)
        {
            box?.SetCurrentValue(ToggleButton.IsCheckedProperty, vm.IsCompleted);
            return;
        }

        // The tick pops, then the card folds away before the task leaves the list
        box?.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
        await AnimateCardOutAsync(vm, box);

        // 1. Mark as completed first (task moves to CompletedTasks)
        bool ok = Vm.CompleteTask(vm, out bool needsManualTime);
        if (!ok) return;

        // 2. Handle manual time entry if needed
        if (needsManualTime)
        {
            var dlg = new ManualTimeDialog();
            if (await ShowModalDialog(dlg) == true && dlg.ResultMinutes.HasValue)
            { vm.ManualTimeMinutes = dlg.ResultMinutes; Vm.SaveTask(vm); }
        }

        // 3. Refresh stats if visible
        if (StatsPage.IsVisible) { Vm.RefreshStats(_statsWeekOffset); BuildStatsUI(); }

        // 4. Show celebration popup with confetti (task is already saved)
        if (!App.Settings.Current.ShowCompletionPopup) return;
        ShowDimOverlay();
        var celebration = new TaskCompletedDialog(vm);
        celebration.Closed += (_, _) => HideDimOverlay();
        celebration.Show(this);
        celebration.Activate();
    }

    private async void Completed_Uncheck(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (GetTaskVm(sender) is not { } vm) return;
        var confirm = new ConfirmDialog(
            "Resume Task",
            $"Move \"{vm.Title}\" back to active tasks?",
            "Resume", ConfirmType.Warning);
        if (await ShowModalDialog(confirm) != true) return;
        Vm.UncompleteTask(vm);
        RefreshCompletedList();
    }

    private void ShowMore_Click(object? sender, RoutedEventArgs e)
    {
        if (GetTaskVm(sender) is { } vm) vm.IsDescriptionExpanded = !vm.IsDescriptionExpanded;
    }

    private void ActiveToggle_Click(object? sender, RoutedEventArgs e)
    {
        if (GetTaskVm(sender) is { } vm) ToggleTimer(vm);
    }

    /// <summary>Starts or stops the task's timer, animating the list as the task moves to the top.</summary>
    private void ToggleTimer(TaskItemViewModel vm)
    {
        var before = DragDropBehavior.CapturePositions(TaskListControl);
        Vm.ToggleActive(vm);
        Dispatcher.UIThread.Post(() => DragDropBehavior.AnimateFlip(TaskListControl, before), DispatcherPriority.Background);
    }

    private void Pause_Click(object? sender, RoutedEventArgs e)
    {
        if (GetTaskVm(sender) is { } vm) Vm.TogglePause(vm);
    }

    private async void Edit_Click(object? sender, RoutedEventArgs e)
    {
        if (GetTaskVm(sender) is { } vm) await EditTaskAsync(vm);
    }

    /// <summary>Opens the Edit Task page for <paramref name="vm"/> (Save / Cancel / Esc come back to the list).</summary>
    private Task EditTaskAsync(TaskItemViewModel vm)
    {
        if (_addPanelOpen) SetAddFormOpen(false);
        var page = new Views.EditTaskPage(vm, Vm);
        page.Saved += task =>
        {
            Vm.SaveTask(task);
            // Preserve the user's custom sort order — just refresh the display
            Vm.RefreshVisibleTasks();
            NavigateTo(AppPage.Main);
            HighlightCard(task);
        };
        page.Cancelled += () => NavigateTo(AppPage.Main);

        _editPage = page;
        EditTaskPageHost.Children.Clear();
        EditTaskPageHost.Children.Add(page);
        ApplyBreakpoints(RootGrid.Bounds.Width);
        NavigateTo(AppPage.EditTask);
        Dispatcher.UIThread.Post(page.FocusTitle, DispatcherPriority.Input);
        return Task.CompletedTask;
    }

    private void CloseEditPage()
    {
        if (_editPage is null) return;
        EditTaskPageHost.Children.Clear();
        _editPage = null;
    }

    /// <summary>After an edit: scroll the card into view and flash its border, so you see what changed.</summary>
    private void HighlightCard(TaskItemViewModel task)
    {
        Dispatcher.UIThread.Post(() =>
        {
            int index = Vm.VisibleTasks.IndexOf(task);
            if (index < 0) return;
            TaskListControl.ScrollIntoView(index);   // builds the card if it was off screen
            if (TaskListControl.ContainerFromIndex(index) is not { } container) return;
            container.BringIntoView();
            if (container.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("taskTile")) is not { } card)
                return;
            card.Classes.Add("justEdited");
            DispatcherTimer.RunOnce(() => card.Classes.Remove("justEdited"), TimeSpan.FromMilliseconds(900));
        }, DispatcherPriority.Background);
    }

    private async void Reminder_Click(object? sender, RoutedEventArgs e)
    {
        if (GetTaskVm(sender) is not { } vm) return;
        await ShowModalDialog(new ReminderDialog(vm.Id, vm.Title, App.Database!));
        Vm.RefreshReminderCount(vm);  // update badge after dialog closes
    }

    private async void Attachment_Click(object? sender, RoutedEventArgs e)
    {
        if (GetTaskVm(sender) is not { } vm) return;
        await ShowModalDialog(new AttachmentDialog(vm.Id, vm.Title, Vm));
    }

    private async void Delete_Click(object? sender, RoutedEventArgs e)
    {
        if (GetTaskVm(sender) is { } vm) await ConfirmAndDeleteAsync(vm);
    }

    private async Task ConfirmAndDeleteAsync(TaskItemViewModel vm)
    {
        var dlg = new ConfirmDialog("Delete Task",
            $"Move \"{vm.Title}\" to the recycle bin?\n\nYou can restore it for {MainViewModel.TrashRetentionDays} days.",
            "Delete", ConfirmType.Danger);
        if (await ShowModalDialog(dlg) == true) Vm.DeleteTask(vm);
    }

    // Postpone

    private void Postpone_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control anchor && GetTaskVm(sender) is { } vm) ShowPostponeMenu(anchor, vm);
    }

    /// <summary>
    /// Tomorrow, the rest of this week, any day of next week, or two weeks out.
    /// Built per task so each item knows its task.
    /// </summary>
    private void ShowPostponeMenu(Control anchor, TaskItemViewModel vm)
    {
        var today = DateTime.Today;
        var menu  = new MenuFlyout();

        MenuItem DayItem(string label, DateTime day)
        {
            var item = new MenuItem { Header = label };
            item.Click += (_, _) => Vm.Postpone(vm, day);
            return item;
        }

        menu.Items.Add(DayItem($"Tomorrow · {today.AddDays(1):ddd d MMM}", today.AddDays(1)));

        var thisWeek = PostponeOptions.RestOfThisWeek(today).ToList();
        if (thisWeek.Count > 0)
        {
            var later = new MenuItem { Header = "Later this week" };
            foreach (var day in thisWeek) later.Items.Add(DayItem(PostponeOptions.DayLabel(day), day));
            menu.Items.Add(later);
        }

        var nextWeek = PostponeOptions.DaysOfNextWeek(today).ToList();
        var next = new MenuItem { Header = "Next week" };
        foreach (var day in nextWeek) next.Items.Add(DayItem(PostponeOptions.DayLabel(day), day));
        menu.Items.Add(next);

        menu.Items.Add(new Separator());
        menu.Items.Add(DayItem($"Next Monday · {nextWeek[0]:d MMM}", nextWeek[0]));
        menu.Items.Add(DayItem($"In 2 weeks · {nextWeek[0].AddDays(7):ddd d MMM}", nextWeek[0].AddDays(7)));

        menu.ShowAt(anchor);
    }

    // ── Task card: click anywhere to edit, with a pressed-tile effect ─────
    // The card's own buttons, checkbox and timer switch handle their clicks themselves, so those
    // never reach the card. A drag to reorder is handled by DragDropBehavior and isn't a click either.

    private Border? _pressedCard;

    private void TaskCard_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border card || !e.GetCurrentPoint(card).Properties.IsLeftButtonPressed) return;
        if (IsInsideControl(e.Source as Visual, card)) return;
        _pressedCard = card;
        card.Classes.Add("pressed");
    }

    /// <summary>Clicking a task opens it for editing (Enter on a selected task still shows the read-only detail).</summary>
    private async void TaskCard_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is not Border card) return;
        bool wasPressed = _pressedCard == card;
        ReleasePressedCard();
        if (!wasPressed || !IsLeftClick(e) || IsInsideControl(e.Source as Visual, card)) return;
        // Only when the button is let go over the same card
        if (!new Rect(card.Bounds.Size).Contains(e.GetPosition(card))) return;
        if (GetTaskVm(card) is { } vm) await EditTaskAsync(vm);
    }

    private void TaskCard_PointerCaptureLost(object? sender, PointerCaptureLostEventArgs e) => ReleasePressedCard();

    private void TaskCard_PointerExited(object? sender, PointerEventArgs e)
    {
        if (sender == _pressedCard) _pressedCard?.Classes.Remove("pressed");   // pops back up; re-entering doesn't click
    }

    private void ReleasePressedCard()
    {
        _pressedCard?.Classes.Remove("pressed");
        _pressedCard = null;
    }

    /// <summary>True when <paramref name="source"/> is in a button, checkbox or text box on the card.</summary>
    private static bool IsInsideControl(Visual? source, Visual card)
    {
        for (var v = source; v is not null && v != card; v = v.GetVisualParent())
            if (v is Button or Avalonia.Controls.Primitives.ToggleButton or TextBox) return true;
        return false;
    }

    private void ShowDimmedDetail(TaskItemViewModel vm)
    {
        ShowDimOverlay();
        var dlg = new TaskDetailDialog(vm, Vm);
        dlg.Closed += (_, _) => HideDimOverlay();
        dlg.Show(this);
    }

    // Completed page

    private void CGroup_Click(object? sender, RoutedEventArgs e)
    {
        _completedGroupFilter = (sender as RadioButton)?.Tag?.ToString() ?? "All";
        // Clear date picker when a chip filter is selected
        CompletedDatePicker.SelectedDate = null;
        RefreshCompletedList();
    }

    private void CompletedDate_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (CompletedDatePicker.SelectedDate == null) return;
        // Deselect all chip filters when a specific date is chosen
        CChipAll.IsChecked = false;
        _completedGroupFilter = "Date";
        RefreshCompletedList();
    }

    private void Sort_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_viewChoicesRestored && SortBox.SelectedItem is ComboBoxItem { Tag: string sort })
            RememberChoice(s => s.CompletedSort = sort);
        if (DataContext is MainViewModel) RefreshCompletedList();
    }

    private void RefreshCompletedList()
    {
        if (DataContext is not MainViewModel) return;
        var search = (Vm.CompletedSearch ?? string.Empty).Trim();
        IEnumerable<TaskItemViewModel> filtered = Vm.CompletedTasks;
        if (!string.IsNullOrEmpty(search))
            filtered = filtered.Where(v =>
                v.Title.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                v.TaskNumberDisplay.Contains(search, StringComparison.OrdinalIgnoreCase));
        if (_completedGroupFilter == "Today")
            filtered = filtered.Where(v => v.CompletedGroup == "Today");
        else if (_completedGroupFilter == "This Week")
            filtered = filtered.Where(v => v.CompletedGroup is "Today" or "This Week");
        else if (_completedGroupFilter == "This Month")
            filtered = filtered.Where(v => v.CompletedAt.HasValue &&
                v.CompletedAt.Value.Year == DateTime.Today.Year &&
                v.CompletedAt.Value.Month == DateTime.Today.Month);
        else if (_completedGroupFilter == "Date" && CompletedDatePicker.SelectedDate is DateTime picked)
            filtered = filtered.Where(v => v.CompletedAt.HasValue &&
                v.CompletedAt.Value.Date == picked.Date);

        var sortTag = (SortBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "CompletedDesc";
        static int TimeKey(TaskItemViewModel v)
            => v.TotalSeconds > 0 ? v.TotalSeconds : (v.ManualTimeMinutes ?? 0) * 60;

        // Only TaskItemViewModels in the source — no mixed string headers
        var items = new List<TaskItemViewModel>();
        foreach (var g in filtered.GroupBy(v => v.CompletedGroup)
                                  .OrderBy(g => g.Key == "Today" ? 0 : g.Key == "This Week" ? 1 : 2))
        {
            IEnumerable<TaskItemViewModel> sorted = sortTag switch
            {
                "oldest"    => g.OrderBy(v => v.CompletedAt ?? DateTime.MinValue),
                "time_desc" => g.OrderByDescending(TimeKey),
                "time_asc"  => g.OrderBy(TimeKey),
                _           => g.OrderByDescending(v => v.CompletedAt ?? DateTime.MinValue)
            };
            items.AddRange(sorted);
        }
        CompletedList.ItemsSource = items;
        CompletedEmptyState.IsVisible = items.Count == 0;
    }

    private async void CompletedTask_Click(object? sender, PointerReleasedEventArgs e)
    {
        if (IsLeftClick(e) && GetTaskVm(sender) is { } vm) await OpenTaskDetail(vm);
    }

    private async void CompletedUpdateTime_Click(object? sender, RoutedEventArgs e)
    {
        if (GetTaskVm(sender) is not { } vm) return;
        var dlg = new ManualTimeDialog();

        // Pre-fill with whatever time is currently showing:
        // timer-tracked seconds take precedence over ManualTimeMinutes
        int currentMinutes = vm.TotalSeconds > 0
            ? Math.Max(1, vm.TotalSeconds / 60)
            : vm.ManualTimeMinutes ?? 0;
        if (currentMinutes > 0)
            dlg.PresetMinutes(currentMinutes);

        if (await ShowModalDialog(dlg) == true && dlg.ResultMinutes.HasValue)
        {
            // Store as accumulated seconds so if the task is restored, the timer
            // continues from this value rather than restarting from zero.
            vm.Model.AccumulatedSeconds = dlg.ResultMinutes.Value * 60;
            vm.ManualTimeMinutes = null;
            Vm.SaveTask(vm);
            RefreshCompletedList();
            InAppNotificationService.Success($"Time updated for \"{vm.Title}\".");
        }
    }

    private async void CompletedDelete_Click(object? sender, RoutedEventArgs e)
    {
        if (GetTaskVm(sender) is not { } vm) return;
        await ConfirmAndDeleteAsync(vm);   // completed tasks go to the recycle bin too
        RefreshCompletedList();
    }

    // Recycle bin

    private void OpenTrash_Click(object? sender, RoutedEventArgs e) => NavigateTo(AppPage.Trash);

    private void RefreshTrashList()
    {
        if (DataContext is not MainViewModel) return;
        TrashEmptyState.IsVisible = Vm.DeletedTasks.Count == 0;
    }

    private void Restore_Click(object? sender, RoutedEventArgs e)
    {
        if (GetTaskVm(sender) is not { } vm) return;
        Vm.RestoreTask(vm);
        RefreshTrashList();
        InAppNotificationService.Success($"Restored \"{vm.Title}\".");
    }

    private async void PermanentDelete_Click(object? sender, RoutedEventArgs e)
    {
        if (GetTaskVm(sender) is not { } vm) return;
        var dlg = new ConfirmDialog("Delete Permanently",
            "Permanently delete \"" + vm.Title + "\"?\n\nThis cannot be undone.",
            "Delete", ConfirmType.Danger);
        if (await ShowModalDialog(dlg) == true) { Vm.PermanentlyDeleteTask(vm); RefreshTrashList(); }
    }

    private async void Migrate_Click(object? sender, RoutedEventArgs e)
    {
        if (App.Database is null) return;
        await ShowModalDialog(new MigrateDialog(new Services.Migration.MigrationService(App.Database)));
    }

    private async void ClearMeetingHistory_Click(object? sender, RoutedEventArgs e)
    {
        var dlg = new ConfirmDialog("Clear Meeting History",
            "Delete all auto-detected meeting sessions?\n\nThis cannot be undone.",
            "Clear", ConfirmType.Danger);
        if (await ShowModalDialog(dlg) != true) return;
        App.Database!.ClearMeetingSessions();
        if (StatsPage.IsVisible) { Vm.RefreshStats(_statsWeekOffset); BuildStatsUI(); }
        InAppNotificationService.Success("Meeting history cleared.");
    }

    private async void EmptyBin_Click(object? sender, RoutedEventArgs e)
    {
        if (Vm.DeletedTasks.Count == 0)
        {
            InAppNotificationService.Info("The recycle bin is already empty.");
            return;
        }
        var dlg = new ConfirmDialog("Empty Recycle Bin",
            $"Permanently delete all {Vm.DeletedTasks.Count} item(s) in the bin?\n\nThis cannot be undone.",
            "Empty Bin", ConfirmType.Danger);
        if (await ShowModalDialog(dlg) == true) { Vm.EmptyTrash(); RefreshTrashList(); }
    }

    // Notes page

    private void NewNote_Click(object? sender, RoutedEventArgs e)
    {
        _addNotePanelOpen = !_addNotePanelOpen;
        AddNotePanel.IsVisible = _addNotePanelOpen;
        if (_addNotePanelOpen)
        {
            NoteTitleBox.Text = string.Empty;
            NoteDescBox.Text  = string.Empty;
            NoteTagsBox.Text  = string.Empty;
            NoteTitleBox.Focus();
        }
        NewNoteAttachments.Clear();
    }

    private void CancelNote_Click(object? sender, RoutedEventArgs e)
    {
        _addNotePanelOpen = false;
        AddNotePanel.IsVisible = false;
        NewNoteAttachments.Clear();
    }

    private void SaveNote_Click(object? sender, RoutedEventArgs e)
    {
        var title = NoteTitleBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(title))
        {
            InAppNotificationService.Warning("Note title cannot be empty.");
            return;
        }
        var note = Vm.AddNote(title, NoteDescBox.Text?.Trim() ?? string.Empty, NoteTagsBox.Text?.Trim() ?? string.Empty);
        int attached = NewNoteAttachments.SaveAll(
            (png, ext) => Vm.SaveNoteAttachmentFromBytes(note.Id, png, ext),
            path => Vm.SaveNoteAttachment(note.Id, path));
        _addNotePanelOpen = false;
        AddNotePanel.IsVisible = false;
        Vm.LoadNotes();   // also refreshes each note's attachment count
        RefreshNotesEmpty();
        InAppNotificationService.Success(attached > 0 ? $"Note saved with {attached} attachment(s)." : "Note saved.");
    }

    private void Note_Click(object? sender, PointerReleasedEventArgs e)
    {
        if (IsLeftClick(e) && sender is Control { DataContext: Note note }) OpenNotePage(note);
    }

    /// <summary>Opens a note on its own page (Back / Esc / Save come back to the notes list).</summary>
    private void OpenNotePage(Note note)
    {
        var page = new Views.NotePage(note, Vm, ShowModalDialog);
        page.Closed += () => NavigateTo(AppPage.Notes);
        _notePage = page;
        NotePageHost.Children.Clear();
        NotePageHost.Children.Add(page);
        NavigateTo(AppPage.NoteDetail);
    }

    private void CloseNotePage()
    {
        if (_notePage is null) return;
        NotePageHost.Children.Clear();
        _notePage = null;
    }

    private async void DeleteNote_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: Note note }) return;
        var dlg = new ConfirmDialog("Delete Note",
            $"Delete \"{note.Title}\"? This cannot be undone.",
            "Delete", ConfirmType.Danger);
        if (await ShowModalDialog(dlg) == true) { Vm.DeleteNote(note); RefreshNotesEmpty(); }
    }

    private void RefreshNotesEmpty()
    {
        NotesEmptyLabel.IsVisible = Vm.FilteredNotes.Count == 0;
    }

    // ── Helpers ───────────────────────────────────────────────────────────
    private static TaskItemViewModel? GetTaskVm(object? sender)
        => (sender as StyledElement)?.DataContext as TaskItemViewModel;

    // ── Narrow window: a card's actions behind ⋯ ─────────────────────────
    private void CardMore_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button more || GetTaskVm(sender) is not { } vm) return;

        var flyout = new Flyout { Placement = PlacementMode.BottomEdgeAlignedRight, FlyoutPresenterClasses = { "workspaceMenu" } };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };

        void Add(string icon, string tip, string cls, EventHandler<RoutedEventArgs> action, bool visible = true)
        {
            if (!visible) return;
            var button = new Button
            {
                Theme       = UiResources.Theme("IconButton"),
                Classes     = { cls },
                DataContext = vm,
                Content     = new LineIcon { Data = UiResources.Geometry(icon) },
            };
            ToolTip.SetTip(button, tip);
            button.Click += (s, args) => { flyout.Hide(); action(s, args); };
            row.Children.Add(button);
        }

        Add(vm.IsPaused ? "Icon.Play" : "Icon.Pause", "Pause / Resume", "timer", Pause_Click, vm.IsActive);
        Add("Icon.Clip",  "Attachments",  "attach",   Attachment_Click);
        Add("Icon.Skip",  "Postpone",     "postpone", Postpone_Click);
        Add("Icon.Edit",  "Edit",         "edit",     Edit_Click);
        Add("Icon.Bell",  "Set reminder", "remind",   Reminder_Click);
        Add("Icon.Trash", "Delete",       "delete",   Delete_Click);

        flyout.Content = new Border
        {
            Background      = UiResources.Brush("CardBackground"),
            BorderBrush     = UiResources.Brush("BorderColor"),
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(12),
            Padding         = new Thickness(4),
            Child           = row,
        };
        flyout.ShowAt(more);
    }

    private static bool IsLeftClick(PointerReleasedEventArgs e)
        => e.InitialPressMouseButton == MouseButton.Left;

    // ── Open task detail dialog ───────────────────────────────────────────
    public async Task OpenTaskDetail(TaskItemViewModel vm)
        => await ShowModalDialog(new TaskDetailDialog(vm, Vm));

    // ── Stats UI ─────────────────────────────────────────────────────────

    private static TextBlock ChartLabel(string text, double fontSize, string brushKey) =>
        new TextBlock { Text = text, FontSize = fontSize, HorizontalAlignment = HorizontalAlignment.Center }
            .WithResource(TextBlock.ForegroundProperty, brushKey);

    private void BuildStatsUI()
    {
        StatTodayCount.Text = Vm.StatsCompletedToday.ToString();
        StatWeekCount.Text  = Vm.StatsCompletedWeek.ToString();
        StatAllTime.Text    = Vm.StatsCompletedAllTime.ToString();
        StatTimeToday.Text  = Vm.StatsTimeToday;
        UpdateMeetingDisplay();

        // Update week navigation label and Next button state
        WeekLabel.Text = Vm.Last7DaysWeekLabel;
        NextWeekBtn.IsEnabled = _statsWeekOffset < 0;

        BarChartPanel.Children.Clear();
        foreach (var (day, count) in Vm.Last7DaysData)
        {
            int    max    = Math.Max(Vm.Last7DaysMax, 1);
            double height = Math.Max(4, 80.0 * count / max);
            var col = new StackPanel
                { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(3, 0, 3, 0) };
            var bar = new Border { Height = height, CornerRadius = new CornerRadius(3, 3, 0, 0) }
                .WithResource(Border.BackgroundProperty, "PrimaryBlue");
            col.Children.Add(ChartLabel(count > 0 ? count.ToString() : string.Empty, 10, "TextPrimary"));
            col.Children.Add(bar);
            col.Children.Add(ChartLabel(day, 10, "TextSecondary"));
            BarChartPanel.Children.Add(col);
        }

        BuildTimeChartPanel();

        TopTasksPanel.Children.Clear();
        static int TimeKey(TaskItemViewModel v)  // local function — avoids closure allocation
            => v.TotalSeconds > 0 ? v.TotalSeconds : (v.ManualTimeMinutes ?? 0) * 60;
        var today = DateTime.Today;
        IEnumerable<TaskItemViewModel> pool = _topTasksFilter switch
        {
            "today" => Vm.CompletedTasks.Where(t => t.CompletedAt?.Date == today),
            "week"  => Vm.CompletedTasks.Where(t => t.CompletedAt?.Date >= today.AddDays(-6)),
            "month" => Vm.CompletedTasks.Where(t => t.CompletedAt.HasValue &&
                           t.CompletedAt.Value.Year == today.Year &&
                           t.CompletedAt.Value.Month == today.Month),
            _       => Vm.CompletedTasks
        };
        var topTasks = pool.Where(t => TimeKey(t) > 0).OrderByDescending(TimeKey).Take(5).ToList();
        TopTasksEmpty.IsVisible = topTasks.Count == 0;
        foreach (var t in topTasks)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var title = new TextBlock { Text = t.Title, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis }
                .WithResource(TextBlock.ForegroundProperty, "TextPrimary");
            Grid.SetColumn(title, 0);
            var time = new TextBlock
                { Text = t.TimeDisplay, FontSize = 12, Margin = new Thickness(8, 0, 0, 0) }
                .WithResource(TextBlock.FontFamilyProperty, "MonoFont")
                .WithResource(TextBlock.ForegroundProperty, "AccentGreen");
            Grid.SetColumn(time, 1);
            row.Children.Add(title);
            row.Children.Add(time);
            TopTasksPanel.Children.Add(row);
        }

        BuildBusiestHourChart();
        BuildWeeklyTimeChart();
        RefreshAndBuildAppUsage();
    }

    private void TopFilter_Click(object? sender, RoutedEventArgs e)
    {
        _topTasksFilter = (sender as RadioButton)?.Tag?.ToString() ?? "overall";
        RememberChoice(s => s.TopTasksFilter = _topTasksFilter);
        BuildStatsUI();
    }

    // ── Week navigation (Stats page) ─────────────────────────────────────────

    private void PrevWeek_Click(object? sender, RoutedEventArgs e)
    {
        _statsWeekOffset--;
        NextWeekBtn.IsEnabled = _statsWeekOffset < 0;
        Vm.RefreshStats(_statsWeekOffset);
        BuildStatsUI();
    }

    private void NextWeek_Click(object? sender, RoutedEventArgs e)
    {
        if (_statsWeekOffset >= 0) return;
        _statsWeekOffset++;
        NextWeekBtn.IsEnabled = _statsWeekOffset < 0;
        Vm.RefreshStats(_statsWeekOffset);
        BuildStatsUI();
    }

    private void WeeklyToggle_Click(object? sender, RoutedEventArgs e)
    {
        _weeklyChartShowTime = WeeklyTimeToggle.IsChecked == true;
        BuildWeeklyTimeChart();
    }

    private void MeetingFilter_Click(object? sender, RoutedEventArgs e)
    {
        _meetingFilter = (sender as RadioButton)?.Tag?.ToString() ?? "week";
        RememberChoice(s => s.MeetingFilter = _meetingFilter);
        // Re-query the DB for fresh data each time the chip changes
        Vm.RefreshStats(_statsWeekOffset);
        UpdateMeetingDisplay();
    }

    private void UpdateMeetingDisplay()
    {
        StatMeetingDisplay.Text = _meetingFilter switch
        {
            "today" => Vm.StatMeetingHoursToday,
            "month" => Vm.StatMeetingHoursMonth,
            _       => Vm.StatMeetingHoursWeek
        };
    }

    private void BuildTimeChartPanel()
    {
        TimeChartPanel.Children.Clear();
        var data = Vm.Last7DaysTimeData;
        int max  = Math.Max(Vm.Last7DaysTimeMax, 1);

        foreach (var (day, secs) in data)
        {
            double height = Math.Max(4, 80.0 * secs / max);
            var col = new StackPanel
                { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(3, 0, 3, 0) };
            var bar = new Border { Height = height, CornerRadius = new CornerRadius(3, 3, 0, 0) }
                .WithResource(Border.BackgroundProperty, "AccentGreen");
            col.Children.Add(ChartLabel(secs > 0 ? FormatSecsShort(secs) : string.Empty, 9, "TextPrimary"));
            col.Children.Add(bar);
            col.Children.Add(ChartLabel(day, 10, "TextSecondary"));
            TimeChartPanel.Children.Add(col);
        }
    }

    private static string FormatSecsShort(int secs)
    {
        if (secs <= 0) return string.Empty;
        var ts = TimeSpan.FromSeconds(secs);
        return ts.TotalHours >= 1 ? $"{(int)ts.TotalHours}h{ts.Minutes:D2}m" : $"{ts.Minutes}m";
    }

    // Full-precision format used in App Usage panel: "1h 20m 19s" or "30m 20s" or "45s"
    private static string FormatSecsFull(int secs)
    {
        if (secs <= 0) return "0s";
        var ts = TimeSpan.FromSeconds(secs);
        if (ts.TotalHours >= 1)
            return $"{(int)ts.TotalHours}h {ts.Minutes}m {ts.Seconds}s";
        if (ts.Minutes > 0)
            return $"{ts.Minutes}m {ts.Seconds}s";
        return $"{ts.Seconds}s";
    }

    private void BuildBusiestHourChart()
    {
        BusiestHourPanel.Children.Clear();
        var data     = Vm.BusiestHourData;   // 15 slots: 7 AM–9 PM, avg/day last 30 days
        double maxVal = data.Max();
        var accent    = UiResources.Brush("PrimaryBlue") as ISolidColorBrush ?? new SolidColorBrush(Colors.CornflowerBlue);

        BusiestHourEmpty.IsVisible = maxVal == 0;
        if (maxVal == 0) return;

        for (int i = 0; i < 15; i++)
        {
            int    hour   = i + 7;
            double avg    = data[i];
            double height = avg == 0 ? 4 : Math.Max(6, 64.0 * avg / maxVal);
            byte   alpha  = avg == 0 ? (byte)35 : (byte)(80 + (int)(175.0 * avg / maxVal));

            var bar = new Border
            {
                Height       = height,
                CornerRadius = new CornerRadius(3, 3, 0, 0),
                Background   = new SolidColorBrush(accent.Color, alpha / 255.0)
            };
            ToolTip.SetTip(bar, $"{hour:D2}:00: avg {avg:F2} completions/active day (last 30 days)");

            var col = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin            = new Thickness(2, 0, 2, 0)
            };
            col.Children.Add(bar);
            col.Children.Add(ChartLabel($"{hour:D2}", 8, "TextSecondary"));
            BusiestHourPanel.Children.Add(col);
        }
    }

    // ── App Usage panel ───────────────────────────────────────────────────────

    private void AppUsageFilter_Click(object? sender, RoutedEventArgs e)
    {
        _appUsageFilter = (sender as RadioButton)?.Tag?.ToString() ?? "today";
        RememberChoice(s => s.AppUsageFilter = _appUsageFilter);
        RefreshAndBuildAppUsage();
    }

    private void AppUsageRefresh_Click(object? sender, RoutedEventArgs e)
        => RefreshAndBuildAppUsage();

    /// <summary>Wheel turns over the horizontal weekly chart scroll the whole Stats page instead.</summary>
    private const double WheelScrollStep = 50;

    private void InnerScroll_PreviewMouseWheel(object? sender, PointerWheelEventArgs e)
    {
        if (e.Handled) return;
        e.Handled = true;
        var offset = StatsScrollViewer.Offset;
        StatsScrollViewer.Offset = offset.WithY(Math.Max(0, offset.Y - e.Delta.Y * WheelScrollStep));
    }

    private void RefreshAndBuildAppUsage()
    {
        var today = DateTime.Today;
        DateTime from, to;
        switch (_appUsageFilter)
        {
            case "week":
                from = today.AddDays(-6);
                to   = today.AddDays(1);
                break;
            case "month":
                from = new DateTime(today.Year, today.Month, 1);
                to   = today.AddDays(1);
                break;
            default:
                from = today;
                to   = today.AddDays(1);
                break;
        }
        Vm.RefreshAppUsage(from, to);
        BuildAppUsagePanel();
    }

    private static readonly Color[] _avatarPalette =
    [
        Color.FromRgb( 99, 102, 241),
        Color.FromRgb( 16, 185, 129),
        Color.FromRgb(245, 158,  11),
        Color.FromRgb(239,  68,  68),
        Color.FromRgb( 59, 130, 246),
        Color.FromRgb(168,  85, 247),
        Color.FromRgb(236,  72, 153),
        Color.FromRgb( 20, 184, 166),
    ];

    private static Color AvatarColor(string name)
        => _avatarPalette[Math.Abs(name.GetHashCode()) % _avatarPalette.Length];

    private void BuildAppUsagePanel()
    {
        AppUsagePanel.Children.Clear();

        var data = Vm.AppUsageData;
        AppUsageEmpty.IsVisible = data.Count == 0;
        if (data.Count == 0) return;

        int maxSecs = Math.Max(1, data[0].Seconds);

        foreach (var (appName, secs, exePath) in data)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Icon or letter-avatar (tooltip shows the full app name)
            Control iconCell;
            if (PlatformServices.AppIcons.GetIcon(exePath) is { } appIcon)
            {
                var img = new Image
                {
                    Source            = appIcon,
                    Width             = 24, Height = 24,
                    VerticalAlignment = VerticalAlignment.Center
                };
                RenderOptions.SetBitmapInterpolationMode(img, BitmapInterpolationMode.HighQuality);
                iconCell = img;
            }
            else
            {
                iconCell = new Border
                {
                    Width             = 24, Height = 24,
                    CornerRadius      = new CornerRadius(6),
                    Background        = new SolidColorBrush(AvatarColor(appName)),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock
                    {
                        Text                = appName.Length > 0 ? appName[0].ToString().ToUpperInvariant() : "?",
                        FontSize            = 12,
                        FontWeight          = FontWeight.Bold,
                        Foreground          = Brushes.White,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment   = VerticalAlignment.Center
                    }
                };
            }
            ToolTip.SetTip(iconCell, appName);
            Grid.SetColumn(iconCell, 0);

            // Progress bar
            var barGrid = new Grid
            {
                Margin            = new Thickness(8, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Height            = 6
            };
            var track = new Border { Height = 6, CornerRadius = new CornerRadius(3) }
                .WithResource(Border.BackgroundProperty, "InputBackground");
            double ratio = (double)secs / maxSecs;
            var fill = new Border
            {
                Height              = 6,
                CornerRadius        = new CornerRadius(3),
                HorizontalAlignment = HorizontalAlignment.Left,
                Background          = new SolidColorBrush(Color.FromRgb(52, 211, 153))
            };
            barGrid.SizeChanged += (_, a) => fill.Width = Math.Max(4, a.NewSize.Width * ratio);
            barGrid.Children.Add(track);
            barGrid.Children.Add(fill);
            Grid.SetColumn(barGrid, 1);

            var timeTb = new TextBlock
            {
                Text              = FormatSecsFull(secs),
                FontSize          = 11,
                VerticalAlignment = VerticalAlignment.Center,
                MinWidth          = 80,
                TextAlignment     = TextAlignment.Right
            }
            .WithResource(TextBlock.FontFamilyProperty, "MonoFont")
            .WithResource(TextBlock.ForegroundProperty, "AccentGreen");
            Grid.SetColumn(timeTb, 2);

            row.Children.Add(iconCell);
            row.Children.Add(barGrid);
            row.Children.Add(timeTb);
            AppUsagePanel.Children.Add(row);
        }
    }

    private void BuildWeeklyTimeChart()
    {
        WeeklyChartPanel.Children.Clear();
        string[] weekDayRanges = { "Days 1–7", "Days 8–14", "Days 15–21", "Days 22–end" };

        bool showTime = _weeklyChartShowTime;
        var data      = showTime ? Vm.WeeklyTimeData : Vm.WeeklyTaskData;
        int maxValue  = Math.Max(1, data.Max());
        bool anyData  = data.Any(v => v > 0);
        WeeklyChartEmpty.Text      = showTime ? "No tracked time yet." : "No task data yet.";
        WeeklyChartEmpty.IsVisible = !anyData;
        if (!anyData) return;

        for (int w = 0; w < 4; w++)
        {
            int    value  = data[w];
            double height = value == 0 ? 4 : Math.Max(8, 64.0 * value / maxValue);
            byte   alpha  = value == 0 ? (byte)30 : (byte)(70 + (int)(185.0 * value / maxValue));
            string tip    = showTime
                ? $"Week {w + 1} ({weekDayRanges[w]})\nAvg per month: {(value > 0 ? TaskItemViewModel.FormatSeconds(value) : "no data")}"
                : $"Week {w + 1} ({weekDayRanges[w]})\nAvg tasks/month: {(value > 0 ? $"{value}" : "no data")}";

            var bar = new Border
            {
                Height = height, Width = 54, CornerRadius = new CornerRadius(4, 4, 0, 0),
                Background = new SolidColorBrush(showTime
                    ? Color.FromArgb(alpha, 34, 197, 94)
                    : Color.FromArgb(alpha, 96, 165, 250))
            };
            ToolTip.SetTip(bar, tip);
            var lbl = ChartLabel($"Week {w + 1}", 10, "TextSecondary");
            lbl.Margin = new Thickness(0, 5, 0, 0);
            var sub = ChartLabel(
                value == 0 ? "—" : showTime ? TaskItemViewModel.FormatSeconds(value) : $"{value} avg",
                9, value == 0 ? "TextSecondary" : showTime ? "AccentGreen" : "PrimaryBlue");
            sub.Margin = new Thickness(0, 2, 0, 0);
            if (showTime) sub.WithResource(TextBlock.FontFamilyProperty, "MonoFont");
            var col = new StackPanel
                { VerticalAlignment = VerticalAlignment.Bottom, Width = 66, Margin = new Thickness(6, 0, 6, 0) };
            col.Children.Add(bar); col.Children.Add(lbl); col.Children.Add(sub);
            WeeklyChartPanel.Children.Add(col);
        }
    }
}

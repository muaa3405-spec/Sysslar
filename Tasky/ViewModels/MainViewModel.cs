using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using Tasky.Helpers;
using Tasky.Models;
using Tasky.Services;
using Tasky.Services.DataExchange;

namespace Tasky.ViewModels;

public enum TaskFilter { All, Today, ThisWeek }

public class MainViewModel : INotifyPropertyChanged
{
    private readonly DatabaseService _db;
    private string _newTitle = string.Empty;
    private string _newDescription = string.Empty;
    private DateTime? _newDueDate = DateTime.Today;
    private bool _showCompleted;
    private string _completedSearch = string.Empty;
    private bool _isDarkTheme;
    private TaskFilter _activeFilter = TaskFilter.Today;
    private int? _selectedGroupFilter = null;
    private string _newGroupName = string.Empty;
    private string _newGroupColor = "📁";
    private string _noteSearch = string.Empty;
    private string _taskSearch = string.Empty;
    private int? _newTaskGroupId = null;
    private RecurrenceType _newTaskRecurrence = RecurrenceType.None;
    private DateTime? _newTaskRecurrenceEndDate;

    private int _missedNotifCount;
    public int MissedNotifCount
    {
        get => _missedNotifCount;
        set { _missedNotifCount = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasMissedNotifs)); }
    }
    public bool HasMissedNotifs => _missedNotifCount > 0;

    public void RefreshMissedNotifCount()
        => MissedNotifCount = _db.GetMissedNotificationCount();

    private readonly AttachmentStorage _attachments;

    public MainViewModel(DatabaseService db)
    {
        _db = db;
        _attachments = new AttachmentStorage(db.AttachmentsRoot);
    }

    public ObservableCollection<TaskItemViewModel> Tasks { get; } = new();
    public ObservableCollection<TaskItemViewModel> CompletedTasks { get; } = new();
    public ObservableCollection<TaskItemViewModel> FilteredCompleted { get; } = new();
    public ObservableCollection<TaskItemViewModel> VisibleTasks { get; } = new();
    public ObservableCollection<TaskItemViewModel> DeletedTasks { get; } = new();

    /// <summary>Tasks are auto-purged from the recycle bin this many days after deletion.</summary>
    public const int TrashRetentionDays = 30;

    // Groups

    public ObservableCollection<Models.TaskGroup> Groups { get; } = new();

    public int? SelectedGroupFilter
    {
        get => _selectedGroupFilter;
        set { _selectedGroupFilter = value; OnPropertyChanged(); RefreshVisibleTasks(); }
    }

    public string NewGroupName
    {
        get => _newGroupName;
        set { _newGroupName = value; OnPropertyChanged(); }
    }

    public string NewGroupColor
    {
        get => _newGroupColor;
        set { _newGroupColor = value; OnPropertyChanged(); }
    }

    public int? NewTaskGroupId
    {
        get => _newTaskGroupId;
        set { _newTaskGroupId = value; OnPropertyChanged(); }
    }

    public RecurrenceType NewTaskRecurrence
    {
        get => _newTaskRecurrence;
        set { _newTaskRecurrence = value; OnPropertyChanged(); OnPropertyChanged(nameof(NewTaskRepeats)); }
    }

    /// <summary>True when the new task repeats, which shows its optional end date.</summary>
    public bool NewTaskRepeats => _newTaskRecurrence != RecurrenceType.None;

    public DateTime? NewTaskRecurrenceEndDate
    {
        get => _newTaskRecurrenceEndDate;
        set { _newTaskRecurrenceEndDate = value; OnPropertyChanged(); }
    }

    public void LoadGroups()
    {
        Groups.Clear();
        TaskItemViewModel.GroupsLookup.Clear();

        var all = _db.GetAllGroups();

        // Seed a default "Other" group if none exist yet
        if (all.Count == 0)
        {
            var seed = _db.InsertGroup(new Models.TaskGroup { Name = "Other", Color = "#6B7280" });
            all.Add(seed);
        }

        foreach (var g in all)
        {
            Groups.Add(g);
            TaskItemViewModel.GroupsLookup[g.Id] = g;
        }

        // Default new-task group: prefer a group named "Other", else first in list
        var other = Groups.FirstOrDefault(g => g.Name.Equals("Other", StringComparison.OrdinalIgnoreCase));
        NewTaskGroupId = (other ?? Groups.FirstOrDefault())?.Id;

        RefreshGroupBadges();
    }

    public void AddGroup(string name, string color)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        var g = _db.InsertGroup(new Models.TaskGroup { Name = name.Trim(), Color = color });
        Groups.Add(g);
        TaskItemViewModel.GroupsLookup[g.Id] = g;
    }

    public void DeleteGroup(int id)
    {
        _db.DeleteGroup(id);
        TaskItemViewModel.GroupsLookup.Remove(id);
        var existing = Groups.FirstOrDefault(g => g.Id == id);
        if (existing is not null) Groups.Remove(existing);
        if (_selectedGroupFilter == id) SelectedGroupFilter = null;
        foreach (var vm in Tasks.Where(t => t.GroupId == id))           vm.GroupId = null;
        foreach (var vm in CompletedTasks.Where(t => t.GroupId == id))  vm.GroupId = null;
        RefreshGroupBadges();
    }

    private void RefreshGroupBadges()
    {
        foreach (var vm in Tasks)          vm.RefreshGroupBadge();
        foreach (var vm in CompletedTasks) vm.RefreshGroupBadge();
    }

    // Notes

    public ObservableCollection<Models.Note> Notes { get; } = new();
    public ObservableCollection<Models.Note> FilteredNotes { get; } = new();

    public string NoteSearch
    {
        get => _noteSearch;
        set { _noteSearch = value; OnPropertyChanged(); RefreshFilteredNotes(); }
    }

    public void LoadNotes()
    {
        Notes.Clear();
        var attachmentCounts = _db.GetNoteAttachmentCounts();
        foreach (var n in _db.GetAllNotes())
        {
            n.AttachmentCount = attachmentCounts.GetValueOrDefault(n.Id);
            Notes.Add(n);
        }
        RefreshFilteredNotes();
    }

    public Models.Note AddNote(string title, string description, string tags = "")
    {
        var note = new Models.Note
        {
            Title       = title.Trim(),
            Description = description.Trim(),
            Tags        = tags.Trim(),
            CreatedAt   = DateTime.Now,
            UpdatedAt   = DateTime.Now
        };
        _db.InsertNote(note);
        Notes.Insert(0, note);
        RefreshFilteredNotes();
        return note;
    }

    public void UpdateNote(Models.Note note)
    {
        _db.UpdateNote(note);
        RefreshFilteredNotes();
    }

    public void DeleteNote(Models.Note note)
    {
        _db.DeleteNote(note.Id);
        _attachments.DeleteFolder(AttachmentFolders.ForNote(note.Id));
        Notes.Remove(note);
        FilteredNotes.Remove(note);
    }

    public void RefreshFilteredNotes()
    {
        FilteredNotes.Clear();
        var q = _noteSearch.Trim();
        foreach (var n in Notes)
        {
            if (string.IsNullOrEmpty(q) ||
                n.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                n.Description.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                n.Tags.Contains(q, StringComparison.OrdinalIgnoreCase))
                FilteredNotes.Add(n);
        }
    }

    // Stats

    public int StatsCompletedToday   { get; private set; }
    public int StatsCompletedWeek    { get; private set; }
    public int StatsCompletedAllTime { get; private set; }
    public string StatsTimeToday     { get; private set; } = "0m 00s";
    public string StatMeetingHoursToday { get; private set; } = "—";
    public string StatMeetingHoursWeek  { get; private set; } = "—";
    public string StatMeetingHoursMonth { get; private set; } = "—";
    public List<(string Day, int Count)> Last7DaysData { get; private set; } = new();
    public List<(string Day, int Seconds)> Last7DaysTimeData { get; private set; } = new();
    public List<TaskItemViewModel> TopTasksByTime      { get; private set; } = new();
    public int Last7DaysMax     { get; private set; } = 1;
    public int Last7DaysTimeMax { get; private set; } = 1;
    public string Last7DaysWeekLabel { get; private set; } = string.Empty;
    // 15 slots: index 0 = 7 AM, index 14 = 9 PM. Values are avg completions/day over last 30 days.
    public double[] BusiestHourData { get; private set; } = new double[15];
    // 4 slots: W1–W4. Values are avg tracked seconds per month over last 12 months.
    public int[] WeeklyTimeData { get; private set; } = new int[4];
    // 4 slots: W1–W4. Values are avg task count per month over last 12 months.
    public int[] WeeklyTaskData { get; private set; } = new int[4];

    public void RefreshStats(int weekOffset = 0)
    {
        var today   = DateTime.Today;
        var weekAgo = today.AddDays(-6);

        StatsCompletedAllTime = CompletedTasks.Count;
        StatsCompletedToday   = CompletedTasks.Count(t => t.CompletedAt.HasValue && t.CompletedAt.Value.Date == today);
        StatsCompletedWeek    = CompletedTasks.Count(t => t.CompletedAt.HasValue && t.CompletedAt.Value.Date >= weekAgo);

        int timeTodaySec = CompletedTasks
            .Where(t => t.CompletedAt.HasValue && t.CompletedAt.Value.Date == today)
            .Sum(t => t.TotalSeconds > 0 ? t.TotalSeconds : (t.ManualTimeMinutes ?? 0) * 60);
        StatsTimeToday = TaskItemViewModel.FormatSeconds(timeTodaySec);

        static int TimeKey(TaskItemViewModel v)
            => v.TotalSeconds > 0 ? v.TotalSeconds : (v.ManualTimeMinutes ?? 0) * 60;

        // ── Week-navigable chart: Mon–Sun of the viewing week ────────────
        int daysFromMonday = ((int)today.DayOfWeek + 6) % 7;
        var currentMonday  = today.AddDays(-daysFromMonday);
        var viewMonday     = currentMonday.AddDays(weekOffset * 7);
        var viewSunday     = viewMonday.AddDays(6);

        int weekNum = System.Globalization.ISOWeek.GetWeekOfYear(viewMonday);
        Last7DaysWeekLabel = $"Week {weekNum}: {viewMonday:d MMM} – {viewSunday:d MMM}";

        var days = new List<(string Day, int Count)>();
        var timeDays = new List<(string Day, int Seconds)>();
        int maxCount = 1, maxSecs = 1;
        for (int i = 0; i < 7; i++)
        {
            var d     = viewMonday.AddDays(i);
            int count = CompletedTasks.Count(t => t.CompletedAt.HasValue && t.CompletedAt.Value.Date == d);
            int secs  = CompletedTasks
                .Where(t => t.CompletedAt.HasValue && t.CompletedAt.Value.Date == d)
                .Sum(TimeKey);
            days.Add((d.ToString("ddd"), count));
            timeDays.Add((d.ToString("ddd"), secs));
            if (count > maxCount) maxCount = count;
            if (secs  > maxSecs)  maxSecs  = secs;
        }
        Last7DaysData     = days;
        Last7DaysMax      = maxCount;
        Last7DaysTimeData = timeDays;
        Last7DaysTimeMax  = maxSecs;

        TopTasksByTime = CompletedTasks
            .Where(t => TimeKey(t) > 0)
            .OrderByDescending(TimeKey)
            .Take(5)
            .ToList();

        // Busiest Hour — avg completions/day per hour, 7 AM–9 PM, last 30 days
        var cutoff    = today.AddDays(-30);
        var last30    = CompletedTasks.Where(t => t.CompletedAt.HasValue &&
                            t.CompletedAt.Value.Date >= cutoff).ToList();
        int activeDays = last30.Select(t => t.CompletedAt!.Value.Date).Distinct().Count();
        double divisor = activeDays > 0 ? activeDays : 1;
        var hourAvg    = new double[15];
        for (int i = 0; i < 15; i++)
        {
            int hour  = i + 7;
            int count = last30.Count(t => t.CompletedAt!.Value.Hour == hour);
            hourAvg[i] = count / divisor;
        }
        BusiestHourData = hourAvg;

        // Weekly Time Summary — 4 bars (W1–W4), avg per month for last 12 months
        var wTotals     = new int[4];
        var wCounts     = new int[4];
        var wTaskTotals = new int[4];
        var wTaskCounts = new int[4];
        int[] wDayStarts = { 1, 8, 15, 22 };
        for (int mo = 0; mo < 12; mo++)
        {
            var mStart = new DateTime(today.Year, today.Month, 1).AddMonths(-11 + mo);
            var mEnd   = mStart.AddMonths(1).AddDays(-1);
            for (int w = 0; w < 4; w++)
            {
                var wStart = new DateTime(mStart.Year, mStart.Month, wDayStarts[w]);
                var wEnd   = w < 3
                    ? new DateTime(mStart.Year, mStart.Month, wDayStarts[w + 1] - 1)
                    : mEnd;
                int secs  = CompletedTasks
                    .Where(t => t.CompletedAt.HasValue &&
                                t.CompletedAt.Value.Date >= wStart &&
                                t.CompletedAt.Value.Date <= wEnd)
                    .Sum(TimeKey);
                int tasks = CompletedTasks
                    .Count(t => t.CompletedAt.HasValue &&
                                t.CompletedAt.Value.Date >= wStart &&
                                t.CompletedAt.Value.Date <= wEnd);
                wTotals[w] += secs;
                if (secs  > 0) wCounts[w]++;
                wTaskTotals[w] += tasks;
                if (tasks > 0) wTaskCounts[w]++;
            }
        }
        WeeklyTimeData = Enumerable.Range(0, 4)
            .Select(w => wCounts[w] > 0 ? wTotals[w] / wCounts[w] : 0).ToArray();
        WeeklyTaskData = Enumerable.Range(0, 4)
            .Select(w => wTaskCounts[w] > 0 ? wTaskTotals[w] / wTaskCounts[w] : 0).ToArray();

        // Meeting hours from auto-detection
        try
        {
            double mToday = _db.GetMeetingHours(today, today.AddDays(1));
            double mWeek  = _db.GetMeetingHours(today.AddDays(-7), today.AddDays(1));
            double mMonth = _db.GetMeetingHours(new DateTime(today.Year, today.Month, 1), today.AddDays(1));
            StatMeetingHoursToday = FormatMeetingDuration(mToday);
            StatMeetingHoursWeek  = FormatMeetingDuration(mWeek);
            StatMeetingHoursMonth = FormatMeetingDuration(mMonth);
        }
        catch
        {
            StatMeetingHoursToday = "—";
            StatMeetingHoursWeek  = "—";
            StatMeetingHoursMonth = "—";
        }

        OnPropertyChanged(nameof(StatsCompletedToday));
        OnPropertyChanged(nameof(StatsCompletedWeek));
        OnPropertyChanged(nameof(StatsCompletedAllTime));
        OnPropertyChanged(nameof(StatsTimeToday));
        OnPropertyChanged(nameof(StatMeetingHoursToday));
        OnPropertyChanged(nameof(StatMeetingHoursWeek));
        OnPropertyChanged(nameof(StatMeetingHoursMonth));
        OnPropertyChanged(nameof(Last7DaysData));
        OnPropertyChanged(nameof(Last7DaysMax));
        OnPropertyChanged(nameof(Last7DaysTimeData));
        OnPropertyChanged(nameof(Last7DaysTimeMax));
        OnPropertyChanged(nameof(Last7DaysWeekLabel));
        OnPropertyChanged(nameof(TopTasksByTime));
        OnPropertyChanged(nameof(BusiestHourData));
        OnPropertyChanged(nameof(WeeklyTimeData));
        OnPropertyChanged(nameof(WeeklyTaskData));
    }

    // ── App Usage ─────────────────────────────────────────────────────────────

    public List<(string AppName, int Seconds, string ExePath)> AppUsageData { get; private set; } = new();

    public void RefreshAppUsage(DateTime from, DateTime to)
    {
        try { AppUsageData = App.Database!.GetTopAppsByTime(from, to); }
        catch { AppUsageData = new(); }
        OnPropertyChanged(nameof(AppUsageData));
    }

    private static string FormatMeetingDuration(double hours)
    {
        var ts = TimeSpan.FromSeconds((int)(hours * 3600));
        return $"{(int)ts.TotalHours}h {ts.Minutes:D2}m {ts.Seconds:D2}s";
    }

    // Form fields

    public string NewTitle
    {
        get => _newTitle;
        set
        {
            _newTitle = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasTitle));
            AddTaskCommand.RaiseCanExecuteChanged();
        }
    }

    public bool HasTitle => !string.IsNullOrWhiteSpace(_newTitle);

    public string NewDescription
    {
        get => _newDescription;
        set { _newDescription = value; OnPropertyChanged(); }
    }

    public DateTime? NewDueDate
    {
        get => _newDueDate;
        set { _newDueDate = value; OnPropertyChanged(); }
    }

    public TaskFilter ActiveFilter
    {
        get => _activeFilter;
        set
        {
            _activeFilter = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(FilterAll));
            OnPropertyChanged(nameof(FilterToday));
            OnPropertyChanged(nameof(FilterThisWeek));
            RefreshVisibleTasks();
        }
    }

    public bool FilterAll      => _activeFilter == TaskFilter.All;
    public bool FilterToday    => _activeFilter == TaskFilter.Today;
    public bool FilterThisWeek => _activeFilter == TaskFilter.ThisWeek;

    public bool HasVisibleTasks => VisibleTasks.Count > 0;

    public string EmptyTasksMessage => _activeFilter switch
    {
        TaskFilter.Today    => "All done for today! 🎉",
        TaskFilter.ThisWeek => "No tasks scheduled this week.",
        _                   => "No tasks yet — add your first task above."
    };

    public bool ShowCompleted
    {
        get => _showCompleted;
        set { _showCompleted = value; OnPropertyChanged(); }
    }

    public string CompletedSearch
    {
        get => _completedSearch;
        set { _completedSearch = value; OnPropertyChanged(); RefreshFilteredCompleted(); }
    }

    public string TaskSearch
    {
        get => _taskSearch;
        set { _taskSearch = value; OnPropertyChanged(); RefreshVisibleTasks(); }
    }

    public bool IsDarkTheme
    {
        get => _isDarkTheme;
        set { _isDarkTheme = value; OnPropertyChanged(); }
    }

    public TaskItemViewModel? ActiveTask => Tasks.FirstOrDefault(t => t.IsActive);

    // Commands

    private RelayCommand? _addTaskCommand;
    public RelayCommand AddTaskCommand => _addTaskCommand ??= new RelayCommand(
        AddTask, () => !string.IsNullOrWhiteSpace(NewTitle));

    private RelayCommand? _toggleCompletedCommand;
    public RelayCommand ToggleCompletedCommand => _toggleCompletedCommand ??= new RelayCommand(
        () => ShowCompleted = !ShowCompleted);

    // Load

    public void LoadTasks()
    {
        foreach (var old in Tasks) old.Detach();
        Tasks.Clear();
        CompletedTasks.Clear();
        FilteredCompleted.Clear();
        VisibleTasks.Clear();

        foreach (var t in _db.GetAllTasks())
        {
            var vm = new TaskItemViewModel(t);
            WireViewModel(vm);
            if (vm.WasInterrupted) SaveTask(vm);   // persist the safety pause
            if (t.IsCompleted) CompletedTasks.Add(vm);
            else Tasks.Add(vm);
        }
        var sorted = CompletedTasks.OrderByDescending(v => v.CompletedAt ?? DateTime.MinValue).ToList();
        CompletedTasks.Clear();
        foreach (var v in sorted) CompletedTasks.Add(v);
        RefreshFilteredCompleted();
        RefreshVisibleTasks();
    }

    /// <summary>
    /// After a sync changed this workspace: brings the lists up to date in place. Existing task objects
    /// are kept and refreshed (open dialogs, the widget and a running timer keep working on them);
    /// new tasks are added and removed ones dropped.
    /// </summary>
    public void ApplySyncedChanges()
    {
        var existing = Tasks.Concat(CompletedTasks).ToDictionary(v => v.Id);
        var open = new List<TaskItemViewModel>();
        var done = new List<TaskItemViewModel>();
        foreach (var t in _db.GetAllTasks())
        {
            if (existing.Remove(t.Id, out var vm))
            {
                vm.RefreshFrom(t);
                vm.ReminderCount   = _db.GetReminderCountForTask(vm.Id);
                vm.AttachmentCount = _db.GetAttachments(vm.Id).Count;
            }
            else
            {
                vm = new TaskItemViewModel(t);
                WireViewModel(vm);
            }
            (vm.IsCompleted ? done : open).Add(vm);
        }
        foreach (var gone in existing.Values) gone.Detach();

        // Completed on another computer while its timer ran here: stop the timer (this saves it)
        foreach (var vm in done.Where(v => v.IsActive)) vm.IsActive = false;

        ReplaceContents(Tasks, open);
        ReplaceContents(CompletedTasks, done.OrderByDescending(v => v.CompletedAt ?? DateTime.MinValue));
        LoadDeletedTasks();
        LoadGroups();
        LoadNotes();
        RefreshFilteredCompleted();
        RefreshVisibleTasks();
        OnPropertyChanged(nameof(ActiveTask));
    }

    private static void ReplaceContents(ObservableCollection<TaskItemViewModel> list, IEnumerable<TaskItemViewModel> items)
    {
        var wanted = items.ToList();
        if (list.SequenceEqual(wanted)) return;
        list.Clear();
        foreach (var vm in wanted) list.Add(vm);
    }

    // Add

    public void AddTask()
    {
        if (string.IsNullOrWhiteSpace(NewTitle)) return;
        var task = new TaskItem
        {
            Title       = NewTitle.Trim(),
            Description = NewDescription.Trim(),
            DueDate     = NewDueDate ?? DateTime.Today,
            SortOrder   = Tasks.Count,
            GroupId     = NewTaskGroupId,
            Recurrence  = NewTaskRecurrence,
            RecurrenceEndDate = NewTaskRecurrence == RecurrenceType.None ? null : NewTaskRecurrenceEndDate
        };
        _db.InsertTask(task);
        var vm = new TaskItemViewModel(task);
        WireViewModel(vm);
        Tasks.Add(vm);
        InsertAtDueDatePosition(vm);  // places the new task without disturbing manual order
        PersistSortOrder();
        NewTitle          = string.Empty;
        NewDescription    = string.Empty;
        NewDueDate        = DateTime.Today;
        NewTaskGroupId    = (Groups.FirstOrDefault(g => g.Name.Equals("Other", StringComparison.OrdinalIgnoreCase)) ?? Groups.FirstOrDefault())?.Id;
        NewTaskRecurrence = RecurrenceType.None;
        NewTaskRecurrenceEndDate = null;
        RefreshVisibleTasks();
    }

    public void QuickAdd(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return;
        var task = new TaskItem { Title = title.Trim(), DueDate = DateTime.Today, SortOrder = Tasks.Count };
        _db.InsertTask(task);
        var vm = new TaskItemViewModel(task);
        WireViewModel(vm);
        Tasks.Add(vm);
        InsertAtDueDatePosition(vm);
        PersistSortOrder();
        RefreshVisibleTasks();
    }

    // Complete / Uncomplete / Delete

    public bool CompleteTask(TaskItemViewModel vm, out bool needsManualTime)
    {
        if (!Tasks.Contains(vm)) { needsManualTime = false; return false; }
        needsManualTime = vm.TotalSeconds == 0 && !vm.ManualTimeMinutes.HasValue;
        vm.CompletedAt = DateTime.Now;
        vm.IsCompleted = true;
        if (vm.IsActive) vm.IsActive = false;
        SaveTask(vm);
        Tasks.Remove(vm);
        int pos = 0;
        while (pos < CompletedTasks.Count &&
               (CompletedTasks[pos].CompletedAt ?? DateTime.MinValue) > (vm.CompletedAt ?? DateTime.MinValue))
            pos++;
        CompletedTasks.Insert(pos, vm);
        RefreshFilteredCompleted();
        RefreshVisibleTasks();
        OnPropertyChanged(nameof(ActiveTask));

        var (nextOccurrence, recurrenceNote) = ScheduleNextOccurrence(vm);
        RecordUndo($"Completed \"{vm.Title}\"", $"Completed \"{vm.Title}\".{recurrenceNote}",
                   () => UndoCompletion(vm, nextOccurrence));
        return true;
    }

    /// <summary>
    /// For a repeating task, adds its next occurrence to the open list.
    /// Returns the new task (null when none was added) and a sentence for the user.
    /// </summary>
    private (TaskItemViewModel? Next, string Note) ScheduleNextOccurrence(TaskItemViewModel completed)
    {
        if (completed.Recurrence == RecurrenceType.None) return (null, string.Empty);

        // Un-completing and re-completing must not stack up copies of the next occurrence.
        if (HasOpenOccurrence(completed)) return (null, string.Empty);

        DateTime nextDue = RecurrenceRules.NextDueDate(completed.Recurrence);
        if (!RecurrenceRules.ContinuesOn(nextDue, completed.RecurrenceEndDate))
            return (null, $" It has finished repeating (ended {completed.RecurrenceEndDate:MMM d}).");

        var newTask = new TaskItem
        {
            Title             = completed.Title,
            Description       = completed.Description,
            DueDate           = nextDue,
            GroupId           = completed.GroupId,
            Recurrence        = completed.Recurrence,
            RecurrenceEndDate = completed.RecurrenceEndDate,
            SortOrder         = Tasks.Count
        };
        _db.InsertTask(newTask);
        var next = new TaskItemViewModel(newTask);
        WireViewModel(next);
        Tasks.Add(next);
        InsertAtDueDatePosition(next);
        PersistSortOrder();
        RefreshVisibleTasks();
        return (next, $" The next one is on {nextDue:MMM d}.");
    }

    private void UndoCompletion(TaskItemViewModel completed, TaskItemViewModel? nextOccurrence)
    {
        if (nextOccurrence is not null && Tasks.Contains(nextOccurrence))
        {
            // The occurrence was created by this completion, so it goes away entirely.
            _db.DeleteTask(nextOccurrence.Id);
            _attachments.DeleteFolder(AttachmentFolders.ForTask(nextOccurrence.Id));
            nextOccurrence.Detach();
            Tasks.Remove(nextOccurrence);
        }
        if (!CompletedTasks.Contains(completed)) return;
        completed.CompletedAt = null;
        UncompleteTask(completed);
        MoveToDueDatePosition(completed);
    }

    // ── Postpone ─────────────────────────────────────────────────────────────

    /// <summary>Moves an open task to a later day; the change can be undone.</summary>
    public void Postpone(TaskItemViewModel vm, DateTime newDueDate)
    {
        if (!Tasks.Contains(vm)) return;
        var previousDueDate = vm.DueDate;
        SetDueDate(vm, newDueDate);
        RecordUndo($"Postponed \"{vm.Title}\"", $"\"{vm.Title}\" moved to {newDueDate:ddd d MMM}.",
                   () => { if (Tasks.Contains(vm)) SetDueDate(vm, previousDueDate); });
    }

    private void SetDueDate(TaskItemViewModel vm, DateTime? dueDate)
    {
        vm.DueDate = dueDate;
        SaveTask(vm);
        MoveToDueDatePosition(vm);
        RefreshVisibleTasks();
    }

    // ── Export / import / print ──────────────────────────────────────────────

    /// <summary>Every open and completed task (not the recycle bin), for an export.</summary>
    public List<TaskExportRow> ExportAllTasks()
        => [.. Tasks.Concat(CompletedTasks).OrderBy(t => t.TaskNumber).Select(ToExportRow)];

    /// <summary>Open tasks in one of the date views, in list order, for printing.</summary>
    public List<TaskExportRow> OpenTasksIn(TaskFilter filter)
        => [.. Tasks.Where(t => TaskDateFilters.Matches(filter, t.DueDate, DateTime.Today)).Select(ToExportRow)];

    private static TaskExportRow ToExportRow(TaskItemViewModel t) => new(
        t.TaskNumber, t.Title, t.Description, t.GroupBadge, t.DueDate, t.Recurrence, t.RecurrenceEndDate,
        t.IsCompleted, t.CompletedAt,
        MinutesSpent: t.TotalSeconds > 0 ? t.TotalSeconds / 60 : t.ManualTimeMinutes ?? 0,
        t.Model.CreatedAt);

    /// <summary>Colours given to groups that an import creates, in turn.</summary>
    private static readonly string[] ImportedGroupColors = ["#3B82F6", "#22C55E", "#F59E0B", "#A855F7", "#EC4899", "#14B8A6"];

    /// <summary>Adds imported tasks (creating any group they name that does not exist yet). Returns how many groups were created.</summary>
    public int ImportTasks(IReadOnlyList<TaskExportRow> rows)
    {
        int groupsBefore = Groups.Count;
        foreach (var row in rows)
        {
            var task = new TaskItem
            {
                Title             = row.Title,
                Description       = row.Description,
                GroupId           = FindOrCreateGroup(row.Group),
                DueDate           = row.DueDate,
                Recurrence        = row.Repeat,
                RecurrenceEndDate = row.Repeat == RecurrenceType.None ? null : row.RepeatEnds,
                IsCompleted       = row.IsCompleted,
                CompletedAt       = row.CompletedAt,
                ManualTimeMinutes = row.MinutesSpent > 0 ? row.MinutesSpent : null,
                CreatedAt         = row.CreatedAt,
                SortOrder         = Tasks.Count
            };
            _db.InsertTask(task);
            var vm = new TaskItemViewModel(task);
            WireViewModel(vm);
            if (task.IsCompleted) CompletedTasks.Add(vm);
            else { Tasks.Add(vm); InsertAtDueDatePosition(vm); }
        }

        var completed = CompletedTasks.OrderByDescending(v => v.CompletedAt ?? DateTime.MinValue).ToList();
        CompletedTasks.Clear();
        foreach (var vm in completed) CompletedTasks.Add(vm);
        PersistSortOrder();
        RefreshFilteredCompleted();
        RefreshVisibleTasks();
        return Groups.Count - groupsBefore;
    }

    private int? FindOrCreateGroup(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return NewTaskGroupId;
        var existing = Groups.FirstOrDefault(g => g.Name.Equals(name.Trim(), StringComparison.CurrentCultureIgnoreCase));
        if (existing is not null) return existing.Id;
        AddGroup(name, ImportedGroupColors[Groups.Count % ImportedGroupColors.Length]);
        return Groups[^1].Id;
    }

    // ── Undo ─────────────────────────────────────────────────────────────────

    public UndoService Undo { get; } = new();

    /// <summary>Reverses the most recent complete / delete / postpone, and tells the user.</summary>
    public void UndoLast()
    {
        if (Undo.UndoLast() is { } description) InAppNotificationService.Success($"Undone: {description}.");
        else                                     InAppNotificationService.Info("There is nothing to undo.");
    }

    private void RecordUndo(string description, string message, Action undo)
    {
        Undo.Record(description, undo);
        InAppNotificationService.Undoable(message, UndoLast);
    }

    /// <summary>
    /// True if a later occurrence of this recurring task is already waiting in the
    /// open list (it was spawned the first time this task was completed).
    /// </summary>
    private bool HasOpenOccurrence(TaskItemViewModel completed)
    {
        var completedDue = (completed.DueDate ?? DateTime.Today).Date;
        return Tasks.Any(t => t != completed
                           && t.Recurrence == completed.Recurrence
                           && t.GroupId    == completed.GroupId
                           && t.DueDate.HasValue && t.DueDate.Value.Date > completedDue
                           && string.Equals(t.Title, completed.Title, StringComparison.Ordinal));
    }

    public void UncompleteTask(TaskItemViewModel vm)
    {
        vm.IsCompleted = false;
        SaveTask(vm);
        CompletedTasks.Remove(vm);
        FilteredCompleted.Remove(vm);
        Tasks.Add(vm);
        RefreshVisibleTasks();
    }

    /// <summary>Move a task to the recycle bin (soft delete) instead of erasing it.</summary>
    public void DeleteTask(TaskItemViewModel vm)
    {
        if (vm.IsActive) vm.IsActive = false;   // stop any running timer before trashing
        var reminders = _db.GetRemindersForTask(vm.Id);   // kept so an undo can put them back
        _db.SoftDeleteTask(vm.Id);
        _db.DeleteRemindersForTask(vm.Id);       // trashed tasks should not fire reminders
        vm.DeletedAt = DateTime.Now;
        Tasks.Remove(vm);
        CompletedTasks.Remove(vm);
        FilteredCompleted.Remove(vm);
        VisibleTasks.Remove(vm);
        DeletedTasks.Insert(0, vm);
        OnPropertyChanged(nameof(ActiveTask));

        RecordUndo($"Deleted \"{vm.Title}\"", $"\"{vm.Title}\" moved to the recycle bin.", () =>
        {
            if (!DeletedTasks.Contains(vm)) return;
            foreach (var reminder in reminders) { reminder.Id = 0; _db.InsertReminder(reminder); }
            RestoreTask(vm);
        });
    }

    // ── Recycle bin ──────────────────────────────────────────────────────────

    /// <summary>Load the recycle-bin contents from the database.</summary>
    public void LoadDeletedTasks()
    {
        DeletedTasks.Clear();
        foreach (var t in _db.GetDeletedTasks())
        {
            var vm = new TaskItemViewModel(t);
            DeletedTasks.Add(vm);
        }
    }

    /// <summary>Restore a task from the recycle bin back to its active / completed list.</summary>
    public void RestoreTask(TaskItemViewModel vm)
    {
        _db.RestoreTask(vm.Id);
        vm.DeletedAt = null;
        DeletedTasks.Remove(vm);

        var restored = new TaskItemViewModel(vm.Model);
        WireViewModel(restored);
        restored.RefreshGroupBadge();
        if (restored.IsCompleted)
        {
            int pos = 0;
            while (pos < CompletedTasks.Count &&
                   (CompletedTasks[pos].CompletedAt ?? DateTime.MinValue) > (restored.CompletedAt ?? DateTime.MinValue))
                pos++;
            CompletedTasks.Insert(pos, restored);
            RefreshFilteredCompleted();
        }
        else
        {
            Tasks.Add(restored);
            ResortTasks();
        }
        RefreshVisibleTasks();
        OnPropertyChanged(nameof(ActiveTask));
    }

    /// <summary>Permanently delete a single task from the recycle bin.</summary>
    public void PermanentlyDeleteTask(TaskItemViewModel vm)
    {
        _db.DeleteTask(vm.Id);
        _attachments.DeleteFolder(AttachmentFolders.ForTask(vm.Id));
        DeletedTasks.Remove(vm);
    }

    /// <summary>Permanently delete everything in the recycle bin.</summary>
    public void EmptyTrash()
    {
        foreach (var id in _db.EmptyTrash()) _attachments.DeleteFolder(AttachmentFolders.ForTask(id));
        DeletedTasks.Clear();
    }

    /// <summary>Drop recycle-bin tasks older than the retention window. Call on startup.</summary>
    public void PurgeExpiredDeletedTasks()
    {
        var cutoff = DateTime.Now.AddDays(-TrashRetentionDays);
        foreach (var id in _db.PurgeDeletedOlderThan(cutoff)) _attachments.DeleteFolder(AttachmentFolders.ForTask(id));
    }

    /// <summary>Removes attachment folders of tasks that were deleted by older versions of Tasky.</summary>
    public void DeleteOrphanAttachmentFolders()
        => _attachments.DeleteOrphanFolders(_db.GetAllTaskIds(), _db.GetAllNoteIds());

    // Re-sort

    public void ResortTasks()
    {
        var sorted = Tasks
            .OrderByDescending(v => v.IsActive)
            .ThenBy(v => v.DueDate ?? DateTime.MaxValue)
            .ThenBy(v => v.Model.CreatedAt)
            .ToList();

        for (int i = 0; i < sorted.Count; i++)
        {
            int cur = Tasks.IndexOf(sorted[i]);
            if (cur != i) Tasks.Move(cur, i);
        }
        PersistSortOrder();   // saves to DB + syncs VisibleTasks in-place
    }

    // Filter

    public void RefreshVisibleTasks()
    {
        VisibleTasks.Clear();
        var today  = DateTime.Today;
        var search = _taskSearch.Trim();
        bool hasSearch = !string.IsNullOrEmpty(search);

        foreach (var vm in Tasks)
        {
            bool textMatch = !hasSearch
                || vm.Title.Contains(search, StringComparison.OrdinalIgnoreCase)
                || vm.TaskNumberDisplay.Contains(search, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrEmpty(vm.Description) && vm.Description.Contains(search, StringComparison.OrdinalIgnoreCase));

            if (!textMatch) continue;

            // When searching, show all matching tasks regardless of date/group filter
            if (hasSearch) { VisibleTasks.Add(vm); continue; }

            bool dateMatch  = TaskDateFilters.Matches(_activeFilter, vm.DueDate, today);
            bool groupMatch = _selectedGroupFilter == null || vm.GroupId == _selectedGroupFilter;
            if (dateMatch && groupMatch) VisibleTasks.Add(vm);
        }
        OnPropertyChanged(nameof(HasVisibleTasks));
        OnPropertyChanged(nameof(EmptyTasksMessage));
    }

    // Active / Pause

    public void ToggleActive(TaskItemViewModel vm)
    {
        if (!vm.IsActive)
        {
            foreach (var other in Tasks.Where(t => t.IsActive && t.Id != vm.Id).ToList())
            { other.IsActive = false; SaveTask(other); }
        }
        vm.IsActive = !vm.IsActive;
        SaveTask(vm);

        if (vm.IsActive && Tasks.Contains(vm))
        {
            int cur = Tasks.IndexOf(vm);
            if (cur > 0) Tasks.Move(cur, 0);
        }
        PersistSortOrder();
        RefreshVisibleTasks();
        OnPropertyChanged(nameof(ActiveTask));
    }

    public void TogglePause(TaskItemViewModel vm)
    {
        vm.TogglePause();
        SaveTask(vm);
        OnPropertyChanged(nameof(ActiveTask));
    }

    /// <summary>
    /// Pauses every running task, counting time only up to <paramref name="pausedAt"/>.
    /// Returns true when at least one task was actually paused.
    /// </summary>
    public bool PauseRunningTasks(DateTime pausedAt)
    {
        var running = Tasks.Where(t => t.IsRunning).ToList();
        foreach (var vm in running)
        {
            vm.Pause(pausedAt);
            SaveTask(vm);
        }
        if (running.Count > 0) OnPropertyChanged(nameof(ActiveTask));
        return running.Count > 0;
    }

    /// <summary>Resumes the active task if it is paused.</summary>
    public void ResumeActiveTask()
    {
        if (ActiveTask is { IsPaused: true } vm) TogglePause(vm);
    }

    /// <summary>Saves running tasks so a crash or power cut loses at most one checkpoint interval.</summary>
    public void CheckpointRunningTasks()
    {
        foreach (var vm in Tasks.Where(t => t.IsRunning)) SaveTask(vm);
    }

    /// <summary>
    /// Keeps "Today / Tomorrow / Overdue" labels and the date filters current as the
    /// clock moves on, without replacing the task objects (open dialogs, the widget
    /// and running timers all keep pointing at the same instances).
    /// </summary>
    public void RefreshTimeSensitiveLabels()
    {
        foreach (var vm in Tasks)          vm.RefreshDateLabels();
        foreach (var vm in CompletedTasks) vm.RefreshDateLabels();
        RefreshVisibleTasks();
    }

    // Attachments

    private const string PastedImageName = "pasted image";

    /// <summary>Copies a file into the task's attachments. Returns false (and tells the user) if it failed.</summary>
    public bool SaveAttachment(int taskId, string sourcePath)
        => TrySaveAttachment(() => _attachments.SaveCopy(AttachmentFolders.ForTask(taskId), sourcePath),
                             dest => InsertTaskAttachment(taskId, dest), Path.GetFileName(sourcePath));

    /// <summary>Saves pasted image bytes as a new attachment. Returns false (and tells the user) if it failed.</summary>
    public bool SaveAttachmentFromBytes(int taskId, byte[] data, string ext)
        => TrySaveAttachment(() => _attachments.SaveBytes(AttachmentFolders.ForTask(taskId), data, ext),
                             dest => InsertTaskAttachment(taskId, dest), PastedImageName);

    /// <summary>Copies a file into the note's attachments. Returns false (and tells the user) if it failed.</summary>
    public bool SaveNoteAttachment(int noteId, string sourcePath)
        => TrySaveAttachment(() => _attachments.SaveCopy(AttachmentFolders.ForNote(noteId), sourcePath),
                             dest => InsertNoteAttachment(noteId, dest), Path.GetFileName(sourcePath));

    /// <summary>Saves pasted image bytes on a note. Returns false (and tells the user) if it failed.</summary>
    public bool SaveNoteAttachmentFromBytes(int noteId, byte[] data, string ext)
        => TrySaveAttachment(() => _attachments.SaveBytes(AttachmentFolders.ForNote(noteId), data, ext),
                             dest => InsertNoteAttachment(noteId, dest), PastedImageName);

    private void InsertTaskAttachment(int taskId, string path)
        => _db.InsertAttachment(new TaskAttachment { TaskId = taskId, FileName = Path.GetFileName(path), FilePath = path });

    private void InsertNoteAttachment(int noteId, string path)
        => _db.InsertNoteAttachment(new NoteAttachment { NoteId = noteId, FileName = Path.GetFileName(path), FilePath = path });

    /// <summary>Writes the file, then records it; on any failure the user is told and false is returned.</summary>
    private static bool TrySaveAttachment(Func<string> writeFile, Action<string> insertRow, string displayName)
    {
        try
        {
            insertRow(writeFile());
            return true;
        }
        catch (Exception ex)
        {
            CrashLog.Write($"Could not save attachment '{displayName}'", ex);
            InAppNotificationService.Error($"Couldn't attach \"{displayName}\".");
            return false;
        }
    }

    public List<TaskAttachment> GetAttachments(int taskId) => _db.GetAttachments(taskId);

    public void DeleteAttachment(TaskAttachment attachment)
    {
        _db.DeleteAttachment(attachment.Id);
        _attachments.DeleteFile(attachment.FilePath);
    }

    public List<NoteAttachment> GetNoteAttachments(int noteId) => _db.GetNoteAttachments(noteId);

    public void DeleteNoteAttachment(NoteAttachment attachment)
    {
        _db.DeleteNoteAttachment(attachment.Id);
        _attachments.DeleteFile(attachment.FilePath);
    }

    /// <summary>Updates the 📎 count on a note card after its attachments changed.</summary>
    public void RefreshNoteAttachmentCount(Models.Note note)
    {
        note.AttachmentCount = _db.GetNoteAttachments(note.Id).Count;
        RefreshFilteredNotes();
    }

    /// <summary>
    /// Moves a newly-appended task to its correct position in Tasks:
    /// after all active tasks, then by due date ascending — without
    /// disturbing the relative order of existing tasks.
    /// </summary>
    private void InsertAtDueDatePosition(TaskItemViewModel newVm)
    {
        int currentPos = Tasks.IndexOf(newVm);
        if (currentPos < 0) return;

        DateTime newDue = newVm.DueDate ?? DateTime.MaxValue;
        int target = 0;

        for (int i = 0; i < Tasks.Count; i++)
        {
            var t = Tasks[i];
            if (t == newVm) break;              // stop when we reach the new task itself
            if (t.IsActive) { target = i + 1; continue; }  // always stay after active tasks
            if ((t.DueDate ?? DateTime.MaxValue) <= newDue) target = i + 1;
            else break;
        }

        if (currentPos != target)
            Tasks.Move(currentPos, target);
    }

    /// <summary>Puts an open task back where its (changed) due date belongs.</summary>
    private void MoveToDueDatePosition(TaskItemViewModel vm)
    {
        int current = Tasks.IndexOf(vm);
        if (current < 0) return;
        Tasks.Move(current, Tasks.Count - 1);   // InsertAtDueDatePosition looks only at the tasks before it
        InsertAtDueDatePosition(vm);
        PersistSortOrder();
    }

    public void PersistSortOrder()
    {
        _db.UpdateSortOrders(Tasks.Select((v, i) => (v.Id, i)));
        SyncVisibleOrder();
    }

    /// <summary>
    /// Re-orders VisibleTasks in-place using Move() to match the current order of Tasks.
    /// Using Move() instead of Clear+Add preserves ItemsControl container elements
    /// so the flip animation can still read their Y positions.
    /// </summary>
    private void SyncVisibleOrder()
    {
        if (VisibleTasks.Count < 2) return;

        // Build a position index from Tasks so we can sort by it
        var taskIndex = new Dictionary<TaskItemViewModel, int>(Tasks.Count);
        for (int i = 0; i < Tasks.Count; i++)
            taskIndex[Tasks[i]] = i;

        var target = VisibleTasks
            .OrderBy(v => taskIndex.TryGetValue(v, out int idx) ? idx : int.MaxValue)
            .ToList();

        for (int i = 0; i < target.Count; i++)
        {
            int cur = VisibleTasks.IndexOf(target[i]);
            if (cur != i) VisibleTasks.Move(cur, i);
        }
    }

    public void RefreshReminderCount(TaskItemViewModel vm)
        => vm.ReminderCount = _db.GetReminderCountForTask(vm.Id);

    public void RefreshAttachmentCount(TaskItemViewModel vm)
        => vm.AttachmentCount = _db.GetAttachments(vm.Id).Count;

    // Wire

    private void WireViewModel(TaskItemViewModel vm)
    {
        vm.ReminderCount   = _db.GetReminderCountForTask(vm.Id);
        vm.AttachmentCount = _db.GetAttachments(vm.Id).Count;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(TaskItemViewModel.IsActive))
            { SaveTask(vm); OnPropertyChanged(nameof(ActiveTask)); }
        };
    }

    public void SaveTask(TaskItemViewModel vm) => _db.UpdateTask(vm.GetModelForSave());

    public void DeleteRemindersForTask(int taskId) => _db.DeleteRemindersForTask(taskId);

    public void RefreshFilteredCompleted()
    {
        FilteredCompleted.Clear();
        var q = _completedSearch.Trim();
        foreach (var vm in CompletedTasks)
        {
            if (string.IsNullOrEmpty(q) ||
                vm.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                vm.TaskNumberDisplay.Contains(q, StringComparison.OrdinalIgnoreCase))
                FilteredCompleted.Add(vm);
        }
    }


    // INotifyPropertyChanged

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

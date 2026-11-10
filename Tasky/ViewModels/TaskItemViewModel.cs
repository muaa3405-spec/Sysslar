using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using Tasky.Models;

namespace Tasky.ViewModels;

/// <summary>
/// Wraps a TaskItem with observable properties and a pauseable timer.
/// AccumulatedSeconds (from DB) + the live session (now − ActiveStartTime) = total elapsed.
/// The live session is worked out from the clock, not by counting timer ticks,
/// so a busy UI thread can never lose or add time.
/// </summary>
public class TaskItemViewModel : INotifyPropertyChanged
{
    private readonly TaskItem _model;
    private DispatcherTimer? _timer;

    public TaskItemViewModel(TaskItem model)
    {
        _model = model;
        if (model.IsActive && !model.IsPaused)
        {
            // Saved as "running" means Tasky stopped without pausing it (crash, power
            // loss, forced shutdown). Running tasks are checkpointed every minute, so
            // the time up to the last checkpoint is already in AccumulatedSeconds.
            // Pause instead of counting the whole gap while the PC was off.
            _model.IsPaused        = true;
            _model.ActiveStartTime = null;
            WasInterrupted         = true;
        }
    }

    /// <summary>True when this task was found still running after an unclean exit and was paused.</summary>
    public bool WasInterrupted { get; }

    // Identity

    public int Id => _model.Id;
    public int TaskNumber => _model.TaskNumber;
    public string TaskNumberDisplay => $"#{_model.TaskNumber:D3}";

    public int? GroupId
    {
        get => _model.GroupId;
        set { _model.GroupId = value; OnPropertyChanged(); OnPropertyChanged(nameof(GroupBadge)); OnPropertyChanged(nameof(GroupBrush)); }
    }

    public RecurrenceType Recurrence
    {
        get => _model.Recurrence;
        set { _model.Recurrence = value; OnPropertyChanged(); OnPropertyChanged(nameof(RecurrenceDisplay)); OnPropertyChanged(nameof(RecurrenceBrush)); OnPropertyChanged(nameof(RecurrenceTooltip)); }
    }

    public DateTime? RecurrenceEndDate
    {
        get => _model.RecurrenceEndDate;
        set { _model.RecurrenceEndDate = value; OnPropertyChanged(); OnPropertyChanged(nameof(RecurrenceTooltip)); }
    }

    /// <summary>Hover text of the repeat badge, e.g. "Repeats weekly until Nov 30, 2026".</summary>
    public string RecurrenceTooltip => _model.RecurrenceEndDate is { } end
        ? $"Repeats {RecurrenceDisplay.ToLowerInvariant()} until {end:MMM d, yyyy}"
        : $"Repeats {RecurrenceDisplay.ToLowerInvariant()}, no end date";

    /// <summary>Label shown on the task card for recurring tasks. Empty for non-recurring.</summary>
    public string RecurrenceDisplay => _model.Recurrence switch
    {
        RecurrenceType.Daily   => "Daily",
        RecurrenceType.Weekly  => "Weekly",
        RecurrenceType.Monthly => "Monthly",
        _                      => string.Empty
    };

    /// <summary>Badge background per recurrence frequency.</summary>
    public IBrush RecurrenceBrush => _model.Recurrence switch
    {
        RecurrenceType.Daily   => RecurrenceDaily,
        RecurrenceType.Weekly  => RecurrenceWeekly,
        RecurrenceType.Monthly => RecurrenceMonthly,
        _                      => GrayBrush
    };

    private static readonly IBrush RecurrenceDaily   = new ImmutableSolidColorBrush(Color.FromRgb(124,  58, 237));  // violet
    private static readonly IBrush RecurrenceWeekly  = new ImmutableSolidColorBrush(Color.FromRgb( 13, 148, 136));  // teal
    private static readonly IBrush RecurrenceMonthly = new ImmutableSolidColorBrush(Color.FromRgb(  3, 105, 161));  // blue
    private static readonly IBrush GrayBrush         = new ImmutableSolidColorBrush(Color.FromRgb(107, 114, 128));

    /// <summary>Group name shown in the badge. Empty = no group assigned.</summary>
    public string GroupBadge
    {
        get
        {
            if (!_model.GroupId.HasValue) return string.Empty;
            if (GroupsLookup.TryGetValue(_model.GroupId.Value, out var g)) return g.Name;
            return string.Empty;
        }
    }

    /// <summary>Colour of the group, used as the card strip. Falls back to gray.</summary>
    public IBrush GroupBrush
    {
        get
        {
            if (!_model.GroupId.HasValue) return Brushes.Transparent;
            if (GroupsLookup.TryGetValue(_model.GroupId.Value, out var g) && Color.TryParse(g.Color, out var c))
                return new ImmutableSolidColorBrush(c);
            return GrayBrush;
        }
    }

    /// <summary>Re-evaluates the relative date labels ("Today", "Overdue …") after the clock moves on.</summary>
    public void RefreshDateLabels()
    {
        OnPropertyChanged(nameof(DueDateTag));
        OnPropertyChanged(nameof(DueDateBrush)); OnPropertyChanged(nameof(IsOverdue)); OnPropertyChanged(nameof(IsDueToday));
        OnPropertyChanged(nameof(CompletedGroup));
    }

    public void RefreshGroupBadge()
    {
        OnPropertyChanged(nameof(GroupBadge));
        OnPropertyChanged(nameof(GroupBrush));
    }

    /// <summary>Shared lookup from MainViewModel — populated when groups load.</summary>
    internal static Dictionary<int, Models.TaskGroup> GroupsLookup { get; } = new();

    // Editable properties

    public string Title
    {
        get => _model.Title;
        set { _model.Title = value; OnPropertyChanged(); }
    }

    public string Description
    {
        get => _model.Description;
        set { _model.Description = value; OnPropertyChanged(); }
    }

    private bool _isDescriptionExpanded;
    public bool IsDescriptionExpanded
    {
        get => _isDescriptionExpanded;
        set { _isDescriptionExpanded = value; OnPropertyChanged(); OnPropertyChanged(nameof(ShowMoreLabel)); }
    }

    public string ShowMoreLabel => _isDescriptionExpanded ? "Show less" : "Show more";

    private bool _isSelected;
    /// <summary>Picked with the arrow keys; keyboard shortcuts act on this task.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected == value) return; _isSelected = value; OnPropertyChanged(); }
    }

    public DateTime? DueDate
    {
        get => _model.DueDate;
        set { _model.DueDate = value; OnPropertyChanged(); OnPropertyChanged(nameof(DueDateTag)); OnPropertyChanged(nameof(DueDateBrush)); OnPropertyChanged(nameof(IsOverdue)); OnPropertyChanged(nameof(IsDueToday)); }
    }

    public string DueDateTag
    {
        get
        {
            if (!_model.DueDate.HasValue) return string.Empty;
            var d = _model.DueDate.Value.Date;
            var today = DateTime.Today;
            if (d == today)             return "Today";
            if (d == today.AddDays(1))  return "Tomorrow";
            if (d == today.AddDays(-1)) return "Yesterday";
            if (d < today)              return $"Overdue {d:MMM d}";
            return d.ToString("MMM d");
        }
    }

    public IBrush DueDateBrush
    {
        get
        {
            if (!_model.DueDate.HasValue) return GrayBrush;
            var d = _model.DueDate.Value.Date;
            var today = DateTime.Today;
            if (d < today)  return OverdueBrush;
            if (d == today) return DueTodayBrush;
            return GrayBrush;
        }
    }

    /// <summary>Starts before today (the date tag is shown in the danger colour).</summary>
    public bool IsOverdue => _model.DueDate.HasValue && _model.DueDate.Value.Date < DateTime.Today;

    /// <summary>Starts today (the date tag is shown in the success colour).</summary>
    public bool IsDueToday => _model.DueDate.HasValue && _model.DueDate.Value.Date == DateTime.Today;

    private static readonly IBrush OverdueBrush  = new ImmutableSolidColorBrush(Color.Parse("#EF4444"));
    private static readonly IBrush DueTodayBrush = new ImmutableSolidColorBrush(Color.Parse("#22C55E"));

    public DateTime? CompletedAt
    {
        get => _model.CompletedAt;
        set { _model.CompletedAt = value; OnPropertyChanged(); OnPropertyChanged(nameof(CompletedAtDisplay)); OnPropertyChanged(nameof(CompletedGroup)); }
    }

    public string CompletedAtDisplay => _model.CompletedAt.HasValue
        ? _model.CompletedAt.Value.ToString("MMM d, yyyy  h:mm tt")
        : string.Empty;

    public string CompletedGroup
    {
        get
        {
            if (!_model.CompletedAt.HasValue) return "Earlier";
            var d = _model.CompletedAt.Value.Date;
            if (d == DateTime.Today) return "Today";
            if (d >= DateTime.Today.AddDays(-6)) return "This Week";
            return "Earlier";
        }
    }

    public DateTime? DeletedAt
    {
        get => _model.DeletedAt;
        set { _model.DeletedAt = value; OnPropertyChanged(); OnPropertyChanged(nameof(DeletedAtDisplay)); }
    }

    public string DeletedAtDisplay => _model.DeletedAt.HasValue
        ? $"Deleted {_model.DeletedAt.Value:MMM d, yyyy  h:mm tt}"
        : string.Empty;

    public bool IsCompleted
    {
        get => _model.IsCompleted;
        set
        {
            _model.IsCompleted = value;
            if (value && _model.IsActive) IsActive = false;
            OnPropertyChanged();
        }
    }

    public bool IsActive
    {
        get => _model.IsActive;
        set
        {
            if (_model.IsActive == value) return;
            if (value)
            {
                _model.IsActive = true;
                _model.IsPaused = false;
                _model.ActiveStartTime = DateTime.Now;
                StartTimer();
            }
            else
            {
                FlushSession(DateTime.Now);   // bank time while still marked active
                StopTimer();
                _model.IsActive = false;
                _model.IsPaused = false;
                OnPropertyChanged(nameof(ElapsedDisplay));
                OnPropertyChanged(nameof(IsPaused));
            }
            OnPropertyChanged();
            OnPropertyChanged(nameof(ActiveStartTime));
        }
    }

    public bool IsPaused
    {
        get => _model.IsPaused;
        private set { _model.IsPaused = value; OnPropertyChanged(); }
    }

    public DateTime? ActiveStartTime => _model.ActiveStartTime;

    public int SortOrder
    {
        get => _model.SortOrder;
        set { _model.SortOrder = value; OnPropertyChanged(); }
    }

    public int AccumulatedSeconds => _model.AccumulatedSeconds;

    public int TotalSeconds => _model.AccumulatedSeconds + SessionSecondsUntil(DateTime.Now);

    /// <summary>True while the timer is counting (active and not paused).</summary>
    public bool IsRunning => _model.IsActive && !_model.IsPaused;

    public int? ManualTimeMinutes
    {
        get => _model.ManualTimeMinutes;
        set { _model.ManualTimeMinutes = value; OnPropertyChanged(); OnPropertyChanged(nameof(TimeDisplay)); }
    }

    public string ElapsedDisplay => FormatSeconds(TotalSeconds);

    public string TimeDisplay
    {
        get
        {
            if (TotalSeconds > 0) return FormatSeconds(TotalSeconds);
            if (_model.ManualTimeMinutes.HasValue)
                return FormatSeconds(_model.ManualTimeMinutes.Value * 60);
            return "—";
        }
    }

    // Reminder count badge

    private int _reminderCount;
    public int ReminderCount
    {
        get => _reminderCount;
        set
        {
            _reminderCount = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ReminderCountDisplay));
            OnPropertyChanged(nameof(HasReminders));
        }
    }
    public bool HasReminders  => _reminderCount > 0;
    public string ReminderCountDisplay => _reminderCount == 1 ? "1 reminder" : $"{_reminderCount} reminders";

    private int _attachmentCount;
    public int AttachmentCount
    {
        get => _attachmentCount;
        set { _attachmentCount = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasAttachments)); OnPropertyChanged(nameof(AttachmentCountDisplay)); }
    }
    public bool HasAttachments => _attachmentCount > 0;
    public string AttachmentCountDisplay => _attachmentCount == 1 ? "1 attachment" : $"{_attachmentCount} attachments";

    // Pause / Resume

    public void TogglePause()
    {
        if (!_model.IsActive) return;
        if (_model.IsPaused) Resume(); else Pause();
    }

    public void Pause() => Pause(DateTime.Now);

    /// <summary>
    /// Pauses the timer, counting time only up to <paramref name="pausedAt"/>.
    /// Pass an earlier moment (e.g. the user's last keyboard/mouse input) so idle
    /// minutes are not counted as work.
    /// </summary>
    public void Pause(DateTime pausedAt)
    {
        if (!IsRunning) return;
        FlushSession(pausedAt);
        StopTimer();
        IsPaused = true;
        OnPropertyChanged(nameof(ElapsedDisplay));
        OnPropertyChanged(nameof(TimeDisplay));
    }

    public void Resume()
    {
        if (!_model.IsActive || !_model.IsPaused) return;
        _model.ActiveStartTime = DateTime.Now;
        IsPaused = false;
        StartTimer();
    }

    /// <summary>Stops the display timer without touching tracked time (the view model is being discarded).</summary>
    public void Detach() => StopTimer();

    // Timer — only refreshes the display; time itself comes from the clock

    private void StartTimer()
    {
        StopTimer();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) =>
        {
            OnPropertyChanged(nameof(ElapsedDisplay));
            OnPropertyChanged(nameof(TimeDisplay));
            OnPropertyChanged(nameof(TotalSeconds));
        };
        _timer.Start();
    }

    private void StopTimer() { _timer?.Stop(); _timer = null; }

    private int SessionSecondsUntil(DateTime until)
    {
        if (!IsRunning || !_model.ActiveStartTime.HasValue) return 0;
        return Math.Max(0, (int)(until - _model.ActiveStartTime.Value).TotalSeconds);
    }

    /// <summary>Moves the live session into AccumulatedSeconds and ends it.</summary>
    private void FlushSession(DateTime until)
    {
        _model.AccumulatedSeconds += SessionSecondsUntil(until);
        _model.ActiveStartTime = null;
        OnPropertyChanged(nameof(AccumulatedSeconds));
    }

    // Model access

    public TaskItem Model => _model;

    /// <summary>
    /// Returns the model ready to save. A running task is checkpointed: time so far
    /// is banked and a new session starts now, so at most one checkpoint interval
    /// can be lost if Tasky stops unexpectedly.
    /// </summary>
    public TaskItem GetModelForSave()
    {
        if (IsRunning)
        {
            var now = DateTime.Now;
            _model.AccumulatedSeconds += SessionSecondsUntil(now);
            _model.ActiveStartTime = now;
        }
        return _model;
    }

    // Helpers

    public static string FormatSeconds(int seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}h {ts.Minutes:D2}m {ts.Seconds:D2}s"
            : $"{ts.Minutes:D2}m {ts.Seconds:D2}s";
    }

    // INotifyPropertyChanged

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

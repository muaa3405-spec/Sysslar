using Avalonia.Threading;
using Tasky.Platform;

namespace Tasky.Services;

/// <summary>
/// Pauses the running task when the computer is not being used:
///   • idle (no keyboard/mouse) for 5 minutes — resumes when activity returns;
///   • session locked — resumes on unlock;
///   • sleep / hibernate — stays paused after wake-up (resume it yourself).
/// A task the user paused by hand is never resumed automatically.
/// </summary>
public sealed class IdleMonitorService : IDisposable
{
    // ─── Configuration ────────────────────────────────────────────────────
    private static readonly TimeSpan IdleThreshold = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PollInterval  = TimeSpan.FromSeconds(30);

    // ─── State ────────────────────────────────────────────────────────────
    private readonly IUserActivity _activity;
    private readonly Func<DateTime, bool> _pauseRunning;   // returns true if something was paused
    private readonly Action _resumeActive;
    private readonly DispatcherTimer _timer;

    /// <summary>True only when this monitor paused a running task (so it may resume it).</summary>
    private bool _isAutoPaused;

    /// <param name="pauseRunning">Pauses running tasks, counting time up to the given moment.</param>
    /// <param name="resumeActive">Resumes the active task.</param>
    public IdleMonitorService(IUserActivity activity, Func<DateTime, bool> pauseRunning, Action resumeActive)
    {
        _activity     = activity;
        _pauseRunning = pauseRunning;
        _resumeActive = resumeActive;
        _timer = new DispatcherTimer { Interval = PollInterval };
        _timer.Tick += OnTimerTick;
        _activity.SessionLocked   += OnSessionLocked;
        _activity.SessionUnlocked += OnSessionUnlocked;
        _activity.SystemSlept     += OnSystemSlept;
    }

    public void Start() => _timer.Start();

    public void Stop()  => _timer.Stop();

    // ─── Idle check ───────────────────────────────────────────────────────

    private void OnTimerTick(object? sender, EventArgs e)
    {
        var idle = _activity.IdleTime;
        if (idle >= IdleThreshold)
        {
            // Count work only up to the last keyboard/mouse input.
            if (!_isAutoPaused) TryAutoPause(DateTime.Now - idle, "Computer idle for 5 minutes");
        }
        else if (_isAutoPaused)
        {
            AutoResume();
        }
    }

    // ─── Lock / unlock ────────────────────────────────────────────────────

    private void OnSessionLocked()
    {
        if (!_isAutoPaused) TryAutoPause(DateTime.Now, "Computer locked");
    }

    private void OnSessionUnlocked()
    {
        if (_isAutoPaused) AutoResume();
    }

    // ─── Sleep / hibernate ────────────────────────────────────────────────

    private void OnSystemSlept(DateTime sleptAt)
    {
        OnUiThread(() =>
        {
            _pauseRunning(sleptAt);
            // After a sleep the user decides when work starts again.
            _isAutoPaused = false;
        });
    }

    // ─── Helpers ──────────────────────────────────────────────────────────

    private void TryAutoPause(DateTime pausedAt, string reason)
    {
        OnUiThread(() =>
        {
            _isAutoPaused = _pauseRunning(pausedAt);
            if (_isAutoPaused)
                InAppNotificationService.Warning($"Timer paused — {reason}.");
        });
    }

    private void AutoResume()
    {
        _isAutoPaused = false;
        OnUiThread(() =>
        {
            _resumeActive();
            InAppNotificationService.Info("Timer resumed — you're back!");
        });
    }

    /// <summary>Session events can arrive on a system thread; tasks are only touched on the UI thread.</summary>
    private static void OnUiThread(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Invoke(action);
    }

    // ─── Disposal ─────────────────────────────────────────────────────────

    public void Dispose()
    {
        _timer.Stop();
        _activity.SessionLocked   -= OnSessionLocked;
        _activity.SessionUnlocked -= OnSessionUnlocked;
        _activity.SystemSlept     -= OnSystemSlept;
    }
}

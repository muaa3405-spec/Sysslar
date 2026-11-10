using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Tasky.Helpers;
using Tasky.Platform;

namespace Tasky.Dialogs;

public partial class ReminderToastWindow : Window
{
    // Named transforms get no generated field in Avalonia; reach it through its owner.
    private TranslateTransform SlideTransform => (TranslateTransform)RootBorder.RenderTransform!;

    /// <summary>Gap between the toast and the edges of the work area.</summary>
    private const double ScreenMargin = 16;

    /// <summary>Distance the toast slides in from (off its right edge).</summary>
    private const double SlideDistance = 360;

    private static readonly TimeSpan SlideInDuration  = TimeSpan.FromMilliseconds(380);
    private static readonly TimeSpan FadeInDuration   = TimeSpan.FromMilliseconds(260);
    private static readonly TimeSpan SlideOutDuration = TimeSpan.FromMilliseconds(280);

    private readonly int _taskId;
    private readonly string _taskTitle;
    private readonly DispatcherTimer _autoDismiss;

    [Obsolete("XAML previewer only")]
    public ReminderToastWindow()
    {
        _taskTitle   = string.Empty;
        _autoDismiss = new DispatcherTimer();
        InitializeComponent();
    }

    /// <param name="displayTitle">Title shown on the toast (with the task number).</param>
    /// <param name="taskTitle">Plain task title, reused when the toast is snoozed.</param>
    public ReminderToastWindow(string displayTitle, string taskTitle, int taskId)
    {
        _taskId    = taskId;
        _taskTitle = taskTitle;
        InitializeComponent();
        Helpers.DialogZoom.Apply(this);
        TitleText.Text = displayTitle;

        // Bottom-right, above the taskbar / Dock. The height is only final once SizeToContent has
        // measured the content, which can be after Opened, so place it again whenever the size changes.
        PlaceAtBottomRight();
        SizeChanged += (_, _) => PlaceAtBottomRight();
        Opacity = 0;

        Opened += (_, _) =>
        {
            PlaceAtBottomRight();

            // Apply accent color from settings to the top strip + header text
            if (Color.TryParse(App.Settings.Current.NotifAccentColor, out var accent))
                AccentStrip.Background = new SolidColorBrush(accent);

            // Slide in: X 360→0 + fade in
            _ = Animator.Animate(SlideTransform, TranslateTransform.XProperty,
                                 SlideDistance, 0, SlideInDuration, new CubicEaseOut());
            _ = Animator.Fade(this, 0, 1, FadeInDuration, new CubicEaseOut());
        };

        // Auto-dismiss — use setting (0 = permanent, else N seconds)
        int secs = App.Settings.Current.NotifAutoDismissSecs;
        _autoDismiss = new DispatcherTimer
            { Interval = TimeSpan.FromSeconds(secs > 0 ? secs : int.MaxValue / 2) };
        _autoDismiss.Tick += (_, _) => SlideClose();
        if (secs > 0) _autoDismiss.Start();

        PlatformServices.Sounds.Play(App.Settings.Current.NotifTune);
    }

    private void PlaceAtBottomRight()
    {
        var area = ScreenHelper.WorkArea(this);
        double height = Bounds.Height > 0 ? Bounds.Height : DesiredSize.Height;
        ScreenHelper.MoveTo(this, new Point(area.Right - Width - ScreenMargin,
                                            area.Bottom - height - ScreenMargin));
    }

    private void Snooze_Click(object? sender, RoutedEventArgs e)
    {
        _autoDismiss.Stop();
        int minutes = (SnoozeBox.SelectedItem as ComboBoxItem)?.Tag is string t
                      && int.TryParse(t, out int m) ? m : 5;
        App.Notifications?.ScheduleSnooze(_taskId, _taskTitle, DateTime.Now.AddMinutes(minutes));
        SlideClose();
    }

    private async void OpenTask_Click(object? sender, RoutedEventArgs e)
    {
        _autoDismiss.Stop();
        if (App.MainAppWindow is { } mw)
        {
            var vm = App.ViewModel?.Tasks.FirstOrDefault(t => t.Id == _taskId)
                  ?? App.ViewModel?.CompletedTasks.FirstOrDefault(t => t.Id == _taskId);
            mw.ShowFromTray();
            mw.Activate();
            // The task detail is modal: the toast slides away once it is closed (as before)
            if (vm is not null) await mw.OpenTaskDetail(vm);
        }
        SlideClose();
    }

    private void Dismiss_Click(object? sender, RoutedEventArgs e)
    {
        _autoDismiss.Stop();
        SlideClose();
    }

    private async void SlideClose()
    {
        _autoDismiss.Stop();

        // Slide out: X 0→360 (exits to the right) + fade out
        var slide = Animator.Animate(SlideTransform, TranslateTransform.XProperty,
                                     0, SlideDistance, SlideOutDuration, new CubicEaseIn());
        var fade  = Animator.Fade(this, 1, 0, SlideOutDuration, new CubicEaseIn());
        await Task.WhenAll(slide, fade);
        Close();
    }
}

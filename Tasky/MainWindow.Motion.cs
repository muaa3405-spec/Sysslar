using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tasky.Helpers;
using Tasky.ViewModels;

namespace Tasky;

/// <summary>
/// Motion in the main window: cards rise in one after another, a completed card folds away,
/// dialogs grow in, and the notification badge pops. Everything jumps to its end state when
/// the OS asks to reduce motion (see <see cref="Motion"/>).
/// </summary>
public partial class MainWindow
{
    private static readonly TimeSpan CardStagger    = TimeSpan.FromMilliseconds(30);
    private static readonly TimeSpan CardRise       = TimeSpan.FromMilliseconds(280);
    private static readonly TimeSpan BatchWindow    = TimeSpan.FromMilliseconds(80);
    private const int MaxStaggeredCards = 10;

    private DateTime _lastCardPrepared = DateTime.MinValue;
    private int _cardsInBatch;
    private int _lastBadgeCount;

    private void SetUpMotion()
    {
        Classes.Set("reduceMotion", !Motion.Enabled);
        TaskListControl.ContainerPrepared += (_, e) => StaggerCardIn(e.Container);
        Loaded += (_, _) =>
        {
            if (DataContext is not MainViewModel vm) return;
            _lastBadgeCount = vm.MissedNotifCount;
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(MainViewModel.MissedNotifCount)) return;
                if (vm.MissedNotifCount > _lastBadgeCount) PopBadge();
                _lastBadgeCount = vm.MissedNotifCount;
            };
        };
    }

    /// <summary>
    /// Cards that appear together (a new filter, search or page) rise in one after another;
    /// a single new card just rises in.
    /// </summary>
    private void StaggerCardIn(Control container)
    {
        if (!Motion.Enabled) return;
        var now = DateTime.UtcNow;
        _cardsInBatch = now - _lastCardPrepared < BatchWindow ? _cardsInBatch + 1 : 0;
        _lastCardPrepared = now;

        var delay = CardStagger * Math.Min(_cardsInBatch, MaxStaggeredCards);
        var lift = new TranslateTransform(0, 10);
        container.RenderTransform = lift;
        container.Opacity = 0;
        DispatcherTimer.RunOnce(() =>
        {
            var ease = new CubicEaseOut();
            _ = Animator.Animate(lift, TranslateTransform.YProperty, 10, 0, CardRise, ease);
            _ = Animator.Fade(container, 0, 1, CardRise, ease);
        }, delay + TimeSpan.FromMilliseconds(1));
    }

    /// <summary>The tick pops, then the task's card fades and folds its height away.</summary>
    private async Task AnimateCardOutAsync(TaskItemViewModel vm, CheckBox? box)
    {
        if (!Motion.Enabled) return;
        int index = Vm.VisibleTasks.IndexOf(vm);
        if (index < 0 || TaskListControl.ContainerFromIndex(index) is not Control container) return;

        if (box is not null)
        {
            var pop = new ScaleTransform(1, 1);
            box.RenderTransform = pop;
            var back = new BackEaseOut();
            _ = Animator.Animate(pop, ScaleTransform.ScaleXProperty, 0.8, 1, TimeSpan.FromMilliseconds(260), back);
            await Animator.Animate(pop, ScaleTransform.ScaleYProperty, 0.8, 1, TimeSpan.FromMilliseconds(260), back);
        }

        var ease = new CubicEaseInOut();
        double height = container.Bounds.Height;
        container.ClipToBounds = true;
        _ = Animator.Fade(container, 1, 0, TimeSpan.FromMilliseconds(180), ease);
        await Animator.Animate(container, HeightProperty, height, 0, TimeSpan.FromMilliseconds(240), ease);

        // The container is reused for other tasks: put it back as it was
        container.ClearValue(HeightProperty);
        container.ClearValue(ClipToBoundsProperty);
        container.Opacity = 1;
        if (box is not null) box.RenderTransform = null;
    }

    /// <summary>Dialogs fade in and grow from 96 %.</summary>
    private static void AnimateDialogIn(Window dialog)
    {
        if (!Motion.Enabled) return;
        dialog.Opened += (_, _) =>
        {
            if (dialog.Content is not Visual content) return;
            var scale = new ScaleTransform(0.96, 0.96);
            content.RenderTransformOrigin = RelativePoint.Center;
            content.RenderTransform = scale;
            var ease = new CubicEaseOut();
            var time = TimeSpan.FromMilliseconds(200);
            _ = Animator.Animate(scale, ScaleTransform.ScaleXProperty, 0.96, 1, time, ease);
            _ = Animator.Animate(scale, ScaleTransform.ScaleYProperty, 0.96, 1, time, ease);
            _ = Animator.Fade(content, 0, 1, time, ease);
        };
    }

    /// <summary>A new missed reminder: the badge on the bell pops.</summary>
    private void PopBadge()
    {
        if (!Motion.Enabled) return;
        Dispatcher.UIThread.Post(() =>
        {
            var scale = new ScaleTransform(0.4, 0.4);
            NotifBadge.RenderTransform = scale;
            var back = new BackEaseOut();
            var time = TimeSpan.FromMilliseconds(320);
            _ = Animator.Animate(scale, ScaleTransform.ScaleXProperty, 0.4, 1, time, back);
            _ = Animator.Animate(scale, ScaleTransform.ScaleYProperty, 0.4, 1, time, back);
        });
    }
}

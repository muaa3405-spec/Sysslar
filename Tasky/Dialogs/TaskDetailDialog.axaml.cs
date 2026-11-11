using System.IO;
using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Tasky.Helpers;
using Tasky.ViewModels;

namespace Tasky.Dialogs;

public partial class TaskDetailDialog : Window, IScrollsOwnContent
{
    // Named transforms get no generated field in Avalonia; reach it through its owner.
    private TranslateTransform SlideTransform => (TranslateTransform)DialogRoot.RenderTransform!;

    private const int ThumbnailDecodeWidth = 200;
    private static readonly TimeSpan OpenAnimationDuration = TimeSpan.FromMilliseconds(280);
    private static readonly TimeSpan ReminderConfirmDelay  = TimeSpan.FromMilliseconds(1200);
    private const double SlideOffset = 18;

    private readonly int _taskId;
    private readonly string _taskTitle = string.Empty;

    [Obsolete("XAML previewer only")]
    public TaskDetailDialog() => InitializeComponent();

    public TaskDetailDialog(TaskItemViewModel vm, MainViewModel mainVm)
    {
        _taskId    = vm.Id;
        _taskTitle = vm.Title;
        InitializeComponent();
        ThemeHelper.Attach(this);

        // ID + title
        TaskNumberLabel.Text = vm.TaskNumberDisplay;
        TitleLabel.Text      = vm.Title;

        // Description
        DescLabel.Text = string.IsNullOrWhiteSpace(vm.Description)
            ? "(no description)" : vm.Description;

        // Status badge
        if (vm.IsCompleted)
        {
            SetStatus("Completed", "Success", "SuccessSoft");
        }
        else if (vm.IsActive)
        {
            if (vm.IsPaused) SetStatus("Paused", "Warning", "WarningSoft");
            else             SetStatus("Timer running", "Success", "SuccessSoft");
        }
        else
        {
            SetStatus("Pending", "TextSecondary", "InputBackground");
        }

        // Time
        TimeLabel.Text = vm.TimeDisplay == "—"
            ? "Not tracked yet"
            : vm.TimeDisplay;

        // Hide reminder section for completed tasks
        if (vm.IsCompleted)
            QuickRemindCard.IsVisible = false;

        // Created + start date
        CreatedLabel.Text = $"Created: {vm.Model.CreatedAt:MMM d yyyy}";
        DueLabel.Text = vm.DueDate.HasValue
            ? vm.DueDateTag.Length > 0 ? $"{vm.DueDateTag} ({vm.DueDate.Value:MMM d yyyy})" : vm.DueDate.Value.ToString("MMM d yyyy")
            : "Not set";

        LoadAttachments(mainVm.GetAttachments(vm.Id));
    }

    private void SetStatus(string text, string foreground, string background)
    {
        StatusLabel.Text = text;
        StatusLabel.WithResource(ForegroundProperty, foreground);
        StatusBadge.WithResource(Border.BackgroundProperty, background);
    }

    // ─── Attachments ──────────────────────────────────────────────────────

    private void LoadAttachments(List<Models.TaskAttachment> attachments)
    {
        if (attachments.Count == 0)
        {
            AttachHeader.IsVisible  = false;
            AttachPanel.IsVisible   = false;
            NoAttachLabel.IsVisible = true;
            return;
        }

        NoAttachLabel.IsVisible = false;
        foreach (var att in attachments)
            AttachPanel.Children.Add(BuildCard(att, attachments));
    }

    private Border BuildCard(Models.TaskAttachment att, List<Models.TaskAttachment> all)
    {
        bool isImg = ImageFiles.IsImage(att.FilePath);

        var card = new Border
        {
            Width = 120, Height = 130,
            Margin = new Thickness(0, 0, 8, 8),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        card.WithResource(Border.BorderBrushProperty, "BorderColor");
        card.WithResource(Border.BackgroundProperty,  "CardBackground");

        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };

        if (isImg && File.Exists(att.FilePath))
        {
            stack.Children.Add(new Image
            {
                Width = 100, Height = 80,
                Stretch = Stretch.UniformToFill,
                Margin = new Thickness(0, 6, 0, 4),
                Source = ImageFiles.Load(att.FilePath, ThumbnailDecodeWidth)
            });
        }
        else
        {
            stack.Children.Add(new Tasky.Controls.LineIcon
            {
                Data = UiResources.Geometry("Icon.File"), Size = 36, StrokeThickness = 1.5,
                Foreground = UiResources.Brush("TextSecondary"),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 14, 0, 4)
            });
        }

        stack.Children.Add(new TextBlock
        {
            Text = att.FileName,
            FontSize = 10,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(4, 0, 4, 4)
        });

        card.Child = stack;
        card.PointerReleased += async (_, e) =>
        {
            if (e.InitialPressMouseButton != MouseButton.Left) return;
            if (!File.Exists(att.FilePath)) return;
            if (isImg)
            {
                var images = all.Where(a => ImageFiles.IsImage(a.FilePath)).ToList();
                int idx = images.FindIndex(a => a.Id == att.Id);
                await new Windows.ImageViewerWindow(images, idx >= 0 ? idx : 0).ShowDialog(this);
            }
            else
            {
                FileLauncher.Open(att.FilePath);
            }
        };

        return card;
    }

    // ─── Open animation ───────────────────────────────────────────────────

    private void Window_Opened(object? sender, EventArgs e)
    {
        var ease = new CubicEaseOut();

        // Fade in
        _ = Animator.Fade(FrameRoot, 0, 1, OpenAnimationDuration, ease);

        // Slide up: Y 18 → 0
        _ = Animator.Animate(SlideTransform, TranslateTransform.YProperty,
                             SlideOffset, 0, OpenAnimationDuration, ease);
    }

    // ─── Actions ──────────────────────────────────────────────────────────

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    private void QuickRemind_Click(object? sender, RoutedEventArgs e)
    {
        int minutes = (QuickRemindBox.SelectedItem as ComboBoxItem)?.Tag is string t
                      && int.TryParse(t, out int m) ? m : 5;
        var fireAt = DateTime.Now.AddMinutes(minutes);
        App.Notifications?.ScheduleSnooze(_taskId, _taskTitle, fireAt);

        // Show green confirmation, then close after 1.2 s
        QuickRemindCard.IsVisible = false;
        RemindSetLabel.Text       = $"Reminder set for {fireAt:HH:mm}";
        RemindSetCard.IsVisible   = true;

        var timer = new DispatcherTimer { Interval = ReminderConfirmDelay };
        timer.Tick += (_, _) => { timer.Stop(); Close(); };
        timer.Start();
    }
}

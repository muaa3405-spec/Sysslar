using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Templates;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Tasky.Models;
using Tasky.Services;
using Tasky.ViewModels;

namespace Tasky.Views;

/// <summary>
/// Edit a task on a page inside the main window (it replaced the Edit Task dialog).
/// Title, description, dates, repeat and group are applied on Save; attachments are saved right away.
/// </summary>
public partial class EditTaskPage : UserControl
{
    private static readonly Color FallbackGroupColor = Color.FromRgb(107, 114, 128);

    private readonly TaskItemViewModel _vm = null!;
    private readonly MainViewModel _mainVm = null!;

    /// <summary>The changes were copied onto the task; the caller saves it and leaves the page.</summary>
    public event Action<TaskItemViewModel>? Saved;

    /// <summary>Back, Cancel or Esc: nothing was changed (apart from attachments, which save on their own).</summary>
    public event Action? Cancelled;

    /// <summary>One entry of the group picker: "(No group)" has no id and no dot.</summary>
    private sealed record GroupOption(int? Id, string Name, IBrush? DotBrush);

    [Obsolete("XAML previewer only")]
    public EditTaskPage() => InitializeComponent();

    public EditTaskPage(TaskItemViewModel vm, MainViewModel mainVm)
    {
        _vm     = vm;
        _mainVm = mainVm;
        InitializeComponent();

        NumberText.Text            = vm.TaskNumberDisplay;
        TitleBox.Text              = vm.Title;
        DescBox.Text               = vm.Description;
        DueDatePicker.SelectedDate = vm.DueDate ?? DateTime.Today;

        PopulateGroupPicker(vm.GroupId);
        SelectRecurrence(vm.Recurrence);
        RepeatEndPicker.SelectedDate = vm.RecurrenceEndDate;

        SetUpAttachments();
    }

    /// <summary>Narrow window: the schedule fields stack in one column.</summary>
    public void SetCompact(bool compact)
    {
        Classes.Set("compact", compact);
        Grid.SetColumn(RepeatField, compact ? 0 : 2);
        Grid.SetRow(RepeatField,    compact ? 2 : 0);
        SchedulePair.RowDefinitions[1].Height = new GridLength(compact ? 12 : 0);
        SchedulePair.ColumnDefinitions[1].Width = new GridLength(compact ? 0 : 12);
        SchedulePair.ColumnDefinitions[2].Width = compact ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
    }

    /// <summary>Puts the cursor in the title, with the text selected.</summary>
    public void FocusTitle()
    {
        TitleBox.Focus();
        TitleBox.SelectAll();
    }

    // ─── Pickers ──────────────────────────────────────────────────────────

    private void PopulateGroupPicker(int? currentGroupId)
    {
        var options = new List<GroupOption> { new(null, "(No group)", null) };
        foreach (var g in _mainVm.Groups)
        {
            var dotColor = Color.TryParse(g.Color, out var c) ? c : FallbackGroupColor;
            options.Add(new GroupOption(g.Id, g.Name, new SolidColorBrush(dotColor)));
        }

        // Coloured dot + group name (same as the filter chips)
        GroupPicker.ItemTemplate = new FuncDataTemplate<GroupOption>((option, _) => BuildGroupContent(option));
        GroupPicker.ItemsSource  = options;

        int selected = options.FindIndex(o => o.Id == currentGroupId);
        GroupPicker.SelectedIndex = selected >= 0 ? selected : 0;
    }

    private static Control BuildGroupContent(GroupOption? option)
    {
        if (option is null) return new TextBlock();
        if (option.DotBrush is null) return new TextBlock { Text = option.Name };

        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing     = 8,
            Children    =
            {
                new Ellipse { Width = 9, Height = 9, Fill = option.DotBrush, VerticalAlignment = VerticalAlignment.Center },
                new TextBlock { Text = option.Name, VerticalAlignment = VerticalAlignment.Center },
            },
        };
    }

    private void SelectRecurrence(RecurrenceType recurrence)
    {
        int recVal = (int)recurrence;
        foreach (var item in RecurrencePicker.Items.OfType<ComboBoxItem>())
        {
            if (item.Tag is string s && int.TryParse(s, out int v) && v == recVal)
            {
                RecurrencePicker.SelectedItem = item;
                break;
            }
        }
        if (RecurrencePicker.SelectedIndex < 0) RecurrencePicker.SelectedIndex = 0;
    }

    private RecurrenceType SelectedRecurrence
        => RecurrencePicker.SelectedItem is ComboBoxItem { Tag: string tag } && int.TryParse(tag, out int value)
            ? (RecurrenceType)value
            : RecurrenceType.None;

    private void RecurrencePicker_Changed(object? sender, SelectionChangedEventArgs e)
    {
        // Fires during InitializeComponent too, before the end-date section exists.
        if (RepeatEndSection is null) return;
        RepeatEndSection.IsVisible = SelectedRecurrence != RecurrenceType.None;
    }

    // ─── Attachments ──────────────────────────────────────────────────────

    private void SetUpAttachments()
    {
        Attachments.FilesAdded      += AttachFiles;
        Attachments.ImagePasted     += AttachPastedImage;
        Attachments.DeleteRequested += RemoveAttachment;
        Attachments.AcceptAttachmentsAnywhereIn(this);
        ReloadAttachments();
    }

    private void ReloadAttachments()
    {
        Attachments.SetAttachments(_mainVm.GetAttachments(_vm.Id));
        _mainVm.RefreshAttachmentCount(_vm);
    }

    private void AttachFiles(IReadOnlyList<string> paths)
    {
        int saved = paths.Count(path => _mainVm.SaveAttachment(_vm.Id, path));
        ReloadAttachments();
        if (saved > 0) InAppNotificationService.Success(saved == 1 ? "File attached." : $"{saved} files attached.");
    }

    private void AttachPastedImage(byte[] png)
    {
        if (_mainVm.SaveAttachmentFromBytes(_vm.Id, png, ".png"))
            InAppNotificationService.Success("Image pasted and attached.");
        ReloadAttachments();
    }

    private void RemoveAttachment(IAttachment attachment)
    {
        if (attachment is not TaskAttachment taskAttachment) return;
        _mainVm.DeleteAttachment(taskAttachment);
        ReloadAttachments();
    }

    // ─── Save / cancel ────────────────────────────────────────────────────

    /// <summary>Copies the fields onto the task and raises <see cref="Saved"/>; false when the title is empty.</summary>
    public bool Save()
    {
        if (string.IsNullOrWhiteSpace(TitleBox.Text))
        {
            InAppNotificationService.Warning("Title cannot be empty.");
            FocusTitle();
            return false;
        }
        _vm.Title       = TitleBox.Text.Trim();
        _vm.Description = (DescBox.Text ?? string.Empty).Trim();
        _vm.DueDate     = DueDatePicker.SelectedDate ?? DateTime.Today;

        if (GroupPicker.SelectedItem is GroupOption group)
            _vm.GroupId = group.Id;

        _vm.Recurrence        = SelectedRecurrence;
        _vm.RecurrenceEndDate = SelectedRecurrence == RecurrenceType.None ? null : RepeatEndPicker.SelectedDate;

        Saved?.Invoke(_vm);
        return true;
    }

    public void Cancel() => Cancelled?.Invoke();

    private void Save_Click(object? sender, RoutedEventArgs e) => Save();

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Cancel();
}

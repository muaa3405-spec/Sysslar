using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Tasky.Dialogs;
using Tasky.Helpers;
using Tasky.ViewModels;

namespace Tasky;

/// <summary>
/// Keyboard shortcuts of the main window (the list users see is <see cref="KeyboardShortcuts"/>).
/// Single keys only work while no text box has focus, so typing is never interrupted.
/// </summary>
public partial class MainWindow
{
    private void RegisterKeyboardShortcuts()
        => AddHandler(KeyDownEvent, Window_KeyDown, RoutingStrategies.Bubble);

    private bool IsTyping => FocusManager?.GetFocusedElement() is TextBox;

    private void Window_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || DataContext is not MainViewModel) return;
        e.Handled = e.KeyModifiers.HasFlag(KeyboardShortcuts.CommandModifier)
            ? HandleCommandShortcut(e)
            : HandleSingleKeyShortcut(e);
    }

    /// <summary>Ctrl+… on Windows, ⌘… on macOS.</summary>
    private bool HandleCommandShortcut(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.N:
                OpenNewTaskForm();
                return true;
            case Key.F:
                FocusTaskSearch();
                return true;
            case Key.OemPlus or Key.Add:
                StepZoom(+1);
                return true;
            case Key.OemMinus or Key.Subtract:
                StepZoom(-1);
                return true;
            case Key.D0 or Key.NumPad0:
                SetZoom(TextSizes.Default);
                return true;
            case Key.OemComma:
                NavigateTo(AppPage.Settings);
                return true;
            case Key.S when _editPage is not null:
                _editPage.Save();
                return true;
            case Key.S when _notePage is not null:
                _notePage.Save();
                return true;
            case Key.Z when !IsTyping:   // in a text box Ctrl+Z undoes typing
                Vm.UndoLast();
                return true;
            default:
                return false;
        }
    }

    private bool HandleSingleKeyShortcut(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) return HandleEscape();
        if (IsTyping || e.KeyModifiers.HasFlag(KeyModifiers.Alt)) return false;

        switch (e.KeySymbol)
        {
            case "?":
                _ = ShowShortcutsAsync();
                return true;
            case "/":
                FocusTaskSearch();
                return true;
        }

        if (!TaskListPage.IsVisible) return false;
        switch (e.Key)
        {
            case Key.N:                            OpenNewTaskForm();                     return true;
            case Key.Down:                         MoveSelection(+1);                     return true;
            case Key.Up:                           MoveSelection(-1);                     return true;
            case Key.D1 or Key.NumPad1:            Vm.ActiveFilter = TaskFilter.Today;    return true;
            case Key.D2 or Key.NumPad2:            Vm.ActiveFilter = TaskFilter.ThisWeek; return true;
            case Key.D3 or Key.NumPad3:            Vm.ActiveFilter = TaskFilter.All;      return true;
        }

        if (SelectedTask is not { } task) return false;
        switch (e.Key)
        {
            case Key.Enter:              ShowDimmedDetail(task);                return true;
            case Key.Space:              _ = ConfirmAndCompleteAsync(task);     return true;
            case Key.E:                  _ = EditTaskAsync(task);               return true;
            case Key.T:                  ToggleTimer(task);                     return true;
            case Key.Delete:             _ = ConfirmAndDeleteAsync(task);       return true;
            case Key.Back when OperatingSystem.IsMacOS():   // the Mac key labelled "delete"
                _ = ConfirmAndDeleteAsync(task);
                return true;
            case Key.P:
                var option = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? PostponeOptions.NextWeek : PostponeOptions.Tomorrow;
                Vm.Postpone(task, option.NewDueDate(DateTime.Today));
                return true;
            default:
                return false;
        }
    }

    /// <summary>Esc closes the new-task form, leaves a sub-page, or clears the task search.</summary>
    private bool HandleEscape()
    {
        if (_editPage is not null)
        {
            _editPage.Cancel();
            return true;
        }
        if (_notePage is not null)
        {
            _notePage.TryLeave();
            return true;
        }
        if (_addPanelOpen)
        {
            AddToggle_Click(this, new RoutedEventArgs());
            return true;
        }
        if (!TaskListPage.IsVisible)
        {
            Back_Click(this, new RoutedEventArgs());
            return true;
        }
        if (IsTyping || !string.IsNullOrEmpty(Vm.TaskSearch))
        {
            Vm.TaskSearch = string.Empty;
            TaskListControl.Focus();
            return true;
        }
        return false;
    }

    private void OpenNewTaskForm()
    {
        if (!TaskListPage.IsVisible) Back_Click(this, new RoutedEventArgs());
        if (_addPanelOpen) TitleInputBox.Focus();
        else AddToggle_Click(this, new RoutedEventArgs());
    }

    private void FocusTaskSearch()
    {
        if (!TaskListPage.IsVisible) Back_Click(this, new RoutedEventArgs());
        TaskSearchBox.Focus();
        TaskSearchBox.SelectAll();
    }

    private Task ShowShortcutsAsync() => ShowModalDialog(new GoodToKnowDialog(showTips: false));

    // ── Picked task ──────────────────────────────────────────────────────

    private TaskItemViewModel? SelectedTask => Vm.VisibleTasks.FirstOrDefault(t => t.IsSelected);

    private void MoveSelection(int step)
    {
        var tasks = Vm.VisibleTasks;
        if (tasks.Count == 0) return;

        int current = SelectedTask is { } selected ? tasks.IndexOf(selected) : -1;
        int next = current < 0
            ? (step > 0 ? 0 : tasks.Count - 1)
            : Math.Clamp(current + step, 0, tasks.Count - 1);

        foreach (var task in Vm.Tasks) task.IsSelected = false;
        tasks[next].IsSelected = true;
        TaskListControl.ContainerFromItem(tasks[next])?.BringIntoView();
    }
}

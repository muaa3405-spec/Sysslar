using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Tasky.Services;
using Tasky.ViewModels;

namespace Tasky;

/// <summary>
/// View choices that are remembered between launches: All / Today / This week, the group filter,
/// the Completed sort order and the Stats card filters (window size, position and zoom are in
/// MainWindow.WindowPlacement and MainWindow.DataTools).
/// </summary>
public partial class MainWindow
{
    private bool _viewChoicesRestored;

    private static void RememberChoice(Action<AppSettings> change)
    {
        change(App.Settings.Current);
        App.Settings.Save();
    }

    private void RestoreViewChoices(MainViewModel vm)
    {
        var s = App.Settings.Current;

        if (Enum.TryParse<TaskFilter>(s.TaskView, out var view)) vm.ActiveFilter = view;
        if (s.GroupFilter is { } groupId && vm.Groups.Any(g => g.Id == groupId)) vm.SelectedGroupFilter = groupId;

        foreach (var item in SortBox.Items.OfType<ComboBoxItem>())
            if (item.Tag as string == s.CompletedSort) SortBox.SelectedItem = item;

        _topTasksFilter = s.TopTasksFilter;
        _meetingFilter  = s.MeetingFilter;
        _appUsageFilter = s.AppUsageFilter;
        Check(s.TopTasksFilter, TopToday, TopWeek, TopMonth, TopOverall);
        Check(s.MeetingFilter,  MeetingFilterToday, MeetingFilterWeek, MeetingFilterMonth);
        Check(s.AppUsageFilter, AppUsageToday, AppUsageWeek, AppUsageMonth);

        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.ActiveFilter))
                RememberChoice(x => x.TaskView = vm.ActiveFilter.ToString());
            else if (e.PropertyName == nameof(MainViewModel.SelectedGroupFilter))
                RememberChoice(x => x.GroupFilter = vm.SelectedGroupFilter);
        };
        _viewChoicesRestored = true;
    }

    /// <summary>Ticks the chip whose Tag is <paramref name="tag"/>.</summary>
    private static void Check(string tag, params ToggleButton[] chips)
    {
        foreach (var chip in chips)
            chip.IsChecked = chip.Tag as string == tag;
    }
}

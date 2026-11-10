using Avalonia.Data.Converters;

namespace Tasky.Helpers;

/// <summary>Tooltip for a task's play / stop button, from whether its timer is running.</summary>
public static class TimerTips
{
    public static readonly IValueConverter Converter =
        new FuncValueConverter<bool, string>(running => running ? "Stop the timer" : "Start the timer");
}

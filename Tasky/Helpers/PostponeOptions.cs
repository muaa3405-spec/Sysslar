namespace Tasky.Helpers;

/// <summary>The "postpone" choices offered on a task, and the dates they move it to.</summary>
public static class PostponeOptions
{
    public sealed record Option(string Label, Func<DateTime, DateTime> NewDueDate);

    public static readonly Option Tomorrow = new("Tomorrow", today => today.AddDays(1));

    /// <summary>Monday of next week (the week starts on Monday, as in the "This Week" filter).</summary>
    public static readonly Option NextWeek = new("Next week (Monday)", NextMonday);

    /// <summary>The days after tomorrow up to and including this Sunday (empty from Saturday on).</summary>
    public static IEnumerable<DateTime> RestOfThisWeek(DateTime today)
    {
        var monday = NextMonday(today);
        for (var d = today.AddDays(2); d < monday; d = d.AddDays(1)) yield return d;
    }

    /// <summary>Monday to Sunday of next week.</summary>
    public static IEnumerable<DateTime> DaysOfNextWeek(DateTime today)
    {
        var monday = NextMonday(today);
        for (int i = 0; i < 7; i++) yield return monday.AddDays(i);
    }

    /// <summary>A menu label for a day, e.g. "Thursday · 12 Nov".</summary>
    public static string DayLabel(DateTime day) => $"{day:dddd} · {day:d MMM}";

    private static DateTime NextMonday(DateTime today)
    {
        int daysUntilMonday = ((int)DayOfWeek.Monday - (int)today.DayOfWeek + 7) % 7;
        return today.AddDays(daysUntilMonday == 0 ? 7 : daysUntilMonday);
    }
}

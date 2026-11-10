using Tasky.Models;

namespace Tasky.Helpers;

/// <summary>How repeating tasks behave, kept in one place for the task list, the forms and their help text.</summary>
public static class RecurrenceRules
{
    /// <summary>Shown by the ⓘ next to every Repeat picker.</summary>
    public const string Explanation =
        "Repeat makes a recurring task. When you mark it done, Sysslar adds the next one for you: " +
        "tomorrow (Daily), a week later (Weekly) or a month later (Monthly), counted from the day you complete it.\n\n" +
        "Ends on (optional): no new copy is added after this date. Leave it empty, or delete the date, to repeat forever.";

    /// <summary>The due date of the copy made when a task with this repeat is completed today.</summary>
    public static DateTime NextDueDate(RecurrenceType recurrence) => recurrence switch
    {
        RecurrenceType.Weekly  => DateTime.Today.AddDays(7),
        RecurrenceType.Monthly => DateTime.Today.AddMonths(1),
        _                      => DateTime.Today.AddDays(1)
    };

    /// <summary>True when a copy due on <paramref name="nextDue"/> is still within the repeat's end date.</summary>
    public static bool ContinuesOn(DateTime nextDue, DateTime? endDate)
        => endDate is null || nextDue.Date <= endDate.Value.Date;
}

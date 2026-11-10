using Avalonia.Input;

namespace Tasky.Helpers;

/// <summary>One row of the shortcut list: the keys to press and what they do.</summary>
public sealed record KeyboardShortcut(string Keys, string Action);

/// <summary>
/// Every keyboard shortcut of the main window, written once. The same list is shown in
/// Settings, in the first-start "Good to know" tips and when the user presses "?".
/// </summary>
public static class KeyboardShortcuts
{
    /// <summary>The command key: Ctrl on Windows, ⌘ on macOS.</summary>
    public static KeyModifiers CommandModifier => OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;

    private static string Command => OperatingSystem.IsMacOS() ? "⌘" : "Ctrl+";

    /// <summary>The system-wide quick-add hotkey registered by the platform layer.</summary>
    public static string QuickAddAnywhere => OperatingSystem.IsMacOS() ? "Control+Option+T" : "Win+Shift+T";

    private static string DeleteKey => OperatingSystem.IsMacOS() ? "⌫ Delete" : "Delete";

    public static IReadOnlyList<KeyboardShortcut> All { get; } =
    [
        new("N",                       "New task"),
        new($"/   or   {Command}F",    "Search tasks"),
        new("↑   ↓",                   "Pick a task in the list"),
        new("Enter",                   "Open the picked task"),
        new("Space",                   "Mark the picked task done"),
        new("E",                       "Edit the picked task"),
        new("T",                       "Start or stop its timer"),
        new("P",                       "Postpone it to tomorrow"),
        new("Shift+P",                 "Postpone it to next week"),
        new(DeleteKey,                 "Move it to the recycle bin"),
        new($"{Command}Z",             "Undo the last complete, delete or postpone"),
        new("1   2   3",               "Show Today, This Week or All"),
        new($"{Command},",             "Open Settings"),
        new("Esc",                     "Close the new-task form, or go back"),
        new("?",                       "Show these shortcuts"),
        new(QuickAddAnywhere,          "Quick add from any app, even when Sysslar is hidden"),
    ];
}

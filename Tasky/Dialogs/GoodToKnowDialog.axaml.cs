using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Tasky.Helpers;

namespace Tasky.Dialogs;

/// <summary>
/// "Good to know": a few tips on the first start, followed by the keyboard shortcuts.
/// Pressing "?" opens the same dialog with the shortcuts only.
/// </summary>
public partial class GoodToKnowDialog : Window
{
    private static IReadOnlyList<string> Tips =>
    [
        $"Add a task from any app with {KeyboardShortcuts.QuickAddAnywhere}, even while Sysslar is in the background.",
        "Switch on a task's timer to see where your time goes. It pauses by itself when you step away or lock the computer.",
        "Use ⏭ on a task to move it to tomorrow or next week, and Undo on the message that appears if you change your mind.",
        "Sysslar backs up your tasks every day. Settings → Backups puts an earlier day back.",
        "New computer? Settings → Migrate packs everything into one password-protected file, for Windows or Mac.",
    ];

    /// <summary>For the XAML loader / previewer.</summary>
    public GoodToKnowDialog() : this(showTips: true) { }

    public GoodToKnowDialog(bool showTips)
    {
        InitializeComponent();
        ThemeHelper.Attach(this);
        TitleLabel.Text       = showTips ? "👋  Good to know" : "⌨  Keyboard shortcuts";
        TipsList.IsVisible    = showTips;
        TipsList.ItemsSource  = showTips ? Tips : null;
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close(true);

    // Allow dragging the borderless window
    private void Window_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
    }
}

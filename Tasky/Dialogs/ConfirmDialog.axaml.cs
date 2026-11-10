using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Tasky.Helpers;

namespace Tasky.Dialogs;

public enum ConfirmType { Danger, Warning, Info }

public partial class ConfirmDialog : Window
{
    public bool Confirmed { get; private set; }

    /// <summary>For the XAML loader / previewer.</summary>
    public ConfirmDialog() : this(string.Empty, string.Empty) { }

    public ConfirmDialog(
        string title,
        string message,
        string confirmLabel = "Confirm",
        ConfirmType type    = ConfirmType.Danger)
    {
        InitializeComponent();
        ThemeHelper.Attach(this);

        TitleLabel.Text    = title;
        MessageLabel.Text  = message;
        ConfirmBtn.Content = confirmLabel;

        var (icon, colour, soft) = type switch
        {
            ConfirmType.Warning => ("Icon.Info",  "Warning",     "WarningSoft"),
            ConfirmType.Info    => ("Icon.Info",  "PrimaryBlue", "AccentSoft"),
            _                   => ("Icon.Trash", "Danger",      "DangerSoft"),   // Danger / Delete
        };

        IconLabel.Data = UiResources.Geometry(icon);
        IconLabel.WithResource(Controls.LineIcon.ForegroundProperty, colour);
        IconBadge.WithResource(Border.BackgroundProperty, soft);

        // Danger confirm button is red
        if (type == ConfirmType.Danger)
        {
            ConfirmBtn.WithResource(BackgroundProperty, "Danger");
            ConfirmBtn.Foreground = Brushes.White;
        }
    }

    private void Confirm_Click(object? sender, RoutedEventArgs e)
    {
        Confirmed = true;
        Close(true);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        Confirmed = false;
        Close(false);
    }

    // Allow dragging the borderless window
    private void Window_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
    }
}

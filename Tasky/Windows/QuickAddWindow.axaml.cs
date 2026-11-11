using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Tasky.ViewModels;

namespace Tasky.Windows;

public partial class QuickAddWindow : Window
{
    private readonly MainViewModel _vm;

    [Obsolete("XAML previewer only")]
    public QuickAddWindow()
    {
        _vm = null!;
        InitializeComponent();
    }

    public QuickAddWindow(MainViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        Helpers.DialogZoom.Apply(this);
        Loaded += (_, _) =>
        {
            TitleBox.Focus();
            TitleBox.SelectAll();
        };
    }

    private void TitleBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Commit();
        else if (e.Key == Key.Escape) Close();
    }

    private void Add_Click(object? sender, RoutedEventArgs e) => Commit();

    private void Commit()
    {
        var text = (TitleBox.Text ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(text))
            _vm.QuickAdd(text);
        Close();
    }

    /// <summary>Set once the window starts closing; it must not be closed again.</summary>
    private bool _closing;

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (!e.Cancel) _closing = true;
    }

    /// <summary>
    /// Clicking elsewhere closes Quick Add. macOS also deactivates a window while it closes (after
    /// Enter, Esc or Add), and closing it a second time then crashes, so only close it once.
    /// </summary>
    private void Window_Deactivated(object? sender, EventArgs e)
    {
        if (!_closing) Close();
    }
}

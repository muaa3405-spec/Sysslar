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

    private void Window_Deactivated(object? sender, EventArgs e) => Close();
}

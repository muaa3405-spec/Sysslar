using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Tasky.Helpers;
using Tasky.Services;

namespace Tasky.Dialogs;

/// <summary>
/// Create, rename, delete and open workspaces. Opening one is left to the caller
/// (it restarts Tasky): the dialog closes with <c>true</c> and sets <see cref="OpenWorkspaceId"/>.
/// </summary>
public partial class WorkspacesDialog : Window
{
    private string _selectedColor = Workspaces.Palette[1].Hex;   // green: differs from the default blue
    private string? _editingId;                                   // null = the form creates a new workspace

    /// <summary>The workspace to switch to, once the dialog closed with <c>true</c>.</summary>
    public string? OpenWorkspaceId { get; private set; }

    /// <summary>Open the migration import right after switching (a new workspace filled from a file).</summary>
    public bool OpenWithImport { get; private set; }

    public WorkspacesDialog() : this(startWithNew: false) { }

    /// <param name="startWithNew">Put the cursor in the name box, ready to make a new workspace.</param>
    public WorkspacesDialog(bool startWithNew)
    {
        InitializeComponent();
        ThemeHelper.Attach(this);
        DialogLayoutHelper.FitToWorkArea(this);
        BuildSwatches();
        RefreshList();
        if (startWithNew) Opened += (_, _) => NameBox.Focus();
    }

    // ── Form ──────────────────────────────────────────────────────────────

    private void BuildSwatches()
    {
        SwatchPanel.Children.Clear();
        foreach (var (hex, name) in Workspaces.Palette)
        {
            var swatch = new Ellipse
            {
                Width  = 24, Height = 24,
                Margin = new Thickness(0, 0, 6, 6),
                Fill   = new SolidColorBrush(Color.Parse(hex)),
                StrokeThickness = hex == _selectedColor ? 2.5 : 0,
                Cursor = new Cursor(StandardCursorType.Hand),
                Tag    = hex,
            };
            swatch.WithResource(Shape.StrokeProperty, "TextPrimary");   // the selection ring shows in both themes
            ToolTip.SetTip(swatch, name);
            swatch.PointerReleased += (_, e) =>
            {
                if (e.InitialPressMouseButton != MouseButton.Left) return;
                SelectColor(hex);
            };
            SwatchPanel.Children.Add(swatch);
        }
    }

    private void SelectColor(string hex)
    {
        _selectedColor = hex;
        foreach (var swatch in SwatchPanel.Children.OfType<Ellipse>())
            swatch.StrokeThickness = string.Equals(swatch.Tag as string, hex, StringComparison.OrdinalIgnoreCase) ? 2.5 : 0;
    }

    private void NameBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (SubmitButton is null) return;   // fires while the XAML is still loading
        FormError.IsVisible = false;
        SubmitButton.IsEnabled = !string.IsNullOrWhiteSpace(NameBox.Text);
    }

    private void NameBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && SubmitButton.IsEnabled) Submit_Click(sender, new RoutedEventArgs());
        if (e.Key == Key.Escape && _editingId is not null) { CancelEdit_Click(sender, new RoutedEventArgs()); e.Handled = true; }
    }

    private void Submit_Click(object? sender, RoutedEventArgs e)
    {
        var name = (NameBox.Text ?? string.Empty).Trim();
        if (name.Length == 0) return;
        if (Workspaces.NameIsTaken(name, _editingId))
        {
            ShowFormError($"There is already a workspace called \"{name}\".");
            return;
        }

        try
        {
            if (_editingId is { } id)
            {
                Workspaces.Update(id, name, _selectedColor);
                if (id == Workspaces.Active.Id) App.MainAppWindow?.RefreshWorkspaceHeader();
                ResetForm();
                RefreshList();
                return;
            }

            var created = Workspaces.Create(name, _selectedColor);
            OpenWorkspaceId = created.Id;
            OpenWithImport  = ImportCheck.IsChecked == true;
            Close(true);
        }
        catch (Exception ex)
        {
            CrashLog.Write("Saving a workspace failed", ex);
            ShowFormError("The workspace couldn't be saved. Details were written to the Sysslar log.");
        }
    }

    private void StartRename(Workspace workspace)
    {
        _editingId = workspace.Id;
        FormTitle.Text = "RENAME WORKSPACE";
        NameBox.Text = workspace.Name;
        SelectColor(workspace.Color);
        ImportCheck.IsVisible = false;
        CancelEditButton.IsVisible = true;
        SubmitButton.Content = "Save";
        SubmitButton.IsEnabled = true;
        FormError.IsVisible = false;
        NameBox.Focus();
        NameBox.SelectAll();
    }

    private void CancelEdit_Click(object? sender, RoutedEventArgs e) => ResetForm();

    private void ResetForm()
    {
        _editingId = null;
        FormTitle.Text = "NEW WORKSPACE";
        NameBox.Text = string.Empty;
        SelectColor(Workspaces.Palette[1].Hex);
        ImportCheck.IsVisible = true;
        ImportCheck.IsChecked = false;
        CancelEditButton.IsVisible = false;
        SubmitButton.Content = "Create and open";
        SubmitButton.IsEnabled = false;
        FormError.IsVisible = false;
    }

    private void ShowFormError(string message)
    {
        FormError.Text = message;
        FormError.IsVisible = true;
    }

    // ── List ──────────────────────────────────────────────────────────────

    private void RefreshList()
    {
        WorkspaceList.Children.Clear();
        foreach (var workspace in Workspaces.All)
            WorkspaceList.Children.Add(BuildRow(workspace));
    }

    private Control BuildRow(Workspace workspace)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto,Auto") };

        grid.Children.Add(new Ellipse
        {
            Width = 12, Height = 12, Margin = new Thickness(0, 0, 10, 0),
            Fill = new SolidColorBrush(Color.Parse(workspace.Color)),
            VerticalAlignment = VerticalAlignment.Center,
        });

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = workspace.Name, FontSize = 13, FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        }.WithResource(TextBlock.ForegroundProperty, "TextPrimary"));
        if (workspace.IsActive)
            text.Children.Add(new TextBlock
            {
                Text = "Open now", FontSize = 11, Margin = new Thickness(0, 1, 0, 0),
            }.WithResource(TextBlock.ForegroundProperty, "AccentGreen"));
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        if (!workspace.IsActive)
        {
            var open = new Button
            {
                Content = "Open", Padding = new Thickness(12, 5), Margin = new Thickness(6, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Theme = UiResources.Theme("SecondaryButton"),
            };
            ToolTip.SetTip(open, $"Switch to \"{workspace.Name}\"");
            open.Click += (_, _) =>
            {
                OpenWorkspaceId = workspace.Id;
                Close(true);
            };
            Grid.SetColumn(open, 2);
            grid.Children.Add(open);
        }

        var rename = IconButton("Icon.Edit", "edit", "Rename", () => StartRename(workspace));
        Grid.SetColumn(rename, 3);
        grid.Children.Add(rename);

        // The first workspace holds Tasky's original data, and the open one is in use: neither can go.
        if (!workspace.IsDefault && !workspace.IsActive)
        {
            var delete = IconButton("Icon.Trash", "delete", "Delete workspace", () => _ = DeleteAsync(workspace));
            Grid.SetColumn(delete, 4);
            grid.Children.Add(delete);
        }

        return new Border
            {
                CornerRadius = new CornerRadius(12), Padding = new Thickness(12, 10), Margin = new Thickness(0, 0, 0, 8),
                BorderThickness = new Thickness(workspace.IsActive ? 2 : 1),
                Child = grid,
            }
            .WithResource(Border.BackgroundProperty, "CardBackground")
            .WithResource(Border.BorderBrushProperty, workspace.IsActive ? "PrimaryBlue" : "BorderColor");
    }

    private Button IconButton(string icon, string hoverClass, string tip, Action onClick)
    {
        var button = new Button
        {
            Theme = UiResources.Theme("IconButton"),
            Classes = { hoverClass },
            Content = UiResources.Icon(icon),
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(button, tip);
        button.Click += (_, _) => onClick();
        return button;
    }

    private async Task DeleteAsync(Workspace workspace)
    {
        var confirm = new ConfirmDialog("Delete Workspace",
            $"Permanently delete \"{workspace.Name}\" and everything in it: its tasks, notes, reminders, " +
            "time history, attachments and backups?\n\nThis can't be undone. To keep a copy, open it first " +
            "and use Settings → Migrate to another computer → Export.",
            "Delete forever", ConfirmType.Danger);
        if (await confirm.ShowDialog<bool?>(this) != true) return;

        try
        {
            Workspaces.Delete(workspace.Id);
            if (_editingId == workspace.Id) ResetForm();
            RefreshList();
            App.MainAppWindow?.RefreshWorkspaceHeader();
        }
        catch (Exception ex)
        {
            CrashLog.Write("Deleting a workspace failed", ex);
            InAppNotificationService.Error("The workspace couldn't be deleted.");
        }
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}

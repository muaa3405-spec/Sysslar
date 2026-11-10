using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Tasky.Dialogs;
using Tasky.Helpers;
using Tasky.Services;
using Tasky.Services.Migration;

namespace Tasky;

/// <summary>The workspace switcher next to the logo (e.g. Personal / Office, each with its own data).</summary>
public partial class MainWindow
{
    private Flyout? _workspaceMenu;

    /// <summary>Shows the open workspace's name and colour in the header and the window title.</summary>
    public void RefreshWorkspaceHeader()
    {
        var active = Workspaces.Active;
        WorkspaceNameText.Text = active.Name;
        WorkspaceDot.Fill = new SolidColorBrush(Color.Parse(active.Color));
        ToolTip.SetTip(WorkspaceButton, $"Workspace: {active.Name}. Click to switch");
        Title = Workspaces.HasSeveral ? $"Sysslar – {active.Name}" : "Sysslar";
    }

    private void WorkspaceButton_Click(object? sender, RoutedEventArgs e)
    {
        _workspaceMenu = new Flyout
        {
            Placement = PlacementMode.BottomEdgeAlignedLeft,
            FlyoutPresenterClasses = { "workspaceMenu" },
            Content = BuildWorkspaceMenu(),
        };
        _workspaceMenu.ShowAt(WorkspaceButton);
    }

    private Control BuildWorkspaceMenu()
    {
        var items = new StackPanel { MinWidth = 220 };
        items.Children.Add(new TextBlock
        {
            Text = "WORKSPACES", Theme = UiResources.Theme("LabelText"), Margin = new Thickness(10, 4, 10, 4),
        });

        foreach (var workspace in Workspaces.All)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
            row.Children.Add(new Ellipse
            {
                Width = 10, Height = 10, Margin = new Thickness(0, 0, 9, 0), VerticalAlignment = VerticalAlignment.Center,
                Fill = new SolidColorBrush(Color.Parse(workspace.Color)),
            });
            var name = new TextBlock
            {
                Text = workspace.Name, FontSize = 13, VerticalAlignment = VerticalAlignment.Center,
                FontWeight = workspace.IsActive ? FontWeight.SemiBold : FontWeight.Normal,
                TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 220,
            }.WithResource(TextBlock.ForegroundProperty, "TextPrimary");
            Grid.SetColumn(name, 1);
            row.Children.Add(name);
            if (workspace.IsActive)
            {
                var check = UiResources.Icon("Icon.Check", 16).WithResource(Controls.LineIcon.ForegroundProperty, "PrimaryBlue");
                check.Margin = new Thickness(10, 0, 0, 0);
                check.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(check, 2);
                row.Children.Add(check);
            }

            var id = workspace.Id;
            items.Children.Add(MenuItemButton(row, () =>
            {
                if (id != Workspaces.Active.Id) _ = SwitchWorkspaceAsync(id);
            }));
        }

        items.Children.Add(new Border { Height = 1, Margin = new Thickness(4, 5) }
            .WithResource(Border.BackgroundProperty, "BorderColor"));
        items.Children.Add(MenuItemButton(MenuText("New workspace…"), () => _ = ShowWorkspacesDialogAsync(startWithNew: true)));
        items.Children.Add(MenuItemButton(MenuText("Manage workspaces…"), () => _ = ShowWorkspacesDialogAsync(startWithNew: false)));

        return new Border
            {
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(6),
                Child = items,
                BoxShadow = new BoxShadows(new BoxShadow { Blur = 18, OffsetY = 4, Color = Color.FromArgb(70, 0, 0, 0) }),
            }
            .WithResource(Border.BackgroundProperty, "CardBackground")
            .WithResource(Border.BorderBrushProperty, "BorderColor");
    }

    private static TextBlock MenuText(string text)
        => new TextBlock { Text = text, FontSize = 13 }.WithResource(TextBlock.ForegroundProperty, "TextPrimary");

    private Button MenuItemButton(Control content, Action onClick)
    {
        var button = new Button { Content = content, Theme = (Avalonia.Styling.ControlTheme)Resources["WorkspaceMenuItem"]! };
        button.Click += (_, _) =>
        {
            _workspaceMenu?.Hide();
            onClick();
        };
        return button;
    }

    private async Task ShowWorkspacesDialogAsync(bool startWithNew)
    {
        var dialog = new WorkspacesDialog(startWithNew);
        bool chosen = await ShowModalDialog(dialog) == true;
        RefreshWorkspaceHeader();   // a rename or delete may have changed the name or the title
        if (chosen && dialog.OpenWorkspaceId is { } id)
            await SwitchWorkspaceAsync(id, dialog.OpenWithImport, confirm: false);
    }

    /// <summary>Saves the choice and restarts Tasky in the other workspace.</summary>
    private async Task SwitchWorkspaceAsync(string id, bool openImport = false, bool confirm = true)
    {
        if (Workspaces.Find(id) is not { } target) return;

        if (confirm && Vm.Tasks.Any(t => t.IsRunning))
        {
            var ask = new ConfirmDialog("Switch Workspace",
                $"Your running timer will be paused and saved, then Sysslar restarts in \"{target.Name}\".",
                "Pause and switch", ConfirmType.Info);
            if (await ShowModalDialog(ask) != true) return;
        }

        try
        {
            Workspaces.OpenOnNextStart(id);   // timers are paused and saved as Tasky exits
            MigrationService.RestartTasky(openImport ? [Workspaces.OpenImportArg] : []);
        }
        catch (Exception ex)
        {
            CrashLog.Write("Switching workspace failed", ex);
            try { Workspaces.KeepActiveOnNextStart(); } catch { /* the list file is unwritable; nothing to undo */ }
            InAppNotificationService.Error("Sysslar couldn't switch workspace. Details were written to the Sysslar log.");
        }
    }

    /// <summary>After "New workspace" with "Import a migration file": go straight to the import.</summary>
    public async void OpenImportForNewWorkspace()
    {
        if (App.Database is not { } db) return;
        await ShowModalDialog(new MigrateDialog(new MigrationService(db), startWithImport: true));
    }
}

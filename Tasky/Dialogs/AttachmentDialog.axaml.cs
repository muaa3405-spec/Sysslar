using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Tasky.Helpers;
using Tasky.Models;
using Tasky.Platform;
using Tasky.Services;
using Tasky.ViewModels;

namespace Tasky.Dialogs;

public partial class AttachmentDialog : Window
{
    private const int ThumbnailDecodeWidth = 200;

    // Drop-zone colours: idle, and while files are dragged over the window.
    private static IBrush IdleBorderBrush   => UiResources.Brush("BorderColor");
    private static IBrush IdleBackground    => UiResources.Brush("CardBackground");
    private static IBrush ActiveBorderBrush => UiResources.Brush("PrimaryBlue");
    private static IBrush ActiveBackground  => UiResources.Brush("AccentSoft");
    private static IBrush DeleteForeground  => UiResources.Brush("Danger");

    private const string IdleIcon   = "Icon.Clipboard";
    private const string ActiveIcon = "Icon.Download";
    private const string ActiveTitle = "Release to attach";
    private static string IdleTitle => $"Click, paste ({KeyGestures.PasteHint}) or drop images here";

    private readonly int _taskId;
    private readonly MainViewModel _vm;

    [Obsolete("XAML previewer only")]
    public AttachmentDialog()
    {
        _vm = null!;
        InitializeComponent();
    }

    public AttachmentDialog(int taskId, string taskTitle, MainViewModel vm)
    {
        _taskId = taskId;
        _vm = vm;
        InitializeComponent();
        ThemeHelper.Attach(this);
        HeaderLabel.Text = $"Attachments — {taskTitle}";
        DropTitle.Text   = IdleTitle;

        AddHandler(KeyDownEvent, Window_PreviewKeyDown, RoutingStrategies.Tunnel);
        AddHandler(DragDrop.DragOverEvent,  Window_DragOver);
        AddHandler(DragDrop.DragLeaveEvent, Window_DragLeave);
        AddHandler(DragDrop.DropEvent,      Window_Drop);

        LoadAttachments();
    }

    // ─── Load saved attachments ───────────────────────────────────────────

    private void LoadAttachments()
    {
        AttachPanel.Children.Clear();
        var list = _vm.GetAttachments(_taskId);
        SavedLabel.IsVisible    = list.Count > 0;
        NoAttachLabel.IsVisible = list.Count == 0;
        foreach (var att in list)
            AttachPanel.Children.Add(BuildCard(att));
    }

    // ─── Attachment card ──────────────────────────────────────────────────

    private Control BuildCard(TaskAttachment att)
    {
        var border = new Border
        {
            Width = 110, Height = 125, Margin = new Thickness(0, 0, 8, 8),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        border.WithResource(Border.BorderBrushProperty, "BorderColor");
        border.WithResource(Border.BackgroundProperty,  "CardBackground");

        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };

        if (ImageFiles.IsImage(att.FilePath) && File.Exists(att.FilePath))
        {
            stack.Children.Add(new Image
            {
                Width = 94, Height = 76,
                Stretch = Stretch.UniformToFill,
                Margin = new Thickness(0, 6, 0, 4),
                Source = ImageFiles.Load(att.FilePath, ThumbnailDecodeWidth)
            });
        }
        else
        {
            stack.Children.Add(new Tasky.Controls.LineIcon
            {
                Data = UiResources.Geometry("Icon.File"), Size = 36, StrokeThickness = 1.5,
                Foreground = UiResources.Brush("TextSecondary"),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 16, 0, 4)
            });
        }

        stack.Children.Add(new TextBlock
        {
            Text = att.FileName, FontSize = 10, TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(4, 0, 4, 4)
        });

        // Delete × button
        var del = new Button
        {
            Theme = UiResources.Theme("PlainButton"),
            Content = UiResources.Icon("Icon.Close", 12), FontSize = 10, Width = 20, Height = 20,
            VerticalAlignment   = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 2, 2, 0),
            Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Cursor = new Cursor(StandardCursorType.Hand), Foreground = DeleteForeground
        };
        del.Click += (_, _) => { _vm.DeleteAttachment(att); LoadAttachments(); };

        var grid = new Grid();
        grid.Children.Add(stack);
        grid.Children.Add(del);
        border.Child = grid;

        border.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton == MouseButton.Left) OpenInViewer(att);
        };

        return border;
    }

    private async void OpenInViewer(TaskAttachment clicked)
    {
        if (!File.Exists(clicked.FilePath)) return;

        if (ImageFiles.IsImage(clicked.FilePath))
        {
            // Images → native Tasky viewer (navigable, with zoom)
            // Pass only image attachments so prev/next skips non-image files
            var images = _vm.GetAttachments(_taskId)
                            .Where(a => ImageFiles.IsImage(a.FilePath))
                            .ToList();
            int index = images.FindIndex(a => a.Id == clicked.Id);
            await new Tasky.Windows.ImageViewerWindow(images, index >= 0 ? index : 0).ShowDialog(this);
        }
        else
        {
            // PDFs, Word docs, etc. → system default application
            FileLauncher.Open(clicked.FilePath);
        }
    }

    // ─── Drop zone click → open file picker ──────────────────────────────

    private void DropZone_Click(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton == MouseButton.Left) OpenFilePicker();
    }

    private void AddFile_Click(object? sender, RoutedEventArgs e) => OpenFilePicker();

    private async void OpenFilePicker()
    {
        var files = await FileDialogs.PickFilesAsync(this, "Select image(s) to attach",
                                                     [FileDialogs.Images, FileDialogs.AllFiles]);
        if (files.Count == 0) return;
        foreach (var f in files) _vm.SaveAttachment(_taskId, f);
        LoadAttachments();
        InAppNotificationService.Success($"{files.Count} file(s) attached.");
    }

    // ─── Paste (Ctrl+V / ⌘V) ──────────────────────────────────────────────

    private void Window_PreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (KeyGestures.IsPaste(e))
            e.Handled = TryPasteFromClipboard();
    }

    private bool TryPasteFromClipboard()
    {
        var clipboard = PlatformServices.Clipboard;
        if (clipboard.ContainsImage())
        {
            var png = clipboard.GetImagePng();
            if (png is null) return false;
            SavePastedImage(png);
            return true;
        }
        if (clipboard.GetFiles() is { Count: > 0 } files)
        {
            int count = 0;
            foreach (var f in files)
                if (ImageFiles.IsImage(f) && _vm.SaveAttachment(_taskId, f)) count++;
            if (count > 0) { LoadAttachments(); InAppNotificationService.Success($"{count} image(s) pasted."); return true; }
        }
        InAppNotificationService.Info("No image in clipboard — copy a screenshot or image first.");
        return false;
    }

    // ─── Drag and drop ────────────────────────────────────────────────────

    private void Window_DragOver(object? sender, DragEventArgs e)
    {
        bool ok = DroppedFiles.Any(e);
        e.DragEffects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        // Highlight drop zone
        ShowDropZone(ok);
        e.Handled = true;
    }

    private void Window_DragLeave(object? sender, DragEventArgs e) => ShowDropZone(active: false);

    private void Window_Drop(object? sender, DragEventArgs e)
    {
        ShowDropZone(active: false);
        var files = DroppedFiles.From(e).ToList();
        if (files is null) return;
        int count = 0;
        foreach (var f in files.Where(ImageFiles.IsImage)) { if (_vm.SaveAttachment(_taskId, f)) count++; }
        if (count > 0) { LoadAttachments(); InAppNotificationService.Success($"{count} image(s) attached."); }
        else InAppNotificationService.Warning("No image files found — drop PNG, JPG, GIF, or WEBP files.");
    }

    private void ShowDropZone(bool active)
    {
        DropZone.BorderBrush = active ? ActiveBorderBrush : IdleBorderBrush;
        DropZone.Background  = active ? ActiveBackground  : IdleBackground;
        DropIcon.Data        = UiResources.Geometry(active ? ActiveIcon : IdleIcon);
        DropTitle.Text       = active ? ActiveTitle       : IdleTitle;
    }

    // ─── Helpers ──────────────────────────────────────────────────────────

    private void SavePastedImage(byte[] png)
    {
        _vm.SaveAttachmentFromBytes(_taskId, png, ".png");
        LoadAttachments();
        InAppNotificationService.Success("Image pasted and saved.");
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}

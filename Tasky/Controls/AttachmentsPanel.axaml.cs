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

namespace Tasky.Controls;

/// <summary>
/// Shows attachments as thumbnails and lets the user open, remove and add them
/// (file picker, paste via <see cref="TryPaste"/>, drag and drop).
/// The panel never touches storage itself: the host handles the events and then
/// calls <see cref="SetAttachments"/> with the refreshed list.
/// </summary>
public partial class AttachmentsPanel : UserControl
{
    private const int CardWidth = 96, CardHeight = 110, ThumbnailDecodeWidth = 160;

    private static readonly IBrush RemoveButtonBackground = new SolidColorBrush(Color.FromArgb(210, 17, 24, 39));
    private static readonly IBrush RemoveButtonForeground = new SolidColorBrush(Color.FromRgb(239, 68, 68));

    private IReadOnlyList<IAttachment> _items = Array.Empty<IAttachment>();

    /// <summary>The user picked or dropped files to attach.</summary>
    public event Action<IReadOnlyList<string>>? FilesAdded;

    /// <summary>The user pasted an image (e.g. a screenshot) to attach, as PNG bytes.</summary>
    public event Action<byte[]>? ImagePasted;

    /// <summary>The user removed an attachment.</summary>
    public event Action<IAttachment>? DeleteRequested;

    public AttachmentsPanel()
    {
        InitializeComponent();
        PasteHintLabel.Text = $"or paste ({KeyGestures.PasteHint}) / drop files here";
        AddHandler(DragDrop.DragOverEvent, Panel_DragOver);
        AddHandler(DragDrop.DropEvent, Panel_Drop);
    }

    /// <summary>
    /// Lets the whole <paramref name="window"/> take attachments: files dropped anywhere on it, and
    /// Ctrl+V / ⌘V for a copied image or files. While typing in a text box, copied text still pastes as text.
    /// </summary>
    public void AcceptAttachmentsAnywhereIn(Control window)
    {
        DragDrop.SetAllowDrop(window, true);
        window.AddHandler(DragDrop.DragOverEvent, Panel_DragOver);
        window.AddHandler(DragDrop.DropEvent, Panel_Drop);
        ClipboardAttachments.HandlePasteIn(window, TryPaste);
    }

    public void SetAttachments(IReadOnlyList<IAttachment> items)
    {
        _items = items;
        Thumbs.Children.Clear();
        foreach (var item in items) Thumbs.Children.Add(BuildCard(item));
        EmptyLabel.IsVisible = items.Count == 0;
    }

    /// <summary>
    /// Attaches whatever image or files are on the clipboard. Returns false when the
    /// clipboard holds neither, so the host can let a normal text paste go ahead.
    /// </summary>
    public bool TryPaste()
        => ClipboardAttachments.TryTake(png => ImagePasted?.Invoke(png), files => FilesAdded?.Invoke(files));

    // ── Adding ────────────────────────────────────────────────────────────

    private async void AddFiles_Click(object? sender, RoutedEventArgs e)
    {
        var files = await FileDialogs.PickFilesAsync(TopLevel.GetTopLevel(this), "Choose files to attach",
                                                     [FileDialogs.AllFiles, FileDialogs.Images]);
        if (files.Count > 0) FilesAdded?.Invoke(files);
    }

    private void Panel_DragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = DroppedFiles.Any(e) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Panel_Drop(object? sender, DragEventArgs e)
    {
        e.Handled = true;   // a drop on the panel must not reach the window's handler as well
        var files = DroppedFiles.From(e).Where(File.Exists).ToList();
        if (files.Count > 0) FilesAdded?.Invoke(files);
    }


    // ── Thumbnails ────────────────────────────────────────────────────────

    private Control BuildCard(IAttachment item)
    {
        var card = new Border
        {
            Width = CardWidth, Height = CardHeight, Margin = new Thickness(0, 0, 8, 8),
            CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1),
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        ToolTip.SetTip(card, item.FileName);
        card.WithResource(Border.BorderBrushProperty, "BorderColor");
        card.WithResource(Border.BackgroundProperty,  "CardBackground");

        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var thumbnail = ImageFiles.IsImage(item.FilePath) ? ImageFiles.Load(item.FilePath, ThumbnailDecodeWidth) : null;
        if (thumbnail is not null)
        {
            stack.Children.Add(new Image
            {
                Source = thumbnail, Width = 80, Height = 64, Stretch = Stretch.UniformToFill,
                Margin = new Thickness(0, 6, 0, 4)
            });
        }
        else
        {
            stack.Children.Add(new Tasky.Controls.LineIcon
            {
                Data = UiResources.Geometry("Icon.File"), Size = 34, StrokeThickness = 1.5,
                Foreground = UiResources.Brush("TextSecondary"), HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 12, 0, 4)
            });
        }
        var name = new TextBlock
        {
            Text = item.FileName, FontSize = 10, TextTrimming = TextTrimming.CharacterEllipsis,
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(4, 0, 4, 4)
        };
        name.WithResource(TextBlock.ForegroundProperty, "TextSecondary");
        stack.Children.Add(name);

        var remove = new Button
        {
            Theme = UiResources.Theme("PlainButton"),
            Content = UiResources.Icon("Icon.Close", 12), Width = 18, Height = 18, FontSize = 10,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 2, 0), Cursor = new Cursor(StandardCursorType.Hand),
            BorderThickness = new Thickness(0),
            Background = RemoveButtonBackground,
            Foreground = RemoveButtonForeground
        };
        ToolTip.SetTip(remove, "Remove");
        remove.Click += (_, e) => { e.Handled = true; DeleteRequested?.Invoke(item); };

        var grid = new Grid();
        grid.Children.Add(stack);
        grid.Children.Add(remove);
        card.Child = grid;
        card.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton == MouseButton.Left) Open(item);
        };
        return card;
    }

    private async void Open(IAttachment item)
    {
        if (!ImageFiles.IsImage(item.FilePath))
        {
            FileLauncher.Open(item.FilePath);
            return;
        }
        var images = _items.Where(a => ImageFiles.IsImage(a.FilePath)).ToList();
        int index = Math.Max(0, images.FindIndex(a => a.Id == item.Id));
        var viewer = new Tasky.Windows.ImageViewerWindow(images, index);
        if (TopLevel.GetTopLevel(this) is Window owner) await viewer.ShowDialog(owner);
        else viewer.Show();
    }
}

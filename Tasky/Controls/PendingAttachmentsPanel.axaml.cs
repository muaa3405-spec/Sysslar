using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Tasky.Helpers;

namespace Tasky.Controls;

/// <summary>
/// Collects attachments for a task or note that does not exist yet (file picker, paste, drag and drop)
/// and shows them as thumbnails. Once the item is saved, the host calls <see cref="SaveAll"/>.
/// </summary>
public partial class PendingAttachmentsPanel : UserControl
{
    private const int ThumbSize = 72;
    private const string PastedImageExtension = ".png";

    private static readonly IBrush RemoveButtonBackground = new SolidColorBrush(Color.FromArgb(210, 17, 24, 39));
    private static readonly IBrush RemoveButtonForeground = new SolidColorBrush(Color.FromRgb(239, 68, 68));

    private readonly List<byte[]> _pastedImages = new();
    private readonly List<string> _files = new();

    public int Count => _pastedImages.Count + _files.Count;

    public PendingAttachmentsPanel()
    {
        InitializeComponent();
        PasteHintLabel.Text = $"or paste ({KeyGestures.PasteHint}) / drop files";
        AcceptAttachmentsAnywhereIn(this);
    }

    /// <summary>Lets files dropped, or pasted with Ctrl+V / ⌘V, anywhere inside <paramref name="area"/> be attached.</summary>
    public void AcceptAttachmentsAnywhereIn(Control area)
    {
        DragDrop.SetAllowDrop(area, true);
        area.AddHandler(DragDrop.DragOverEvent, Area_DragOver);
        area.AddHandler(DragDrop.DropEvent, Area_Drop);
        if (area != this) ClipboardAttachments.HandlePasteIn(area, TryPaste);
    }

    public bool TryPaste() => ClipboardAttachments.TryTake(AddPastedImage, AddFiles);

    /// <summary>
    /// Saves every pending attachment with the given callbacks (each returns whether it worked),
    /// empties the panel, and returns how many were saved.
    /// </summary>
    public int SaveAll(Func<byte[], string, bool> saveImage, Func<string, bool> saveFile)
    {
        int saved = _pastedImages.Count(png => saveImage(png, PastedImageExtension))
                  + _files.Count(saveFile);
        Clear();
        return saved;
    }

    public void Clear()
    {
        _pastedImages.Clear();
        _files.Clear();
        RebuildThumbs();
    }

    // ── Adding ────────────────────────────────────────────────────────────

    private async void AttachFiles_Click(object? sender, RoutedEventArgs e)
    {
        var files = await FileDialogs.PickFilesAsync(TopLevel.GetTopLevel(this), "Choose files to attach",
                                                     [FileDialogs.AllFiles, FileDialogs.Images]);
        AddFiles(files);
    }

    private void Area_DragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = DroppedFiles.Any(e) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Area_Drop(object? sender, DragEventArgs e)
    {
        if (e.Handled) return;   // the panel and its form both listen; take each drop once
        e.Handled = true;
        AddFiles(DroppedFiles.From(e).Where(File.Exists).ToList());
    }

    private void AddPastedImage(byte[] png)
    {
        _pastedImages.Add(png);
        RebuildThumbs();
    }

    private void AddFiles(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;
        _files.AddRange(paths);
        RebuildThumbs();
    }

    // ── Thumbnails ────────────────────────────────────────────────────────

    private void RebuildThumbs()
    {
        Thumbs.Children.Clear();
        foreach (var png in _pastedImages.ToList())
            Thumbs.Children.Add(BuildThumb(ImageFiles.FromBytes(png), () => _pastedImages.Remove(png)));
        foreach (var path in _files.ToList())
            Thumbs.Children.Add(BuildThumb(ImageFiles.IsImage(path) ? ImageFiles.Load(path) : null,
                                           () => _files.Remove(path), Path.GetFileName(path)));
        Thumbs.IsVisible = Count > 0;
    }

    private Control BuildThumb(Bitmap? image, Func<bool> remove, string? fileName = null)
    {
        var card = new Border
        {
            Width = ThumbSize, Height = ThumbSize, Margin = new Thickness(0, 0, 8, 8),
            CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1)
        }
        .WithResource(Border.BorderBrushProperty, "BorderColor")
        .WithResource(Border.BackgroundProperty,  "CardBackground");
        if (fileName is not null) ToolTip.SetTip(card, fileName);

        var grid = new Grid();
        grid.Children.Add(image is not null
            ? new Image { Source = image, Stretch = Stretch.UniformToFill }
            : new Tasky.Controls.LineIcon
            {
                Data = UiResources.Geometry("Icon.File"), Size = 32, StrokeThickness = 1.5,
                Foreground = UiResources.Brush("TextSecondary"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center
            });

        var removeButton = new Button
        {
            Theme   = UiResources.Theme("PlainButton"),
            Content = UiResources.Icon("Icon.Close", 12), Width = 18, Height = 18, FontSize = 10,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment   = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 2, 0), Cursor = new Cursor(StandardCursorType.Hand),
            BorderThickness = new Thickness(0),
            Background = RemoveButtonBackground,
            Foreground = RemoveButtonForeground
        };
        ToolTip.SetTip(removeButton, "Remove");
        removeButton.Click += (_, _) => { remove(); RebuildThumbs(); };

        grid.Children.Add(removeButton);
        card.Child = grid;
        return card;
    }
}

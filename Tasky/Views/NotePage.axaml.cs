using Avalonia.Controls;
using Avalonia.Interactivity;
using Tasky.Dialogs;
using Tasky.Models;
using Tasky.Platform;
using Tasky.Services;
using Tasky.ViewModels;

namespace Tasky.Views;

/// <summary>
/// Read and edit a note on a page inside the main window (it replaced the note dialog).
/// Title, description and tags are saved with Save; attachments are saved right away.
/// Leaving with unsaved changes asks first.
/// </summary>
public partial class NotePage : UserControl
{
    private readonly Note _note = null!;
    private readonly MainViewModel _vm = null!;
    private readonly Func<Window, Task<bool?>> _showModal = null!;

    /// <summary>The page is done: go back to the list of notes.</summary>
    public event Action? Closed;

    [Obsolete("XAML previewer only")]
    public NotePage() => InitializeComponent();

    /// <param name="showModal">Shows a dialog over the main window (dimmed behind it).</param>
    public NotePage(Note note, MainViewModel vm, Func<Window, Task<bool?>> showModal)
    {
        _note      = note;
        _vm        = vm;
        _showModal = showModal;
        InitializeComponent();

        TitleBox.Text = _note.Title;
        DescBox.Text  = _note.Description;
        TagsBox.Text  = _note.Tags;
        HeaderTitle.Text = string.IsNullOrWhiteSpace(_note.Title) ? "Note" : _note.Title;
        SaveBtn.IsEnabled = false;

        Attachments.FilesAdded      += AttachFiles;
        Attachments.ImagePasted     += AttachPastedImage;
        Attachments.DeleteRequested += RemoveAttachment;
        Attachments.AcceptAttachmentsAnywhereIn(this);
        ReloadAttachments();
    }

    /// <summary>A text box's text; Avalonia may report an untouched box as null.</summary>
    private static string TextOf(TextBox box) => box.Text ?? string.Empty;

    private bool HasChanges => TitleBox is not null
        && (TextOf(TitleBox) != _note.Title || TextOf(DescBox) != _note.Description || TextOf(TagsBox) != _note.Tags);

    // ─── Attachments (saved immediately) ──────────────────────────────────

    private void ReloadAttachments()
    {
        Attachments.SetAttachments(_vm.GetNoteAttachments(_note.Id));
        _vm.RefreshNoteAttachmentCount(_note);
    }

    private void AttachFiles(IReadOnlyList<string> paths)
    {
        int saved = paths.Count(path => _vm.SaveNoteAttachment(_note.Id, path));
        ReloadAttachments();
        if (saved > 0) InAppNotificationService.Success(saved == 1 ? "File attached." : $"{saved} files attached.");
    }

    private void AttachPastedImage(byte[] png)
    {
        if (_vm.SaveNoteAttachmentFromBytes(_note.Id, png, ".png"))
            InAppNotificationService.Success("Image pasted and attached.");
        ReloadAttachments();
    }

    private async void RemoveAttachment(IAttachment attachment)
    {
        if (attachment is not NoteAttachment noteAttachment) return;
        var confirm = new ConfirmDialog("Remove Attachment",
            $"Remove \"{attachment.FileName}\" from this note?", "Remove", ConfirmType.Warning);
        if (await _showModal(confirm) != true) return;
        _vm.DeleteNoteAttachment(noteAttachment);
        ReloadAttachments();
    }

    // ─── Editing ──────────────────────────────────────────────────────────

    private void Field_Changed(object? sender, TextChangedEventArgs e)
    {
        // Fires during InitializeComponent too, before every box exists.
        if (TitleBox is null || DescBox is null || TagsBox is null || SaveBtn is null) return;
        SaveBtn.IsEnabled = HasChanges;
    }

    /// <summary>Saves the note and goes back to the list; false when the title is empty.</summary>
    public bool Save()
    {
        if (string.IsNullOrWhiteSpace(TitleBox.Text))
        {
            InAppNotificationService.Warning("Title cannot be empty.");
            TitleBox.Focus();
            return false;
        }
        if (HasChanges)
        {
            _note.Title       = TextOf(TitleBox).Trim();
            _note.Description = TextOf(DescBox).Trim();
            _note.Tags        = TextOf(TagsBox).Trim();
            _vm.UpdateNote(_note);
            InAppNotificationService.Success("Note saved.");
        }
        Closed?.Invoke();
        return true;
    }

    /// <summary>Back / Esc: leaves straight away, or asks first when there are unsaved changes.</summary>
    public async void TryLeave()
    {
        if (HasChanges)
        {
            var confirm = new ConfirmDialog("Unsaved Changes",
                "You have unsaved changes. Leave without saving?", "Discard", ConfirmType.Warning);
            if (await _showModal(confirm) != true) return;
        }
        Closed?.Invoke();
    }

    private void Save_Click(object? sender, RoutedEventArgs e) => Save();

    private void Back_Click(object? sender, RoutedEventArgs e) => TryLeave();

    private void CopyDesc_Click(object? sender, RoutedEventArgs e)
    {
        var text = DescBox.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            InAppNotificationService.Warning("Description is empty — nothing to copy.");
            return;
        }
        if (!PlatformServices.Clipboard.SetText(text))
        {
            InAppNotificationService.Warning("Clipboard is busy — try again in a moment.");
            return;
        }
        InAppNotificationService.Info("Description copied to clipboard.");
    }
}

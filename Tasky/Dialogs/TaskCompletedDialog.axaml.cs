using System.IO;
using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Tasky.Helpers;
using Tasky.Platform;
using Tasky.Services;
using Tasky.ViewModels;

namespace Tasky.Dialogs;

public partial class TaskCompletedDialog : Window
{
    private static readonly TimeSpan FadeInDuration = TimeSpan.FromMilliseconds(300);

    /// <summary>The banner image is rendered at 3× for a crisp screenshot.</summary>
    private const double BannerScale = 3.0;
    private const double ScreenDpi   = 96;

    private readonly TaskItemViewModel _vm = null!;

    [Obsolete("XAML previewer only")]
    public TaskCompletedDialog() => InitializeComponent();

    public TaskCompletedDialog(TaskItemViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        Helpers.DialogZoom.Apply(this);
        DialogLayoutHelper.FitToWorkArea(this);
        Loaded += OnLoaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        PopulateDetails();
        _ = Animator.Fade(CardRoot, 0, 1, FadeInDuration, new CubicEaseOut());
        ConfettiLayer.UpdateLayout();
        var center = new Point(ConfettiLayer.Bounds.Width / 2, ConfettiLayer.Bounds.Height / 2);
        ConfettiLayer.Burst(center);
    }

    private void PopulateDetails()
    {
        var completedAt = _vm.CompletedAt ?? DateTime.Now;
        var dateStr     = completedAt.ToString("MMM d, yyyy  HH:mm");

        TaskNumberLabel.Text = _vm.TaskNumberDisplay;
        TitleLabel.Text      = _vm.Title;
        SubtitleLabel.Text   = "Great work! Keep up the momentum.";
        DateLabel.Text       = dateStr;

        if (!string.IsNullOrWhiteSpace(_vm.Description))
        {
            DescLabel.Text      = _vm.Description;
            DescLabel.IsVisible = true;
        }
        if (!string.IsNullOrEmpty(_vm.GroupBadge))
        {
            GroupLabel.Text       = _vm.GroupBadge;
            GroupBadge.Background = _vm.GroupBrush;
            GroupBadge.IsVisible  = true;
        }

        // Banner fields
        BannerTaskNum.Text = _vm.TaskNumberDisplay;
        BannerTitle.Text   = _vm.Title;
        BannerDate.Text    = $"Completed on  {dateStr}";
    }

    // ─── Screenshot banner ────────────────────────────────────────────────

    private void Screenshot_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var png = RenderBannerPng();
            PlatformServices.Clipboard.SetImagePng(png);

            string message;
            if (App.Settings.Current.SaveScreenshotToDesktop)
            {
                var desktop  = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                var filename = $"Sysslar_{_vm.TaskNumberDisplay}_{DateTime.Now:yyyyMMdd_HHmmss}.png";
                File.WriteAllBytes(Path.Combine(desktop, filename), png);
                message = "Banner saved to Desktop & copied to clipboard.";
            }
            else
            {
                message = "Banner copied to clipboard.";
            }

            Close();
            InAppNotificationService.Success(message);
        }
        catch (Exception ex)
        {
            ActionButtons.IsVisible = true;
            BannerCapture.IsVisible = false;
            InAppNotificationService.Error($"Screenshot failed: {ex.Message}");
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }

    /// <summary>
    /// Lays the banner out at its own natural size, decoupled from the dialog's width,
    /// and renders exactly that area, so the capture is a tight, small banner with no
    /// empty background around it.
    /// </summary>
    private byte[] RenderBannerPng()
    {
        BannerCapture.IsVisible = true;
        BannerCapture.Measure(Size.Infinity);

        // Round up to whole output pixels so the banner's own background covers every
        // pixel and no transparent edge remains.
        var desired = BannerCapture.DesiredSize;
        int w = (int)Math.Ceiling(desired.Width  * BannerScale);
        int h = (int)Math.Ceiling(desired.Height * BannerScale);
        BannerCapture.Arrange(new Rect(0, 0, w / BannerScale, h / BannerScale));

        using var rtb = new RenderTargetBitmap(new PixelSize(w, h),
            new Vector(ScreenDpi * BannerScale, ScreenDpi * BannerScale));
        rtb.Render(BannerCapture);

        using var stream = new MemoryStream();
        rtb.Save(stream);
        return stream.ToArray();
    }

    // ─── Window behaviour ─────────────────────────────────────────────────

    private void Window_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
        base.OnKeyDown(e);
    }
}

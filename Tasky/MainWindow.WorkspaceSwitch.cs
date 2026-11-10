using System.Globalization;
using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Tasky.Helpers;
using Tasky.Services;

namespace Tasky;

/// <summary>
/// A soft workspace switch. Switching restarts Sysslar (each workspace has its own database, timers,
/// reminders and backups), so the old window fades to a "Switching to …" screen with a spinner, and the
/// new one opens in the same place and size showing the same screen, then fades it away once loaded.
/// </summary>
public partial class MainWindow
{
    /// <summary>Restart argument: this start is a workspace switch (show the arrival screen).</summary>
    public const string SwitchingArg = "--switching";

    /// <summary>Restart argument: "--window=left,top,width,height" of the window being replaced.</summary>
    private const string WindowArgPrefix = "--window=";

    /// <summary>The screen is shown at least this long, so the switch reads as one smooth step.</summary>
    private static readonly TimeSpan MinimumSwitchScreen = TimeSpan.FromMilliseconds(550);

    /// <summary>Where the replaced window was (device-independent units), used instead of the saved placement.</summary>
    private Rect? _arrivalBounds;

    /// <summary>Old window: fade to the switch screen, then return the restart arguments.</summary>
    private async Task<string[]> ShowSwitchScreenAsync(Workspace target)
    {
        ShowSwitchScreen($"Switching to {target.Name}", target.Color);
        await Task.Delay(MinimumSwitchScreen);

        var args = new List<string> { SwitchingArg };
        if (WindowState == WindowState.Normal)
        {
            double scaling = DesktopScaling > 0 ? DesktopScaling : 1;
            args.Add(WindowArgPrefix + string.Join(",", new[] { Position.X / scaling, Position.Y / scaling, Width, Height }
                .Select(v => v.ToString("0.##", CultureInfo.InvariantCulture))));
        }
        return [.. args];
    }

    /// <summary>New window (before Show): start on the switch screen, in the old window's place.</summary>
    public void BeginArrival(string[] args)
    {
        if (!args.Contains(SwitchingArg)) return;

        var workspace = Workspaces.Active;
        ShowSwitchScreen($"Opening {workspace.Name}", workspace.Color, animate: false);

        if (args.FirstOrDefault(a => a.StartsWith(WindowArgPrefix, StringComparison.Ordinal)) is { } windowArg)
        {
            var parts = windowArg[WindowArgPrefix.Length..].Split(',');
            if (parts.Length == 4 && parts.All(p => double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
            {
                var v = parts.Select(p => double.Parse(p, CultureInfo.InvariantCulture)).ToArray();
                _arrivalBounds = new Rect(v[0], v[1], v[2], v[3]);
                Width  = Math.Max(v[2], MinWidth);
                Height = Math.Max(v[3], MinHeight);
                WindowStartupLocation = WindowStartupLocation.Manual;
            }
        }

        // Once the tasks are on screen, let the switch screen fade away
        void OnOpened(object? sender, EventArgs e)
        {
            Opened -= OnOpened;
            DispatcherTimer.RunOnce(async () =>
            {
                await Animator.Fade(SwitchOverlay, 1, 0, TimeSpan.FromMilliseconds(320), new CubicEaseInOut());
                SwitchOverlay.IsVisible = false;
                SwitchOverlay.Opacity   = 1;
            }, TimeSpan.FromMilliseconds(450));
        }
        Opened += OnOpened;
    }

    /// <summary>
    /// Switches workspace inside the running app: fade to "Switching to …", swap the data and services
    /// (<see cref="App.SwitchWorkspace"/>), refresh the window for the new workspace, fade back in.
    /// </summary>
    private async Task SwitchWorkspaceInPlaceAsync(Workspace target)
    {
        ShowSwitchScreen($"Switching to {target.Name}", target.Color);
        await Task.Delay(TimeSpan.FromMilliseconds(260));   // let the screen cover the window first

        // Close anything that belongs to the old workspace's data
        CloseEditPage();
        CloseNotePage();
        if (_addPanelOpen) SetAddFormOpen(false);
        NewTaskAttachments.Clear();

        bool switched = App.SwitchWorkspace(target.Id);

        // The new workspace's data, settings, theme and zoom
        DataContext = App.ViewModel;
        if (App.ViewModel is { } vm) AttachViewModel(vm);
        App.Instance.ReapplyTheme();
        ApplyTextScale(App.Settings.Current.EffectiveZoom);
        RefreshWorkspaceHeader();
        NavigateTo(AppPage.Main);

        await Task.Delay(MinimumSwitchScreen);
        await Animator.Fade(SwitchOverlay, 1, 0, TimeSpan.FromMilliseconds(300), new CubicEaseInOut());
        SwitchOverlay.IsVisible = false;
        SwitchOverlay.Opacity   = 1;

        if (switched) InAppNotificationService.Success($"You're in {target.Name}.");
        else InAppNotificationService.Error("Sysslar couldn't open that workspace, so it stayed in the current one. Details are in the Sysslar log.");
    }

    private void ShowSwitchScreen(string title, string colorHex, bool animate = true)
    {
        SwitchTitle.Text = title;
        if (Color.TryParse(colorHex, out var color)) SwitchDot.Fill = new SolidColorBrush(color);
        SwitchOverlay.IsVisible = true;
        if (animate) _ = Animator.Fade(SwitchOverlay, 0, 1, TimeSpan.FromMilliseconds(220), new CubicEaseOut());
        else SwitchOverlay.Opacity = 1;
    }
}

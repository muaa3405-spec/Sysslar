using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;

namespace Tasky.Platform.MacOS;

/// <summary>
/// The menu-bar icon on macOS, through Avalonia's native tray support.
/// macOS has no tray balloons; Tasky's in-app notifications cover those messages.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class MacMenuBarIcon : ISystemTray
{
    private static readonly Uri IconUri = new("avares://Tasky/Assets/sysslar-logo.png");

    private TrayIcon? _icon;

    public event Action? OpenRequested;

    public void Show(string tooltip, IReadOnlyList<TrayMenuItem> menu)
    {
        var nativeMenu = new NativeMenu();
        foreach (var item in menu)
        {
            if (item.Text is null) { nativeMenu.Items.Add(new NativeMenuItemSeparator()); continue; }
            var entry = new NativeMenuItem(item.Text);
            entry.Click += (_, _) => item.Click?.Invoke();
            nativeMenu.Items.Add(entry);
        }

        _icon = new TrayIcon
        {
            Icon        = new WindowIcon(AssetLoader.Open(IconUri)),
            ToolTipText = tooltip,
            Menu        = nativeMenu,
            IsVisible   = true
        };
        _icon.Clicked += (_, _) => OpenRequested?.Invoke();
        TrayIcon.SetIcons(Application.Current!, [_icon]);
    }

    public void ShowBalloon(string title, string message, TimeSpan duration) { }

    public void Dispose()
    {
        if (_icon is null) return;
        _icon.IsVisible = false;
        _icon.Dispose();
        _icon = null;
    }
}

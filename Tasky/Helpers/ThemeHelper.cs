using Avalonia.Controls;
using Tasky.Platform;

namespace Tasky.Helpers;

public static class ThemeHelper
{
    /// <summary>Sets a popup up: native title bar matching the app theme, zoom, size, scrolling and centring.</summary>
    public static void Attach(Window window)
    {
        window.Classes.Set("reduceMotion", !Motion.Enabled);
        DialogLayoutHelper.Prepare(window);
        window.Opened += (_, _) => PlatformServices.Chrome.ApplyTheme(window, App.Instance.IsDarkTheme);
    }
}

using Avalonia.Controls;
using Tasky.Platform;

namespace Tasky.Helpers;

public static class ThemeHelper
{
    /// <summary>Gives a dialog a dark or light native title bar matching the app theme.</summary>
    public static void Attach(Window window)
    {
        window.Classes.Set("reduceMotion", !Motion.Enabled);
        DialogZoom.Apply(window);
        window.Opened += (_, _) => PlatformServices.Chrome.ApplyTheme(window, App.Instance.IsDarkTheme);
    }
}

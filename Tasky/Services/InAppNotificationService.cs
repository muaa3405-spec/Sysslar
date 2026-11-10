using Avalonia.Threading;

namespace Tasky.Services;

/// <summary>A button shown on an in-app notification, such as "Undo".</summary>
public sealed record NotificationAction(string Label, Action Run);

/// <summary>What a notification is about; sets its icon and colour.</summary>
public enum NotificationKind { Success, Info, Warning, Error, Undo }

/// <summary>
/// Shows themed in-app notifications anchored to the bottom-right corner of
/// the main window — never on the desktop.
/// </summary>
public static class InAppNotificationService
{
    public static void Success(string message) => Show(message, NotificationKind.Success);
    public static void Info(string message)    => Show(message, NotificationKind.Info);
    public static void Warning(string message) => Show(message, NotificationKind.Warning);
    public static void Error(string message)   => Show(message, NotificationKind.Error);

    /// <summary>A notification with an "Undo" button that runs <paramref name="undo"/>.</summary>
    public static void Undoable(string message, Action undo) => Show(message, NotificationKind.Undo, new NotificationAction("Undo", undo));

    // Legacy overloads (title param ignored — kept for call-site compatibility)
    public static void Success(string message, string _) => Success(message);
    public static void Info(string message,    string _) => Info(message);
    public static void Warning(string message, string _) => Warning(message);
    public static void Error(string message,   string _) => Error(message);

    private static void Show(string message, NotificationKind kind, NotificationAction? action = null)
    {
        void ShowOnMainWindow() => App.MainAppWindow?.ShowInAppNotification(message, kind, action);

        if (Dispatcher.UIThread.CheckAccess()) ShowOnMainWindow();
        else Dispatcher.UIThread.Post(ShowOnMainWindow);
    }
}

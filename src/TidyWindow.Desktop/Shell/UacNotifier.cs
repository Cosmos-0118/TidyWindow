using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using TidyWindow.Next.Core;

namespace TidyWindow.Next.Desktop;

/// <summary>
/// Tells the person when Windows is waiting for them to approve an administrator prompt that TidyWindow's work needs. Inside TidyWindow it is a toast over whatever page is open;
/// anywhere else in Windows it is a notification at the corner of the screen. Either one has a button that brings the prompt forward, and goes away by itself when the prompt does.
/// </summary>
internal sealed class UacNotifier : IDisposable
{
    private const string Key = "uac-waiting", Tag = "uac-waiting";
    private readonly ToastCenter toasts;
    private readonly ActivityLog log;
    private readonly Action<Action> post;
    private readonly UacPromptWatcher watcher;
    private readonly bool system;
    private bool disposed;

    public UacNotifier(ToastCenter toasts, ActivityLog log, Func<bool> hasWork, Action<Action> post)
    {
        this.toasts = toasts; this.log = log; this.post = post;
        try
        {
            // Handler first: a click that arrives while registering must not be lost.
            AppNotificationManager.Default.NotificationInvoked += Invoked;
            AppNotificationManager.Default.Register();
            system = true;
        }
        catch (Exception ex)
        {
            log.Write(ActivityLevel.Warning, ActivityArea.App, "Windows notifications are unavailable", ex.Message + "\nA waiting permission prompt is still announced inside TidyWindow.");
        }
        watcher = new UacPromptWatcher(UacPrompt.IsOpen, hasWork);
        watcher.Changed += open => post(() => Changed(open));
    }

    private void Changed(bool open)
    {
        if (disposed) return;
        if (!open)
        {
            toasts.Remove(Key);
            if (system) _ = RemoveAsync();
            return;
        }
        // The prompt is waiting. In TidyWindow a toast says so; with another program in front, so does a notification, since the toast would be unseen.
        if (toasts.Attentive || !system) ShowToast();
        else ShowNotification();
    }

    private void ShowToast() => toasts.Show(Key, ToastKind.Warning, "Windows is waiting for your approval",
        "A permission prompt is open and the work is paused until it is answered.", "Show prompt", ShowPromptAsync, seconds: 0);

    private void ShowNotification()
    {
        try
        {
            var notification = new AppNotificationBuilder()
                .AddText("Windows is waiting for your approval")
                .AddText("TidyWindow is paused until a permission prompt is answered.")
                .AddButton(new AppNotificationButton("Show prompt").AddArgument("action", "uac"))
                .SetScenario(AppNotificationScenario.Reminder)
                .BuildNotification();
            notification.Tag = Tag;
            notification.ExpiresOnReboot = true;
            AppNotificationManager.Default.Show(notification);
        }
        catch (Exception ex)
        {
            log.Write(ActivityLevel.Warning, ActivityArea.App, "The permission-prompt notification could not be shown", ex.Message);
            ShowToast();
        }
    }

    private void Invoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        if (args.Arguments.TryGetValue("action", out var action) && action == "uac") post(() => _ = ShowPromptAsync());
    }

    private Task ShowPromptAsync()
    {
        if (UacPrompt.TryShow()) return Task.CompletedTask;
        // On Windows' own dimmed screen it is already in front of everything; anywhere else the prompt was not found, which is worth a line in the log.
        log.Write(ActivityLevel.Info, ActivityArea.App, "The permission prompt could not be brought forward", "Windows keeps it on a screen other programs cannot reach, or it was answered already.");
        toasts.Show("uac-hint", ToastKind.Info, "Look for the Windows permission prompt",
            "If it is not on screen, press Alt+Tab or look for a flashing shield in the taskbar.", seconds: 12);
        return Task.CompletedTask;
    }

    private static async Task RemoveAsync()
    {
        try { await AppNotificationManager.Default.RemoveByTagAsync(Tag); }
        catch (Exception ex) when (ex is not OperationCanceledException) { }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        watcher.Dispose();
        if (!system) return;
        // A notification left behind would start the app if it were clicked later.
        try { AppNotificationManager.Default.NotificationInvoked -= Invoked; AppNotificationManager.Default.RemoveAllAsync().AsTask().Wait(TimeSpan.FromSeconds(2)); AppNotificationManager.Default.Unregister(); }
        catch (Exception ex) when (ex is not OperationCanceledException) { }
    }
}

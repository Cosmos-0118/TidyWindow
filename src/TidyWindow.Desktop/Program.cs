using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using TidyWindow.Next.Core;

namespace TidyWindow.Next.Desktop;

/// <summary>
/// The entry point, written by hand (instead of the XAML-generated one) so a second launch can hand over to the window that is already open and quit,
/// before it spends any time creating a window of its own.
/// </summary>
public static class Program
{
    // A development build has its own key, so running it never hands over to an installed copy of the app (or the other way round).
#if DEBUG
    private const string InstanceKey = "TidyWindow.Next.Debug";
#else
    private const string InstanceKey = "TidyWindow.Next";
#endif

    /// <summary>The splash covering the window that is opening, or null when there is none. The app dismisses it once its first frame is drawn.</summary>
    internal static NativeSplash? Splash { get; private set; }

    // How long a new launch waits for the open window to take over. If that window is stuck, the new one starts instead of doing nothing.
    private static readonly TimeSpan HandOverWait = TimeSpan.FromSeconds(8);

    // WinUI needs the thread that runs the interface to be single-threaded. The generated entry point has this attribute, and without it
    // the app starts but fails at random moments within seconds, with a native error that nothing in the app can catch or log.
    [STAThread]
    private static void Main()
    {
        // First of all, before anything is read, started or registered: see AppElevation for why.
        if (AppElevation.IsElevated())
        {
            MessageBoxW(IntPtr.Zero, AppElevation.Refusal, "TidyWindow", 0x40 /* MB_ICONINFORMATION */);
            return;
        }
        WinRT.ComWrappersSupport.InitializeComWrappers();
        if (Environment.GetEnvironmentVariable("TW_TOAST_DEMO") is null && HandedOverToRunningInstance()) return; // TEMP-DEMO
        // Before anything slow: the splash is on screen while the interface framework and the main window are still being set up.
        Splash = NativeSplash.Start();

        Application.Start(_ =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            new App();
        });
    }

    /// <summary>True when another TidyWindow already owns this user's key and has taken over this launch. Otherwise this process becomes the owner.</summary>
    private static bool HandedOverToRunningInstance()
    {
        AppInstance owner;
        try { owner = AppInstance.FindOrRegisterForKey(InstanceKey); }
        catch (Exception ex) when (ex is COMException or InvalidOperationException) { return false; }

        if (owner.IsCurrent)
        {
            // Raised on a background thread whenever a later launch is handed to this one.
            owner.Activated += (_, _) => App.ShowRunningWindow();
            return false;
        }

        try
        {
            // The new process was just started by the person, so it may give the open window the right to come to the front.
            AllowSetForegroundWindow((uint)owner.ProcessId);
            var args = AppInstance.GetCurrent().GetActivatedEventArgs();
            var done = new ManualResetEventSlim();
            var handedOver = false; var gaveUp = 0;
            // The call must not run on this thread: waiting on it here would block the message loop the hand-over needs.
            Task.Run(async () =>
            {
                try { await owner.RedirectActivationToAsync(args); handedOver = true; }
                catch (Exception ex) when (ex is COMException or InvalidOperationException or UnauthorizedAccessException) { }
                finally { done.Set(); }
                // Answered only after this launch stopped waiting: the open window has come forward, so this copy must not also open one.
                if (handedOver && Interlocked.CompareExchange(ref gaveUp, 0, 0) == 1) App.HandedOverLate();
            });
            if (done.Wait(HandOverWait)) return handedOver;
            Interlocked.Exchange(ref gaveUp, 1);
            // Settled in the moment between the wait running out and this line: that still counts as handed over.
            return done.IsSet && handedOver;
        }
        // The open copy could not be reached (for example it runs with different rights), so this launch carries on rather than silently doing nothing.
        catch (Exception ex) when (ex is COMException or InvalidOperationException or UnauthorizedAccessException) { return false; }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);
}

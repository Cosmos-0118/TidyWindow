using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using TidyWindow.Next.Core;

namespace TidyWindow.Next.Desktop;

public partial class App : Application
{
    private Window? window;
    private static readonly string LogFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TidyWindow.Next", "Logs");
    private readonly ActivityLog log = new(LogFolder);
    public App()
    {
        InitializeComponent();
        // A crash in Windows or the runtime ends the process before any handler below can write, so a copy that vanished is noticed on the next start.
        new SessionGuard(Path.Combine(LogFolder, "sessions"), log).Begin();
        // Record the failure before the process ends; these handlers do not suppress the crash. The app's version and the Windows build go in
        // with it, because a report rarely says which they were.
        // Crash file: once ActivityLog can write one, hand it the full text here and keep this entry as the pointer to it.
        UnhandledException += (_, e) => log.WriteCrash("TidyWindow hit an unexpected error", Describe(e.Exception?.ToString() ?? e.Message));
        AppDomain.CurrentDomain.UnhandledException += (_, e) => log.WriteCrash("TidyWindow stopped unexpectedly", Describe(e.ExceptionObject.ToString()));
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            // Cancellation of abandoned work is expected and not worth recording.
            if (!e.Exception.Flatten().InnerExceptions.All(x => x is OperationCanceledException))
                log.Write(ActivityLevel.Error, ActivityArea.App, "A background task failed", e.Exception.ToString());
        };
    }

    /// <summary>A failure's detail behind the app's version and the Windows build.</summary>
    private static string Describe(string? detail) => $"TidyWindow {AppUpdateService.ReadCurrentVersion()} on {RuntimeInformation.OSDescription}\n{detail}";

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            // The splash has been up since the process began. The window opens underneath it, already in its final place.
            window = new MainWindow(log);
            interfaceQueue = window.DispatcherQueue;
            window.Activate();
            Program.Splash?.KeepAbove(WinRT.Interop.WindowNative.GetWindowHandle(window));
            // The splash fades out only once the window has drawn its first frames, so what it reveals is finished, not still arriving.
            await ((MainWindow)window).FirstFrame.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException) { }
        catch (Exception ex)
        {
            // An exception leaving here ends the process with no usable record; and a launch that could not make its window has nothing to keep open.
            log.WriteCrash("TidyWindow could not open its window", Describe(ex.ToString()));
            if (window is null) Exit();
        }
        finally { Program.Splash?.Dismiss(); }
    }

    private static DispatcherQueue? interfaceQueue;

    /// <summary>Brings the open window forward, restoring it first if it was minimized. Safe to call from any thread, and does nothing before the window exists.</summary>
    internal static void ShowRunningWindow() => interfaceQueue?.TryEnqueue(() =>
    {
        if (Current is not App { window: { } open }) return;
        if (open.AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter) presenter.Restore();
        open.AppWindow.Show();
        open.Activate();
        SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(open));
    });

    /// <summary>
    /// Called when the copy already open took this launch over only after this process had given up waiting on it and started a window of
    /// its own. That copy has been brought forward, so before this window is on screen the process ends; once it is up it stays, since the
    /// person may already be using it, and the overlap is noted in Logs.
    /// </summary>
    internal static void HandedOverLate()
    {
        if (interfaceQueue is not { } queue) { Environment.Exit(0); return; }
        queue.TryEnqueue(() => (Current as App)?.log.Write(ActivityLevel.Warning, ActivityArea.App, "Two TidyWindow windows are open",
            "The window that was already open took too long to answer, so this one opened as well. Close the one you are not using."));
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);
}

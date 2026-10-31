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
        // Record the failure before the process ends; these handlers do not suppress the crash.
        UnhandledException += (_, e) => log.Write(ActivityLevel.Error, ActivityArea.App, "TidyWindow hit an unexpected error", e.Exception?.ToString() ?? e.Message);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => log.Write(ActivityLevel.Error, ActivityArea.App, "TidyWindow stopped unexpectedly", e.ExceptionObject.ToString());
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            // Cancellation of abandoned work is expected and not worth recording.
            if (!e.Exception.Flatten().InnerExceptions.All(x => x is OperationCanceledException))
                log.Write(ActivityLevel.Error, ActivityArea.App, "A background task failed", e.Exception.ToString());
        };
    }
    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        // The splash has been up since the process began. The window opens underneath it, already in its final place.
        window = new MainWindow(log);
        interfaceQueue = window.DispatcherQueue;
        window.Activate();
        Program.Splash?.KeepAbove(WinRT.Interop.WindowNative.GetWindowHandle(window));
        try
        {
            // The splash fades out only once the window has drawn its first frames, so what it reveals is finished, not still arriving.
            await ((MainWindow)window).FirstFrame.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException) { }
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

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);
}

using Microsoft.UI.Xaml;
using TidyWindow.Next.Core;

namespace TidyWindow.Next.Desktop;

public partial class App : Application
{
    private Window? window;
    private readonly ActivityLog log = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TidyWindow.Next", "Logs"));
    public App()
    {
        InitializeComponent();
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
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        window = new MainWindow(log);
        window.Activate();
    }
}

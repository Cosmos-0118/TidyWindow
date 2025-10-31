using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace TidyWindow.App.Services;

/// <summary>
/// Logs unhandled exceptions to disk so silent failures can be diagnosed.
/// </summary>
public sealed class CrashLogService : IDisposable
{
    private readonly string _logDirectory;
    private readonly object _sync = new();
    private System.Windows.Application? _application;
    private bool _isAttached;
    private bool _disposed;

    public CrashLogService(string? logDirectory = null)
    {
        var root = string.IsNullOrWhiteSpace(logDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TidyWindow", "logs")
            : logDirectory;

        _logDirectory = root.Trim();
        if (string.IsNullOrWhiteSpace(_logDirectory))
        {
            _logDirectory = Path.Combine(Path.GetTempPath(), "TidyWindow", "logs");
        }
    }

    public string? LastLogFilePath { get; private set; }

    public void Attach(System.Windows.Application application)
    {
        if (application is null)
        {
            throw new ArgumentNullException(nameof(application));
        }

        if (_isAttached)
        {
            return;
        }

        _application = application;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        application.DispatcherUnhandledException += OnDispatcherUnhandledException;
        _isAttached = true;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log("Dispatcher", e.Exception, terminating: false);
        // Let WPF decide whether to terminate; we only observe.
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Log("TaskScheduler", e.Exception, terminating: false);
        e.SetObserved();
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        Log("AppDomain", e.ExceptionObject as Exception, e.IsTerminating);
    }

    private void Log(string source, Exception? exception, bool terminating)
    {
        try
        {
            var data = BuildLog(source, exception, terminating);
            Directory.CreateDirectory(_logDirectory);

            var fileName = $"tidywindow-crash-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}.log";
            var fullPath = Path.Combine(_logDirectory, fileName);

            lock (_sync)
            {
                File.WriteAllText(fullPath, data);
                LastLogFilePath = fullPath;
            }

            Console.Error.WriteLine($"Crash log written to {fullPath}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to write crash log: {ex}");
        }
    }

    private static string BuildLog(string source, Exception? exception, bool terminating)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Timestamp (UTC): {DateTime.UtcNow:O}");
        builder.AppendLine($"Source: {source}");
        builder.AppendLine($"Terminating: {terminating}");
        builder.AppendLine($"Process: {Process.GetCurrentProcess().ProcessName} ({Environment.ProcessId})");
        builder.AppendLine($"Thread: {Environment.CurrentManagedThreadId}");
        builder.AppendLine($"OS: {Environment.OSVersion}");
        builder.AppendLine($"64-bit OS: {Environment.Is64BitOperatingSystem}");
        builder.AppendLine($"64-bit Process: {Environment.Is64BitProcess}");
        var assembly = typeof(CrashLogService).Assembly.GetName();
        builder.AppendLine($"Assembly: {assembly.Name} {assembly.Version}");
        builder.AppendLine();
        builder.AppendLine("Exception:");
        builder.AppendLine(exception?.ToString() ?? "(null)");
        return builder.ToString();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;

        if (_application is not null)
        {
            _application.DispatcherUnhandledException -= OnDispatcherUnhandledException;
            _application = null;
        }
    }
}

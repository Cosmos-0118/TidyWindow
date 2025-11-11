using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using WpfApplication = System.Windows.Application;

namespace TidyWindow.App.Services;

public sealed class AppRestartService
{
    private readonly ActivityLogService _activityLog;
    private readonly ITrayService _trayService;

    public AppRestartService(ActivityLogService activityLog, ITrayService trayService)
    {
        _activityLog = activityLog ?? throw new ArgumentNullException(nameof(activityLog));
        _trayService = trayService ?? throw new ArgumentNullException(nameof(trayService));
    }

    public bool TryRestart(bool launchMinimized = false)
    {
        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            _activityLog.LogError("PulseGuard", "Restart aborted: unable to locate executable path.");
            return false;
        }

        var arguments = GatherArguments(launchMinimized);
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                Arguments = BuildArgumentString(arguments),
                UseShellExecute = true,
                WorkingDirectory = Environment.CurrentDirectory
            };

            Process.Start(startInfo);
            _activityLog.LogInformation("PulseGuard", "Restarting TidyWindow to apply recent changes.");

            _trayService.PrepareForExit();
            WpfApplication.Current?.Dispatcher.Invoke(() => WpfApplication.Current?.Shutdown());
            return true;
        }
        catch (Exception ex)
        {
            _activityLog.LogError("PulseGuard", $"Restart failed: {ex.Message}");
            return false;
        }
    }

    private static IReadOnlyList<string> GatherArguments(bool launchMinimized)
    {
        var args = Environment.GetCommandLineArgs().Skip(1).ToList();
        if (launchMinimized && !args.Any(arg => string.Equals(arg, "--minimized", StringComparison.OrdinalIgnoreCase)))
        {
            args.Add("--minimized");
        }

        return args;
    }

    private static string BuildArgumentString(IEnumerable<string> arguments)
    {
        return string.Join(' ', arguments.Select(QuoteArgument));
    }

    private static string QuoteArgument(string argument)
    {
        if (string.IsNullOrEmpty(argument))
        {
            return "\"\"";
        }

        var containsWhitespace = argument.Any(char.IsWhiteSpace) || argument.Contains('"');
        if (!containsWhitespace)
        {
            return argument;
        }

        var escaped = argument.Replace("\\", "\\\\").Replace("\"", "\\\"");
        return $"\"{escaped}\"";
    }
}

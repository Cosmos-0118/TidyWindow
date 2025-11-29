using System;
using System.Diagnostics;
using System.IO;

namespace TidyWindow.App.Services;

public sealed class AppAutoStartService
{
    private const string TaskFullName = "\\TidyWindow\\TidyWindowElevatedStartup";

    public bool TrySetEnabled(bool enabled, out string? error)
    {
        error = null;

        try
        {
            if (!OperatingSystem.IsWindows())
            {
                return true;
            }

            if (enabled)
            {
                var executablePath = ResolveExecutablePath();
                if (executablePath is null)
                {
                    error = "Unable to resolve the TidyWindow executable path.";
                    return false;
                }

                return TryCreateTask(executablePath, out error);
            }

            return TryDeleteTask(out error);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool TryCreateTask(string executablePath, out string? error)
    {
        var actionArgument = $"\"\\\"{executablePath}\\\" --minimized\"";
        var arguments = $"/Create /TN \"{TaskFullName}\" /F /SC ONLOGON /RL HIGHEST /TR {actionArgument}";
        return RunSchtasks(arguments, out error);
    }

    private static bool TryDeleteTask(out string? error)
    {
        return RunSchtasks($"/Delete /TN \"{TaskFullName}\" /F", out error, ignoreMissing: true);
    }

    private static bool RunSchtasks(string arguments, out string? error, bool ignoreMissing = false)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "schtasks.exe",
            Arguments = arguments,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            error = "Failed to launch schtasks.exe.";
            return false;
        }

        var stdOut = process.StandardOutput.ReadToEnd();
        var stdErr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode == 0)
        {
            error = null;
            return true;
        }

        var failure = CombineOutput(stdOut, stdErr);
        if (ignoreMissing && IsMissingTaskMessage(failure))
        {
            error = null;
            return true;
        }

        error = failure.Length == 0
            ? $"schtasks.exe exited with code {process.ExitCode}."
            : failure;
        return false;
    }

    private static bool IsMissingTaskMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        return message.Contains("cannot find", StringComparison.OrdinalIgnoreCase)
            || message.Contains("does not exist", StringComparison.OrdinalIgnoreCase)
            || message.Contains("not exist", StringComparison.OrdinalIgnoreCase);
    }

    private static string CombineOutput(string stdOut, string stdErr)
    {
        var hasErr = !string.IsNullOrWhiteSpace(stdErr);
        var hasOut = !string.IsNullOrWhiteSpace(stdOut);

        if (hasErr && hasOut)
        {
            return (stdErr + Environment.NewLine + stdOut).Trim();
        }

        if (hasErr)
        {
            return stdErr.Trim();
        }

        if (hasOut)
        {
            return stdOut.Trim();
        }

        return string.Empty;
    }

    private static string? ResolveExecutablePath()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            var modulePath = process.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(modulePath))
            {
                return null;
            }

            return Path.GetFullPath(modulePath);
        }
        catch
        {
            return null;
        }
    }
}

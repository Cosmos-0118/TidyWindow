using System;
using System.Diagnostics;
using System.IO;

namespace TidyWindow.App.Services;

public sealed class AppAutoStartService
{
    private const string TaskFullName = "\\TidyWindow\\TidyWindowElevatedStartup";
    private readonly IProcessRunner _processRunner;

    public AppAutoStartService(IProcessRunner processRunner)
    {
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
    }

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

    private bool TryCreateTask(string executablePath, out string? error)
    {
        var actionArgument = $"\"\\\"{executablePath}\\\" --minimized\"";
        var arguments = $"/Create /TN \"{TaskFullName}\" /F /SC ONLOGON /RL HIGHEST /TR {actionArgument}";
        return RunSchtasks(arguments, out error);
    }

    private bool TryDeleteTask(out string? error)
    {
        return RunSchtasks($"/Delete /TN \"{TaskFullName}\" /F", out error, ignoreMissing: true);
    }

    private bool RunSchtasks(string arguments, out string? error, bool ignoreMissing = false)
    {
        var result = _processRunner.Run("schtasks.exe", arguments);
        if (result.ExitCode == 0)
        {
            error = null;
            return true;
        }

        var failure = CombineOutput(result.StandardOutput, result.StandardError);
        if (ignoreMissing && IsMissingTaskMessage(failure))
        {
            error = null;
            return true;
        }

        error = failure.Length == 0
            ? $"schtasks.exe exited with code {result.ExitCode}."
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

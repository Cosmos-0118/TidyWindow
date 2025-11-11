using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace TidyWindow.App.Services;

public sealed class AppAutoStartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "TidyWindow";

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

                using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
                if (key is null)
                {
                    error = "Unable to open the Windows Run registry key.";
                    return false;
                }

                key.SetValue(ValueName, $"\"{executablePath}\" --minimized");
            }
            else
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
                key?.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
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

using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Xml;
using Microsoft.Win32;

namespace TidyWindow.App.Services;

/// <summary>
/// Manages TidyWindow auto-start registration using multiple fallback strategies
/// for maximum reliability across different Windows configurations.
/// </summary>
/// <remarks>
/// Strategy order:
/// 1. Task Scheduler via XML import (most reliable, supports elevated/delayed start)
/// 2. Task Scheduler via schtasks.exe command line (fallback)
/// 3. Registry Run key (fallback for non-admin or restricted environments)
/// </remarks>
public sealed class AppAutoStartService
{
    private const string TaskFolderName = "TidyWindow";
    private const string TaskName = "TidyWindowElevatedStartup";
    private const string TaskFullName = $"\\{TaskFolderName}\\{TaskName}";
    private const string RegistryRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RegistryValueName = "TidyWindow";

    private readonly IProcessRunner _processRunner;

    public AppAutoStartService(IProcessRunner processRunner)
    {
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
    }

    /// <summary>
    /// Gets whether TidyWindow is currently registered to start automatically.
    /// Checks both Task Scheduler and Registry Run key.
    /// </summary>
    public bool IsEnabled
    {
        get
        {
            if (!OperatingSystem.IsWindows())
            {
                return false;
            }

            // Check Task Scheduler first
            if (IsTaskRegistered())
            {
                return true;
            }

            // Fall back to registry check
            return IsRegistryRunKeySet();
        }
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

            var executablePath = ResolveExecutablePath();
            if (enabled && executablePath is null)
            {
                error = "Unable to resolve the TidyWindow executable path.";
                return false;
            }

            if (enabled)
            {
                return TryEnableStartup(executablePath!, out error);
            }

            return TryDisableStartup(out error);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private bool TryEnableStartup(string executablePath, out string? error)
    {
        // Strategy 1: Try XML-based task creation (most reliable)
        if (TryCreateTaskViaXml(executablePath, out error))
        {
            // Clean up any registry fallback entry
            TryRemoveRegistryRunKey(out _);
            return true;
        }

        // Strategy 2: Try schtasks.exe command line
        if (TryCreateTaskViaCommandLine(executablePath, out error))
        {
            TryRemoveRegistryRunKey(out _);
            return true;
        }

        // Strategy 3: Fall back to registry Run key (works without admin in some cases)
        if (TrySetRegistryRunKey(executablePath, out error))
        {
            return true;
        }

        error = "Failed to register TidyWindow for startup using all available methods. " +
                "Ensure the application is running with administrator privileges.";
        return false;
    }

    private bool TryDisableStartup(out string? error)
    {
        var taskDeleted = TryDeleteTask(out var taskError);
        var registryRemoved = TryRemoveRegistryRunKey(out var registryError);

        if (taskDeleted && registryRemoved)
        {
            error = null;
            return true;
        }

        // If both failed, combine errors
        if (!taskDeleted && !registryRemoved)
        {
            error = $"Task Scheduler: {taskError}; Registry: {registryError}";
            return false;
        }

        // At least one succeeded, consider it a success
        error = null;
        return true;
    }

    #region Task Scheduler - XML Import

    private bool TryCreateTaskViaXml(string executablePath, out string? error)
    {
        string? tempXmlPath = null;
        try
        {
            // First ensure the TidyWindow folder exists in Task Scheduler
            EnsureTaskFolder();

            var xml = GenerateTaskXml(executablePath);
            tempXmlPath = Path.Combine(Path.GetTempPath(), $"TidyWindowTask_{Guid.NewGuid():N}.xml");
            File.WriteAllText(tempXmlPath, xml, Encoding.Unicode);

            // Import the task using schtasks /Create /XML
            var arguments = $"/Create /TN \"{TaskFullName}\" /XML \"{tempXmlPath}\" /F";
            var result = _processRunner.Run("schtasks.exe", arguments);

            if (result.ExitCode == 0)
            {
                error = null;
                return true;
            }

            error = CombineOutput(result.StandardOutput, result.StandardError);
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            if (tempXmlPath is not null)
            {
                try
                {
                    File.Delete(tempXmlPath);
                }
                catch
                {
                    // Best effort cleanup
                }
            }
        }
    }

    private void EnsureTaskFolder()
    {
        // Create the TidyWindow folder in Task Scheduler if it doesn't exist
        // This is done by creating a dummy task and immediately deleting it,
        // or by using PowerShell - we'll use a simple check first
        var checkResult = _processRunner.Run("schtasks.exe", $"/Query /TN \"\\{TaskFolderName}\" 2>nul");
        if (checkResult.ExitCode != 0)
        {
            // Folder might not exist, but schtasks /Create will create it automatically
            // when we create the task with the full path
        }
    }

    private static string GenerateTaskXml(string executablePath)
    {
        // Escape the path for XML
        var escapedPath = System.Security.SecurityElement.Escape(executablePath);

        return $@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.4"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo>
    <Description>Launches TidyWindow at user sign-in for system maintenance and monitoring.</Description>
    <Author>TidyWindow</Author>
  </RegistrationInfo>
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <Delay>PT30S</Delay>
    </LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id=""Author"">
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <DisallowStartOnRemoteAppSession>false</DisallowStartOnRemoteAppSession>
    <UseUnifiedSchedulingEngine>true</UseUnifiedSchedulingEngine>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context=""Author"">
    <Exec>
      <Command>{escapedPath}</Command>
      <Arguments>--minimized</Arguments>
    </Exec>
  </Actions>
</Task>";
    }

    #endregion

    #region Task Scheduler - Command Line

    private bool TryCreateTaskViaCommandLine(string executablePath, out string? error)
    {
        // Use simpler quoting that's more compatible across Windows versions
        var command = $"\"{executablePath}\"";
        var arguments = $"/Create /TN \"{TaskFullName}\" /F /SC ONLOGON /RL HIGHEST /TR \"{command} --minimized\" /DELAY 0000:30";

        var result = _processRunner.Run("schtasks.exe", arguments);
        if (result.ExitCode == 0)
        {
            error = null;
            return true;
        }

        // Try without /DELAY if it failed (older Windows versions may not support it)
        arguments = $"/Create /TN \"{TaskFullName}\" /F /SC ONLOGON /RL HIGHEST /TR \"{command} --minimized\"";
        result = _processRunner.Run("schtasks.exe", arguments);

        if (result.ExitCode == 0)
        {
            error = null;
            return true;
        }

        error = CombineOutput(result.StandardOutput, result.StandardError);
        return false;
    }

    #endregion

    #region Registry Run Key Fallback

    private bool TrySetRegistryRunKey(string executablePath, out string? error)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryRunKey, writable: true);
            if (key is null)
            {
                error = "Could not open HKCU Run registry key.";
                return false;
            }

            var value = $"\"{executablePath}\" --minimized";
            key.SetValue(RegistryValueName, value, RegistryValueKind.String);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = $"Registry write failed: {ex.Message}";
            return false;
        }
    }

    private bool TryRemoveRegistryRunKey(out string? error)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryRunKey, writable: true);
            if (key is null)
            {
                error = null;
                return true; // Key doesn't exist, nothing to remove
            }

            var existingValue = key.GetValue(RegistryValueName);
            if (existingValue is null)
            {
                error = null;
                return true; // Value doesn't exist
            }

            key.DeleteValue(RegistryValueName, throwOnMissingValue: false);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = $"Registry delete failed: {ex.Message}";
            return false;
        }
    }

    private static bool IsRegistryRunKeySet()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryRunKey, writable: false);
            if (key is null)
            {
                return false;
            }

            var value = key.GetValue(RegistryValueName) as string;
            return !string.IsNullOrWhiteSpace(value) &&
                   value.Contains("TidyWindow", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    #endregion

    #region Task Query and Delete

    private bool IsTaskRegistered()
    {
        try
        {
            var result = _processRunner.Run("schtasks.exe", $"/Query /TN \"{TaskFullName}\"");
            return result.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private bool TryDeleteTask(out string? error)
    {
        var result = _processRunner.Run("schtasks.exe", $"/Delete /TN \"{TaskFullName}\" /F");
        if (result.ExitCode == 0)
        {
            error = null;
            return true;
        }

        var failure = CombineOutput(result.StandardOutput, result.StandardError);
        if (IsMissingTaskMessage(failure))
        {
            error = null;
            return true;
        }

        error = failure.Length == 0
            ? $"schtasks.exe exited with code {result.ExitCode}."
            : failure;
        return false;
    }

    #endregion

    #region Helpers

    private static bool IsMissingTaskMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        return message.Contains("cannot find", StringComparison.OrdinalIgnoreCase)
            || message.Contains("does not exist", StringComparison.OrdinalIgnoreCase)
            || message.Contains("not exist", StringComparison.OrdinalIgnoreCase)
            || message.Contains("cannot be found", StringComparison.OrdinalIgnoreCase);
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

    #endregion
}

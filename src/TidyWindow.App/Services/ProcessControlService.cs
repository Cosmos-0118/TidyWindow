using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.ServiceProcess;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TidyWindow.App.Services;

/// <summary>
/// Issues start/stop/restart commands for Windows services that back the Known Processes tab.
/// </summary>
public sealed class ProcessControlService
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(25);
    private static readonly string OriginalStartTypesPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TidyWindow", "service-original-starttypes.json");

    /// <summary>
    /// Remembers the original start type of each service before we disabled it,
    /// so we can restore it accurately when the user switches back to Keep.
    /// Maps service name → sc.exe start type string (e.g. "auto", "demand", "delayed-auto").
    /// </summary>
    private readonly ConcurrentDictionary<string, string> _originalStartTypes;

    public ProcessControlService()
        : this(OriginalStartTypesPath)
    {
    }

    /// <summary>Internal constructor for testing — accepts a custom persistence path.</summary>
    internal ProcessControlService(string startTypesFilePath)
    {
        _startTypesFilePath = startTypesFilePath;
        _originalStartTypes = LoadOriginalStartTypes(startTypesFilePath);
    }

    private readonly string _startTypesFilePath;

    public Task<ProcessControlResult> StopAsync(string serviceName, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        return Task.Run(() => ControlService(serviceName, timeout, (controller, effectiveTimeout) =>
        {
            controller.Refresh();
            if (controller.StartType == ServiceStartMode.Disabled)
            {
                return ProcessControlResult.CreateSuccess($"{serviceName} is disabled; skipping stop.");
            }

            if (controller.Status == ServiceControllerStatus.Stopped)
            {
                return ProcessControlResult.CreateSuccess($"{serviceName} is already stopped.");
            }

            controller.Stop();
            controller.WaitForStatus(ServiceControllerStatus.Stopped, effectiveTimeout);
            return ProcessControlResult.CreateSuccess($"Stopped {serviceName}.");
        }), cancellationToken);
    }

    public Task<ProcessControlResult> StartAsync(string serviceName, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        return Task.Run(() => ControlService(serviceName, timeout, (controller, effectiveTimeout) =>
        {
            controller.Refresh();
            if (controller.StartType == ServiceStartMode.Disabled)
            {
                return ProcessControlResult.CreateFailure($"{serviceName} is disabled and cannot be started.");
            }

            if (controller.Status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending)
            {
                return ProcessControlResult.CreateSuccess($"{serviceName} is already running.");
            }

            controller.Start();
            controller.WaitForStatus(ServiceControllerStatus.Running, effectiveTimeout);
            return ProcessControlResult.CreateSuccess($"Started {serviceName}.");
        }), cancellationToken);
    }

    public async Task<ProcessControlResult> RestartAsync(string serviceName, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        // First check if the service is disabled before attempting restart.
        var disabledCheck = await Task.Run(() =>
        {
            if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(serviceName))
            {
                return (IsDisabled: false, Message: string.Empty);
            }

            try
            {
                using var controller = new ServiceController(serviceName.Trim());
                controller.Refresh();
                if (controller.StartType == ServiceStartMode.Disabled)
                {
                    return (IsDisabled: true, Message: $"{serviceName} is disabled and cannot be restarted.");
                }
            }
            catch
            {
                // If we can't check, proceed with normal restart flow.
            }

            return (IsDisabled: false, Message: string.Empty);
        }, cancellationToken).ConfigureAwait(false);

        if (disabledCheck.IsDisabled)
        {
            return ProcessControlResult.CreateFailure(disabledCheck.Message);
        }

        var stopResult = await StopAsync(serviceName, timeout, cancellationToken).ConfigureAwait(false);
        if (!stopResult.Success)
        {
            return stopResult;
        }

        return await StartAsync(serviceName, timeout, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Stops a service and disables it so Windows cannot restart it via recovery policies.
    /// </summary>
    public Task<ProcessControlResult> StopAndDisableAsync(string serviceName, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        return Task.Run(() => ControlService(serviceName, timeout, (controller, effectiveTimeout) =>
        {
            controller.Refresh();

            if (controller.StartType == ServiceStartMode.Disabled && controller.Status == ServiceControllerStatus.Stopped)
            {
                return ProcessControlResult.CreateSuccess($"{serviceName} is already disabled and stopped.");
            }

            // Save the original start type BEFORE disabling, so we can restore it later.
            SaveOriginalStartType(serviceName, controller.StartType);

            if (controller.Status is not (ServiceControllerStatus.Stopped or ServiceControllerStatus.StopPending))
            {
                controller.Stop();
                controller.WaitForStatus(ServiceControllerStatus.Stopped, effectiveTimeout);
            }

            // Disable via sc.exe — ServiceController doesn't expose StartType mutation.
            var disableResult = SetServiceStartType(serviceName, "disabled");

            // Clear recovery actions so Windows doesn't auto-restart the service.
            ClearServiceRecoveryActions(serviceName);

            if (!disableResult)
            {
                return ProcessControlResult.CreateSuccess($"Stopped {serviceName} but could not disable it.");
            }

            return ProcessControlResult.CreateSuccess($"Stopped and disabled {serviceName}.");
        }), cancellationToken);
    }

    /// <summary>
    /// Re-enables a previously disabled service by setting its start type to Manual (demand).
    /// Optionally starts it afterward.
    /// </summary>
    public Task<ProcessControlResult> EnableAsync(string serviceName, bool startAfterEnable = false, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        return Task.Run(() => ControlService(serviceName, timeout, (controller, effectiveTimeout) =>
        {
            controller.Refresh();

            // Only re-enable if currently disabled.
            if (controller.StartType != ServiceStartMode.Disabled)
            {
                return ProcessControlResult.CreateSuccess($"{serviceName} is already enabled ({controller.StartType}).");
            }

            var targetStartType = GetOriginalStartType(serviceName);
            var enableResult = SetServiceStartType(serviceName, targetStartType);
            if (!enableResult)
            {
                return ProcessControlResult.CreateFailure($"Could not re-enable {serviceName}.");
            }

            // Clean up saved original — it's been restored.
            RemoveOriginalStartType(serviceName);

            // Restore a sensible default recovery action (restart once after 60s).
            RestoreDefaultRecoveryActions(serviceName);

            var startTypeLabel = targetStartType switch
            {
                "auto" => "Automatic",
                "delayed-auto" => "Automatic (Delayed Start)",
                "demand" => "Manual",
                _ => targetStartType
            };

            if (startAfterEnable)
            {
                controller.Refresh();
                if (controller.Status == ServiceControllerStatus.Stopped)
                {
                    controller.Start();
                    controller.WaitForStatus(ServiceControllerStatus.Running, effectiveTimeout);
                    return ProcessControlResult.CreateSuccess($"Re-enabled and started {serviceName} ({startTypeLabel}).");
                }
            }

            return ProcessControlResult.CreateSuccess($"Re-enabled {serviceName} (set to {startTypeLabel} start).");
        }), cancellationToken);
    }

    /// <summary>
    /// Restores a sensible default recovery policy: restart the service once
    /// after 60 seconds, then take no action on subsequent failures.
    /// </summary>
    private static void RestoreDefaultRecoveryActions(string serviceName)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = $"failure \"{serviceName}\" reset= 86400 actions= restart/60000/\"\"/0/\"\"/0",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });

            process?.WaitForExit(10_000);
        }
        catch
        {
            // Best-effort.
        }
    }

    /// <summary>
    /// Clears recovery (failure) actions for a service so Windows won't
    /// automatically restart it on crash or stop.
    /// </summary>
    private static void ClearServiceRecoveryActions(string serviceName)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = $"failure \"{serviceName}\" reset= 0 actions= \"\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });

            process?.WaitForExit(10_000);
        }
        catch
        {
            // Best-effort — if we can't clear recovery actions, the disable should still help.
        }
    }

    /// <summary>
    /// Uses sc.exe to change the start type of a Windows service.
    /// </summary>
    private static bool SetServiceStartType(string serviceName, string startType)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = $"config \"{serviceName}\" start= {startType}",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });

            if (process is null)
            {
                return false;
            }

            process.WaitForExit(10_000);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    // ── Original start type tracking ──────────────────────────────────

    /// <summary>
    /// Converts <see cref="ServiceStartMode"/> to the sc.exe start type string.
    /// </summary>
    internal static string MapStartModeToScString(ServiceStartMode mode) => mode switch
    {
        ServiceStartMode.Automatic => "auto",
        ServiceStartMode.Manual => "demand",
        ServiceStartMode.Disabled => "demand", // fallback — shouldn't normally save Disabled
        ServiceStartMode.Boot => "boot",
        ServiceStartMode.System => "system",
        _ => "demand"
    };

    /// <summary>
    /// Saves the current start type before we disable. If delayed-auto, uses registry to detect it.
    /// </summary>
    internal void SaveOriginalStartType(string serviceName, ServiceStartMode currentMode)
    {
        var key = serviceName.Trim();
        if (_originalStartTypes.ContainsKey(key))
        {
            return; // Already saved from a previous stop — don't overwrite.
        }

        var scValue = MapStartModeToScString(currentMode);

        // ServiceStartMode.Automatic doesn't distinguish normal Auto from Delayed-Auto.
        // Check the registry to determine if it's delayed.
        if (currentMode == ServiceStartMode.Automatic && IsDelayedAutoStart(serviceName))
        {
            scValue = "delayed-auto";
        }

        _originalStartTypes[key] = scValue;
        PersistOriginalStartTypes();
    }

    /// <summary>
    /// Gets the saved original start type, falling back to "demand" (Manual) if unknown.
    /// </summary>
    internal string GetOriginalStartType(string serviceName)
    {
        return _originalStartTypes.TryGetValue(serviceName.Trim(), out var startType)
            ? startType
            : "demand";
    }

    /// <summary>
    /// Removes the saved original start type after successful restore.
    /// </summary>
    internal void RemoveOriginalStartType(string serviceName)
    {
        if (_originalStartTypes.TryRemove(serviceName.Trim(), out _))
        {
            PersistOriginalStartTypes();
        }
    }

    /// <summary>
    /// Checks the registry to determine if a service is set to Delayed Auto-Start.
    /// </summary>
    private static bool IsDelayedAutoStart(string serviceName)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Services\{serviceName}");
            if (key?.GetValue("DelayedAutostart") is int delayed)
            {
                return delayed == 1;
            }
        }
        catch
        {
            // Best-effort.
        }

        return false;
    }

    /// <summary>
    /// Persists the original start type map to a sidecar JSON file so it
    /// survives app restarts. Fire-and-forget, best-effort.
    /// </summary>
    private void PersistOriginalStartTypes()
    {
        try
        {
            var dir = Path.GetDirectoryName(_startTypesFilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var dict = new Dictionary<string, string>(_originalStartTypes, StringComparer.OrdinalIgnoreCase);
            var json = JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_startTypesFilePath, json);
        }
        catch
        {
            // Best-effort persistence.
        }
    }

    /// <summary>
    /// Loads previously saved original start types from the sidecar file.
    /// </summary>
    private static ConcurrentDictionary<string, string> LoadOriginalStartTypes(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                var json = File.ReadAllText(filePath);
                var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                if (dict is not null)
                {
                    return new ConcurrentDictionary<string, string>(dict, StringComparer.OrdinalIgnoreCase);
                }
            }
        }
        catch
        {
            // Corrupted file — start fresh.
        }

        return new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Stops a service and also kills any associated process by executable name.
    /// If the service stop fails or the service doesn't exist, falls back to process kill.
    /// </summary>
    public async Task<ProcessControlResult> StopServiceAndProcessAsync(string serviceName, string? processName = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var serviceResult = await StopAsync(serviceName, timeout, cancellationToken).ConfigureAwait(false);

        // If the service stopped successfully and no process name specified, we're done.
        if (serviceResult.Success && string.IsNullOrWhiteSpace(processName))
        {
            return serviceResult;
        }

        // Also try to kill the process by name as a fallback / supplement.
        if (!string.IsNullOrWhiteSpace(processName))
        {
            var killResult = await KillProcessByNameAsync(processName, cancellationToken).ConfigureAwait(false);
            if (!serviceResult.Success && killResult.Success)
            {
                return killResult;
            }
        }

        return serviceResult;
    }

    /// <summary>
    /// Terminates all running processes that match the given process name (without .exe extension).
    /// </summary>
    public Task<ProcessControlResult> KillProcessByNameAsync(string processName, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            if (string.IsNullOrWhiteSpace(processName))
            {
                return ProcessControlResult.CreateFailure("Process name was not provided.");
            }

            var name = processName.Trim();
            // Strip .exe extension if provided.
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                name = name[..^4];
            }

            try
            {
                var processes = Process.GetProcessesByName(name);
                if (processes.Length == 0)
                {
                    return ProcessControlResult.CreateSuccess($"No running instances of {name} found.");
                }

                var killed = 0;
                var failed = 0;
                foreach (var proc in processes)
                {
                    try
                    {
                        if (!proc.HasExited)
                        {
                            proc.Kill(entireProcessTree: true);
                            killed++;
                        }
                    }
                    catch
                    {
                        failed++;
                    }
                    finally
                    {
                        proc.Dispose();
                    }
                }

                if (killed > 0 && failed == 0)
                {
                    return ProcessControlResult.CreateSuccess($"Terminated {killed} instance(s) of {name}.");
                }
                else if (killed > 0)
                {
                    return ProcessControlResult.CreateSuccess($"Terminated {killed} instance(s) of {name}; {failed} could not be killed.");
                }
                else
                {
                    return ProcessControlResult.CreateFailure($"Could not terminate any instance of {name}.");
                }
            }
            catch (Exception ex)
            {
                return ProcessControlResult.CreateFailure($"Failed to kill {name}: {ex.Message}");
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Checks whether any process with the given name is currently running.
    /// </summary>
    public static bool IsProcessRunning(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return false;
        }

        var name = processName.Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }

        try
        {
            var processes = Process.GetProcessesByName(name);
            var running = processes.Length > 0;
            foreach (var p in processes) p.Dispose();
            return running;
        }
        catch
        {
            return false;
        }
    }

    private static ProcessControlResult ControlService(string? serviceName, TimeSpan? timeout, Func<ServiceController, TimeSpan, ProcessControlResult> action)
    {
        if (!OperatingSystem.IsWindows())
        {
            return ProcessControlResult.CreateFailure("Service control is only supported on Windows.");
        }

        if (string.IsNullOrWhiteSpace(serviceName))
        {
            return ProcessControlResult.CreateFailure("Service name was not provided.");
        }

        var trimmedName = serviceName.Trim();

        try
        {
            var effectiveTimeout = ResolveTimeout(timeout);
            using var controller = new ServiceController(trimmedName);
            return action(controller, effectiveTimeout);
        }
        catch (InvalidOperationException ex) when (IsServiceMissing(ex))
        {
            return ProcessControlResult.CreateSuccess($"{trimmedName} is not installed; skipping.");
        }
        catch (Exception ex)
        {
            var message = string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message;
            return ProcessControlResult.CreateFailure(message);
        }
    }

    private static bool IsServiceMissing(InvalidOperationException exception)
    {
        if (exception.InnerException is Win32Exception win32 && win32.NativeErrorCode == 1060)
        {
            return true;
        }

        return exception.Message?.IndexOf("does not exist", StringComparison.OrdinalIgnoreCase) >= 0
            || exception.Message?.IndexOf("cannot open", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static TimeSpan ResolveTimeout(TimeSpan? timeout)
    {
        if (timeout is null || timeout <= TimeSpan.Zero)
        {
            return DefaultTimeout;
        }

        return timeout.Value;
    }
}

public readonly record struct ProcessControlResult(bool Success, string Message)
{
    public static ProcessControlResult CreateSuccess(string message) => new(true, string.IsNullOrWhiteSpace(message) ? "Operation succeeded." : message);

    public static ProcessControlResult CreateFailure(string message) => new(false, string.IsNullOrWhiteSpace(message) ? "Operation failed." : message);
}

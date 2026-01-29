using System;
using System.ComponentModel;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;

namespace TidyWindow.App.Services;

/// <summary>
/// Issues start/stop/restart commands for Windows services that back the Known Processes tab.
/// </summary>
public sealed class ProcessControlService
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(25);

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

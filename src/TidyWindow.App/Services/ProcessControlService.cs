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

    public Task<ProcessControlResult> StopAsync(string serviceName, CancellationToken cancellationToken = default)
    {
        return Task.Run(() => ControlService(serviceName, controller =>
        {
            controller.Refresh();
            if (controller.Status == ServiceControllerStatus.Stopped)
            {
                return ProcessControlResult.CreateSuccess($"{serviceName} is already stopped.");
            }

            controller.Stop();
            controller.WaitForStatus(ServiceControllerStatus.Stopped, DefaultTimeout);
            return ProcessControlResult.CreateSuccess($"Stopped {serviceName}.");
        }), cancellationToken);
    }

    public Task<ProcessControlResult> StartAsync(string serviceName, CancellationToken cancellationToken = default)
    {
        return Task.Run(() => ControlService(serviceName, controller =>
        {
            controller.Refresh();
            if (controller.Status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending)
            {
                return ProcessControlResult.CreateSuccess($"{serviceName} is already running.");
            }

            controller.Start();
            controller.WaitForStatus(ServiceControllerStatus.Running, DefaultTimeout);
            return ProcessControlResult.CreateSuccess($"Started {serviceName}.");
        }), cancellationToken);
    }

    public async Task<ProcessControlResult> RestartAsync(string serviceName, CancellationToken cancellationToken = default)
    {
        var stopResult = await StopAsync(serviceName, cancellationToken).ConfigureAwait(false);
        if (!stopResult.Success)
        {
            return stopResult;
        }

        return await StartAsync(serviceName, cancellationToken).ConfigureAwait(false);
    }

    private static ProcessControlResult ControlService(string? serviceName, Func<ServiceController, ProcessControlResult> action)
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
            using var controller = new ServiceController(trimmedName);
            return action(controller);
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
}

public readonly record struct ProcessControlResult(bool Success, string Message)
{
    public static ProcessControlResult CreateSuccess(string message) => new(true, string.IsNullOrWhiteSpace(message) ? "Operation succeeded." : message);

    public static ProcessControlResult CreateFailure(string message) => new(false, string.IsNullOrWhiteSpace(message) ? "Operation failed." : message);
}

using System;
using System.IO;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.TaskScheduler;
using SystemTasks = System.Threading.Tasks;

namespace TidyWindow.Core.Startup;

/// <summary>
/// Converts user-scope startup entries into delayed logon tasks so they run after boot.
/// </summary>
public sealed class StartupDelayService
{
    private static readonly TimeSpan MinimumDelay = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MaximumDelay = TimeSpan.FromMinutes(10);
    private const string FolderPath = "\\TidyWindow\\DelayedStartup";

    private readonly StartupControlService _controlService;
    private readonly StartupDelayPlanStore _planStore;

    public StartupDelayService(StartupControlService? controlService = null, StartupDelayPlanStore? planStore = null)
    {
        _controlService = controlService ?? new StartupControlService();
        _planStore = planStore ?? new StartupDelayPlanStore();
    }

    public async SystemTasks.Task<StartupDelayResult> DelayAsync(StartupItem item, TimeSpan delay, CancellationToken cancellationToken = default)
    {
        EnsureElevated();

        if (!IsUserScope(item))
        {
            return new StartupDelayResult(false, null, "Delay is only supported for current-user Run keys or Startup folder entries.");
        }

        if (item.SourceKind == StartupItemSourceKind.Service || item.SourceKind == StartupItemSourceKind.ScheduledTask)
        {
            return new StartupDelayResult(false, null, "Delaying services or existing tasks is not supported.");
        }

        if (delay < MinimumDelay)
        {
            delay = MinimumDelay;
        }
        else if (delay > MaximumDelay)
        {
            delay = MaximumDelay;
        }

        var command = item.RawCommand ?? BuildCommand(item.ExecutablePath, item.Arguments);
        if (string.IsNullOrWhiteSpace(command) || string.IsNullOrWhiteSpace(item.ExecutablePath))
        {
            return new StartupDelayResult(false, null, "No executable command available for delay.");
        }

        var taskName = SanitizeName(item.Id);
        var taskPath = $"{FolderPath}\\{taskName}";

        try
        {
            using var service = new TaskService();
            var folder = EnsureFolder(service);
            var definition = BuildTaskDefinition(service, item, delay);
            folder.RegisterTaskDefinition(taskName, definition, TaskCreation.CreateOrUpdate, null, null, TaskLogonType.InteractiveToken, null);

            var disableResult = await _controlService.DisableAsync(item, cancellationToken).ConfigureAwait(false);
            if (!disableResult.Succeeded)
            {
                return new StartupDelayResult(false, taskPath, disableResult.ErrorMessage ?? "Failed to disable original entry.");
            }

            _planStore.Save(new StartupDelayPlan(item.Id, item.SourceKind, taskPath, (int)delay.TotalSeconds, DateTimeOffset.UtcNow));
            return new StartupDelayResult(true, taskPath, null);
        }
        catch (Exception ex)
        {
            return new StartupDelayResult(false, taskPath, ex.Message);
        }
    }

    private static TaskDefinition BuildTaskDefinition(TaskService service, StartupItem item, TimeSpan delay)
    {
        var definition = service.NewTask();
        definition.RegistrationInfo.Description = $"Delayed startup for {item.Name}";
        definition.Principal.UserId = WindowsIdentity.GetCurrent().Name;
        definition.Principal.LogonType = TaskLogonType.InteractiveToken;

        var trigger = new LogonTrigger { Delay = delay };
        definition.Triggers.Add(trigger);

        var workingDir = Path.GetDirectoryName(item.ExecutablePath);
        definition.Actions.Add(new ExecAction(item.ExecutablePath, item.Arguments, workingDir));

        definition.Settings.DisallowStartIfOnBatteries = false;
        definition.Settings.StopIfGoingOnBatteries = false;
        definition.Settings.ExecutionTimeLimit = TimeSpan.FromMinutes(5);
        definition.Settings.StartWhenAvailable = true;
        definition.Settings.MultipleInstances = TaskInstancesPolicy.IgnoreNew;

        return definition;
    }

    private static TaskFolder EnsureFolder(TaskService service)
    {
        try
        {
            return service.GetFolder(FolderPath);
        }
        catch
        {
            return service.RootFolder.CreateFolder(FolderPath, null, false);
        }
    }

    private static bool IsUserScope(StartupItem item)
    {
        if (item.SourceKind is StartupItemSourceKind.RunKey or StartupItemSourceKind.RunOnce)
        {
            return item.EntryLocation?.StartsWith("HKCU", StringComparison.OrdinalIgnoreCase) == true;
        }

        if (item.SourceKind == StartupItemSourceKind.StartupFolder)
        {
            var userStartup = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            return !string.IsNullOrWhiteSpace(userStartup) && item.EntryLocation?.StartsWith(userStartup, StringComparison.OrdinalIgnoreCase) == true;
        }

        return false;
    }

    private static string BuildCommand(string executablePath, string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return executablePath;
        }

        return executablePath.Contains(' ', StringComparison.Ordinal)
            ? $"\"{executablePath}\" {arguments}"
            : $"{executablePath} {arguments}";
    }

    private static string SanitizeName(string value)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var safe = new char[value.Length];
        for (var i = 0; i < value.Length; i++)
        {
            var ch = value[i];
            safe[i] = Array.IndexOf(invalid, ch) >= 0 ? '_' : ch;
        }

        var result = new string(safe).Trim('_');
        return string.IsNullOrWhiteSpace(result) ? "DelayedEntry" : result;
    }

    private static void EnsureElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        if (!principal.IsInRole(WindowsBuiltInRole.Administrator))
        {
            throw new InvalidOperationException("Delaying startup requires administrative privileges.");
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Microsoft.Win32.TaskScheduler;
using TaskSchedulerTask = Microsoft.Win32.TaskScheduler.Task;
using SystemTasks = System.Threading.Tasks;

namespace TidyWindow.Core.Startup;

/// <summary>
/// Provides enable/disable operations for startup items with reversible backups.
/// </summary>
public sealed class StartupControlService
{
    private readonly StartupBackupStore _backupStore;

    public StartupControlService(StartupBackupStore? backupStore = null)
    {
        _backupStore = backupStore ?? new StartupBackupStore();
    }

    public StartupBackupStore BackupStore => _backupStore;

    public SystemTasks.Task<StartupToggleResult> DisableAsync(StartupItem item, CancellationToken cancellationToken = default)
    {
        return DisableAsync(item, terminateRunningProcesses: false, cancellationToken);
    }

    /// <summary>
    /// Disables the startup item. When <paramref name="terminateRunningProcesses"/> is true,
    /// also terminates any running instances of the executable.
    /// </summary>
    public SystemTasks.Task<StartupToggleResult> DisableAsync(StartupItem item, bool terminateRunningProcesses, CancellationToken cancellationToken = default)
    {
        EnsureElevated();

        var result = item.SourceKind switch
        {
            StartupItemSourceKind.RunKey or StartupItemSourceKind.RunOnce => DisableRunEntry(item),
            StartupItemSourceKind.StartupFolder => DisableStartupFile(item),
            StartupItemSourceKind.ScheduledTask => DisableScheduledTask(item),
            StartupItemSourceKind.Service => DisableService(item),
            StartupItemSourceKind.PackagedTask => DisablePackagedTask(item),
            StartupItemSourceKind.Winlogon => DisableWinlogonEntry(item),
            StartupItemSourceKind.ActiveSetup => DisableActiveSetup(item),
            StartupItemSourceKind.ExplorerRun => DisableExplorerRun(item),
            StartupItemSourceKind.AppInitDll => DisableAppInitDll(item),
            StartupItemSourceKind.ImageFileExecutionOptions => DisableIfeoDebugger(item),
            StartupItemSourceKind.ShellExtension => DisableShellExtension(item),
            StartupItemSourceKind.BrowserHelperObject => DisableBrowserHelperObject(item),
            StartupItemSourceKind.ProtocolFilter => DisableProtocolFilter(item),
            StartupItemSourceKind.PrintMonitor => DisablePrintMonitor(item),
            StartupItemSourceKind.ShellFolder => DisableShellFolder(item),
            // Dangerous system components - warn user
            StartupItemSourceKind.BootExecute => new StartupToggleResult(false, item, null, "⚠️ CRITICAL: Boot Execute entries are essential for system startup. Modifying them may prevent Windows from booting. This operation is blocked for safety."),
            StartupItemSourceKind.LsaProvider => new StartupToggleResult(false, item, null, "⚠️ CRITICAL: LSA Security Providers handle authentication and security. Disabling them may lock you out of Windows or cause security failures. This operation is blocked for safety."),
            StartupItemSourceKind.KnownDll => new StartupToggleResult(false, item, null, "⚠️ CRITICAL: Known DLLs are core Windows libraries loaded by all processes. Modifying them will cause system instability or crashes. This operation is blocked for safety."),
            StartupItemSourceKind.WinsockProvider => new StartupToggleResult(false, item, null, "⚠️ CRITICAL: Winsock Providers handle all network communication. Disabling them will break internet connectivity and may require recovery mode to fix. This operation is blocked for safety."),
            StartupItemSourceKind.ScmExtension => new StartupToggleResult(false, item, null, "⚠️ CRITICAL: Security Configuration Manager extensions are required for Windows security policies. Modifying them may break Group Policy and security features. This operation is blocked for safety."),
            StartupItemSourceKind.FontDriver => new StartupToggleResult(false, item, null, "⚠️ WARNING: Font Drivers run at kernel level. Disabling them incorrectly may cause display issues or blue screens. This operation is blocked for safety."),
            _ => new StartupToggleResult(false, item, null, "Unsupported startup source.")
        };

        // Terminate running processes if requested and disable succeeded
        if (result.Succeeded && terminateRunningProcesses && !string.IsNullOrWhiteSpace(item.ExecutablePath))
        {
            TerminateProcessesByPath(item.ExecutablePath);
        }

        return SystemTasks.Task.FromResult(result);
    }

    public SystemTasks.Task<StartupToggleResult> EnableAsync(StartupItem item, CancellationToken cancellationToken = default)
    {
        EnsureElevated();
        return SystemTasks.Task.FromResult(item.SourceKind switch
        {
            StartupItemSourceKind.RunKey or StartupItemSourceKind.RunOnce => EnableRunEntry(item),
            StartupItemSourceKind.StartupFolder => EnableStartupFile(item),
            StartupItemSourceKind.ScheduledTask => EnableScheduledTask(item),
            StartupItemSourceKind.Service => EnableService(item),
            StartupItemSourceKind.PackagedTask => EnablePackagedTask(item),
            StartupItemSourceKind.Winlogon => EnableWinlogonEntry(item),
            StartupItemSourceKind.ActiveSetup => EnableActiveSetup(item),
            StartupItemSourceKind.ExplorerRun => EnableExplorerRun(item),
            StartupItemSourceKind.AppInitDll => EnableAppInitDll(item),
            StartupItemSourceKind.ImageFileExecutionOptions => EnableIfeoDebugger(item),
            StartupItemSourceKind.ShellExtension => EnableShellExtension(item),
            StartupItemSourceKind.BrowserHelperObject => EnableBrowserHelperObject(item),
            StartupItemSourceKind.ProtocolFilter => EnableProtocolFilter(item),
            StartupItemSourceKind.PrintMonitor => EnablePrintMonitor(item),
            StartupItemSourceKind.ShellFolder => EnableShellFolder(item),
            // Dangerous system components - warn user
            StartupItemSourceKind.BootExecute => new StartupToggleResult(false, item, null, "⚠️ CRITICAL: Boot Execute entries cannot be modified through this interface for safety reasons."),
            StartupItemSourceKind.LsaProvider => new StartupToggleResult(false, item, null, "⚠️ CRITICAL: LSA Security Providers cannot be modified through this interface for safety reasons."),
            StartupItemSourceKind.KnownDll => new StartupToggleResult(false, item, null, "⚠️ CRITICAL: Known DLLs cannot be modified through this interface for safety reasons."),
            StartupItemSourceKind.WinsockProvider => new StartupToggleResult(false, item, null, "⚠️ CRITICAL: Winsock Providers cannot be modified through this interface for safety reasons."),
            StartupItemSourceKind.ScmExtension => new StartupToggleResult(false, item, null, "⚠️ CRITICAL: Security Configuration Manager extensions cannot be modified through this interface for safety reasons."),
            StartupItemSourceKind.FontDriver => new StartupToggleResult(false, item, null, "⚠️ WARNING: Font Drivers cannot be modified through this interface for safety reasons."),
            _ => new StartupToggleResult(false, item, null, "Unsupported startup source.")
        });
    }

    /// <summary>
    /// Terminates all running instances of an executable by path.
    /// </summary>
    public static int TerminateProcessesByPath(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return 0;
        }

        var terminated = 0;
        try
        {
            var processName = Path.GetFileNameWithoutExtension(executablePath);
            var processes = Process.GetProcessesByName(processName);

            foreach (var process in processes)
            {
                try
                {
                    // Verify it's the same executable path
                    string? processPath = null;
                    try
                    {
                        processPath = process.MainModule?.FileName;
                    }
                    catch
                    {
                        // Access denied - can't verify path, skip to be safe
                        continue;
                    }

                    if (processPath is null || !processPath.Equals(executablePath, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    process.Kill();
                    process.WaitForExit(5000);
                    terminated++;
                }
                catch
                {
                    // Process may have already exited
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
        catch
        {
            // Non-fatal
        }

        return terminated;
    }

    private StartupToggleResult DisablePackagedTask(StartupItem item)
    {
        if (!TryParsePackagedTask(item, out var packageFamilyName, out var taskId))
        {
            return new StartupToggleResult(false, item, null, "Packaged startup task identity missing.");
        }

        var registryPath = GetPackagedTaskRegistryPath(packageFamilyName, taskId);

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(registryPath, writable: true) ?? Registry.CurrentUser.CreateSubKey(registryPath, writable: true);
            if (key is null)
            {
                return new StartupToggleResult(false, item, null, "Registry path for packaged task not found.");
            }

            var previousState = Convert.ToInt32(key.GetValue("State", 2), CultureInfo.InvariantCulture);
            key.SetValue("State", 1, RegistryValueKind.DWord);

            var backup = new StartupEntryBackup(
                item.Id,
                item.SourceKind,
                "HKCU",
                registryPath,
                "State",
                previousState.ToString(CultureInfo.InvariantCulture),
                FileOriginalPath: null,
                FileBackupPath: null,
                TaskPath: null,
                TaskEnabled: null,
                ServiceName: null,
                ServiceStartValue: null,
                ServiceDelayedAutoStart: null,
                CreatedAtUtc: DateTimeOffset.UtcNow);

            _backupStore.Save(backup);
            return new StartupToggleResult(true, item with { IsEnabled = false }, backup, null);
        }
        catch (Exception ex)
        {
            return new StartupToggleResult(false, item, null, ex.Message);
        }
    }

    private StartupToggleResult EnablePackagedTask(StartupItem item)
    {
        if (!TryParsePackagedTask(item, out var packageFamilyName, out var taskId))
        {
            return new StartupToggleResult(false, item, null, "Packaged startup task identity missing.");
        }

        var registryPath = GetPackagedTaskRegistryPath(packageFamilyName, taskId);
        var backup = _backupStore.Get(item.Id);
        var targetState = backup?.RegistryValueData;
        var desiredState = int.TryParse(targetState, out var parsedState) ? parsedState : 2;

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(registryPath, writable: true) ?? Registry.CurrentUser.CreateSubKey(registryPath, writable: true);
            if (key is null)
            {
                return new StartupToggleResult(false, item, backup, "Registry path for packaged task not found.");
            }

            key.SetValue("State", desiredState, RegistryValueKind.DWord);

            if (backup is not null)
            {
                _backupStore.Remove(item.Id);
            }

            return new StartupToggleResult(true, item with { IsEnabled = IsEnabledStartupState(desiredState) }, backup, null);
        }
        catch (Exception ex)
        {
            return new StartupToggleResult(false, item, backup, ex.Message);
        }
    }

    private StartupToggleResult DisableRunEntry(StartupItem item)
    {
        if (!TryParseRegistryLocation(item.EntryLocation, out var root, out var subKey))
        {
            return new StartupToggleResult(false, item, null, "Invalid registry location.");
        }

        var valueName = ExtractValueName(item);
        try
        {
            using var key = root.OpenSubKey(subKey, writable: true);
            if (key is null)
            {
                return new StartupToggleResult(false, item, null, "Registry key not found.");
            }

            var currentValue = key.GetValue(valueName)?.ToString();
            if (currentValue is null)
            {
                return new StartupToggleResult(true, item with { IsEnabled = false }, null, null);
            }

            var approvedSubKey = GetStartupApprovedSubKey(item);
            var canUseStartupApproved = !string.IsNullOrWhiteSpace(approvedSubKey);

            if (canUseStartupApproved)
            {
                if (!TrySetStartupApprovedState(root, approvedSubKey!, valueName, enabled: false, out var approvedError))
                {
                    return new StartupToggleResult(false, item, null, approvedError);
                }
            }
            else
            {
                key.DeleteValue(valueName, throwOnMissingValue: false);
            }

            var backup = new StartupEntryBackup(
                item.Id,
                item.SourceKind,
                GetRootName(root),
                subKey,
                valueName,
                currentValue,
                FileOriginalPath: null,
                FileBackupPath: null,
                TaskPath: null,
                TaskEnabled: null,
                ServiceName: null,
                ServiceStartValue: null,
                ServiceDelayedAutoStart: null,
                CreatedAtUtc: DateTimeOffset.UtcNow);

            _backupStore.Save(backup);
            return new StartupToggleResult(true, item with { IsEnabled = false }, backup, null);
        }
        catch (Exception ex)
        {
            return new StartupToggleResult(false, item, null, ex.Message);
        }
    }

    private StartupToggleResult EnableRunEntry(StartupItem item)
    {
        if (!TryParseRegistryLocation(item.EntryLocation, out var root, out var subKey))
        {
            return new StartupToggleResult(false, item, null, "Invalid registry location.");
        }

        var valueName = ExtractValueName(item);
        var backup = _backupStore.Get(item.Id);

        if (backup is null || string.IsNullOrWhiteSpace(backup.RegistryValueData))
        {
            var fallback = _backupStore.FindLatestByValueName(valueName);
            if (fallback is not null)
            {
                backup = fallback;
            }
        }

        try
        {
            using var key = root.OpenSubKey(subKey, writable: true) ?? root.CreateSubKey(subKey, writable: true);
            if (key is null)
            {
                return new StartupToggleResult(false, item, backup, "Failed to open registry key.");
            }

            var approvedSubKey = GetStartupApprovedSubKey(item);
            var canUseStartupApproved = !string.IsNullOrWhiteSpace(approvedSubKey);

            if (key.GetValue(valueName) is null)
            {
                var data = backup?.RegistryValueData ?? item.RawCommand ?? BuildCommand(item.ExecutablePath, item.Arguments);
                if (string.IsNullOrWhiteSpace(data))
                {
                    return new StartupToggleResult(false, item, backup, "No data available to restore.");
                }

                key.SetValue(valueName, data);
            }

            if (canUseStartupApproved)
            {
                if (!TrySetStartupApprovedState(root, approvedSubKey!, valueName, enabled: true, out var approvedError))
                {
                    return new StartupToggleResult(false, item, backup, approvedError);
                }
            }

            if (backup is not null)
            {
                _backupStore.Remove(item.Id);
            }

            return new StartupToggleResult(true, item with { IsEnabled = true }, backup, null);
        }
        catch (Exception ex)
        {
            return new StartupToggleResult(false, item, backup, ex.Message);
        }
    }

    private StartupToggleResult DisableStartupFile(StartupItem item)
    {
        if (string.IsNullOrWhiteSpace(item.EntryLocation) || string.IsNullOrWhiteSpace(item.ExecutablePath))
        {
            return new StartupToggleResult(false, item, null, "Missing startup file path.");
        }

        try
        {
            var entryName = Path.GetFileName(item.RawCommand ?? item.ExecutablePath);
            var root = ResolveStartupFolderRoot(item.EntryLocation);

            // Find the actual shortcut file in the startup folder
            var startupFolderPath = ResolveStartupFolderPath(item.EntryLocation);
            string? originalFilePath = null;
            string? backupFilePath = null;

            if (!string.IsNullOrWhiteSpace(startupFolderPath) && Directory.Exists(startupFolderPath))
            {
                // Look for shortcut files (.lnk) that match the entry
                var possibleFiles = Directory.GetFiles(startupFolderPath, "*.lnk")
                    .Concat(Directory.GetFiles(startupFolderPath, "*.url"))
                    .ToArray();

                // Try to find the matching file by name
                originalFilePath = possibleFiles.FirstOrDefault(f =>
                    Path.GetFileNameWithoutExtension(f).Equals(entryName, StringComparison.OrdinalIgnoreCase) ||
                    Path.GetFileName(f).Equals(entryName, StringComparison.OrdinalIgnoreCase));

                // If not found by exact name, try matching by executable path in the shortcut
                if (originalFilePath is null && !string.IsNullOrWhiteSpace(item.ExecutablePath))
                {
                    var exeName = Path.GetFileNameWithoutExtension(item.ExecutablePath);
                    originalFilePath = possibleFiles.FirstOrDefault(f =>
                        Path.GetFileNameWithoutExtension(f).Contains(exeName, StringComparison.OrdinalIgnoreCase));
                }
            }

            // Move the file to backup location if found
            if (!string.IsNullOrWhiteSpace(originalFilePath) && File.Exists(originalFilePath))
            {
                var backupFolder = Path.Combine(_backupStore.BackupDirectory, "StartupFiles");
                Directory.CreateDirectory(backupFolder);

                var safeFileName = SanitizeFileName(Path.GetFileName(originalFilePath));
                backupFilePath = Path.Combine(backupFolder, $"{item.Id.GetHashCode():X8}_{safeFileName}");

                // Ensure unique filename if collision
                var counter = 1;
                var basePath = backupFilePath;
                while (File.Exists(backupFilePath))
                {
                    backupFilePath = $"{basePath}_{counter++}";
                }

                File.Move(originalFilePath, backupFilePath);
            }
            else
            {
                // Fallback: use StartupApproved registry if we can't find/move the file
                if (!string.IsNullOrWhiteSpace(entryName) && root is not null)
                {
                    if (!TrySetStartupApprovedState(root, "Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\StartupApproved\\StartupFolder", entryName, enabled: false, out var approvedError))
                    {
                        return new StartupToggleResult(false, item, null, approvedError);
                    }
                }
            }

            var backup = new StartupEntryBackup(
                item.Id,
                item.SourceKind,
                RegistryRoot: null,
                RegistrySubKey: item.EntryLocation,
                RegistryValueName: entryName,
                RegistryValueData: null,
                FileOriginalPath: originalFilePath ?? item.ExecutablePath,
                FileBackupPath: backupFilePath,
                TaskPath: null,
                TaskEnabled: null,
                ServiceName: null,
                ServiceStartValue: null,
                ServiceDelayedAutoStart: null,
                CreatedAtUtc: DateTimeOffset.UtcNow);

            _backupStore.Save(backup);
            return new StartupToggleResult(true, item with { IsEnabled = false }, backup, null);
        }
        catch (Exception ex)
        {
            return new StartupToggleResult(false, item, null, ex.Message);
        }
    }

    private static string? ResolveStartupFolderPath(string? entryLocation)
    {
        if (string.IsNullOrWhiteSpace(entryLocation))
        {
            return null;
        }

        // Common startup folder paths
        if (entryLocation.Contains("Common Startup", StringComparison.OrdinalIgnoreCase) ||
            entryLocation.Contains("ProgramData", StringComparison.OrdinalIgnoreCase))
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup);
        }

        // User startup folder
        return Environment.GetFolderPath(Environment.SpecialFolder.Startup);
    }

    private StartupToggleResult EnableStartupFile(StartupItem item)
    {
        var backup = _backupStore.Get(item.Id);

        try
        {
            var entryName = Path.GetFileName(item.ExecutablePath ?? backup?.FileOriginalPath);
            var root = ResolveStartupFolderRoot(item.EntryLocation ?? backup?.RegistrySubKey);
            var fileWasRestored = false;
            var fileAlreadyExists = false;

            // Try to restore the file from backup location
            if (backup is not null && !string.IsNullOrWhiteSpace(backup.FileBackupPath) && File.Exists(backup.FileBackupPath))
            {
                var originalPath = backup.FileOriginalPath;
                if (!string.IsNullOrWhiteSpace(originalPath))
                {
                    // Check if file already exists at destination (app re-created the shortcut)
                    if (File.Exists(originalPath))
                    {
                        // File already exists - the application has re-created its startup entry
                        // Delete our obsolete backup file and clean up
                        fileAlreadyExists = true;
                        try
                        {
                            File.Delete(backup.FileBackupPath);
                        }
                        catch
                        {
                            // Non-fatal: backup file cleanup failed
                        }
                    }
                    else
                    {
                        // Ensure the destination directory exists
                        var destDir = Path.GetDirectoryName(originalPath);
                        if (!string.IsNullOrWhiteSpace(destDir))
                        {
                            Directory.CreateDirectory(destDir);
                        }

                        // Move the file back to its original location
                        File.Move(backup.FileBackupPath, originalPath, overwrite: false);
                        fileWasRestored = true;
                    }
                }
            }

            // If file wasn't restored (no backup file), try StartupApproved as fallback
            if (!fileWasRestored && !fileAlreadyExists)
            {
                if (!string.IsNullOrWhiteSpace(entryName) && root is not null)
                {
                    if (!TrySetStartupApprovedState(root, "Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\StartupApproved\\StartupFolder", entryName, enabled: true, out var approvedError))
                    {
                        return new StartupToggleResult(false, item, backup, approvedError);
                    }
                }
            }

            if (backup is not null)
            {
                _backupStore.Remove(item.Id);
            }
            return new StartupToggleResult(true, item with { IsEnabled = true }, backup, null);
        }
        catch (Exception ex)
        {
            return new StartupToggleResult(false, item, backup, ex.Message);
        }
    }

    private StartupToggleResult DisableScheduledTask(StartupItem item)
    {
        if (string.IsNullOrWhiteSpace(item.EntryLocation))
        {
            return new StartupToggleResult(false, item, null, "Task path missing.");
        }

        try
        {
            using var service = new TaskService();
            TaskSchedulerTask? task = service.GetTask(item.EntryLocation);
            if (task is null)
            {
                return new StartupToggleResult(false, item, null, "Task not found.");
            }

            var backup = new StartupEntryBackup(
                item.Id,
                item.SourceKind,
                RegistryRoot: null,
                RegistrySubKey: null,
                RegistryValueName: null,
                RegistryValueData: null,
                FileOriginalPath: null,
                FileBackupPath: null,
                TaskPath: item.EntryLocation,
                TaskEnabled: task.Enabled,
                ServiceName: null,
                ServiceStartValue: null,
                ServiceDelayedAutoStart: null,
                CreatedAtUtc: DateTimeOffset.UtcNow);

            task.Enabled = false;
            _backupStore.Save(backup);
            return new StartupToggleResult(true, item with { IsEnabled = false }, backup, null);
        }
        catch (Exception ex)
        {
            return new StartupToggleResult(false, item, null, ex.Message);
        }
    }

    private StartupToggleResult EnableScheduledTask(StartupItem item)
    {
        if (string.IsNullOrWhiteSpace(item.EntryLocation))
        {
            return new StartupToggleResult(false, item, null, "Task path missing.");
        }

        var backup = _backupStore.Get(item.Id);
        try
        {
            using var service = new TaskService();
            TaskSchedulerTask? task = service.GetTask(item.EntryLocation);
            if (task is null)
            {
                return new StartupToggleResult(false, item, backup, "Task not found.");
            }

            var targetEnabled = backup?.TaskEnabled ?? true;
            task.Enabled = targetEnabled;
            if (backup is not null)
            {
                _backupStore.Remove(item.Id);
            }

            return new StartupToggleResult(true, item with { IsEnabled = targetEnabled }, backup, null);
        }
        catch (Exception ex)
        {
            return new StartupToggleResult(false, item, backup, ex.Message);
        }
    }

    private StartupToggleResult DisableService(StartupItem item)
    {
        var serviceName = ExtractServiceName(item);
        if (string.IsNullOrWhiteSpace(serviceName))
        {
            return new StartupToggleResult(false, item, null, "Service name not available.");
        }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($"SYSTEM\\CurrentControlSet\\Services\\{serviceName}", writable: true);
            if (key is null)
            {
                return new StartupToggleResult(false, item, null, "Service registry key not found.");
            }

            var startValue = Convert.ToInt32(key.GetValue("Start", -1), CultureInfo.InvariantCulture);
            var delayed = Convert.ToInt32(key.GetValue("DelayedAutoStart", 0), CultureInfo.InvariantCulture);

            // Backup recovery options before disabling
            var failureActions = BackupServiceRecoveryOptions(serviceName);

            var backup = new StartupEntryBackup(
                item.Id,
                item.SourceKind,
                RegistryRoot: "HKLM",
                RegistrySubKey: key.Name,
                RegistryValueName: "Start",
                RegistryValueData: $"{startValue}|{delayed}|{failureActions ?? ""}",
                FileOriginalPath: null,
                FileBackupPath: null,
                TaskPath: null,
                TaskEnabled: null,
                ServiceName: serviceName,
                ServiceStartValue: startValue,
                ServiceDelayedAutoStart: delayed,
                CreatedAtUtc: DateTimeOffset.UtcNow);

            // Disable the service
            key.SetValue("Start", 4, RegistryValueKind.DWord);
            key.SetValue("DelayedAutoStart", 0, RegistryValueKind.DWord);

            // Also disable recovery options to prevent automatic restart
            DisableServiceRecoveryOptions(serviceName);

            // Attempt to stop the service if it's running
            TryStopService(serviceName);

            _backupStore.Save(backup);
            return new StartupToggleResult(true, item with { IsEnabled = false }, backup, null);
        }
        catch (Exception ex)
        {
            return new StartupToggleResult(false, item, null, ex.Message);
        }
    }

    private StartupToggleResult EnableService(StartupItem item)
    {
        var serviceName = ExtractServiceName(item);
        if (string.IsNullOrWhiteSpace(serviceName))
        {
            return new StartupToggleResult(false, item, null, "Service name not available.");
        }

        var backup = _backupStore.Get(item.Id);
        var startValue = backup?.ServiceStartValue ?? 2;
        var delayed = backup?.ServiceDelayedAutoStart ?? 0;

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($"SYSTEM\\CurrentControlSet\\Services\\{serviceName}", writable: true);
            if (key is null)
            {
                return new StartupToggleResult(false, item, backup, "Service registry key not found.");
            }

            key.SetValue("Start", startValue, RegistryValueKind.DWord);
            key.SetValue("DelayedAutoStart", delayed, RegistryValueKind.DWord);

            // Restore recovery options if we backed them up
            if (backup is not null && !string.IsNullOrWhiteSpace(backup.RegistryValueData))
            {
                var parts = backup.RegistryValueData.Split('|');
                if (parts.Length >= 3 && !string.IsNullOrWhiteSpace(parts[2]))
                {
                    RestoreServiceRecoveryOptions(serviceName, parts[2]);
                }
            }

            if (backup is not null)
            {
                _backupStore.Remove(item.Id);
            }

            return new StartupToggleResult(true, item with { IsEnabled = startValue != 4 }, backup, null);
        }
        catch (Exception ex)
        {
            return new StartupToggleResult(false, item, backup, ex.Message);
        }
    }

    private static void TryStopService(string serviceName)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "sc.exe",
                    Arguments = $"stop \"{serviceName}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            process.Start();
            process.WaitForExit(5000);
        }
        catch
        {
            // Non-fatal - service may not be running
        }
    }

    private static string? BackupServiceRecoveryOptions(string serviceName)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "sc.exe",
                    Arguments = $"qfailure \"{serviceName}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            process.Start();
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(output));
        }
        catch
        {
            return null;
        }
    }

    private static void DisableServiceRecoveryOptions(string serviceName)
    {
        try
        {
            // Set recovery to "take no action" to prevent automatic restart
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "sc.exe",
                    Arguments = $"failure \"{serviceName}\" reset= 0 actions= \"\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            process.Start();
            process.WaitForExit(5000);
        }
        catch
        {
            // Non-fatal
        }
    }

    private static void RestoreServiceRecoveryOptions(string serviceName, string backupData)
    {
        if (string.IsNullOrWhiteSpace(backupData))
        {
            return;
        }

        try
        {
            // Decode the base64 backup data
            var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(backupData));
            if (string.IsNullOrWhiteSpace(decoded))
            {
                return;
            }

            // Parse the sc qfailure output to extract recovery actions
            // Format: RESET_PERIOD (in seconds), FAILURE_ACTIONS
            var resetPeriod = 0;
            var actions = new List<string>();

            foreach (var line in decoded.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim();

                // Parse RESET_PERIOD
                if (trimmed.StartsWith("RESET_PERIOD", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = trimmed.Split(':');
                    if (parts.Length >= 2)
                    {
                        var valueStr = parts[1].Trim().Split(' ')[0];
                        int.TryParse(valueStr, out resetPeriod);
                    }
                }

                // Parse FAILURE_ACTIONS entries (e.g., "RESTART -- Delay = 60000 milliseconds")
                if (trimmed.Contains("--", StringComparison.Ordinal) && trimmed.Contains("Delay", StringComparison.OrdinalIgnoreCase))
                {
                    var actionType = "";
                    var delay = 0;

                    if (trimmed.StartsWith("RESTART", StringComparison.OrdinalIgnoreCase))
                    {
                        actionType = "restart";
                    }
                    else if (trimmed.StartsWith("RUN PROCESS", StringComparison.OrdinalIgnoreCase))
                    {
                        actionType = "run";
                    }
                    else if (trimmed.StartsWith("REBOOT", StringComparison.OrdinalIgnoreCase))
                    {
                        actionType = "reboot";
                    }
                    else
                    {
                        continue; // Skip "NONE" or unknown actions
                    }

                    // Extract delay value
                    var delayMatch = System.Text.RegularExpressions.Regex.Match(trimmed, @"Delay\s*=\s*(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (delayMatch.Success)
                    {
                        int.TryParse(delayMatch.Groups[1].Value, out delay);
                    }

                    actions.Add($"{actionType}/{delay}");
                }
            }

            if (actions.Count == 0)
            {
                return; // No actions to restore
            }

            // Build the sc failure command
            // Format: sc failure servicename reset= <period> actions= <action1>/<delay1>/<action2>/<delay2>/...
            var actionsArg = string.Join("/", actions);
            var arguments = $"failure \"{serviceName}\" reset= {resetPeriod} actions= {actionsArg}";

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "sc.exe",
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            process.Start();
            process.WaitForExit(5000);
        }
        catch
        {
            // Non-fatal: recovery restoration failed, but service is still enabled
        }
    }

    private static bool TryParseRegistryLocation(string? location, out RegistryKey root, out string subKey)
    {
        root = Registry.CurrentUser;
        subKey = string.Empty;

        if (string.IsNullOrWhiteSpace(location))
        {
            return false;
        }

        var parts = location.Split(new[] { '\\' }, 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return false;
        }

        root = NormalizeRoot(parts[0]);
        subKey = parts[1];
        return true;
    }

    private static RegistryKey NormalizeRoot(string rootName)
    {
        return rootName.ToUpperInvariant() switch
        {
            "HKCU" or "HKEY_CURRENT_USER" => Registry.CurrentUser,
            "HKLM" or "HKEY_LOCAL_MACHINE" => Registry.LocalMachine,
            _ => Registry.CurrentUser
        };
    }

    private static string GetRootName(RegistryKey key)
    {
        if (key == Registry.CurrentUser)
        {
            return "HKCU";
        }

        if (key == Registry.LocalMachine)
        {
            return "HKLM";
        }

        return key.Name;
    }

    private static string ExtractValueName(StartupItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.Id) && item.Id.Contains(':', StringComparison.Ordinal))
        {
            return item.Id[(item.Id.LastIndexOf(':') + 1)..];
        }

        if (!string.IsNullOrWhiteSpace(item.Name))
        {
            return item.Name;
        }

        return "StartupItem";
    }

    private static string ExtractServiceName(StartupItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.Id) && item.Id.StartsWith("svc:", StringComparison.OrdinalIgnoreCase))
        {
            return item.Id[4..];
        }

        if (!string.IsNullOrWhiteSpace(item.Name))
        {
            return item.Name;
        }

        return string.Empty;
    }

    private static bool TryParsePackagedTask(StartupItem item, out string packageFamilyName, out string taskId)
    {
        packageFamilyName = string.Empty;
        taskId = string.Empty;

        if (!string.IsNullOrWhiteSpace(item.Id) && item.Id.StartsWith("appx:", StringComparison.OrdinalIgnoreCase))
        {
            var payload = item.Id[5..];
            var separator = payload.IndexOf('!');
            if (separator > 0 && separator + 1 < payload.Length)
            {
                packageFamilyName = payload[..separator];
                taskId = payload[(separator + 1)..];
            }
        }

        return !string.IsNullOrWhiteSpace(packageFamilyName) && !string.IsNullOrWhiteSpace(taskId);
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

    private static string GetPackagedTaskRegistryPath(string packageFamilyName, string taskId)
    {
        return $"Software\\Classes\\Local Settings\\Software\\Microsoft\\Windows\\CurrentVersion\\AppModel\\SystemAppData\\{packageFamilyName}\\{taskId}";
    }

    private static bool IsEnabledStartupState(int state)
    {
        return state is 2 or 4 or 5;
    }

    private static string SanitizeFileName(string id)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(id.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "startup" : cleaned;
    }

    private static RegistryKey? ResolveStartupFolderRoot(string? entryLocation)
    {
        if (string.IsNullOrWhiteSpace(entryLocation))
        {
            return null;
        }

        var commonStartup = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup);
        if (!string.IsNullOrWhiteSpace(commonStartup) && entryLocation.StartsWith(commonStartup, StringComparison.OrdinalIgnoreCase))
        {
            return Registry.LocalMachine;
        }

        return Registry.CurrentUser;
    }

    private static string? GetStartupApprovedSubKey(StartupItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.EntryLocation) && item.EntryLocation.Contains("RunServices", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var baseName = item.SourceKind == StartupItemSourceKind.RunOnce ? "RunOnce" : "Run";
        var subKey = $"Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\StartupApproved\\{baseName}";

        if (!string.IsNullOrWhiteSpace(item.EntryLocation) && item.EntryLocation.Contains("Wow6432Node", StringComparison.OrdinalIgnoreCase))
        {
            subKey += "32";
        }

        return subKey;
    }

    private static bool TrySetStartupApprovedState(RegistryKey root, string subKey, string entryName, bool enabled, out string? error)
    {
        try
        {
            using var key = root.OpenSubKey(subKey, writable: true) ?? root.CreateSubKey(subKey, writable: true);
            if (key is null)
            {
                error = "Failed to open StartupApproved registry key.";
                return false;
            }

            var data = new byte[12];
            data[0] = enabled ? (byte)2 : (byte)3;
            key.SetValue(entryName, data, RegistryValueKind.Binary);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static void EnsureElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        if (!principal.IsInRole(WindowsBuiltInRole.Administrator))
        {
            throw new InvalidOperationException("Startup control requires administrative privileges.");
        }
    }

    #region Winlogon Entry Control

    private StartupToggleResult DisableWinlogonEntry(StartupItem item)
    {
        if (!TryParseRegistryLocation(item.EntryLocation, out var root, out var subKey))
        {
            return new StartupToggleResult(false, item, null, "Invalid Winlogon registry location.");
        }

        // Winlogon entries are critical - we disable by adding a backup prefix rather than deleting
        // This makes them ineffective but keeps the entry for safe restoration
        try
        {
            // Parse the value name from the source tag (e.g., "HKLM Winlogon (Shell)")
            var valueName = ExtractWinlogonValueName(item);
            if (string.IsNullOrWhiteSpace(valueName))
            {
                return new StartupToggleResult(false, item, null, "Cannot determine Winlogon value name.");
            }

            using var key = root.OpenSubKey(subKey, writable: true);
            if (key is null)
            {
                return new StartupToggleResult(false, item, null, "Winlogon registry key not found.");
            }

            var currentValue = key.GetValue(valueName)?.ToString();
            if (string.IsNullOrWhiteSpace(currentValue))
            {
                return new StartupToggleResult(true, item with { IsEnabled = false }, null, null);
            }

            // For multi-value entries (comma-separated), remove just the specific entry
            var entries = currentValue.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(e => e.Trim())
                .ToList();

            var targetEntry = item.RawCommand ?? item.ExecutablePath;
            var found = false;

            for (var i = entries.Count - 1; i >= 0; i--)
            {
                if (entries[i].Equals(targetEntry, StringComparison.OrdinalIgnoreCase) ||
                    entries[i].Contains(Path.GetFileName(item.ExecutablePath) ?? "", StringComparison.OrdinalIgnoreCase))
                {
                    entries.RemoveAt(i);
                    found = true;
                }
            }

            if (!found)
            {
                return new StartupToggleResult(false, item, null, "Entry not found in Winlogon value.");
            }

            var backup = new StartupEntryBackup(
                item.Id,
                item.SourceKind,
                GetRootName(root),
                subKey,
                valueName,
                currentValue,
                FileOriginalPath: null,
                FileBackupPath: null,
                TaskPath: null,
                TaskEnabled: null,
                ServiceName: null,
                ServiceStartValue: null,
                ServiceDelayedAutoStart: null,
                CreatedAtUtc: DateTimeOffset.UtcNow);

            // Write back the modified value
            var newValue = string.Join(",", entries);
            if (string.IsNullOrWhiteSpace(newValue))
            {
                // Restore default if we're removing the last non-default entry
                newValue = valueName.Equals("Shell", StringComparison.OrdinalIgnoreCase) ? "explorer.exe" :
                           valueName.Equals("Userinit", StringComparison.OrdinalIgnoreCase) ? @"C:\Windows\system32\userinit.exe," :
                           newValue;
            }

            key.SetValue(valueName, newValue);
            _backupStore.Save(backup);

            return new StartupToggleResult(true, item with { IsEnabled = false }, backup, null);
        }
        catch (Exception ex)
        {
            return new StartupToggleResult(false, item, null, ex.Message);
        }
    }

    private StartupToggleResult EnableWinlogonEntry(StartupItem item)
    {
        var backup = _backupStore.Get(item.Id);
        if (backup is null || string.IsNullOrWhiteSpace(backup.RegistryValueData))
        {
            return new StartupToggleResult(false, item, null, "No backup available to restore Winlogon entry.");
        }

        if (!TryParseRegistryLocation(item.EntryLocation, out var root, out var subKey))
        {
            return new StartupToggleResult(false, item, backup, "Invalid Winlogon registry location.");
        }

        try
        {
            using var key = root.OpenSubKey(subKey, writable: true);
            if (key is null)
            {
                return new StartupToggleResult(false, item, backup, "Winlogon registry key not found.");
            }

            key.SetValue(backup.RegistryValueName!, backup.RegistryValueData);
            _backupStore.Remove(item.Id);

            return new StartupToggleResult(true, item with { IsEnabled = true }, backup, null);
        }
        catch (Exception ex)
        {
            return new StartupToggleResult(false, item, backup, ex.Message);
        }
    }

    private static string? ExtractWinlogonValueName(StartupItem item)
    {
        if (item.SourceTag?.Contains("Shell", StringComparison.OrdinalIgnoreCase) == true)
        {
            return "Shell";
        }

        if (item.SourceTag?.Contains("Userinit", StringComparison.OrdinalIgnoreCase) == true)
        {
            return "Userinit";
        }

        if (item.SourceTag?.Contains("Taskman", StringComparison.OrdinalIgnoreCase) == true)
        {
            return "Taskman";
        }

        return null;
    }

    #endregion

    #region Active Setup Control

    private StartupToggleResult DisableActiveSetup(StartupItem item)
    {
        if (!TryParseRegistryLocation(item.EntryLocation, out var root, out var subKey))
        {
            return new StartupToggleResult(false, item, null, "Invalid Active Setup registry location.");
        }

        try
        {
            using var key = root.OpenSubKey(subKey, writable: true);
            if (key is null)
            {
                return new StartupToggleResult(false, item, null, "Active Setup registry key not found.");
            }

            var currentInstalled = key.GetValue("IsInstalled");
            var backup = new StartupEntryBackup(
                item.Id,
                item.SourceKind,
                GetRootName(root),
                subKey,
                "IsInstalled",
                currentInstalled?.ToString() ?? "1",
                FileOriginalPath: null,
                FileBackupPath: null,
                TaskPath: null,
                TaskEnabled: null,
                ServiceName: null,
                ServiceStartValue: null,
                ServiceDelayedAutoStart: null,
                CreatedAtUtc: DateTimeOffset.UtcNow);

            key.SetValue("IsInstalled", 0, RegistryValueKind.DWord);
            _backupStore.Save(backup);

            return new StartupToggleResult(true, item with { IsEnabled = false }, backup, null);
        }
        catch (Exception ex)
        {
            return new StartupToggleResult(false, item, null, ex.Message);
        }
    }

    private StartupToggleResult EnableActiveSetup(StartupItem item)
    {
        var backup = _backupStore.Get(item.Id);

        if (!TryParseRegistryLocation(item.EntryLocation, out var root, out var subKey))
        {
            return new StartupToggleResult(false, item, backup, "Invalid Active Setup registry location.");
        }

        try
        {
            using var key = root.OpenSubKey(subKey, writable: true);
            if (key is null)
            {
                return new StartupToggleResult(false, item, backup, "Active Setup registry key not found.");
            }

            var restoreValue = int.TryParse(backup?.RegistryValueData, out var parsed) ? parsed : 1;
            key.SetValue("IsInstalled", restoreValue, RegistryValueKind.DWord);

            if (backup is not null)
            {
                _backupStore.Remove(item.Id);
            }

            return new StartupToggleResult(true, item with { IsEnabled = true }, backup, null);
        }
        catch (Exception ex)
        {
            return new StartupToggleResult(false, item, backup, ex.Message);
        }
    }

    #endregion

    #region Explorer Run Control

    private StartupToggleResult DisableExplorerRun(StartupItem item)
    {
        if (!TryParseRegistryLocation(item.EntryLocation, out var root, out var subKey))
        {
            return new StartupToggleResult(false, item, null, "Invalid Explorer Run registry location.");
        }

        var valueName = ExtractValueName(item);

        try
        {
            using var key = root.OpenSubKey(subKey, writable: true);
            if (key is null)
            {
                return new StartupToggleResult(false, item, null, "Explorer Run registry key not found.");
            }

            var currentValue = key.GetValue(valueName)?.ToString();
            if (currentValue is null)
            {
                return new StartupToggleResult(true, item with { IsEnabled = false }, null, null);
            }

            var backup = new StartupEntryBackup(
                item.Id,
                item.SourceKind,
                GetRootName(root),
                subKey,
                valueName,
                currentValue,
                FileOriginalPath: null,
                FileBackupPath: null,
                TaskPath: null,
                TaskEnabled: null,
                ServiceName: null,
                ServiceStartValue: null,
                ServiceDelayedAutoStart: null,
                CreatedAtUtc: DateTimeOffset.UtcNow);

            key.DeleteValue(valueName, throwOnMissingValue: false);
            _backupStore.Save(backup);

            return new StartupToggleResult(true, item with { IsEnabled = false }, backup, null);
        }
        catch (Exception ex)
        {
            return new StartupToggleResult(false, item, null, ex.Message);
        }
    }

    private StartupToggleResult EnableExplorerRun(StartupItem item)
    {
        var backup = _backupStore.Get(item.Id);

        if (!TryParseRegistryLocation(item.EntryLocation, out var root, out var subKey))
        {
            return new StartupToggleResult(false, item, backup, "Invalid Explorer Run registry location.");
        }

        try
        {
            using var key = root.OpenSubKey(subKey, writable: true) ?? root.CreateSubKey(subKey, writable: true);
            if (key is null)
            {
                return new StartupToggleResult(false, item, backup, "Failed to open Explorer Run registry key.");
            }

            var data = backup?.RegistryValueData ?? item.RawCommand ?? BuildCommand(item.ExecutablePath, item.Arguments);
            if (string.IsNullOrWhiteSpace(data))
            {
                return new StartupToggleResult(false, item, backup, "No data available to restore.");
            }

            var valueName = backup?.RegistryValueName ?? ExtractValueName(item);
            key.SetValue(valueName, data);

            if (backup is not null)
            {
                _backupStore.Remove(item.Id);
            }

            return new StartupToggleResult(true, item with { IsEnabled = true }, backup, null);
        }
        catch (Exception ex)
        {
            return new StartupToggleResult(false, item, backup, ex.Message);
        }
    }

    #endregion

    #region AppInit_DLLs Control

    private StartupToggleResult DisableAppInitDll(StartupItem item)
    {
        if (!TryParseRegistryLocation(item.EntryLocation, out var root, out var subKey))
        {
            return new StartupToggleResult(false, item, null, "Invalid AppInit_DLLs registry location.");
        }

        try
        {
            using var key = root.OpenSubKey(subKey, writable: true);
            if (key is null)
            {
                return new StartupToggleResult(false, item, null, "AppInit_DLLs registry key not found.");
            }

            var currentDlls = key.GetValue("AppInit_DLLs")?.ToString();
            if (string.IsNullOrWhiteSpace(currentDlls))
            {
                return new StartupToggleResult(true, item with { IsEnabled = false }, null, null);
            }

            // Remove this specific DLL from the list
            var dlls = currentDlls.Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(d => d.Trim().Trim('"'))
                .ToList();

            var targetDll = item.ExecutablePath;
            var found = false;

            for (var i = dlls.Count - 1; i >= 0; i--)
            {
                if (dlls[i].Equals(targetDll, StringComparison.OrdinalIgnoreCase))
                {
                    dlls.RemoveAt(i);
                    found = true;
                }
            }

            if (!found)
            {
                return new StartupToggleResult(false, item, null, "DLL not found in AppInit_DLLs.");
            }

            var backup = new StartupEntryBackup(
                item.Id,
                item.SourceKind,
                GetRootName(root),
                subKey,
                "AppInit_DLLs",
                currentDlls,
                FileOriginalPath: null,
                FileBackupPath: null,
                TaskPath: null,
                TaskEnabled: null,
                ServiceName: null,
                ServiceStartValue: null,
                ServiceDelayedAutoStart: null,
                CreatedAtUtc: DateTimeOffset.UtcNow);

            key.SetValue("AppInit_DLLs", string.Join(" ", dlls));
            _backupStore.Save(backup);

            return new StartupToggleResult(true, item with { IsEnabled = false }, backup, null);
        }
        catch (Exception ex)
        {
            return new StartupToggleResult(false, item, null, ex.Message);
        }
    }

    private StartupToggleResult EnableAppInitDll(StartupItem item)
    {
        var backup = _backupStore.Get(item.Id);
        if (backup is null || string.IsNullOrWhiteSpace(backup.RegistryValueData))
        {
            return new StartupToggleResult(false, item, null, "No backup available to restore AppInit_DLL.");
        }

        if (!TryParseRegistryLocation(item.EntryLocation, out var root, out var subKey))
        {
            return new StartupToggleResult(false, item, backup, "Invalid AppInit_DLLs registry location.");
        }

        try
        {
            using var key = root.OpenSubKey(subKey, writable: true);
            if (key is null)
            {
                return new StartupToggleResult(false, item, backup, "AppInit_DLLs registry key not found.");
            }

            key.SetValue("AppInit_DLLs", backup.RegistryValueData);
            _backupStore.Remove(item.Id);

            return new StartupToggleResult(true, item with { IsEnabled = true }, backup, null);
        }
        catch (Exception ex)
        {
            return new StartupToggleResult(false, item, backup, ex.Message);
        }
    }

    #endregion

    #region IFEO Debugger Control

    private StartupToggleResult DisableIfeoDebugger(StartupItem item)
    {
        if (!TryParseRegistryLocation(item.EntryLocation, out var root, out var subKey))
        {
            return new StartupToggleResult(false, item, null, "Invalid IFEO registry location.");
        }

        try
        {
            using var key = root.OpenSubKey(subKey, writable: true);
            if (key is null)
            {
                return new StartupToggleResult(false, item, null, "IFEO registry key not found.");
            }

            var currentDebugger = key.GetValue("Debugger")?.ToString();
            if (string.IsNullOrWhiteSpace(currentDebugger))
            {
                return new StartupToggleResult(true, item with { IsEnabled = false }, null, null);
            }

            var backup = new StartupEntryBackup(
                item.Id,
                item.SourceKind,
                GetRootName(root),
                subKey,
                "Debugger",
                currentDebugger,
                FileOriginalPath: null,
                FileBackupPath: null,
                TaskPath: null,
                TaskEnabled: null,
                ServiceName: null,
                ServiceStartValue: null,
                ServiceDelayedAutoStart: null,
                CreatedAtUtc: DateTimeOffset.UtcNow);

            key.DeleteValue("Debugger", throwOnMissingValue: false);
            _backupStore.Save(backup);

            return new StartupToggleResult(true, item with { IsEnabled = false }, backup, null);
        }
        catch (Exception ex)
        {
            return new StartupToggleResult(false, item, null, ex.Message);
        }
    }

    private StartupToggleResult EnableIfeoDebugger(StartupItem item)
    {
        var backup = _backupStore.Get(item.Id);
        if (backup is null || string.IsNullOrWhiteSpace(backup.RegistryValueData))
        {
            return new StartupToggleResult(false, item, null, "No backup available to restore IFEO debugger.");
        }

        if (!TryParseRegistryLocation(item.EntryLocation, out var root, out var subKey))
        {
            return new StartupToggleResult(false, item, backup, "Invalid IFEO registry location.");
        }

        try
        {
            using var key = root.OpenSubKey(subKey, writable: true);
            if (key is null)
            {
                return new StartupToggleResult(false, item, backup, "IFEO registry key not found.");
            }

            key.SetValue("Debugger", backup.RegistryValueData);
            _backupStore.Remove(item.Id);

            return new StartupToggleResult(true, item with { IsEnabled = true }, backup, null);
        }
        catch (Exception ex)
        {
            return new StartupToggleResult(false, item, backup, ex.Message);
        }
    }

    #endregion

    #region Shell Extension Control

    private StartupToggleResult DisableShellExtension(StartupItem item)
    {
        if (!TryParseRegistryLocation(item.EntryLocation, out var root, out var subKey))
        {
            return new StartupToggleResult(false, item, null, "Invalid Shell Extension registry location.");
        }

        // The CLSID is stored in RawCommand for shell extensions
        var clsid = item.RawCommand;
        if (string.IsNullOrWhiteSpace(clsid))
        {
            return new StartupToggleResult(false, item, null, "Shell Extension CLSID missing.");
        }

        try
        {
            using var key = root.OpenSubKey(subKey, writable: true);
            if (key is null)
            {
                return new StartupToggleResult(false, item, null, "Shell Extension registry key not found.");
            }

            // Get the current display name value before removing
            var currentValue = key.GetValue(clsid)?.ToString();
            if (currentValue is null)
            {
                // Already removed
                return new StartupToggleResult(true, item with { IsEnabled = false }, null, null);
            }

            var backup = new StartupEntryBackup(
                item.Id,
                item.SourceKind,
                GetRootName(root),
                subKey,
                clsid,
                currentValue,
                FileOriginalPath: null,
                FileBackupPath: null,
                TaskPath: null,
                TaskEnabled: null,
                ServiceName: null,
                ServiceStartValue: null,
                ServiceDelayedAutoStart: null,
                CreatedAtUtc: DateTimeOffset.UtcNow);

            // Remove the CLSID from the Approved list to disable the shell extension
            key.DeleteValue(clsid, throwOnMissingValue: false);
            _backupStore.Save(backup);

            return new StartupToggleResult(true, item with { IsEnabled = false }, backup, null);
        }
        catch (Exception ex)
        {
            return new StartupToggleResult(false, item, null, ex.Message);
        }
    }

    private StartupToggleResult EnableShellExtension(StartupItem item)
    {
        var backup = _backupStore.Get(item.Id);

        if (!TryParseRegistryLocation(item.EntryLocation, out var root, out var subKey))
        {
            return new StartupToggleResult(false, item, backup, "Invalid Shell Extension registry location.");
        }

        // The CLSID is stored in RawCommand for shell extensions
        var clsid = item.RawCommand;
        if (string.IsNullOrWhiteSpace(clsid))
        {
            clsid = backup?.RegistryValueName;
        }

        if (string.IsNullOrWhiteSpace(clsid))
        {
            return new StartupToggleResult(false, item, backup, "Shell Extension CLSID missing.");
        }

        try
        {
            using var key = root.OpenSubKey(subKey, writable: true) ?? root.CreateSubKey(subKey, writable: true);
            if (key is null)
            {
                return new StartupToggleResult(false, item, backup, "Failed to open Shell Extension registry key.");
            }

            // Check if already enabled
            if (key.GetValue(clsid) is not null)
            {
                if (backup is not null)
                {
                    _backupStore.Remove(item.Id);
                }
                return new StartupToggleResult(true, item with { IsEnabled = true }, backup, null);
            }

            // Restore the CLSID to the Approved list
            var displayName = backup?.RegistryValueData ?? item.Name ?? clsid;
            key.SetValue(clsid, displayName, RegistryValueKind.String);

            if (backup is not null)
            {
                _backupStore.Remove(item.Id);
            }

            return new StartupToggleResult(true, item with { IsEnabled = true }, backup, null);
        }
        catch (Exception ex)
        {
            return new StartupToggleResult(false, item, backup, ex.Message);
        }
    }

    #endregion

    #region Browser Helper Object Control

    private StartupToggleResult DisableBrowserHelperObject(StartupItem item)
    {
        if (!TryParseRegistryLocation(item.EntryLocation, out var root, out var subKey))
        {
            return new StartupToggleResult(false, item, null, "Invalid BHO registry location.");
        }

        try
        {
            // BHO entries are subkeys, so we need to delete the entire subkey
            using var parentKey = root.OpenSubKey(Path.GetDirectoryName(subKey)?.Replace('\\', '/').Replace('/', '\\') ?? subKey, writable: true);
            if (parentKey is null)
            {
                return new StartupToggleResult(false, item, null, "BHO parent registry key not found.");
            }

            var clsid = item.RawCommand ?? Path.GetFileName(subKey);
            if (string.IsNullOrWhiteSpace(clsid))
            {
                return new StartupToggleResult(false, item, null, "BHO CLSID missing.");
            }

            // Check if the subkey exists
            using var bhoKey = parentKey.OpenSubKey(clsid, writable: false);
            if (bhoKey is null)
            {
                return new StartupToggleResult(true, item with { IsEnabled = false }, null, null);
            }

            var backup = new StartupEntryBackup(
                item.Id,
                item.SourceKind,
                GetRootName(root),
                subKey,
                clsid,
                item.Name,
                FileOriginalPath: null,
                FileBackupPath: null,
                TaskPath: null,
                TaskEnabled: null,
                ServiceName: null,
                ServiceStartValue: null,
                ServiceDelayedAutoStart: null,
                CreatedAtUtc: DateTimeOffset.UtcNow);

            parentKey.DeleteSubKeyTree(clsid, throwOnMissingSubKey: false);
            _backupStore.Save(backup);

            return new StartupToggleResult(true, item with { IsEnabled = false }, backup, null);
        }
        catch (Exception ex)
        {
            return new StartupToggleResult(false, item, null, ex.Message);
        }
    }

    private StartupToggleResult EnableBrowserHelperObject(StartupItem item)
    {
        var backup = _backupStore.Get(item.Id);

        if (!TryParseRegistryLocation(item.EntryLocation, out var root, out var subKey))
        {
            return new StartupToggleResult(false, item, backup, "Invalid BHO registry location.");
        }

        var clsid = item.RawCommand ?? backup?.RegistryValueName;
        if (string.IsNullOrWhiteSpace(clsid))
        {
            return new StartupToggleResult(false, item, backup, "BHO CLSID missing.");
        }

        try
        {
            var parentPath = Path.GetDirectoryName(subKey)?.Replace('/', '\\');
            if (string.IsNullOrWhiteSpace(parentPath))
            {
                return new StartupToggleResult(false, item, backup, "Invalid BHO registry path.");
            }

            using var parentKey = root.OpenSubKey(parentPath, writable: true) ?? root.CreateSubKey(parentPath, writable: true);
            if (parentKey is null)
            {
                return new StartupToggleResult(false, item, backup, "Failed to open BHO registry key.");
            }

            // Check if already exists
            using var existingKey = parentKey.OpenSubKey(clsid, writable: false);
            if (existingKey is not null)
            {
                if (backup is not null)
                {
                    _backupStore.Remove(item.Id);
                }
                return new StartupToggleResult(true, item with { IsEnabled = true }, backup, null);
            }

            // Re-create the BHO subkey
            using var newKey = parentKey.CreateSubKey(clsid, writable: true);
            if (newKey is null)
            {
                return new StartupToggleResult(false, item, backup, "Failed to create BHO registry key.");
            }

            if (backup is not null)
            {
                _backupStore.Remove(item.Id);
            }

            return new StartupToggleResult(true, item with { IsEnabled = true }, backup, null);
        }
        catch (Exception ex)
        {
            return new StartupToggleResult(false, item, backup, ex.Message);
        }
    }

    #endregion

    #region Protocol Filter Control

    private StartupToggleResult DisableProtocolFilter(StartupItem item)
    {
        if (!TryParseRegistryLocation(item.EntryLocation, out var root, out var subKey))
        {
            return new StartupToggleResult(false, item, null, "Invalid Protocol Filter registry location.");
        }

        try
        {
            using var key = root.OpenSubKey(subKey, writable: true);
            if (key is null)
            {
                return new StartupToggleResult(false, item, null, "Protocol Filter registry key not found.");
            }

            var clsid = key.GetValue("CLSID")?.ToString();
            if (string.IsNullOrWhiteSpace(clsid))
            {
                clsid = item.RawCommand;
            }

            var backup = new StartupEntryBackup(
                item.Id,
                item.SourceKind,
                GetRootName(root),
                subKey,
                "CLSID",
                clsid,
                FileOriginalPath: null,
                FileBackupPath: null,
                TaskPath: null,
                TaskEnabled: null,
                ServiceName: null,
                ServiceStartValue: null,
                ServiceDelayedAutoStart: null,
                CreatedAtUtc: DateTimeOffset.UtcNow);

            // Delete the CLSID value to disable the filter
            key.DeleteValue("CLSID", throwOnMissingValue: false);
            _backupStore.Save(backup);

            return new StartupToggleResult(true, item with { IsEnabled = false }, backup, null);
        }
        catch (Exception ex)
        {
            return new StartupToggleResult(false, item, null, ex.Message);
        }
    }

    private StartupToggleResult EnableProtocolFilter(StartupItem item)
    {
        var backup = _backupStore.Get(item.Id);
        if (backup is null || string.IsNullOrWhiteSpace(backup.RegistryValueData))
        {
            return new StartupToggleResult(false, item, null, "No backup available to restore Protocol Filter.");
        }

        if (!TryParseRegistryLocation(item.EntryLocation, out var root, out var subKey))
        {
            return new StartupToggleResult(false, item, backup, "Invalid Protocol Filter registry location.");
        }

        try
        {
            using var key = root.OpenSubKey(subKey, writable: true) ?? root.CreateSubKey(subKey, writable: true);
            if (key is null)
            {
                return new StartupToggleResult(false, item, backup, "Failed to open Protocol Filter registry key.");
            }

            key.SetValue("CLSID", backup.RegistryValueData, RegistryValueKind.String);
            _backupStore.Remove(item.Id);

            return new StartupToggleResult(true, item with { IsEnabled = true }, backup, null);
        }
        catch (Exception ex)
        {
            return new StartupToggleResult(false, item, backup, ex.Message);
        }
    }

    #endregion

    #region Print Monitor Control

    private StartupToggleResult DisablePrintMonitor(StartupItem item)
    {
        if (!TryParseRegistryLocation(item.EntryLocation, out var root, out var subKey))
        {
            return new StartupToggleResult(false, item, null, "Invalid Print Monitor registry location.");
        }

        try
        {
            using var key = root.OpenSubKey(subKey, writable: true);
            if (key is null)
            {
                return new StartupToggleResult(false, item, null, "Print Monitor registry key not found.");
            }

            var driver = key.GetValue("Driver")?.ToString();
            if (string.IsNullOrWhiteSpace(driver))
            {
                driver = item.RawCommand;
            }

            var backup = new StartupEntryBackup(
                item.Id,
                item.SourceKind,
                GetRootName(root),
                subKey,
                "Driver",
                driver,
                FileOriginalPath: null,
                FileBackupPath: null,
                TaskPath: null,
                TaskEnabled: null,
                ServiceName: null,
                ServiceStartValue: null,
                ServiceDelayedAutoStart: null,
                CreatedAtUtc: DateTimeOffset.UtcNow);

            // Delete the Driver value to disable the print monitor
            key.DeleteValue("Driver", throwOnMissingValue: false);
            _backupStore.Save(backup);

            return new StartupToggleResult(true, item with { IsEnabled = false }, backup, null);
        }
        catch (Exception ex)
        {
            return new StartupToggleResult(false, item, null, ex.Message);
        }
    }

    private StartupToggleResult EnablePrintMonitor(StartupItem item)
    {
        var backup = _backupStore.Get(item.Id);
        if (backup is null || string.IsNullOrWhiteSpace(backup.RegistryValueData))
        {
            return new StartupToggleResult(false, item, null, "No backup available to restore Print Monitor.");
        }

        if (!TryParseRegistryLocation(item.EntryLocation, out var root, out var subKey))
        {
            return new StartupToggleResult(false, item, backup, "Invalid Print Monitor registry location.");
        }

        try
        {
            using var key = root.OpenSubKey(subKey, writable: true);
            if (key is null)
            {
                return new StartupToggleResult(false, item, backup, "Print Monitor registry key not found.");
            }

            key.SetValue("Driver", backup.RegistryValueData, RegistryValueKind.String);
            _backupStore.Remove(item.Id);

            return new StartupToggleResult(true, item with { IsEnabled = true }, backup, null);
        }
        catch (Exception ex)
        {
            return new StartupToggleResult(false, item, backup, ex.Message);
        }
    }

    #endregion

    #region Shell Folder Control

    private StartupToggleResult DisableShellFolder(StartupItem item)
    {
        if (!TryParseRegistryLocation(item.EntryLocation, out var root, out var subKey))
        {
            return new StartupToggleResult(false, item, null, "Invalid Shell Folder registry location.");
        }

        // Parse the value name from the item ID (format: shellfolder:path:valueName)
        var valueName = ExtractShellFolderValueName(item.Id);
        if (string.IsNullOrWhiteSpace(valueName))
        {
            return new StartupToggleResult(false, item, null, "Shell Folder value name missing.");
        }

        try
        {
            using var key = root.OpenSubKey(subKey, writable: true);
            if (key is null)
            {
                return new StartupToggleResult(false, item, null, "Shell Folder registry key not found.");
            }

            var currentValue = key.GetValue(valueName)?.ToString();
            if (string.IsNullOrWhiteSpace(currentValue))
            {
                return new StartupToggleResult(true, item with { IsEnabled = false }, null, null);
            }

            // Get the default value for this shell folder
            var defaultPath = GetDefaultShellFolderPath(valueName);

            var backup = new StartupEntryBackup(
                item.Id,
                item.SourceKind,
                GetRootName(root),
                subKey,
                valueName,
                currentValue,
                FileOriginalPath: null,
                FileBackupPath: null,
                TaskPath: null,
                TaskEnabled: null,
                ServiceName: null,
                ServiceStartValue: null,
                ServiceDelayedAutoStart: null,
                CreatedAtUtc: DateTimeOffset.UtcNow);

            // Restore to default path (or delete if no default)
            if (!string.IsNullOrWhiteSpace(defaultPath))
            {
                key.SetValue(valueName, defaultPath, RegistryValueKind.ExpandString);
            }
            else
            {
                key.DeleteValue(valueName, throwOnMissingValue: false);
            }

            _backupStore.Save(backup);

            return new StartupToggleResult(true, item with { IsEnabled = false }, backup, null);
        }
        catch (Exception ex)
        {
            return new StartupToggleResult(false, item, null, ex.Message);
        }
    }

    private StartupToggleResult EnableShellFolder(StartupItem item)
    {
        var backup = _backupStore.Get(item.Id);
        if (backup is null || string.IsNullOrWhiteSpace(backup.RegistryValueData))
        {
            return new StartupToggleResult(false, item, null, "No backup available to restore Shell Folder.");
        }

        if (!TryParseRegistryLocation(item.EntryLocation, out var root, out var subKey))
        {
            return new StartupToggleResult(false, item, backup, "Invalid Shell Folder registry location.");
        }

        var valueName = backup.RegistryValueName;
        if (string.IsNullOrWhiteSpace(valueName))
        {
            return new StartupToggleResult(false, item, backup, "Shell Folder value name missing.");
        }

        try
        {
            using var key = root.OpenSubKey(subKey, writable: true);
            if (key is null)
            {
                return new StartupToggleResult(false, item, backup, "Shell Folder registry key not found.");
            }

            key.SetValue(valueName, backup.RegistryValueData, RegistryValueKind.ExpandString);
            _backupStore.Remove(item.Id);

            return new StartupToggleResult(true, item with { IsEnabled = true }, backup, null);
        }
        catch (Exception ex)
        {
            return new StartupToggleResult(false, item, backup, ex.Message);
        }
    }

    private static string? ExtractShellFolderValueName(string id)
    {
        // ID format: shellfolder:path:valueName
        var parts = id.Split(':');
        return parts.Length >= 3 ? parts[^1] : null;
    }

    private static string? GetDefaultShellFolderPath(string valueName)
    {
        return valueName switch
        {
            "Startup" => @"%USERPROFILE%\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup",
            "Common Startup" => @"%ProgramData%\Microsoft\Windows\Start Menu\Programs\Startup",
            _ => null
        };
    }

    #endregion
}

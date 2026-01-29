using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.Win32;
using Microsoft.Win32.TaskScheduler;
using TaskSchedulerTask = Microsoft.Win32.TaskScheduler.Task;
using SystemTasks = System.Threading.Tasks;

namespace TidyWindow.Core.Startup;

/// <summary>
/// Enumerates every startup source (Run/RunOnce, Startup folders, logon tasks, autostart services) into a unified model with signing and impact hints.
/// </summary>
public sealed class StartupInventoryService
{
    public SystemTasks.Task<StartupInventorySnapshot> GetInventoryAsync(StartupInventoryOptions? options = null, CancellationToken cancellationToken = default)
    {
        var effectiveOptions = options ?? StartupInventoryOptions.Default;

        // Run the inventory on a dedicated STA thread so UI threads stay responsive and COM shortcut resolution keeps working.
        var tcs = new SystemTasks.TaskCompletionSource<StartupInventorySnapshot>(SystemTasks.TaskCreationOptions.RunContinuationsAsynchronously);

        var thread = new Thread(() =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var snapshot = BuildSnapshot(effectiveOptions, cancellationToken);
                tcs.TrySetResult(snapshot);
            }
            catch (OperationCanceledException oce)
            {
                tcs.TrySetCanceled(oce.CancellationToken);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        })
        {
            IsBackground = true
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        if (cancellationToken.CanBeCanceled)
        {
            cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
        }

        return tcs.Task;
    }

    private static StartupInventorySnapshot BuildSnapshot(StartupInventoryOptions effectiveOptions, CancellationToken cancellationToken)
    {
        var items = new List<StartupItem>();
        var warnings = new List<string>();

        ExecuteSafe(() => EnumerateRunKeys(effectiveOptions, items, warnings, cancellationToken), warnings, "Registry Run keys");
        ExecuteSafe(() => EnumerateStartupFolders(effectiveOptions, items, warnings, cancellationToken), warnings, "Startup folders");
        ExecuteSafe(() => EnumerateLogonTasks(effectiveOptions, items, warnings, cancellationToken), warnings, "Logon tasks");
        ExecuteSafe(() => EnumerateAutostartServices(effectiveOptions, items, warnings, cancellationToken), warnings, "Autostart services");
        ExecuteSafe(() => EnumeratePackagedStartupTasks(effectiveOptions, items, warnings, cancellationToken), warnings, "Packaged startup tasks");
        ExecuteSafe(() => AppendStartupApprovedOrphans(effectiveOptions, items, warnings, cancellationToken), warnings, "StartupApproved orphans");

        // Additional hidden/legacy startup locations
        ExecuteSafe(() => EnumerateWinlogonEntries(effectiveOptions, items, warnings, cancellationToken), warnings, "Winlogon entries");
        ExecuteSafe(() => EnumerateActiveSetup(effectiveOptions, items, warnings, cancellationToken), warnings, "Active Setup");
        ExecuteSafe(() => EnumerateShellFolders(effectiveOptions, items, warnings, cancellationToken), warnings, "Shell folders");
        ExecuteSafe(() => EnumerateExplorerRun(effectiveOptions, items, warnings, cancellationToken), warnings, "Explorer Run");
        ExecuteSafe(() => EnumerateAppInitDlls(effectiveOptions, items, warnings, cancellationToken), warnings, "AppInit_DLLs");
        ExecuteSafe(() => EnumerateImageFileExecutionOptions(effectiveOptions, items, warnings, cancellationToken), warnings, "Image File Execution Options");
        ExecuteSafe(() => EnumerateBootExecute(effectiveOptions, items, warnings, cancellationToken), warnings, "Boot Execute");

        AppendDelayWarnings(items, warnings);

        return new StartupInventorySnapshot(items, warnings, DateTimeOffset.UtcNow, warnings.Count > 0);
    }

    private static void ExecuteSafe(System.Action action, List<string> warnings, string context)
    {
        try
        {
            action();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            warnings.Add($"{context} enumeration failed: {ex.Message}");
        }
    }

    private static void EnumerateRunKeys(StartupInventoryOptions options, List<StartupItem> items, List<string> warnings, CancellationToken cancellationToken)
    {
        if (!options.IncludeRunKeys && !options.IncludeRunOnce)
        {
            return;
        }

        if (options.IncludeRunKeys)
        {
            EnumerateRunKey(Registry.CurrentUser, "Software\\Microsoft\\Windows\\CurrentVersion\\Run", StartupItemSourceKind.RunKey, "HKCU Run", isMachineScope: false, preferWow: false, items, warnings, cancellationToken);
            EnumerateRunKey(Registry.CurrentUser, "Software\\Wow6432Node\\Microsoft\\Windows\\CurrentVersion\\Run", StartupItemSourceKind.RunKey, "HKCU Run (32-bit)", isMachineScope: false, preferWow: true, items, warnings, cancellationToken);
            EnumerateRunKey(Registry.LocalMachine, "Software\\Microsoft\\Windows\\CurrentVersion\\Run", StartupItemSourceKind.RunKey, "HKLM Run", isMachineScope: true, preferWow: false, items, warnings, cancellationToken);
            EnumerateRunKey(Registry.LocalMachine, "Software\\Wow6432Node\\Microsoft\\Windows\\CurrentVersion\\Run", StartupItemSourceKind.RunKey, "HKLM Run (32-bit)", isMachineScope: true, preferWow: true, items, warnings, cancellationToken);
            EnumerateRunKey(Registry.CurrentUser, "Software\\Microsoft\\Windows\\CurrentVersion\\Policies\\Explorer\\Run", StartupItemSourceKind.RunKey, "HKCU Policies Run", isMachineScope: false, preferWow: false, items, warnings, cancellationToken);
            EnumerateRunKey(Registry.LocalMachine, "Software\\Microsoft\\Windows\\CurrentVersion\\Policies\\Explorer\\Run", StartupItemSourceKind.RunKey, "HKLM Policies Run", isMachineScope: true, preferWow: false, items, warnings, cancellationToken);
            EnumerateRunKey(Registry.CurrentUser, "Software\\Microsoft\\Windows\\CurrentVersion\\RunServices", StartupItemSourceKind.RunKey, "HKCU RunServices", isMachineScope: false, preferWow: false, items, warnings, cancellationToken);
            EnumerateRunKey(Registry.LocalMachine, "Software\\Microsoft\\Windows\\CurrentVersion\\RunServices", StartupItemSourceKind.RunKey, "HKLM RunServices", isMachineScope: true, preferWow: false, items, warnings, cancellationToken);
            EnumerateRunKey(Registry.LocalMachine, "Software\\Wow6432Node\\Microsoft\\Windows\\CurrentVersion\\RunServices", StartupItemSourceKind.RunKey, "HKLM RunServices (32-bit)", isMachineScope: true, preferWow: true, items, warnings, cancellationToken);
        }

        if (options.IncludeRunOnce)
        {
            EnumerateRunKey(Registry.CurrentUser, "Software\\Microsoft\\Windows\\CurrentVersion\\RunOnce", StartupItemSourceKind.RunOnce, "HKCU RunOnce", isMachineScope: false, preferWow: false, items, warnings, cancellationToken);
            EnumerateRunKey(Registry.CurrentUser, "Software\\Wow6432Node\\Microsoft\\Windows\\CurrentVersion\\RunOnce", StartupItemSourceKind.RunOnce, "HKCU RunOnce (32-bit)", isMachineScope: false, preferWow: true, items, warnings, cancellationToken);
            EnumerateRunKey(Registry.LocalMachine, "Software\\Microsoft\\Windows\\CurrentVersion\\RunOnce", StartupItemSourceKind.RunOnce, "HKLM RunOnce", isMachineScope: true, preferWow: false, items, warnings, cancellationToken);
            EnumerateRunKey(Registry.LocalMachine, "Software\\Wow6432Node\\Microsoft\\Windows\\CurrentVersion\\RunOnce", StartupItemSourceKind.RunOnce, "HKLM RunOnce (32-bit)", isMachineScope: true, preferWow: true, items, warnings, cancellationToken);
            EnumerateRunKey(Registry.CurrentUser, "Software\\Microsoft\\Windows\\CurrentVersion\\RunServicesOnce", StartupItemSourceKind.RunOnce, "HKCU RunServicesOnce", isMachineScope: false, preferWow: false, items, warnings, cancellationToken);
            EnumerateRunKey(Registry.LocalMachine, "Software\\Microsoft\\Windows\\CurrentVersion\\RunServicesOnce", StartupItemSourceKind.RunOnce, "HKLM RunServicesOnce", isMachineScope: true, preferWow: false, items, warnings, cancellationToken);
            EnumerateRunKey(Registry.LocalMachine, "Software\\Wow6432Node\\Microsoft\\Windows\\CurrentVersion\\RunServicesOnce", StartupItemSourceKind.RunOnce, "HKLM RunServicesOnce (32-bit)", isMachineScope: true, preferWow: true, items, warnings, cancellationToken);
        }
    }

    private static void AppendStartupApprovedOrphans(StartupInventoryOptions options, List<StartupItem> items, List<string> warnings, CancellationToken cancellationToken)
    {
        if (!options.IncludeStartupApprovedOrphans)
        {
            return;
        }

        var existingIds = new HashSet<string>(items.Select(static i => i.Id), StringComparer.OrdinalIgnoreCase);

        // Run / RunOnce (user + machine, 32-bit variants)
        AppendRunApprovedOrphans(Registry.CurrentUser, "HKCU Run", "Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\StartupApproved\\Run", StartupItemSourceKind.RunKey, isMachineScope: false, existingIds, items, warnings, cancellationToken);
        AppendRunApprovedOrphans(Registry.CurrentUser, "HKCU Run (32-bit)", "Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\StartupApproved\\Run32", StartupItemSourceKind.RunKey, isMachineScope: false, existingIds, items, warnings, cancellationToken);
        AppendRunApprovedOrphans(Registry.LocalMachine, "HKLM Run", "Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\StartupApproved\\Run", StartupItemSourceKind.RunKey, isMachineScope: true, existingIds, items, warnings, cancellationToken);
        AppendRunApprovedOrphans(Registry.LocalMachine, "HKLM Run (32-bit)", "Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\StartupApproved\\Run32", StartupItemSourceKind.RunKey, isMachineScope: true, existingIds, items, warnings, cancellationToken);
        AppendRunApprovedOrphans(Registry.CurrentUser, "HKCU RunOnce", "Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\StartupApproved\\RunOnce", StartupItemSourceKind.RunOnce, isMachineScope: false, existingIds, items, warnings, cancellationToken);
        AppendRunApprovedOrphans(Registry.CurrentUser, "HKCU RunOnce (32-bit)", "Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\StartupApproved\\RunOnce32", StartupItemSourceKind.RunOnce, isMachineScope: false, existingIds, items, warnings, cancellationToken);
        AppendRunApprovedOrphans(Registry.LocalMachine, "HKLM RunOnce", "Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\StartupApproved\\RunOnce", StartupItemSourceKind.RunOnce, isMachineScope: true, existingIds, items, warnings, cancellationToken);
        AppendRunApprovedOrphans(Registry.LocalMachine, "HKLM RunOnce (32-bit)", "Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\StartupApproved\\RunOnce32", StartupItemSourceKind.RunOnce, isMachineScope: true, existingIds, items, warnings, cancellationToken);

        // Startup folders (user + common)
        AppendStartupFolderApprovedOrphans(isMachineScope: false, existingIds, items, warnings, cancellationToken);
        AppendStartupFolderApprovedOrphans(isMachineScope: true, existingIds, items, warnings, cancellationToken);
    }

    private static void AppendRunApprovedOrphans(RegistryKey root, string sourceTag, string subKey, StartupItemSourceKind kind, bool isMachineScope, HashSet<string> existingIds, List<StartupItem> items, List<string> warnings, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            using var key = root.OpenSubKey(subKey, writable: false);
            if (key is null)
            {
                return;
            }

            foreach (var entryName in key.GetValueNames())
            {
                cancellationToken.ThrowIfCancellationRequested();

                var approved = ResolveStartupApproved(root, subKey, entryName, preferWow: false);
                if (approved is null)
                {
                    continue;
                }

                var id = $"run:{sourceTag}:{entryName}";
                if (existingIds.Contains(id))
                {
                    continue;
                }

                items.Add(new StartupItem(
                    id,
                    string.IsNullOrWhiteSpace(entryName) ? sourceTag : entryName,
                    ExecutablePath: string.Empty,
                    kind,
                    sourceTag,
                    Arguments: null,
                    RawCommand: null,
                    IsEnabled: approved.Value,
                    EntryLocation: $"{GetRootName(root)}\\{InferRunLocationFromApproved(subKey)}",
                    Publisher: null,
                    SignatureStatus: StartupSignatureStatus.Unknown,
                    Impact: ClassifyImpact(kind, isMachineScope, isDelayed: false, fileSizeBytes: null),
                    FileSizeBytes: null,
                    LastModifiedUtc: null,
                    UserContext: isMachineScope ? "Machine" : "CurrentUser"));

                existingIds.Add(id);
            }
        }
        catch (Exception ex)
        {
            warnings.Add($"StartupApproved scan failed for {subKey}: {ex.Message}");
        }
    }

    private static string InferRunLocationFromApproved(string approvedSubKey)
    {
        if (approvedSubKey.Contains("RunOnce", StringComparison.OrdinalIgnoreCase))
        {
            return approvedSubKey.Contains("RunOnce32", StringComparison.OrdinalIgnoreCase)
                ? "Software\\Wow6432Node\\Microsoft\\Windows\\CurrentVersion\\RunOnce"
                : "Software\\Microsoft\\Windows\\CurrentVersion\\RunOnce";
        }

        return approvedSubKey.Contains("Run32", StringComparison.OrdinalIgnoreCase)
            ? "Software\\Wow6432Node\\Microsoft\\Windows\\CurrentVersion\\Run"
            : "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
    }

    private static void AppendStartupFolderApprovedOrphans(bool isMachineScope, HashSet<string> existingIds, List<StartupItem> items, List<string> warnings, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var root = isMachineScope ? Registry.LocalMachine : Registry.CurrentUser;
            using var key = root.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\StartupApproved\\StartupFolder", writable: false);
            if (key is null)
            {
                return;
            }

            var startupPath = Environment.GetFolderPath(isMachineScope ? Environment.SpecialFolder.CommonStartup : Environment.SpecialFolder.Startup);
            var sourceTag = isMachineScope ? "Common Startup" : "Startup Folder";

            foreach (var entryName in key.GetValueNames())
            {
                cancellationToken.ThrowIfCancellationRequested();

                var approved = ResolveStartupApproved(root, "Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\StartupApproved\\StartupFolder", entryName, preferWow: false);
                if (approved is null)
                {
                    continue;
                }

                var id = $"startup:{sourceTag}:{entryName}";
                if (existingIds.Contains(id))
                {
                    continue;
                }

                items.Add(new StartupItem(
                    id,
                    string.IsNullOrWhiteSpace(entryName) ? sourceTag : entryName,
                    ExecutablePath: string.Empty,
                    StartupItemSourceKind.StartupFolder,
                    sourceTag,
                    Arguments: null,
                    RawCommand: null,
                    IsEnabled: approved.Value,
                    EntryLocation: startupPath,
                    Publisher: null,
                    SignatureStatus: StartupSignatureStatus.Unknown,
                    Impact: StartupImpact.Low,
                    FileSizeBytes: null,
                    LastModifiedUtc: null,
                    UserContext: isMachineScope ? "Machine" : "CurrentUser"));

                existingIds.Add(id);
            }
        }
        catch (Exception ex)
        {
            warnings.Add($"StartupApproved scan failed for StartupFolder: {ex.Message}");
        }
    }

    private static void EnumerateRunKey(RegistryKey root, string subKey, StartupItemSourceKind kind, string sourceTag, bool isMachineScope, bool preferWow, List<StartupItem> items, List<string> warnings, CancellationToken cancellationToken)
    {
        using var key = root.OpenSubKey(subKey, writable: false);
        if (key is null)
        {
            return;
        }

        foreach (var valueName in key.GetValueNames())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var raw = key.GetValue(valueName)?.ToString();
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var (exe, args) = ParseCommand(raw);
            if (string.IsNullOrWhiteSpace(exe))
            {
                continue;
            }

            var metadata = InspectFile(exe);
            var name = string.IsNullOrWhiteSpace(valueName) ? Path.GetFileName(exe) ?? sourceTag : valueName;
            var id = $"run:{sourceTag}:{valueName}";
            var approved = ResolveStartupApproved(root, kind == StartupItemSourceKind.RunOnce
                ? "Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\StartupApproved\\RunOnce"
                : "Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\StartupApproved\\Run",
                valueName,
                preferWow);
            var isEnabled = approved ?? true;
            items.Add(new StartupItem(
                id,
                name,
                exe,
                kind,
                sourceTag,
                args,
                raw,
                isEnabled,
                $"{GetRootName(root)}\\{subKey}",
                metadata.Publisher,
                metadata.SignatureStatus,
                ClassifyImpact(kind, isMachineScope, isDelayed: false, metadata.FileSizeBytes),
                metadata.FileSizeBytes,
                metadata.LastWriteTimeUtc,
                isMachineScope ? "Machine" : "CurrentUser"));
        }
    }

    private static void EnumerateStartupFolders(StartupInventoryOptions options, List<StartupItem> items, List<string> warnings, CancellationToken cancellationToken)
    {
        if (!options.IncludeStartupFolders)
        {
            return;
        }

        EnumerateStartupFolder(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "Startup Folder", isMachineScope: false, items, warnings, cancellationToken);
        EnumerateStartupFolder(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), "Common Startup", isMachineScope: true, items, warnings, cancellationToken);
    }

    private static void EnumerateStartupFolder(string? folderPath, string sourceTag, bool isMachineScope, List<StartupItem> items, List<string> warnings, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(folderPath, "*", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var extension = Path.GetExtension(file);
            if (!string.Equals(extension, ".lnk", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(extension, ".exe", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var rawCommand = file;
            string executable;
            string? arguments;

            if (string.Equals(extension, ".lnk", StringComparison.OrdinalIgnoreCase))
            {
                (executable, arguments) = ResolveShortcut(file);
                if (string.IsNullOrWhiteSpace(executable))
                {
                    warnings.Add($"Shortcut target missing for {file}.");
                    continue;
                }
            }
            else
            {
                executable = file;
                arguments = null;
            }

            var metadata = InspectFile(executable);
            var id = $"startup:{sourceTag}:{Path.GetFileName(file)}";
            var name = Path.GetFileName(executable);
            var approved = ResolveStartupApproved(isMachineScope ? Registry.LocalMachine : Registry.CurrentUser, "Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\StartupApproved\\StartupFolder", Path.GetFileName(file));
            var isEnabled = approved ?? true;
            items.Add(new StartupItem(
                id,
                string.IsNullOrWhiteSpace(name) ? sourceTag : name!,
                executable,
                StartupItemSourceKind.StartupFolder,
                sourceTag,
                arguments,
                rawCommand,
                isEnabled,
                folderPath,
                metadata.Publisher,
                metadata.SignatureStatus,
                ClassifyImpact(StartupItemSourceKind.StartupFolder, isMachineScope, isDelayed: false, metadata.FileSizeBytes),
                metadata.FileSizeBytes,
                metadata.LastWriteTimeUtc,
                isMachineScope ? "Machine" : "CurrentUser"));
        }
    }

    private static void EnumerateLogonTasks(StartupInventoryOptions options, List<StartupItem> items, List<string> warnings, CancellationToken cancellationToken)
    {
        if (!options.IncludeScheduledTasks)
        {
            return;
        }

        using var service = new TaskService();
        foreach (TaskSchedulerTask task in service.AllTasks)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!HasLogonTrigger(task))
            {
                continue;
            }

            if (!options.IncludeDisabled && !task.Enabled)
            {
                continue;
            }

            var execActions = task.Definition.Actions.OfType<ExecAction>().ToArray();
            if (execActions.Length == 0)
            {
                continue;
            }

            for (var i = 0; i < execActions.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var action = execActions[i];
                var path = Environment.ExpandEnvironmentVariables(action.Path ?? string.Empty);
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                var arguments = string.IsNullOrWhiteSpace(action.Arguments) ? null : action.Arguments.Trim();
                var metadata = InspectFile(path);
                var id = $"task:{task.Path}#{i}";
                var name = string.IsNullOrWhiteSpace(task.Name) ? Path.GetFileName(path) ?? task.Path : task.Name;
                items.Add(new StartupItem(
                    id,
                    name,
                    path,
                    StartupItemSourceKind.ScheduledTask,
                    "Task Scheduler (Logon)",
                    arguments,
                    BuildExecCommand(path, arguments),
                    task.Enabled,
                    task.Path,
                    metadata.Publisher,
                    metadata.SignatureStatus,
                    ClassifyImpact(StartupItemSourceKind.ScheduledTask, isMachineScope: true, isDelayed: false, metadata.FileSizeBytes),
                    metadata.FileSizeBytes,
                    metadata.LastWriteTimeUtc,
                    task.Definition.Principal.UserId));
            }
        }
    }

    private static void EnumerateAutostartServices(StartupInventoryOptions options, List<StartupItem> items, List<string> warnings, CancellationToken cancellationToken)
    {
        if (!options.IncludeServices)
        {
            return;
        }

        using var servicesRoot = Registry.LocalMachine.OpenSubKey(@"SYSTEM\\CurrentControlSet\\Services", writable: false);
        if (servicesRoot is null)
        {
            return;
        }

        foreach (var serviceName in servicesRoot.GetSubKeyNames())
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var key = servicesRoot.OpenSubKey(serviceName, writable: false);
            if (key is null)
            {
                continue;
            }

            var startValue = Convert.ToInt32(key.GetValue("Start", -1));
            if (startValue != 2) // Automatic start only.
            {
                continue;
            }

            var delayed = Convert.ToInt32(key.GetValue("DelayedAutoStart", 0)) != 0;
            var imagePath = key.GetValue("ImagePath")?.ToString();
            var (exe, args) = ParseCommand(imagePath);
            if (string.IsNullOrWhiteSpace(exe))
            {
                continue;
            }

            var metadata = InspectFile(exe);
            var displayName = key.GetValue("DisplayName")?.ToString();
            var description = key.GetValue("Description")?.ToString();
            var objectName = key.GetValue("ObjectName")?.ToString();
            var tag = delayed ? "Service (Automatic, Delayed)" : "Service (Automatic)";
            var id = $"svc:{serviceName}";
            var name = string.IsNullOrWhiteSpace(displayName) ? serviceName : displayName!.Trim();
            items.Add(new StartupItem(
                id,
                name,
                exe,
                StartupItemSourceKind.Service,
                tag,
                args,
                imagePath,
                startValue == 2,
                $"HKLM\\SYSTEM\\CurrentControlSet\\Services\\{serviceName}",
                metadata.Publisher,
                metadata.SignatureStatus,
                ClassifyImpact(StartupItemSourceKind.Service, isMachineScope: true, isDelayed: delayed, metadata.FileSizeBytes),
                metadata.FileSizeBytes,
                metadata.LastWriteTimeUtc,
                objectName ?? "LocalSystem"));
        }
    }

    private static void EnumeratePackagedStartupTasks(StartupInventoryOptions options, List<StartupItem> items, List<string> warnings, CancellationToken cancellationToken)
    {
        if (!options.IncludePackagedApps)
        {
            return;
        }

        var packageInfos = CollectPackageManagerPackages(warnings);
        var familyNames = new HashSet<string>(CollectPackageFamilyNames(warnings), StringComparer.OrdinalIgnoreCase);
        foreach (var familyName in packageInfos.Keys)
        {
            familyNames.Add(familyName);
        }

        foreach (var familyName in familyNames)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                string? packageRoot = null;
                string? packageDisplayName = null;

                if (packageInfos.TryGetValue(familyName, out var info))
                {
                    packageRoot = info.InstalledLocation;
                    packageDisplayName = info.DisplayName;
                }

                if (string.IsNullOrWhiteSpace(packageRoot) || !Directory.Exists(packageRoot))
                {
                    if (!TryResolvePackageInfo(familyName, out packageRoot, out var registryDisplayName))
                    {
                        warnings.Add($"Skipped packaged app {familyName}: manifest location not found");
                        continue;
                    }

                    packageDisplayName ??= registryDisplayName;
                }

                var tasks = ParsePackagedStartupTasks(packageRoot, packageDisplayName, familyName, warnings);
                foreach (var task in tasks)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var state = GetPackagedStartupState(familyName, task.TaskId);
                    var isEnabled = state is null ? task.EnabledByManifest : IsEnabledStartupState(state.Value);
                    var metadata = InspectFile(task.ExecutablePath);
                    var id = $"appx:{familyName}!{task.TaskId}";
                    var name = string.IsNullOrWhiteSpace(task.DisplayName) ? (packageDisplayName ?? task.TaskId) : task.DisplayName!;
                    var location = $"HKCU\\Software\\Classes\\Local Settings\\Software\\Microsoft\\Windows\\CurrentVersion\\AppModel\\SystemAppData\\{familyName}\\{task.TaskId}";

                    items.Add(new StartupItem(
                        id,
                        name,
                        task.ExecutablePath,
                        StartupItemSourceKind.PackagedTask,
                        "Packaged Startup Task",
                        task.Arguments,
                        BuildExecCommand(task.ExecutablePath, task.Arguments),
                        isEnabled,
                        location,
                        metadata.Publisher,
                        metadata.SignatureStatus,
                        ClassifyImpact(StartupItemSourceKind.PackagedTask, isMachineScope: false, isDelayed: false, metadata.FileSizeBytes),
                        metadata.FileSizeBytes,
                        metadata.LastWriteTimeUtc,
                        "CurrentUser"));
                }
            }
            catch (Exception ex)
            {
                warnings.Add($"Packaged startup task scan failed for {familyName}: {ex.Message}");
            }
        }
    }

    private static void AppendDelayWarnings(IReadOnlyCollection<StartupItem> items, List<string> warnings)
    {
        try
        {
            var store = new StartupDelayPlanStore();
            var plans = store.GetAll();
            foreach (var plan in plans)
            {
                var selfHealed = items.Any(i => string.Equals(i.Id, plan.Id, StringComparison.OrdinalIgnoreCase));
                if (selfHealed)
                {
                    warnings.Add($"Delayed entry '{plan.Id}' was re-added by its installer; consider delaying it again.");
                }

                if (!string.IsNullOrWhiteSpace(plan.ReplacementTaskPath))
                {
                    var replacementPresent = items.Any(i => i.SourceKind == StartupItemSourceKind.ScheduledTask && string.Equals(i.EntryLocation, plan.ReplacementTaskPath, StringComparison.OrdinalIgnoreCase));
                    if (!replacementPresent)
                    {
                        warnings.Add($"Delayed task missing for '{plan.Id}'. The deferred launch may not run.");
                    }
                }
            }
        }
        catch
        {
            // Non-fatal: warnings are advisory.
        }
    }

    private static IReadOnlyCollection<string> CollectPackageFamilyNames(List<string> warnings)
    {
        var families = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var systemAppDataRoot = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData", writable: false);
            if (systemAppDataRoot is not null)
            {
                foreach (var familyName in systemAppDataRoot.GetSubKeyNames())
                {
                    if (!string.IsNullOrWhiteSpace(familyName))
                    {
                        families.Add(familyName);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            warnings.Add($"Failed to enumerate packaged startup state: {ex.Message}");
        }

        try
        {
            using var packagesRoot = Registry.ClassesRoot.OpenSubKey("Local Settings\\Software\\Microsoft\\Windows\\CurrentVersion\\AppModel\\Repository\\Packages", writable: false);
            if (packagesRoot is not null)
            {
                foreach (var packageFullName in packagesRoot.GetSubKeyNames())
                {
                    var familyName = BuildPackageFamilyNameFromFullName(packageFullName);
                    if (!string.IsNullOrWhiteSpace(familyName))
                    {
                        families.Add(familyName);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            warnings.Add($"Failed to enumerate AppX packages: {ex.Message}");
        }

        return families;
    }

    private static Dictionary<string, PackageManagerPackageInfo> CollectPackageManagerPackages(List<string> warnings)
    {
        var packages = new Dictionary<string, PackageManagerPackageInfo>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var pmType = Type.GetType("Windows.Management.Deployment.PackageManager, Windows, ContentType=WindowsRuntime");
            if (pmType is null)
            {
                warnings.Add("PackageManager type not available; packaged apps may be incomplete");
                return packages;
            }

            var pm = Activator.CreateInstance(pmType);
            if (pm is null)
            {
                warnings.Add("PackageManager could not be created; packaged apps may be incomplete");
                return packages;
            }

            var findMethod = pmType.GetMethod("FindPackagesForUser", new[] { typeof(string) });
            if (findMethod is null)
            {
                warnings.Add("PackageManager.FindPackagesForUser not found; packaged apps may be incomplete");
                return packages;
            }

            var result = findMethod.Invoke(pm, new object?[] { string.Empty }) as System.Collections.IEnumerable;
            if (result is null)
            {
                warnings.Add("PackageManager returned null package list; packaged apps may be incomplete");
                return packages;
            }

            foreach (var pkg in result)
            {
                if (pkg is null)
                {
                    continue;
                }

                var packageType = pkg.GetType();
                var idVal = packageType.GetProperty("Id")?.GetValue(pkg);
                var family = idVal?.GetType().GetProperty("FamilyName")?.GetValue(idVal) as string;
                if (string.IsNullOrWhiteSpace(family))
                {
                    continue;
                }

                string? installedPath = null;
                string? displayName = null;

                var installedLocation = packageType.GetProperty("InstalledLocation")?.GetValue(pkg);
                if (installedLocation is not null)
                {
                    installedPath = installedLocation.GetType().GetProperty("Path")?.GetValue(installedLocation) as string;
                }

                displayName = packageType.GetProperty("DisplayName")?.GetValue(pkg) as string;

                packages[family] = new PackageManagerPackageInfo(family, installedPath, displayName);
            }
        }
        catch (Exception ex)
        {
            warnings.Add($"PackageManager scan failed: {ex.Message}");
        }

        return packages;
    }

    private static bool TryResolvePackageInfo(string packageFamilyName, out string packageRoot, out string? packageDisplayName)
    {
        packageRoot = string.Empty;
        packageDisplayName = null;

        var packagesRoot = Registry.ClassesRoot.OpenSubKey("Local Settings\\Software\\Microsoft\\Windows\\CurrentVersion\\AppModel\\Repository\\Packages", writable: false);
        if (packagesRoot is null)
        {
            return false;
        }

        var (familyName, publisherId) = SplitPackageFamilyName(packageFamilyName);
        if (string.IsNullOrWhiteSpace(familyName) || string.IsNullOrWhiteSpace(publisherId))
        {
            return false;
        }

        string? selectedKeyName = null;
        Version? selectedVersion = null;

        foreach (var candidate in packagesRoot.GetSubKeyNames())
        {
            if (!candidate.StartsWith(familyName + "_", StringComparison.OrdinalIgnoreCase) || !candidate.EndsWith("__" + publisherId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var versionText = ExtractVersion(candidate, familyName.Length + 1);
            if (versionText is null || !Version.TryParse(versionText, out var version))
            {
                continue;
            }

            if (selectedVersion is null || version > selectedVersion)
            {
                selectedVersion = version;
                selectedKeyName = candidate;
            }
        }

        if (selectedKeyName is null)
        {
            return false;
        }

        using var packageKey = packagesRoot.OpenSubKey(selectedKeyName, writable: false);
        if (packageKey is null)
        {
            return false;
        }

        packageRoot = packageKey.GetValue("PackageRootFolder")?.ToString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(packageRoot) || !Directory.Exists(packageRoot))
        {
            return false;
        }

        packageDisplayName = packageKey.GetValue("DisplayName")?.ToString();
        return true;
    }

    private static IReadOnlyList<PackagedStartupTaskDefinition> ParsePackagedStartupTasks(string packageRoot, string? packageDisplayName, string packageFamilyName, List<string> warnings)
    {
        var manifestPath = Path.Combine(packageRoot, "AppxManifest.xml");
        if (!File.Exists(manifestPath))
        {
            warnings.Add($"Appx manifest missing for {packageFamilyName} at {manifestPath}");
            return Array.Empty<PackagedStartupTaskDefinition>();
        }

        try
        {
            var document = XDocument.Load(manifestPath, LoadOptions.None);
            var tasks = new List<PackagedStartupTaskDefinition>();

            foreach (var extension in document.Descendants().Where(static e => string.Equals(e.Name.LocalName, "Extension", StringComparison.OrdinalIgnoreCase)))
            {
                var category = extension.Attribute("Category")?.Value;
                if (!string.Equals(category, "windows.startupTask", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (var startupTask in extension.Elements().Where(static e => string.Equals(e.Name.LocalName, "StartupTask", StringComparison.OrdinalIgnoreCase)))
                {
                    var taskId = startupTask.Attribute("TaskId")?.Value;
                    if (string.IsNullOrWhiteSpace(taskId))
                    {
                        continue;
                    }

                    var executableRaw = startupTask.Attribute("Executable")?.Value ?? extension.Attribute("Executable")?.Value;
                    if (string.IsNullOrWhiteSpace(executableRaw))
                    {
                        continue;
                    }

                    var normalizedExecutable = NormalizePackagedPath(packageRoot, executableRaw);
                    var arguments = startupTask.Attribute("Parameters")?.Value ?? extension.Attribute("Parameters")?.Value;

                    var displayName = startupTask.Attribute("DisplayName")?.Value ?? packageDisplayName;
                    var enabledText = startupTask.Attribute("Enabled")?.Value;
                    var enabledByManifest = string.IsNullOrWhiteSpace(enabledText) || enabledText.Equals("true", StringComparison.OrdinalIgnoreCase);

                    tasks.Add(new PackagedStartupTaskDefinition(taskId, displayName, normalizedExecutable, string.IsNullOrWhiteSpace(arguments) ? null : arguments, enabledByManifest));
                }
            }

            return tasks;
        }
        catch (Exception ex)
        {
            warnings.Add($"Failed to parse manifest for {packageFamilyName}: {ex.Message}");
            return Array.Empty<PackagedStartupTaskDefinition>();
        }
    }

    private sealed record PackageManagerPackageInfo(string FamilyName, string? InstalledLocation, string? DisplayName);

    private static int? GetPackagedStartupState(string packageFamilyName, string taskId)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey($"Software\\Classes\\Local Settings\\Software\\Microsoft\\Windows\\CurrentVersion\\AppModel\\SystemAppData\\{packageFamilyName}\\{taskId}", writable: false);
            var raw = key?.GetValue("State");
            return raw is null ? null : Convert.ToInt32(raw);
        }
        catch
        {
            return null;
        }
    }

    private static bool IsEnabledStartupState(int state)
    {
        return state is 2 or 4 or 5;
    }

    private static string? BuildPackageFamilyNameFromFullName(string packageFullName)
    {
        if (string.IsNullOrWhiteSpace(packageFullName))
        {
            return null;
        }

        var publisherSeparator = packageFullName.LastIndexOf("__", StringComparison.Ordinal);
        if (publisherSeparator <= 0 || publisherSeparator + 2 >= packageFullName.Length)
        {
            return null;
        }

        var publisherId = packageFullName[(publisherSeparator + 2)..];
        var nameAndVersion = packageFullName[..publisherSeparator];
        var versionSeparator = nameAndVersion.IndexOf('_');
        if (versionSeparator <= 0)
        {
            return null;
        }

        var packageName = nameAndVersion[..versionSeparator];
        if (string.IsNullOrWhiteSpace(packageName) || string.IsNullOrWhiteSpace(publisherId))
        {
            return null;
        }

        return $"{packageName}_{publisherId}";
    }

    private static (string FamilyName, string PublisherId) SplitPackageFamilyName(string packageFamilyName)
    {
        var separatorIndex = packageFamilyName.LastIndexOf('_');
        if (separatorIndex < 1 || separatorIndex + 1 >= packageFamilyName.Length)
        {
            return (string.Empty, string.Empty);
        }

        return (packageFamilyName[..separatorIndex], packageFamilyName[(separatorIndex + 1)..]);
    }

    private static string? ExtractVersion(string packageFullName, int startIndex)
    {
        if (startIndex >= packageFullName.Length)
        {
            return null;
        }

        var remainder = packageFullName[startIndex..];
        var stopIndex = remainder.IndexOf('_');
        return stopIndex <= 0 ? null : remainder[..stopIndex];
    }

    private static string NormalizePackagedPath(string packageRoot, string executableRelativePath)
    {
        var candidate = executableRelativePath.Replace('/', Path.DirectorySeparatorChar);
        var combined = Path.IsPathRooted(candidate) ? candidate : Path.Combine(packageRoot, candidate);

        try
        {
            return Path.GetFullPath(combined);
        }
        catch
        {
            return combined;
        }
    }

    private static string BuildExecCommand(string executablePath, string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return executablePath;
        }

        return executablePath.Contains(' ', StringComparison.Ordinal)
            ? $"\"{executablePath}\" {arguments}"
            : $"{executablePath} {arguments}";
    }

    private static bool? ResolveStartupApproved(RegistryKey root, string baseSubKey, string entryName, bool preferWow = false)
    {
        // Task Manager stores disable/enable state under StartupApproved Run/Run32 (and RunOnce/RunOnce32 for 32-bit apps on 64-bit OS).
        if (preferWow)
        {
            return GetStartupApprovedState(root, baseSubKey + "32", entryName)
                   ?? GetStartupApprovedState(root, baseSubKey, entryName);
        }

        return GetStartupApprovedState(root, baseSubKey, entryName)
               ?? GetStartupApprovedState(root, baseSubKey + "32", entryName);
    }

    private static bool? GetStartupApprovedState(RegistryKey root, string subKey, string entryName)
    {
        try
        {
            using var key = root.OpenSubKey(subKey, writable: false);
            if (key is null)
            {
                return null;
            }

            var data = key.GetValue(entryName) as byte[];
            if (data is null || data.Length == 0)
            {
                return null;
            }

            return data[0] switch
            {
                2 => true,
                3 => false,
                _ => null
            };
        }
        catch
        {
            return null;
        }
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

    private static (string ExecutablePath, string? Arguments) ParseCommand(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return (string.Empty, null);
        }

        var expanded = Environment.ExpandEnvironmentVariables(raw.Trim());
        if (expanded.StartsWith("\"", StringComparison.Ordinal))
        {
            var closing = expanded.IndexOf('"', 1);
            if (closing > 1)
            {
                var exe = expanded[1..closing];
                var args = closing + 1 < expanded.Length ? expanded[(closing + 1)..].Trim() : null;
                return (exe, string.IsNullOrWhiteSpace(args) ? null : args);
            }
        }

        var firstSpace = expanded.IndexOf(' ');
        if (firstSpace > 0)
        {
            var exe = expanded[..firstSpace];
            var args = expanded[(firstSpace + 1)..].Trim();
            return (exe, string.IsNullOrWhiteSpace(args) ? null : args);
        }

        return (expanded, null);
    }

    private static (string ExecutablePath, string? Arguments) ResolveShortcut(string shortcutPath)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null)
            {
                return (string.Empty, null);
            }

            object? shellObj = null;
            object? shortcutObj = null;
            try
            {
                shellObj = Activator.CreateInstance(shellType);
                if (shellObj is null)
                {
                    return (string.Empty, null);
                }

                var createShortcut = shellType.GetMethod("CreateShortcut");
                if (createShortcut is null)
                {
                    return (string.Empty, null);
                }

                shortcutObj = createShortcut.Invoke(shellObj, new object?[] { shortcutPath });
                if (shortcutObj is null)
                {
                    return (string.Empty, null);
                }

                var targetProp = shortcutObj.GetType().GetProperty("TargetPath");
                var argsProp = shortcutObj.GetType().GetProperty("Arguments");
                var target = targetProp?.GetValue(shortcutObj) as string;
                var arguments = argsProp?.GetValue(shortcutObj) as string;
                return (target ?? string.Empty, string.IsNullOrWhiteSpace(arguments) ? null : arguments.Trim());
            }
            finally
            {
                if (shortcutObj is not null && Marshal.IsComObject(shortcutObj))
                {
                    Marshal.FinalReleaseComObject(shortcutObj);
                }

                if (shellObj is not null && Marshal.IsComObject(shellObj))
                {
                    Marshal.FinalReleaseComObject(shellObj);
                }
            }
        }
        catch
        {
            return (string.Empty, null);
        }
    }

    private static bool HasLogonTrigger(TaskSchedulerTask task)
    {
        return task.Definition.Triggers.Any(static trigger => trigger.TriggerType == TaskTriggerType.Logon);
    }

    private static StartupImpact ClassifyImpact(StartupItemSourceKind source, bool isMachineScope, bool isDelayed, long? fileSizeBytes)
    {
        StartupImpact impact = StartupImpact.Unknown;

        switch (source)
        {
            case StartupItemSourceKind.Service:
                impact = isDelayed ? StartupImpact.Medium : StartupImpact.High;
                break;
            case StartupItemSourceKind.ScheduledTask:
                impact = StartupImpact.Medium;
                break;
            case StartupItemSourceKind.RunKey:
                impact = isMachineScope ? StartupImpact.Medium : StartupImpact.Low;
                break;
            case StartupItemSourceKind.RunOnce:
                impact = StartupImpact.Low;
                break;
            case StartupItemSourceKind.StartupFolder:
                impact = StartupImpact.Low;
                break;
            case StartupItemSourceKind.PackagedTask:
                impact = StartupImpact.Low;
                break;
        }

        if (fileSizeBytes is { } size)
        {
            if (size > 80 * 1024 * 1024)
            {
                impact = StartupImpact.High;
            }
            else if (size > 20 * 1024 * 1024 && impact < StartupImpact.High)
            {
                impact = StartupImpact.Medium;
            }
            else if (size < 2 * 1024 * 1024 && impact == StartupImpact.Unknown)
            {
                impact = StartupImpact.Low;
            }
        }

        return impact == StartupImpact.Unknown ? StartupImpact.Low : impact;
    }

    private static FileMetadata InspectFile(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            return FileMetadata.Unknown;
        }

        try
        {
            var info = new FileInfo(executablePath);
            var signature = GetSignature(executablePath);
            var version = TryGetCompanyName(executablePath);
            var publisher = string.IsNullOrWhiteSpace(signature.Publisher)
                ? (string.IsNullOrWhiteSpace(version) ? null : version)
                : signature.Publisher;

            return new FileMetadata(
                publisher,
                signature.Status,
                info.Exists ? info.Length : null,
                info.Exists ? info.LastWriteTimeUtc : null);
        }
        catch
        {
            return FileMetadata.Unknown;
        }
    }

    private static FileSignature GetSignature(string executablePath)
    {
        try
        {
            using var cert = X509Certificate.CreateFromSignedFile(executablePath);
            using var cert2 = new X509Certificate2(cert);

            var chain = new X509Chain
            {
                ChainPolicy =
                {
                    RevocationMode = X509RevocationMode.NoCheck,
                    VerificationFlags = X509VerificationFlags.NoFlag
                }
            };

            var trusted = chain.Build(cert2);
            var publisher = cert2.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
            return new FileSignature(string.IsNullOrWhiteSpace(publisher) ? null : publisher, trusted ? StartupSignatureStatus.SignedTrusted : StartupSignatureStatus.Signed);
        }
        catch (CryptographicException)
        {
            return new FileSignature(null, StartupSignatureStatus.Unsigned);
        }
        catch
        {
            return new FileSignature(null, StartupSignatureStatus.Unknown);
        }
    }

    private static string? TryGetCompanyName(string executablePath)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(executablePath);
            return string.IsNullOrWhiteSpace(info.CompanyName) ? null : info.CompanyName.Trim();
        }
        catch
        {
            return null;
        }
    }

    private sealed record FileSignature(string? Publisher, StartupSignatureStatus Status);

    private sealed record PackagedStartupTaskDefinition(string TaskId, string? DisplayName, string ExecutablePath, string? Arguments, bool EnabledByManifest);

    private sealed record FileMetadata(string? Publisher, StartupSignatureStatus SignatureStatus, long? FileSizeBytes, DateTimeOffset? LastWriteTimeUtc)
    {
        public static FileMetadata Unknown { get; } = new(null, StartupSignatureStatus.Unknown, null, null);
    }

    #region Additional Startup Locations

    /// <summary>
    /// Enumerates Winlogon Shell, Userinit, and related entries that run at logon.
    /// These are commonly hijacked by malware.
    /// </summary>
    private static void EnumerateWinlogonEntries(StartupInventoryOptions options, List<StartupItem> items, List<string> warnings, CancellationToken cancellationToken)
    {
        if (!options.IncludeRunKeys)
        {
            return;
        }

        // HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon
        var winlogonPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon";
        var winlogonValues = new[] { "Shell", "Userinit", "Taskman" };

        EnumerateWinlogonKey(Registry.LocalMachine, winlogonPath, winlogonValues, "HKLM Winlogon", items, warnings, cancellationToken);
        EnumerateWinlogonKey(Registry.CurrentUser, winlogonPath, winlogonValues, "HKCU Winlogon", items, warnings, cancellationToken);

        // Also check WOW6432Node
        var winlogonPath32 = @"SOFTWARE\Wow6432Node\Microsoft\Windows NT\CurrentVersion\Winlogon";
        EnumerateWinlogonKey(Registry.LocalMachine, winlogonPath32, winlogonValues, "HKLM Winlogon (32-bit)", items, warnings, cancellationToken);
    }

    private static void EnumerateWinlogonKey(RegistryKey root, string subKey, string[] valueNames, string sourceTag, List<StartupItem> items, List<string> warnings, CancellationToken cancellationToken)
    {
        try
        {
            using var key = root.OpenSubKey(subKey, writable: false);
            if (key is null)
            {
                return;
            }

            foreach (var valueName in valueNames)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var raw = key.GetValue(valueName)?.ToString();
                if (string.IsNullOrWhiteSpace(raw))
                {
                    continue;
                }

                // Winlogon values can contain multiple comma-separated entries
                var entries = raw.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                for (var i = 0; i < entries.Length; i++)
                {
                    var entry = entries[i].Trim();
                    if (string.IsNullOrWhiteSpace(entry))
                    {
                        continue;
                    }

                    // Skip default Windows values
                    if (valueName == "Shell" && entry.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (valueName == "Userinit" && entry.EndsWith("userinit.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var (exe, args) = ParseCommand(entry);
                    if (string.IsNullOrWhiteSpace(exe))
                    {
                        continue;
                    }

                    var metadata = InspectFile(exe);
                    var id = $"winlogon:{sourceTag}:{valueName}#{i}";
                    var name = Path.GetFileName(exe) ?? entry;

                    items.Add(new StartupItem(
                        id,
                        name,
                        exe,
                        StartupItemSourceKind.Winlogon,
                        $"{sourceTag} ({valueName})",
                        args,
                        entry,
                        true, // Winlogon entries are always "enabled" if present
                        $"{GetRootName(root)}\\{subKey}",
                        metadata.Publisher,
                        metadata.SignatureStatus,
                        StartupImpact.High, // Winlogon entries have high impact
                        metadata.FileSizeBytes,
                        metadata.LastWriteTimeUtc,
                        root == Registry.LocalMachine ? "Machine" : "CurrentUser"));
                }
            }
        }
        catch (Exception ex)
        {
            warnings.Add($"Winlogon enumeration failed for {sourceTag}: {ex.Message}");
        }
    }

    /// <summary>
    /// Enumerates Active Setup entries. These run once per user on first logon.
    /// </summary>
    private static void EnumerateActiveSetup(StartupInventoryOptions options, List<StartupItem> items, List<string> warnings, CancellationToken cancellationToken)
    {
        if (!options.IncludeRunOnce)
        {
            return;
        }

        var activeSetupPath = @"SOFTWARE\Microsoft\Active Setup\Installed Components";
        EnumerateActiveSetupKey(Registry.LocalMachine, activeSetupPath, "HKLM Active Setup", items, warnings, cancellationToken);
        EnumerateActiveSetupKey(Registry.CurrentUser, activeSetupPath, "HKCU Active Setup", items, warnings, cancellationToken);

        // 32-bit on 64-bit OS
        var activeSetupPath32 = @"SOFTWARE\Wow6432Node\Microsoft\Active Setup\Installed Components";
        EnumerateActiveSetupKey(Registry.LocalMachine, activeSetupPath32, "HKLM Active Setup (32-bit)", items, warnings, cancellationToken);
    }

    private static void EnumerateActiveSetupKey(RegistryKey root, string subKey, string sourceTag, List<StartupItem> items, List<string> warnings, CancellationToken cancellationToken)
    {
        try
        {
            using var key = root.OpenSubKey(subKey, writable: false);
            if (key is null)
            {
                return;
            }

            foreach (var componentGuid in key.GetSubKeyNames())
            {
                cancellationToken.ThrowIfCancellationRequested();

                using var componentKey = key.OpenSubKey(componentGuid, writable: false);
                if (componentKey is null)
                {
                    continue;
                }

                var stubPath = componentKey.GetValue("StubPath")?.ToString();
                if (string.IsNullOrWhiteSpace(stubPath))
                {
                    continue;
                }

                var isEnabled = componentKey.GetValue("IsInstalled");
                if (isEnabled is int installed && installed == 0)
                {
                    continue; // Disabled
                }

                var (exe, args) = ParseCommand(stubPath);
                if (string.IsNullOrWhiteSpace(exe))
                {
                    continue;
                }

                var metadata = InspectFile(exe);
                var displayName = componentKey.GetValue(null)?.ToString() ?? componentKey.GetValue("(Default)")?.ToString() ?? componentGuid;
                var id = $"activesetup:{componentGuid}";

                items.Add(new StartupItem(
                    id,
                    displayName,
                    exe,
                    StartupItemSourceKind.ActiveSetup,
                    sourceTag,
                    args,
                    stubPath,
                    true, // Active Setup entries run once per user
                    $"{GetRootName(root)}\\{subKey}\\{componentGuid}",
                    metadata.Publisher,
                    metadata.SignatureStatus,
                    StartupImpact.Medium,
                    metadata.FileSizeBytes,
                    metadata.LastWriteTimeUtc,
                    root == Registry.LocalMachine ? "Machine" : "CurrentUser"));
            }
        }
        catch (Exception ex)
        {
            warnings.Add($"Active Setup enumeration failed for {sourceTag}: {ex.Message}");
        }
    }

    /// <summary>
    /// Enumerates Shell Folders (user shell startup locations specified in registry).
    /// </summary>
    private static void EnumerateShellFolders(StartupInventoryOptions options, List<StartupItem> items, List<string> warnings, CancellationToken cancellationToken)
    {
        if (!options.IncludeStartupFolders)
        {
            return;
        }

        // Check for custom Startup folder paths that differ from defaults
        var shellFolderPaths = new[]
        {
            (@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Shell Folders", "Startup"),
            (@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders", "Startup"),
            (@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Shell Folders", "Common Startup"),
            (@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders", "Common Startup")
        };

        foreach (var (path, valueName) in shellFolderPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(path, writable: false);
                var folderPath = key?.GetValue(valueName)?.ToString();
                if (string.IsNullOrWhiteSpace(folderPath))
                {
                    continue;
                }

                // Expand environment variables
                folderPath = Environment.ExpandEnvironmentVariables(folderPath);

                // Skip if this is the standard startup folder (already enumerated)
                var userStartup = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
                var commonStartup = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup);
                if (folderPath.Equals(userStartup, StringComparison.OrdinalIgnoreCase) ||
                    folderPath.Equals(commonStartup, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Check for non-standard startup folder hijacking
                if (Directory.Exists(folderPath))
                {
                    var id = $"shellfolder:{path}:{valueName}";
                    var isMachineScope = valueName.Contains("Common", StringComparison.OrdinalIgnoreCase);

                    items.Add(new StartupItem(
                        id,
                        $"Custom Shell Folder: {valueName}",
                        folderPath,
                        StartupItemSourceKind.ShellFolder,
                        "Custom Shell Folder",
                        null,
                        folderPath,
                        true,
                        $"HKCU\\{path}",
                        null,
                        StartupSignatureStatus.Unknown,
                        StartupImpact.High, // Non-standard shell folders are suspicious
                        null,
                        null,
                        isMachineScope ? "Machine" : "CurrentUser"));
                }
            }
            catch
            {
                // Non-fatal, continue to next path
            }
        }
    }

    /// <summary>
    /// Enumerates Explorer Run and RunOnce entries (separate from standard Run keys).
    /// </summary>
    private static void EnumerateExplorerRun(StartupInventoryOptions options, List<StartupItem> items, List<string> warnings, CancellationToken cancellationToken)
    {
        if (!options.IncludeRunKeys)
        {
            return;
        }

        // Explorer-specific Run keys that run in the context of Explorer shell
        var explorerRunPaths = new[]
        {
            (@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Run", "HKCU Explorer Run"),
            (@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\RunOnce", "HKCU Explorer RunOnce")
        };

        foreach (var (path, sourceTag) in explorerRunPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(path, writable: false);
                if (key is null)
                {
                    continue;
                }

                foreach (var valueName in key.GetValueNames())
                {
                    var raw = key.GetValue(valueName)?.ToString();
                    if (string.IsNullOrWhiteSpace(raw))
                    {
                        continue;
                    }

                    var (exe, args) = ParseCommand(raw);
                    if (string.IsNullOrWhiteSpace(exe))
                    {
                        continue;
                    }

                    var metadata = InspectFile(exe);
                    var id = $"explorer:{sourceTag}:{valueName}";
                    var name = string.IsNullOrWhiteSpace(valueName) ? Path.GetFileName(exe) ?? sourceTag : valueName;

                    items.Add(new StartupItem(
                        id,
                        name,
                        exe,
                        StartupItemSourceKind.ExplorerRun,
                        sourceTag,
                        args,
                        raw,
                        true,
                        $"HKCU\\{path}",
                        metadata.Publisher,
                        metadata.SignatureStatus,
                        StartupImpact.Medium,
                        metadata.FileSizeBytes,
                        metadata.LastWriteTimeUtc,
                        "CurrentUser"));
                }
            }
            catch (Exception ex)
            {
                warnings.Add($"Explorer Run enumeration failed for {sourceTag}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Enumerates AppInit_DLLs entries. These DLLs are loaded into every process that uses User32.dll.
    /// Very high impact and commonly exploited.
    /// </summary>
    private static void EnumerateAppInitDlls(StartupInventoryOptions options, List<StartupItem> items, List<string> warnings, CancellationToken cancellationToken)
    {
        if (!options.IncludeRunKeys)
        {
            return;
        }

        var appInitPaths = new[]
        {
            (@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Windows", false),
            (@"SOFTWARE\Wow6432Node\Microsoft\Windows NT\CurrentVersion\Windows", true)
        };

        foreach (var (path, is32Bit) in appInitPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(path, writable: false);
                if (key is null)
                {
                    continue;
                }

                var loadAppInit = key.GetValue("LoadAppInit_DLLs");
                if (loadAppInit is not int loadValue || loadValue == 0)
                {
                    continue; // AppInit_DLLs loading is disabled
                }

                var appInitDlls = key.GetValue("AppInit_DLLs")?.ToString();
                if (string.IsNullOrWhiteSpace(appInitDlls))
                {
                    continue;
                }

                // AppInit_DLLs can contain multiple space or comma-separated DLLs
                var dlls = appInitDlls.Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
                for (var i = 0; i < dlls.Length; i++)
                {
                    var dll = dlls[i].Trim().Trim('"');
                    if (string.IsNullOrWhiteSpace(dll))
                    {
                        continue;
                    }

                    var metadata = InspectFile(dll);
                    var sourceTag = is32Bit ? "AppInit_DLLs (32-bit)" : "AppInit_DLLs";
                    var id = $"appinit:{path}#{i}";

                    items.Add(new StartupItem(
                        id,
                        Path.GetFileName(dll) ?? dll,
                        dll,
                        StartupItemSourceKind.AppInitDll,
                        sourceTag,
                        null,
                        dll,
                        true,
                        $"HKLM\\{path}",
                        metadata.Publisher,
                        metadata.SignatureStatus,
                        StartupImpact.High, // AppInit_DLLs have very high impact
                        metadata.FileSizeBytes,
                        metadata.LastWriteTimeUtc,
                        "Machine"));
                }
            }
            catch (Exception ex)
            {
                warnings.Add($"AppInit_DLLs enumeration failed: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Enumerates Image File Execution Options debugger entries.
    /// Can be used to hijack process execution.
    /// </summary>
    private static void EnumerateImageFileExecutionOptions(StartupInventoryOptions options, List<StartupItem> items, List<string> warnings, CancellationToken cancellationToken)
    {
        if (!options.IncludeRunKeys)
        {
            return;
        }

        var ifeoPaths = new[]
        {
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options",
            @"SOFTWARE\Wow6432Node\Microsoft\Windows NT\CurrentVersion\Image File Execution Options"
        };

        foreach (var path in ifeoPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(path, writable: false);
                if (key is null)
                {
                    continue;
                }

                foreach (var imageName in key.GetSubKeyNames())
                {
                    using var imageKey = key.OpenSubKey(imageName, writable: false);
                    if (imageKey is null)
                    {
                        continue;
                    }

                    var debugger = imageKey.GetValue("Debugger")?.ToString();
                    if (string.IsNullOrWhiteSpace(debugger))
                    {
                        continue;
                    }

                    var (exe, args) = ParseCommand(debugger);
                    if (string.IsNullOrWhiteSpace(exe))
                    {
                        continue;
                    }

                    var metadata = InspectFile(exe);
                    var sourceTag = path.Contains("Wow6432Node") ? "IFEO Debugger (32-bit)" : "IFEO Debugger";
                    var id = $"ifeo:{imageName}";

                    items.Add(new StartupItem(
                        id,
                        $"{imageName} Debugger",
                        exe,
                        StartupItemSourceKind.ImageFileExecutionOptions,
                        sourceTag,
                        args,
                        debugger,
                        true,
                        $"HKLM\\{path}\\{imageName}",
                        metadata.Publisher,
                        metadata.SignatureStatus,
                        StartupImpact.High, // IFEO debuggers can hijack any process
                        metadata.FileSizeBytes,
                        metadata.LastWriteTimeUtc,
                        "Machine"));
                }
            }
            catch (Exception ex)
            {
                warnings.Add($"IFEO enumeration failed: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Enumerates Boot Execute entries. These run very early in the boot process.
    /// </summary>
    private static void EnumerateBootExecute(StartupInventoryOptions options, List<StartupItem> items, List<string> warnings, CancellationToken cancellationToken)
    {
        if (!options.IncludeRunKeys)
        {
            return;
        }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager", writable: false);
            if (key is null)
            {
                return;
            }

            var bootExecute = key.GetValue("BootExecute") as string[];
            if (bootExecute is null || bootExecute.Length == 0)
            {
                return;
            }

            for (var i = 0; i < bootExecute.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var entry = bootExecute[i];
                if (string.IsNullOrWhiteSpace(entry))
                {
                    continue;
                }

                // Skip the default autocheck entry
                if (entry.Equals("autocheck autochk *", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var (exe, args) = ParseCommand(entry);
                if (string.IsNullOrWhiteSpace(exe))
                {
                    exe = entry;
                }

                var id = $"bootexec:{i}";

                items.Add(new StartupItem(
                    id,
                    $"Boot Execute: {exe}",
                    exe,
                    StartupItemSourceKind.BootExecute,
                    "Boot Execute",
                    args,
                    entry,
                    true,
                    @"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager",
                    null,
                    StartupSignatureStatus.Unknown,
                    StartupImpact.High, // Boot Execute runs before Windows fully loads
                    null,
                    null,
                    "Machine"));
            }
        }
        catch (Exception ex)
        {
            warnings.Add($"Boot Execute enumeration failed: {ex.Message}");
        }
    }

    #endregion
}

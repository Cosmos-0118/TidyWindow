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
        var items = new List<StartupItem>();
        var warnings = new List<string>();

        ExecuteSafe(() => EnumerateRunKeys(effectiveOptions, items, warnings, cancellationToken), warnings, "Registry Run keys");
        ExecuteSafe(() => EnumerateStartupFolders(effectiveOptions, items, warnings, cancellationToken), warnings, "Startup folders");
        ExecuteSafe(() => EnumerateLogonTasks(effectiveOptions, items, warnings, cancellationToken), warnings, "Logon tasks");
        ExecuteSafe(() => EnumerateAutostartServices(effectiveOptions, items, warnings, cancellationToken), warnings, "Autostart services");
        ExecuteSafe(() => EnumeratePackagedStartupTasks(effectiveOptions, items, warnings, cancellationToken), warnings, "Packaged startup tasks");

        AppendDelayWarnings(items, warnings);

        var snapshot = new StartupInventorySnapshot(items, warnings, DateTimeOffset.UtcNow, warnings.Count > 0);
        return SystemTasks.Task.FromResult(snapshot);
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
            EnumerateRunKey(Registry.LocalMachine, "Software\\Microsoft\\Windows\\CurrentVersion\\Run", StartupItemSourceKind.RunKey, "HKLM Run", isMachineScope: true, preferWow: false, items, warnings, cancellationToken);
            EnumerateRunKey(Registry.LocalMachine, "Software\\Wow6432Node\\Microsoft\\Windows\\CurrentVersion\\Run", StartupItemSourceKind.RunKey, "HKLM Run (32-bit)", isMachineScope: true, preferWow: true, items, warnings, cancellationToken);
        }

        if (options.IncludeRunOnce)
        {
            EnumerateRunKey(Registry.CurrentUser, "Software\\Microsoft\\Windows\\CurrentVersion\\RunOnce", StartupItemSourceKind.RunOnce, "HKCU RunOnce", isMachineScope: false, preferWow: false, items, warnings, cancellationToken);
            EnumerateRunKey(Registry.LocalMachine, "Software\\Microsoft\\Windows\\CurrentVersion\\RunOnce", StartupItemSourceKind.RunOnce, "HKLM RunOnce", isMachineScope: true, preferWow: false, items, warnings, cancellationToken);
            EnumerateRunKey(Registry.LocalMachine, "Software\\Wow6432Node\\Microsoft\\Windows\\CurrentVersion\\RunOnce", StartupItemSourceKind.RunOnce, "HKLM RunOnce (32-bit)", isMachineScope: true, preferWow: true, items, warnings, cancellationToken);
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

        using var systemAppDataRoot = Registry.CurrentUser.OpenSubKey(@"Software\\Classes\\Local Settings\\Software\\Microsoft\\Windows\\CurrentVersion\\AppModel\\SystemAppData", writable: false);
        if (systemAppDataRoot is null)
        {
            return;
        }

        foreach (var familyName in systemAppDataRoot.GetSubKeyNames())
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (!TryResolvePackageInfo(familyName, out var packageRoot, out var packageDisplayName))
                {
                    continue;
                }

                var tasks = ParsePackagedStartupTasks(packageRoot, packageDisplayName);
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

    private static IReadOnlyList<PackagedStartupTaskDefinition> ParsePackagedStartupTasks(string packageRoot, string? packageDisplayName)
    {
        var manifestPath = Path.Combine(packageRoot, "AppxManifest.xml");
        if (!File.Exists(manifestPath))
        {
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

                var executableRaw = extension.Attribute("Executable")?.Value;
                if (string.IsNullOrWhiteSpace(executableRaw))
                {
                    continue;
                }

                var normalizedExecutable = NormalizePackagedPath(packageRoot, executableRaw);
                var arguments = extension.Attribute("Parameters")?.Value;

                foreach (var startupTask in extension.Elements().Where(static e => string.Equals(e.Name.LocalName, "StartupTask", StringComparison.OrdinalIgnoreCase)))
                {
                    var taskId = startupTask.Attribute("TaskId")?.Value;
                    if (string.IsNullOrWhiteSpace(taskId))
                    {
                        continue;
                    }

                    var displayName = startupTask.Attribute("DisplayName")?.Value ?? packageDisplayName;
                    var enabledText = startupTask.Attribute("Enabled")?.Value;
                    var enabledByManifest = string.IsNullOrWhiteSpace(enabledText) || enabledText.Equals("true", StringComparison.OrdinalIgnoreCase);

                    tasks.Add(new PackagedStartupTaskDefinition(taskId, displayName, normalizedExecutable, string.IsNullOrWhiteSpace(arguments) ? null : arguments, enabledByManifest));
                }
            }

            return tasks;
        }
        catch
        {
            return Array.Empty<PackagedStartupTaskDefinition>();
        }
    }

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
}

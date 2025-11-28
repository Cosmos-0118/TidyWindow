using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using TidyWindow.Core.Processes.AntiSystem;

namespace TidyWindow.App.Services;

/// <summary>
/// Collects runtime context (running processes + startup entries) and executes Anti-System scans.
/// </summary>
public sealed class AntiSystemScanService
{
    private readonly AntiSystemDetectionService _detectionService;

    public AntiSystemScanService(AntiSystemDetectionService detectionService)
    {
        _detectionService = detectionService ?? throw new ArgumentNullException(nameof(detectionService));
    }

    public Task<AntiSystemDetectionResult> RunScanAsync(CancellationToken cancellationToken = default)
    {
        var processes = SnapshotProcesses();
        var startupEntries = SnapshotStartupEntries();
        var request = new AntiSystemDetectionRequest(processes, startupEntries);
        return _detectionService.RunScanAsync(request, cancellationToken);
    }

    public ValueTask<ThreatIntelResult> ScanFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        return _detectionService.ScanFileAsync(filePath, cancellationToken);
    }

    private static IReadOnlyList<RunningProcessSnapshot> SnapshotProcesses()
    {
        var list = new List<RunningProcessSnapshot>();
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                var path = TryGetProcessPath(process);
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                var snapshot = new RunningProcessSnapshot(
                    process.Id,
                    NormalizeProcessName(process),
                    path,
                    commandLine: null,
                    parentProcessId: null,
                    parentProcessName: null,
                    grandParentProcessId: null,
                    grandParentProcessName: null,
                    startedAtUtc: TryGetStartTime(process),
                    isElevated: false);

                list.Add(snapshot);
            }
            catch
            {
                // Intentionally ignore inaccessible processes.
            }
            finally
            {
                process.Dispose();
            }
        }

        return list;
    }

    private static IReadOnlyList<StartupEntrySnapshot> SnapshotStartupEntries()
    {
        var list = new List<StartupEntrySnapshot>();
        InspectRunKey(Registry.CurrentUser, "Software\\Microsoft\\Windows\\CurrentVersion\\Run", StartupEntryLocation.RunKey, "HKCU Run", list);
        InspectRunKey(Registry.LocalMachine, "Software\\Microsoft\\Windows\\CurrentVersion\\Run", StartupEntryLocation.RunKey, "HKLM Run", list);
        InspectStartupFolder(Environment.SpecialFolder.Startup, StartupEntryLocation.StartupFolder, "Startup Folder", list);
        InspectStartupFolder(Environment.SpecialFolder.CommonStartup, StartupEntryLocation.StartupFolder, "Common Startup", list);
        return list;
    }

    private static void InspectRunKey(RegistryKey root, string subKey, StartupEntryLocation location, string source, List<StartupEntrySnapshot> list)
    {
        if (root is null)
        {
            return;
        }

        try
        {
            using var key = root.OpenSubKey(subKey, writable: false);
            if (key is null)
            {
                return;
            }

            foreach (var valueName in key.GetValueNames())
            {
                var rawValue = key.GetValue(valueName)?.ToString();
                if (string.IsNullOrWhiteSpace(rawValue))
                {
                    continue;
                }

                var executable = ExtractExecutablePath(rawValue);
                if (string.IsNullOrWhiteSpace(executable))
                {
                    continue;
                }

                var entryId = $"{source}:{valueName}";
                var processName = Path.GetFileName(executable);
                list.Add(new StartupEntrySnapshot(
                    entryId,
                    string.IsNullOrWhiteSpace(processName) ? valueName : processName,
                    executable,
                    location,
                    arguments: ExtractArguments(rawValue),
                    source: source,
                    description: rawValue));
            }
        }
        catch
        {
            // Ignore registry access failures.
        }
    }

    private static void InspectStartupFolder(Environment.SpecialFolder folder, StartupEntryLocation location, string source, List<StartupEntrySnapshot> list)
    {
        var folderPath = Environment.GetFolderPath(folder);
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            return;
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(folderPath, "*", SearchOption.TopDirectoryOnly))
            {
                var extension = Path.GetExtension(file);
                if (!string.Equals(extension, ".lnk", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(extension, ".exe", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                list.Add(new StartupEntrySnapshot(
                    entryId: $"{source}:{Path.GetFileName(file)}",
                    processName: Path.GetFileName(file) ?? source,
                    executablePath: file,
                    location: location,
                    arguments: null,
                    source: source,
                    description: file));
            }
        }
        catch
        {
            // Ignore filesystem enumeration errors.
        }
    }

    private static string NormalizeProcessName(Process process)
    {
        var name = process?.ProcessName;
        if (string.IsNullOrWhiteSpace(name))
        {
            return "unknown";
        }

        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? name
            : name + ".exe";
    }

    private static string TryGetProcessPath(Process process)
    {
        try
        {
            return process.MainModule?.FileName ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static DateTimeOffset? TryGetStartTime(Process process)
    {
        try
        {
            return process.StartTime.ToUniversalTime();
        }
        catch
        {
            return null;
        }
    }

    private static string ExtractExecutablePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var expanded = Environment.ExpandEnvironmentVariables(value.Trim());
        if (expanded.StartsWith("\"", StringComparison.Ordinal))
        {
            var closing = expanded.IndexOf('"', 1);
            if (closing > 1)
            {
                return expanded[1..closing];
            }
        }

        var separatorIndex = expanded.IndexOf(' ');
        return separatorIndex > 0 ? expanded[..separatorIndex] : expanded;
    }

    private static string? ExtractArguments(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (trimmed.StartsWith("\"", StringComparison.Ordinal))
        {
            var closing = trimmed.IndexOf('"', 1);
            if (closing > 0 && closing + 1 < trimmed.Length)
            {
                return trimmed[(closing + 1)..].Trim();
            }

            return null;
        }

        var separatorIndex = trimmed.IndexOf(' ');
        return separatorIndex > 0 && separatorIndex + 1 < trimmed.Length
            ? trimmed[(separatorIndex + 1)..].Trim()
            : null;
    }
}

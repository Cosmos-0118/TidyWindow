using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TidyWindow.Core.Processes.ThreatWatch;
using TidyWindow.Core.Startup;

namespace TidyWindow.App.Services;

/// <summary>
/// Collects runtime context (running processes + startup entries) and executes Threat Watch scans.
/// </summary>
public sealed class ThreatWatchScanService
{
    private readonly ThreatWatchDetectionService _detectionService;
    private readonly StartupInventoryService _startupInventoryService;

    public ThreatWatchScanService(ThreatWatchDetectionService detectionService, StartupInventoryService startupInventoryService)
    {
        _detectionService = detectionService ?? throw new ArgumentNullException(nameof(detectionService));
        _startupInventoryService = startupInventoryService ?? throw new ArgumentNullException(nameof(startupInventoryService));
    }

    // Legacy overload retained for existing tests; defaults to a new inventory service.
    public ThreatWatchScanService(ThreatWatchDetectionService detectionService)
        : this(detectionService, new StartupInventoryService())
    {
    }

    public async Task<ThreatWatchDetectionResult> RunScanAsync(CancellationToken cancellationToken = default)
    {
        var processes = SnapshotProcesses();
        var startupSnapshot = await _startupInventoryService.GetInventoryAsync(StartupInventoryOptions.ForThreatWatch(), cancellationToken).ConfigureAwait(false);
        var startupEntries = startupSnapshot.Items
            .Select(MapStartupItem)
            .Where(static entry => entry is not null)
            .Select(static entry => entry!)
            .ToArray();

        var request = new ThreatWatchDetectionRequest(processes, startupEntries);
        return await _detectionService.RunScanAsync(request, cancellationToken).ConfigureAwait(false);
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

    private static StartupEntrySnapshot? MapStartupItem(StartupItem item)
    {
        if (string.IsNullOrWhiteSpace(item.ExecutablePath))
        {
            return null;
        }

        var location = item.SourceKind switch
        {
            StartupItemSourceKind.RunKey => StartupEntryLocation.RunKey,
            StartupItemSourceKind.RunOnce => StartupEntryLocation.RunKey,
            StartupItemSourceKind.StartupFolder => StartupEntryLocation.StartupFolder,
            StartupItemSourceKind.ScheduledTask => StartupEntryLocation.ScheduledTask,
            StartupItemSourceKind.Service => StartupEntryLocation.Services,
            _ => StartupEntryLocation.Unknown
        };

        return new StartupEntrySnapshot(
            item.Id,
            item.Name,
            item.ExecutablePath,
            location,
            item.Arguments,
            item.SourceTag,
            item.RawCommand ?? item.SourceTag);
    }
}

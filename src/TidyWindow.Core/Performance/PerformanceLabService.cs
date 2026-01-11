using System;
using System.Diagnostics;
using System.Management;
using System.Text.RegularExpressions;
using TidyWindow.Core.Automation;

namespace TidyWindow.Core.Performance;

public interface IPerformanceLabService
{
    Task<PowerShellInvocationResult> EnableUltimatePowerPlanAsync(CancellationToken cancellationToken = default);
    Task<PowerShellInvocationResult> RestorePowerPlanAsync(CancellationToken cancellationToken = default);
    Task<PowerShellInvocationResult> ApplyServiceSlimmingAsync(string? templateId = null, CancellationToken cancellationToken = default);
    Task<string?> DetectServiceTemplateAsync(CancellationToken cancellationToken = default);
    Task<PowerShellInvocationResult> RestoreServicesAsync(string? statePath = null, CancellationToken cancellationToken = default);
    Task<PowerShellInvocationResult> DetectHardwareReservedMemoryAsync(CancellationToken cancellationToken = default);
    Task<PowerShellInvocationResult> ApplyHardwareReservedFixAsync(CancellationToken cancellationToken = default);
    Task<PowerShellInvocationResult> RestoreMemoryCompressionAsync(CancellationToken cancellationToken = default);
    Task<PowerShellInvocationResult> DetectVbsHvciAsync(CancellationToken cancellationToken = default);
    Task<PowerShellInvocationResult> DisableVbsHvciAsync(CancellationToken cancellationToken = default);
    Task<PowerShellInvocationResult> RestoreVbsHvciAsync(CancellationToken cancellationToken = default);
    Task<PowerShellInvocationResult> DetectEtwTracingAsync(CancellationToken cancellationToken = default);
    Task<PowerShellInvocationResult> CleanupEtwTracingAsync(string mode = "Minimal", CancellationToken cancellationToken = default);
    Task<PowerShellInvocationResult> RestoreEtwTracingAsync(CancellationToken cancellationToken = default);
    Task<PowerShellInvocationResult> ApplyKernelBootActionAsync(string action, bool skipRestorePoint = false, CancellationToken cancellationToken = default);
    Task<PowerShellInvocationResult> DetectPagefileAsync(CancellationToken cancellationToken = default);
    Task<PowerShellInvocationResult> ApplyPagefilePresetAsync(string preset, string? targetDrive = null, int? initialMb = null, int? maxMb = null, bool sweepWorkingSets = false, bool includePinned = false, CancellationToken cancellationToken = default);
    Task<PowerShellInvocationResult> SweepWorkingSetsAsync(bool includePinned = false, CancellationToken cancellationToken = default);
    Task<PowerShellInvocationResult> DetectSchedulerAffinityAsync(CancellationToken cancellationToken = default);
    Task<PowerShellInvocationResult> ApplySchedulerAffinityAsync(string preset, string? processNames = null, CancellationToken cancellationToken = default);
    Task<PowerShellInvocationResult> RestoreSchedulerAffinityAsync(CancellationToken cancellationToken = default);
    Task<PowerShellInvocationResult> DetectDirectStorageAsync(CancellationToken cancellationToken = default);
    Task<PowerShellInvocationResult> ApplyIoPriorityBoostAsync(bool boostIoPriority = true, bool boostThreadPriority = true, CancellationToken cancellationToken = default);
    Task<PowerShellInvocationResult> RestoreIoPriorityAsync(CancellationToken cancellationToken = default);
    Task<PowerShellInvocationResult> DetectAutoTuneAsync(CancellationToken cancellationToken = default);
    Task<PowerShellInvocationResult> StartAutoTuneAsync(string? processNames = null, string? preset = null, CancellationToken cancellationToken = default);
    Task<PowerShellInvocationResult> StopAutoTuneAsync(CancellationToken cancellationToken = default);
    Task<PowerPlanStatus> GetPowerPlanStatusAsync(CancellationToken cancellationToken = default);
    Task<KernelBootStatus> GetKernelBootStatusAsync(CancellationToken cancellationToken = default);
    ServiceSlimmingStatus GetServiceSlimmingStatus();
}

public sealed class PerformanceLabService : IPerformanceLabService
{
    private static readonly Regex ActivePlanRegex = new("GUID:\\s*([0-9a-fA-F-]{36})\\s*\\((.+)\\)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private const string UltimateGuid = "e9a42b02-d5df-448d-aa00-03f14749eb61";
    private readonly PowerShellInvoker _invoker;
    private readonly string _automationRoot;
    private static readonly string[] BalancedServices = { "DiagTrack", "dmwappushservice", "RetailDemo", "XblAuthManager", "XblGameSave", "XboxNetApiSvc" };
    private static readonly string[] MinimalExtras = { "OneSyncSvc", "WalletService", "MapsBroker", "CDPSvc" };

    public PerformanceLabService(PowerShellInvoker invoker)
    {
        _invoker = invoker ?? throw new ArgumentNullException(nameof(invoker));
        _automationRoot = Path.Combine(AppContext.BaseDirectory, "automation", "performance");
    }

    public Task<PowerShellInvocationResult> EnableUltimatePowerPlanAsync(CancellationToken cancellationToken = default)
    {
        return InvokeScriptAsync("ultimate-power-plan.ps1", new Dictionary<string, object?>
        {
            ["Enable"] = true,
            ["PassThru"] = true
        }, cancellationToken);
    }

    public Task<PowerShellInvocationResult> RestorePowerPlanAsync(CancellationToken cancellationToken = default)
    {
        return InvokeScriptAsync("ultimate-power-plan.ps1", new Dictionary<string, object?>
        {
            ["Restore"] = true,
            ["PassThru"] = true
        }, cancellationToken);
    }

    public Task<PowerShellInvocationResult> ApplyServiceSlimmingAsync(string? templateId = null, CancellationToken cancellationToken = default)
    {
        var template = string.IsNullOrWhiteSpace(templateId) ? "Balanced" : templateId;
        return InvokeScriptAsync("service-slimming.ps1", new Dictionary<string, object?>
        {
            ["Template"] = template,
            ["PassThru"] = true
        }, cancellationToken);
    }

    public async Task<string?> DetectServiceTemplateAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var svcData = await Task.Run(() => BalancedServices
                .Concat(MinimalExtras)
                .Select(name => (Name: name, Service: System.ServiceProcess.ServiceController.GetServices().FirstOrDefault(s => s.ServiceName.Equals(name, StringComparison.OrdinalIgnoreCase))))
                .ToArray(), cancellationToken).ConfigureAwait(false);

            var missingBalanced = svcData.Where(s => BalancedServices.Contains(s.Name) && s.Service is null).Any();
            var minimalDisabled = svcData.Where(s => MinimalExtras.Contains(s.Name) && s.Service is not null)
                .All(s => string.Equals(GetStartMode(s.Service), "Disabled", StringComparison.OrdinalIgnoreCase));
            var balancedManual = svcData.Where(s => BalancedServices.Contains(s.Name) && s.Service is not null)
                .All(s => string.Equals(GetStartMode(s.Service), "Manual", StringComparison.OrdinalIgnoreCase) || string.Equals(GetStartMode(s.Service), "Disabled", StringComparison.OrdinalIgnoreCase));

            if (!missingBalanced && minimalDisabled)
            {
                return "Minimal";
            }

            if (!missingBalanced && balancedManual)
            {
                return "Balanced";
            }
        }
        catch
        {
            // Ignore detection errors; return null to keep existing state.
        }

        return null;
    }

    public Task<PowerShellInvocationResult> DetectHardwareReservedMemoryAsync(CancellationToken cancellationToken = default)
    {
        return InvokeScriptAsync("hardware-memory-fixer.ps1", new Dictionary<string, object?>
        {
            ["Detect"] = true,
            ["PassThru"] = true
        }, cancellationToken);
    }

    public Task<PowerShellInvocationResult> ApplyHardwareReservedFixAsync(CancellationToken cancellationToken = default)
    {
        return InvokeScriptAsync("hardware-memory-fixer.ps1", new Dictionary<string, object?>
        {
            ["ApplyFix"] = true,
            ["PassThru"] = true
        }, cancellationToken);
    }

    public Task<PowerShellInvocationResult> RestoreMemoryCompressionAsync(CancellationToken cancellationToken = default)
    {
        return InvokeScriptAsync("hardware-memory-fixer.ps1", new Dictionary<string, object?>
        {
            ["RestoreCompression"] = true,
            ["PassThru"] = true
        }, cancellationToken);
    }

    public Task<PowerShellInvocationResult> DetectVbsHvciAsync(CancellationToken cancellationToken = default)
    {
        return InvokeScriptAsync("vbs-hvci-controls.ps1", new Dictionary<string, object?>
        {
            ["Detect"] = true,
            ["PassThru"] = true
        }, cancellationToken);
    }

    public Task<PowerShellInvocationResult> DisableVbsHvciAsync(CancellationToken cancellationToken = default)
    {
        return InvokeScriptAsync("vbs-hvci-controls.ps1", new Dictionary<string, object?>
        {
            ["Disable"] = true,
            ["PassThru"] = true
        }, cancellationToken);
    }

    public Task<PowerShellInvocationResult> RestoreVbsHvciAsync(CancellationToken cancellationToken = default)
    {
        return InvokeScriptAsync("vbs-hvci-controls.ps1", new Dictionary<string, object?>
        {
            ["RestoreDefaults"] = true,
            ["PassThru"] = true
        }, cancellationToken);
    }

    public Task<PowerShellInvocationResult> DetectEtwTracingAsync(CancellationToken cancellationToken = default)
    {
        return InvokeScriptAsync("etw-trace-cleanup.ps1", new Dictionary<string, object?>
        {
            ["Detect"] = true,
            ["PassThru"] = true
        }, cancellationToken);
    }

    public Task<PowerShellInvocationResult> CleanupEtwTracingAsync(string mode = "Minimal", CancellationToken cancellationToken = default)
    {
        var effectiveMode = string.IsNullOrWhiteSpace(mode) ? "Minimal" : mode;
        return InvokeScriptAsync("etw-trace-cleanup.ps1", new Dictionary<string, object?>
        {
            ["StopTier"] = effectiveMode,
            ["PassThru"] = true
        }, cancellationToken);
    }

    public Task<PowerShellInvocationResult> RestoreEtwTracingAsync(CancellationToken cancellationToken = default)
    {
        return InvokeScriptAsync("etw-trace-cleanup.ps1", new Dictionary<string, object?>
        {
            ["RestoreDefaults"] = true,
            ["PassThru"] = true
        }, cancellationToken);
    }

    public Task<PowerShellInvocationResult> DetectPagefileAsync(CancellationToken cancellationToken = default)
    {
        return InvokeScriptAsync("pagefile-memory.ps1", new Dictionary<string, object?>
        {
            ["Detect"] = true,
            ["PassThru"] = true
        }, cancellationToken);
    }

    public Task<PowerShellInvocationResult> ApplyPagefilePresetAsync(string preset, string? targetDrive = null, int? initialMb = null, int? maxMb = null, bool sweepWorkingSets = false, bool includePinned = false, CancellationToken cancellationToken = default)
    {
        var parameters = new Dictionary<string, object?>
        {
            ["Preset"] = string.IsNullOrWhiteSpace(preset) ? "SystemManaged" : preset,
            ["PassThru"] = true
        };

        if (!string.IsNullOrWhiteSpace(targetDrive))
        {
            parameters["TargetDrive"] = targetDrive;
        }

        if (initialMb.HasValue)
        {
            parameters["InitialMB"] = initialMb.Value;
        }

        if (maxMb.HasValue)
        {
            parameters["MaxMB"] = maxMb.Value;
        }

        if (sweepWorkingSets)
        {
            parameters["SweepWorkingSets"] = true;
            if (includePinned)
            {
                parameters["IncludePinned"] = true;
            }
        }

        return InvokeScriptAsync("pagefile-memory.ps1", parameters, cancellationToken);
    }

    public Task<PowerShellInvocationResult> SweepWorkingSetsAsync(bool includePinned = false, CancellationToken cancellationToken = default)
    {
        var parameters = new Dictionary<string, object?>
        {
            ["SweepWorkingSets"] = true,
            ["PassThru"] = true
        };

        if (includePinned)
        {
            parameters["IncludePinned"] = true;
        }

        return InvokeScriptAsync("pagefile-memory.ps1", parameters, cancellationToken);
    }

    public Task<PowerShellInvocationResult> DetectSchedulerAffinityAsync(CancellationToken cancellationToken = default)
    {
        return InvokeScriptAsync("scheduler-affinity.ps1", new Dictionary<string, object?>
        {
            ["Detect"] = true,
            ["PassThru"] = true
        }, cancellationToken);
    }

    public Task<PowerShellInvocationResult> ApplySchedulerAffinityAsync(string preset, string? processNames = null, CancellationToken cancellationToken = default)
    {
        var parameters = new Dictionary<string, object?>
        {
            ["Preset"] = string.IsNullOrWhiteSpace(preset) ? "Balanced" : preset,
            ["PassThru"] = true
        };

        if (!string.IsNullOrWhiteSpace(processNames))
        {
            parameters["ProcessNames"] = processNames
                .Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToArray();
        }

        return InvokeScriptAsync("scheduler-affinity.ps1", parameters, cancellationToken);
    }

    public Task<PowerShellInvocationResult> RestoreSchedulerAffinityAsync(CancellationToken cancellationToken = default)
    {
        return InvokeScriptAsync("scheduler-affinity.ps1", new Dictionary<string, object?>
        {
            ["RestoreDefaults"] = true,
            ["PassThru"] = true
        }, cancellationToken);
    }

    public Task<PowerShellInvocationResult> DetectDirectStorageAsync(CancellationToken cancellationToken = default)
    {
        return InvokeScriptAsync("directstorage-readiness.ps1", new Dictionary<string, object?>
        {
            ["Detect"] = true,
            ["PassThru"] = true
        }, cancellationToken);
    }

    public Task<PowerShellInvocationResult> ApplyIoPriorityBoostAsync(bool boostIoPriority = true, bool boostThreadPriority = true, CancellationToken cancellationToken = default)
    {
        var parameters = new Dictionary<string, object?>
        {
            ["PassThru"] = true
        };

        if (boostIoPriority)
        {
            parameters["BoostIO"] = true;
        }

        if (boostThreadPriority)
        {
            parameters["BoostThreads"] = true;
        }

        if (!boostIoPriority && !boostThreadPriority)
        {
            parameters["Detect"] = true;
        }

        return InvokeScriptAsync("directstorage-readiness.ps1", parameters, cancellationToken);
    }

    public Task<PowerShellInvocationResult> RestoreIoPriorityAsync(CancellationToken cancellationToken = default)
    {
        return InvokeScriptAsync("directstorage-readiness.ps1", new Dictionary<string, object?>
        {
            ["Restore"] = true,
            ["PassThru"] = true
        }, cancellationToken);
    }

    public Task<PowerShellInvocationResult> DetectAutoTuneAsync(CancellationToken cancellationToken = default)
    {
        return InvokeScriptAsync("autotune-monitor.ps1", new Dictionary<string, object?>
        {
            ["Detect"] = true,
            ["PassThru"] = true
        }, cancellationToken);
    }

    public Task<PowerShellInvocationResult> StartAutoTuneAsync(string? processNames = null, string? preset = null, CancellationToken cancellationToken = default)
    {
        var parameters = new Dictionary<string, object?>
        {
            ["Start"] = true,
            ["Preset"] = string.IsNullOrWhiteSpace(preset) ? "LatencyBoost" : preset,
            ["PassThru"] = true
        };

        if (!string.IsNullOrWhiteSpace(processNames))
        {
            parameters["ProcessNames"] = processNames
                .Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToArray();
        }

        return InvokeScriptAsync("autotune-monitor.ps1", parameters, cancellationToken);
    }

    public Task<PowerShellInvocationResult> StopAutoTuneAsync(CancellationToken cancellationToken = default)
    {
        return InvokeScriptAsync("autotune-monitor.ps1", new Dictionary<string, object?>
        {
            ["Stop"] = true,
            ["PassThru"] = true
        }, cancellationToken);
    }

    public Task<PowerShellInvocationResult> ApplyKernelBootActionAsync(string action, bool skipRestorePoint = false, CancellationToken cancellationToken = default)
    {
        var effectiveAction = string.IsNullOrWhiteSpace(action) ? "Recommended" : action;
        return InvokeScriptAsync("kernel-boot-controls.ps1", new Dictionary<string, object?>
        {
            ["Action"] = effectiveAction,
            ["SkipRestorePoint"] = skipRestorePoint,
            ["PassThru"] = true
        }, cancellationToken);
    }

    public Task<PowerShellInvocationResult> RestoreServicesAsync(string? statePath = null, CancellationToken cancellationToken = default)
    {
        var resolvedStatePath = statePath;
        if (string.IsNullOrWhiteSpace(resolvedStatePath))
        {
            resolvedStatePath = GetServiceSlimmingStatus().LastBackupPath;
        }

        if (string.IsNullOrWhiteSpace(resolvedStatePath))
        {
            return Task.FromResult(new PowerShellInvocationResult(Array.Empty<string>(), new[] { "No service backup found to restore." }, 1));
        }

        var parameters = new Dictionary<string, object?>
        {
            ["Restore"] = true,
            ["PassThru"] = true,
            ["StatePath"] = resolvedStatePath
        };

        return InvokeScriptAsync("service-slimming.ps1", parameters, cancellationToken);
    }

    public async Task<PowerPlanStatus> GetPowerPlanStatusAsync(CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "powercfg",
            Arguments = "/getactivescheme",
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var output = await outputTask.ConfigureAwait(false);
        var errors = await errorTask.ConfigureAwait(false);

        if (process.ExitCode != 0 && !string.IsNullOrWhiteSpace(errors))
        {
            return new PowerPlanStatus(null, errors.Trim(), false, GetPowerPlanStatePath());
        }

        var guid = default(string?);
        var name = default(string?);
        var match = ActivePlanRegex.Match(output);
        if (match.Success)
        {
            guid = match.Groups[1].Value;
            name = match.Groups[2].Value;
        }
        else if (!string.IsNullOrWhiteSpace(output))
        {
            name = output.Trim();
        }

        var isUltimate = guid is not null && guid.Equals(UltimateGuid, StringComparison.OrdinalIgnoreCase);
        if (!isUltimate && !string.IsNullOrWhiteSpace(name))
        {
            var normalized = name.Trim();
            if (normalized.Contains("ultimate", StringComparison.OrdinalIgnoreCase)
                && normalized.Contains("performance", StringComparison.OrdinalIgnoreCase))
            {
                isUltimate = true;
            }
        }
        return new PowerPlanStatus(guid, name, isUltimate, GetPowerPlanStatePath());
    }

    public ServiceSlimmingStatus GetServiceSlimmingStatus()
    {
        var storage = GetStorageDirectory();
        if (!Directory.Exists(storage))
        {
            return new ServiceSlimmingStatus(null);
        }

        var latest = Directory.GetFiles(storage, "service-backup-*.json", SearchOption.TopDirectoryOnly)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();

        return new ServiceSlimmingStatus(latest);
    }

    public async Task<KernelBootStatus> GetKernelBootStatusAsync(CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "bcdedit",
            Arguments = "/enum {current}",
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var output = await outputTask.ConfigureAwait(false);
        var errors = await errorTask.ConfigureAwait(false);

        var lines = (output ?? string.Empty).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .ToList();

        string? GetValue(string key)
        {
            var line = lines.FirstOrDefault(l => l.StartsWith(key, StringComparison.OrdinalIgnoreCase));
            if (line is null)
            {
                return null;
            }

            var idx = line.IndexOf(' ');
            return idx >= 0 && idx < line.Length - 1 ? line[(idx + 1)..].Trim() : null;
        }

        var disabledDynamicTick = ParseBool(GetValue("disabledynamictick"));
        var usePlatformClock = ParseBool(GetValue("useplatformclock"));
        var tscSyncPolicy = GetValue("tscsyncpolicy");
        var linear57 = ParseBool(GetValue("linearaddress57"));

        var isRecommended = (disabledDynamicTick == true) && (usePlatformClock == true) && string.Equals(tscSyncPolicy, "Enhanced", StringComparison.OrdinalIgnoreCase);

        var summaryParts = new List<string>();
        if (disabledDynamicTick.HasValue) summaryParts.Add($"dynamic tick: {(disabledDynamicTick.Value ? "off" : "on")}");
        if (usePlatformClock.HasValue) summaryParts.Add($"platform clock: {(usePlatformClock.Value ? "on" : "default")}");
        if (!string.IsNullOrWhiteSpace(tscSyncPolicy)) summaryParts.Add($"tscsync: {tscSyncPolicy}");
        if (linear57.HasValue) summaryParts.Add($"linear57: {(linear57.Value ? "on" : "off")}");

        var summary = summaryParts.Count > 0
            ? string.Join(", ", summaryParts)
            : (!string.IsNullOrWhiteSpace(errors) ? errors.Trim() : "Unable to read kernel boot values.");

        return new KernelBootStatus(isRecommended, summary, lines, errors);
    }

    private Task<PowerShellInvocationResult> InvokeScriptAsync(string fileName, IReadOnlyDictionary<string, object?> parameters, CancellationToken cancellationToken)
    {
        var scriptPath = Path.Combine(_automationRoot, fileName);
        return _invoker.InvokeScriptAsync(scriptPath, parameters, cancellationToken);
    }

    private static string GetStorageDirectory()
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TidyWindow", "PerformanceLab");
    }

    private static string? GetPowerPlanStatePath()
    {
        var path = Path.Combine(GetStorageDirectory(), "powerplan-state.json");
        return File.Exists(path) ? path : null;
    }

    private static string? GetStartMode(System.ServiceProcess.ServiceController? service)
    {
        if (service is null)
        {
            return null;
        }

        try
        {
            var query = new ManagementObjectSearcher($"SELECT StartMode FROM Win32_Service WHERE Name='{service.ServiceName}'");
            var mode = query.Get().Cast<ManagementObject>().FirstOrDefault()?["StartMode"]?.ToString();
            return mode;
        }
        catch
        {
            return null;
        }
    }

    private static bool? ParseBool(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("on", StringComparison.OrdinalIgnoreCase);
    }
}

public sealed record PowerPlanStatus(string? ActiveSchemeId, string? ActiveSchemeName, bool IsUltimateActive, string? LastBackupPath);

public sealed record ServiceSlimmingStatus(string? LastBackupPath);

public sealed record KernelBootStatus(bool IsRecommended, string Summary, IReadOnlyList<string> OutputLines, string? Errors);

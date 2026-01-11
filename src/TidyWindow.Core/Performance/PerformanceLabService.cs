using System.Diagnostics;
using System.Text.RegularExpressions;
using TidyWindow.Core.Automation;

namespace TidyWindow.Core.Performance;

public interface IPerformanceLabService
{
    Task<PowerShellInvocationResult> EnableUltimatePowerPlanAsync(CancellationToken cancellationToken = default);
    Task<PowerShellInvocationResult> RestorePowerPlanAsync(CancellationToken cancellationToken = default);
    Task<PowerShellInvocationResult> ApplyServiceSlimmingAsync(string? templateId = null, CancellationToken cancellationToken = default);
    Task<PowerShellInvocationResult> RestoreServicesAsync(string? statePath = null, CancellationToken cancellationToken = default);
    Task<PowerShellInvocationResult> DetectHardwareReservedMemoryAsync(CancellationToken cancellationToken = default);
    Task<PowerShellInvocationResult> ApplyHardwareReservedFixAsync(CancellationToken cancellationToken = default);
    Task<PowerShellInvocationResult> RestoreMemoryCompressionAsync(CancellationToken cancellationToken = default);
    Task<PowerShellInvocationResult> ApplyKernelBootActionAsync(string action, bool skipRestorePoint = false, CancellationToken cancellationToken = default);
    Task<PowerPlanStatus> GetPowerPlanStatusAsync(CancellationToken cancellationToken = default);
    ServiceSlimmingStatus GetServiceSlimmingStatus();
}

public sealed class PerformanceLabService : IPerformanceLabService
{
    private static readonly Regex ActivePlanRegex = new("GUID:\\s*([0-9a-fA-F-]{36})\\s*\\((.+)\\)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private const string UltimateGuid = "e9a42b02-d5df-448d-aa00-03f14749eb61";
    private readonly PowerShellInvoker _invoker;
    private readonly string _automationRoot;

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
}

public sealed record PowerPlanStatus(string? ActiveSchemeId, string? ActiveSchemeName, bool IsUltimateActive, string? LastBackupPath);

public sealed record ServiceSlimmingStatus(string? LastBackupPath);

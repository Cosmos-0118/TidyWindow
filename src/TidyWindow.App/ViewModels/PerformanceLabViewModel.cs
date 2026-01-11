using System.Collections.ObjectModel;
using System.IO;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TidyWindow.App.Services;
using TidyWindow.Core.Automation;
using TidyWindow.Core.Performance;

namespace TidyWindow.App.ViewModels;

public sealed class PerformanceTemplateOption
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public int ServiceCount { get; init; }
}

public sealed partial class PerformanceLabViewModel : ObservableObject
{
    private readonly IPerformanceLabService _service;
    private readonly ActivityLogService _activityLog;

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private string powerPlanHeadline = "Power plan status not checked";

    [ObservableProperty]
    private string powerPlanDetails = "Refresh to detect the current scheme.";

    [ObservableProperty]
    private bool isUltimateActive;

    [ObservableProperty]
    private string servicesHeadline = "Service templates ready";

    [ObservableProperty]
    private string servicesDetails = "Refresh to see the latest backup.";

    [ObservableProperty]
    private string? lastServiceBackupPath;

    [ObservableProperty]
    private string? lastPowerPlanBackupPath;

    [ObservableProperty]
    private bool hasPowerPlanBackup;

    [ObservableProperty]
    private bool hasServiceBackup;

    [ObservableProperty]
    private string powerPlanStatusMessage = "No power plan actions run yet.";

    [ObservableProperty]
    private string serviceStatusMessage = "No service actions run yet.";

    [ObservableProperty]
    private string hardwareStatusMessage = "Detect to view reserved memory.";

    [ObservableProperty]
    private string kernelStatusMessage = "Apply the recommended preset to harden timers.";

    [ObservableProperty]
    private bool isPowerPlanSuccess;

    [ObservableProperty]
    private bool isServiceSuccess;

    [ObservableProperty]
    private bool isHardwareSuccess;

    [ObservableProperty]
    private bool isKernelSuccess;

    [ObservableProperty]
    private string vbsStatusMessage = "Detect VBS/HVCI to view Core Isolation state.";

    [ObservableProperty]
    private string etwStatusMessage = "Detect ETW sessions to see trace load.";

    [ObservableProperty]
    private bool isVbsSuccess;

    [ObservableProperty]
    private bool isEtwSuccess;

    [ObservableProperty]
    private string powerPlanStatusTimestamp = "–";

    [ObservableProperty]
    private string serviceStatusTimestamp = "–";

    [ObservableProperty]
    private string hardwareStatusTimestamp = "–";

    [ObservableProperty]
    private string kernelStatusTimestamp = "–";

    [ObservableProperty]
    private string vbsStatusTimestamp = "–";

    [ObservableProperty]
    private string etwStatusTimestamp = "–";

    private static readonly Regex AnsiRegex = new("\\u001B\\[[0-9;]*m", RegexOptions.Compiled);

    public ObservableCollection<PerformanceTemplateOption> Templates { get; }

    [ObservableProperty]
    private PerformanceTemplateOption? selectedTemplate;

    public IAsyncRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand EnableUltimatePlanCommand { get; }
    public IAsyncRelayCommand RestorePowerPlanCommand { get; }
    public IAsyncRelayCommand<PerformanceTemplateOption?> ApplyServiceTemplateCommand { get; }
    public IAsyncRelayCommand RestoreServicesCommand { get; }
    public IAsyncRelayCommand DetectHardwareReservedCommand { get; }
    public IAsyncRelayCommand ApplyHardwareFixCommand { get; }
    public IAsyncRelayCommand RestoreCompressionCommand { get; }
    public IAsyncRelayCommand ApplyKernelPresetCommand { get; }
    public IAsyncRelayCommand RestoreKernelDefaultsCommand { get; }
    public IAsyncRelayCommand DetectVbsHvciCommand { get; }
    public IAsyncRelayCommand DisableVbsHvciCommand { get; }
    public IAsyncRelayCommand RestoreVbsHvciCommand { get; }
    public IAsyncRelayCommand DetectEtwSessionsCommand { get; }
    public IAsyncRelayCommand CleanupEtwMinimalCommand { get; }
    public IAsyncRelayCommand CleanupEtwAggressiveCommand { get; }
    public IAsyncRelayCommand RestoreEtwDefaultsCommand { get; }

    public PerformanceLabViewModel(IPerformanceLabService service, ActivityLogService activityLog)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _activityLog = activityLog ?? throw new ArgumentNullException(nameof(activityLog));

        Templates = new ObservableCollection<PerformanceTemplateOption>
        {
            new() { Id = "Balanced", Name = "Balanced", Description = "Stops telemetry/Xbox/consumer services; sets them to Manual.", ServiceCount = 6 },
            new() { Id = "Minimal", Name = "Minimal", Description = "Adds CDPSvc/OneSync/Wallet to the balanced set; disables instead of manual.", ServiceCount = 10 }
        };
        SelectedTemplate = Templates.FirstOrDefault();

        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        EnableUltimatePlanCommand = new AsyncRelayCommand(EnableUltimatePlanAsync, () => !IsBusy);
        RestorePowerPlanCommand = new AsyncRelayCommand(RestorePowerPlanAsync, () => !IsBusy);
        ApplyServiceTemplateCommand = new AsyncRelayCommand<PerformanceTemplateOption?>(ApplyServiceTemplateAsync, _ => !IsBusy);
        RestoreServicesCommand = new AsyncRelayCommand(RestoreServicesAsync, () => !IsBusy);
        DetectHardwareReservedCommand = new AsyncRelayCommand(DetectHardwareReservedAsync, () => !IsBusy);
        ApplyHardwareFixCommand = new AsyncRelayCommand(ApplyHardwareFixAsync, () => !IsBusy);
        RestoreCompressionCommand = new AsyncRelayCommand(RestoreCompressionAsync, () => !IsBusy);
        ApplyKernelPresetCommand = new AsyncRelayCommand(ApplyKernelPresetAsync, () => !IsBusy);
        RestoreKernelDefaultsCommand = new AsyncRelayCommand(RestoreKernelDefaultsAsync, () => !IsBusy);
        DetectVbsHvciCommand = new AsyncRelayCommand(DetectVbsHvciAsync, () => !IsBusy);
        DisableVbsHvciCommand = new AsyncRelayCommand(DisableVbsHvciAsync, () => !IsBusy);
        RestoreVbsHvciCommand = new AsyncRelayCommand(RestoreVbsHvciAsync, () => !IsBusy);
        DetectEtwSessionsCommand = new AsyncRelayCommand(DetectEtwTracingAsync, () => !IsBusy);
        CleanupEtwMinimalCommand = new AsyncRelayCommand(() => CleanupEtwAsync("Minimal"), () => !IsBusy);
        CleanupEtwAggressiveCommand = new AsyncRelayCommand(() => CleanupEtwAsync("Aggressive"), () => !IsBusy);
        RestoreEtwDefaultsCommand = new AsyncRelayCommand(RestoreEtwDefaultsAsync, () => !IsBusy);
    }

    partial void OnIsBusyChanged(bool value)
    {
        EnableUltimatePlanCommand.NotifyCanExecuteChanged();
        RestorePowerPlanCommand.NotifyCanExecuteChanged();
        ApplyServiceTemplateCommand.NotifyCanExecuteChanged();
        RestoreServicesCommand.NotifyCanExecuteChanged();
        DetectHardwareReservedCommand.NotifyCanExecuteChanged();
        ApplyHardwareFixCommand.NotifyCanExecuteChanged();
        RestoreCompressionCommand.NotifyCanExecuteChanged();
        ApplyKernelPresetCommand.NotifyCanExecuteChanged();
        RestoreKernelDefaultsCommand.NotifyCanExecuteChanged();
        DetectVbsHvciCommand.NotifyCanExecuteChanged();
        DisableVbsHvciCommand.NotifyCanExecuteChanged();
        RestoreVbsHvciCommand.NotifyCanExecuteChanged();
        DetectEtwSessionsCommand.NotifyCanExecuteChanged();
        CleanupEtwMinimalCommand.NotifyCanExecuteChanged();
        CleanupEtwAggressiveCommand.NotifyCanExecuteChanged();
        RestoreEtwDefaultsCommand.NotifyCanExecuteChanged();
    }

    private async Task RefreshAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var vbsMessage = VbsStatusMessage;
            var vbsSuccess = IsVbsSuccess;
            var vbsTimestamp = VbsStatusTimestamp;
            var etwMessage = EtwStatusMessage;
            var etwSuccess = IsEtwSuccess;
            var etwTimestamp = EtwStatusTimestamp;

            var existingServiceMessage = ServiceStatusMessage;
            var existingServiceSuccess = IsServiceSuccess;

            var plan = await _service.GetPowerPlanStatusAsync().ConfigureAwait(true);
            IsUltimateActive = plan.IsUltimateActive;
            PowerPlanHeadline = plan.IsUltimateActive ? "Ultimate Performance active" : "Standard plan active";
            PowerPlanDetails = string.IsNullOrWhiteSpace(plan.ActiveSchemeName)
                ? "Unable to read current scheme."
                : $"{plan.ActiveSchemeName} ({plan.ActiveSchemeId ?? "unknown GUID"})";
            LastPowerPlanBackupPath = plan.LastBackupPath;
            HasPowerPlanBackup = !string.IsNullOrWhiteSpace(plan.LastBackupPath);
            PowerPlanStatusMessage = plan.IsUltimateActive
                ? "Ultimate Performance is active"
                : (!string.IsNullOrWhiteSpace(plan.ActiveSchemeName) ? $"Active: {plan.ActiveSchemeName}" : "Active plan detected");
            IsPowerPlanSuccess = true;
            PowerPlanStatusTimestamp = DateTime.Now.ToString("HH:mm:ss");

            var services = _service.GetServiceSlimmingStatus();
            ServicesHeadline = services.LastBackupPath is null
                ? "No service backups yet"
                : "Service backups available";
            ServicesDetails = services.LastBackupPath is null
                ? "Apply a template to create a baseline backup."
                : $"Latest backup: {Path.GetFileName(services.LastBackupPath)}";
            LastServiceBackupPath = services.LastBackupPath;
            HasServiceBackup = !string.IsNullOrWhiteSpace(services.LastBackupPath);
            if (HasServiceBackup)
            {
                // Preserve the last action message (e.g., the template applied) instead of overwriting it with a generic backup note.
                var hasCustomMessage = !string.IsNullOrWhiteSpace(existingServiceMessage)
                    && !string.Equals(existingServiceMessage, "No service actions run yet.", StringComparison.OrdinalIgnoreCase);
                ServiceStatusMessage = hasCustomMessage
                    ? existingServiceMessage
                    : $"Backup: {Path.GetFileName(services.LastBackupPath)}";
                IsServiceSuccess = existingServiceSuccess || HasServiceBackup;
            }
            else
            {
                ServiceStatusMessage = "No service backup yet";
                IsServiceSuccess = false;
            }
            ServiceStatusTimestamp = DateTime.Now.ToString("HH:mm:ss");

            var detectedTemplate = await _service.DetectServiceTemplateAsync().ConfigureAwait(true);
            if (!string.IsNullOrWhiteSpace(detectedTemplate))
            {
                SelectedTemplate = Templates.FirstOrDefault(t => string.Equals(t.Name, detectedTemplate, StringComparison.OrdinalIgnoreCase)) ?? SelectedTemplate;
                ServiceStatusMessage = ServiceStatusMessage.Contains(detectedTemplate ?? string.Empty, StringComparison.OrdinalIgnoreCase)
                    ? ServiceStatusMessage
                    : $"Detected template: {detectedTemplate}";
                IsServiceSuccess = true;
            }

            var hardwareResult = await _service.DetectHardwareReservedMemoryAsync().ConfigureAwait(true);
            HandleHardwareResult("PerformanceLab", "Hardware reserved memory detected", hardwareResult);

            var kernelStatus = await _service.GetKernelBootStatusAsync().ConfigureAwait(true);
            KernelStatusMessage = kernelStatus.Summary;
            IsKernelSuccess = kernelStatus.IsRecommended;
            KernelStatusTimestamp = DateTime.Now.ToString("HH:mm:ss");

            // Preserve VBS/HVCI and ETW status across refresh; these steps are not re-run during a refresh.
            VbsStatusMessage = vbsMessage;
            IsVbsSuccess = vbsSuccess;
            VbsStatusTimestamp = vbsTimestamp;
            EtwStatusMessage = etwMessage;
            IsEtwSuccess = etwSuccess;
            EtwStatusTimestamp = etwTimestamp;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task EnableUltimatePlanAsync()
    {
        await RunOperationAsync(async () =>
        {
            var result = await _service.EnableUltimatePowerPlanAsync().ConfigureAwait(true);
            HandlePlanResult("PerformanceLab", "Ultimate Performance enabled", result);
            await RefreshAsync().ConfigureAwait(true);
        }).ConfigureAwait(false);
    }

    private async Task DetectHardwareReservedAsync()
    {
        await RunOperationAsync(async () =>
        {
            var result = await _service.DetectHardwareReservedMemoryAsync().ConfigureAwait(true);
            HandleHardwareResult("PerformanceLab", "Hardware reserved memory detected", result);
        }).ConfigureAwait(false);
    }

    private async Task ApplyHardwareFixAsync()
    {
        await RunOperationAsync(async () =>
        {
            var result = await _service.ApplyHardwareReservedFixAsync().ConfigureAwait(true);
            HandleHardwareResult("PerformanceLab", "Cleared BCD memory caps and disabled compression", result);
        }).ConfigureAwait(false);
    }

    private async Task RestoreCompressionAsync()
    {
        await RunOperationAsync(async () =>
        {
            var result = await _service.RestoreMemoryCompressionAsync().ConfigureAwait(true);
            HandleHardwareResult("PerformanceLab", "Memory compression restored", result);
        }).ConfigureAwait(false);
    }

    private async Task ApplyKernelPresetAsync()
    {
        await RunOperationAsync(async () =>
        {
            var result = await _service.ApplyKernelBootActionAsync("Recommended").ConfigureAwait(true);
            HandleKernelResult("PerformanceLab", "Kernel preset applied (dynamic tick off, platform clock on, linear57 on)", result);
        }).ConfigureAwait(false);
    }

    private async Task RestoreKernelDefaultsAsync()
    {
        await RunOperationAsync(async () =>
        {
            var result = await _service.ApplyKernelBootActionAsync("RestoreDefaults", skipRestorePoint: true).ConfigureAwait(true);
            HandleKernelResult("PerformanceLab", "Kernel boot values restored to defaults", result);
        }).ConfigureAwait(false);
    }

    private async Task DetectVbsHvciAsync()
    {
        await RunOperationAsync(async () =>
        {
            var result = await _service.DetectVbsHvciAsync().ConfigureAwait(true);
            HandleVbsResult("PerformanceLab", "VBS/HVCI status captured", result);
        }).ConfigureAwait(false);
    }

    private async Task DisableVbsHvciAsync()
    {
        await RunOperationAsync(async () =>
        {
            var result = await _service.DisableVbsHvciAsync().ConfigureAwait(true);
            HandleVbsResult("PerformanceLab", "VBS/HVCI disabled (hypervisor off, HVCI off)", result);
        }).ConfigureAwait(false);
    }

    private async Task RestoreVbsHvciAsync()
    {
        await RunOperationAsync(async () =>
        {
            var result = await _service.RestoreVbsHvciAsync().ConfigureAwait(true);
            HandleVbsResult("PerformanceLab", "VBS/HVCI defaults restored", result);
        }).ConfigureAwait(false);
    }

    private async Task DetectEtwTracingAsync()
    {
        await RunOperationAsync(async () =>
        {
            var result = await _service.DetectEtwTracingAsync().ConfigureAwait(true);
            HandleEtwResult("PerformanceLab", "ETW sessions inspected", result);
        }).ConfigureAwait(false);
    }

    private async Task CleanupEtwAsync(string mode)
    {
        var tier = string.IsNullOrWhiteSpace(mode) ? "Minimal" : mode;

        await RunOperationAsync(async () =>
        {
            var result = await _service.CleanupEtwTracingAsync(tier).ConfigureAwait(true);
            HandleEtwResult("PerformanceLab", $"ETW sessions stopped ({tier})", result);
        }).ConfigureAwait(false);
    }

    private async Task RestoreEtwDefaultsAsync()
    {
        await RunOperationAsync(async () =>
        {
            var result = await _service.RestoreEtwTracingAsync().ConfigureAwait(true);
            HandleEtwResult("PerformanceLab", "ETW defaults restored", result);
        }).ConfigureAwait(false);
    }

    private async Task RestorePowerPlanAsync()
    {
        await RunOperationAsync(async () =>
        {
            var result = await _service.RestorePowerPlanAsync().ConfigureAwait(true);
            HandlePlanResult("PerformanceLab", "Power plan restored", result);
            await RefreshAsync().ConfigureAwait(true);
        }).ConfigureAwait(false);
    }

    private async Task ApplyServiceTemplateAsync(PerformanceTemplateOption? option)
    {
        var template = option ?? SelectedTemplate ?? Templates.First();

        await RunOperationAsync(async () =>
        {
            var result = await _service.ApplyServiceSlimmingAsync(template.Id).ConfigureAwait(true);
            HandleServiceResult("PerformanceLab", $"Applied service template {template.Name}", result);
            await RefreshAsync().ConfigureAwait(true);
        }).ConfigureAwait(false);
    }

    private async Task RestoreServicesAsync()
    {
        await RunOperationAsync(async () =>
        {
            var result = await _service.RestoreServicesAsync(LastServiceBackupPath).ConfigureAwait(true);
            HandleServiceResult("PerformanceLab", "Services restored", result);
            await RefreshAsync().ConfigureAwait(true);
        }).ConfigureAwait(false);
    }

    private async Task RunOperationAsync(Func<Task> action)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await action().ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void HandlePlanResult(string source, string successMessage, PowerShellInvocationResult result)
    {
        if (result.IsSuccess)
        {
            _activityLog.LogSuccess(source, successMessage, BuildDetails(result));
            PowerPlanStatusMessage = successMessage;
            IsPowerPlanSuccess = true;
            PowerPlanStatusTimestamp = DateTime.Now.ToString("HH:mm:ss");
        }
        else
        {
            var message = result.Errors.FirstOrDefault() ?? "Operation failed.";
            _activityLog.LogWarning(source, message, BuildDetails(result));
            PowerPlanStatusMessage = message;
            IsPowerPlanSuccess = false;
            PowerPlanStatusTimestamp = DateTime.Now.ToString("HH:mm:ss");
        }
    }

    private void HandleServiceResult(string source, string successMessage, PowerShellInvocationResult result)
    {
        if (result.IsSuccess)
        {
            _activityLog.LogSuccess(source, successMessage, BuildDetails(result));
            ServiceStatusMessage = successMessage;
            IsServiceSuccess = true;
            ServiceStatusTimestamp = DateTime.Now.ToString("HH:mm:ss");
        }
        else
        {
            var message = result.Errors.FirstOrDefault() ?? "Operation failed.";
            _activityLog.LogWarning(source, message, BuildDetails(result));
            ServiceStatusMessage = message;
            IsServiceSuccess = false;
            ServiceStatusTimestamp = DateTime.Now.ToString("HH:mm:ss");
        }
    }

    private void HandleHardwareResult(string source, string successMessage, PowerShellInvocationResult result)
    {
        if (result.IsSuccess)
        {
            var details = BuildDetails(result);
            var primary = details.FirstOrDefault(d => !d.StartsWith("exitCode", StringComparison.OrdinalIgnoreCase))
                          ?? details.FirstOrDefault();
            _activityLog.LogSuccess(source, successMessage, details);
            HardwareStatusMessage = primary ?? successMessage;
            IsHardwareSuccess = true;
        }
        else
        {
            var message = result.Errors.FirstOrDefault() ?? "Operation failed.";
            _activityLog.LogWarning(source, message, BuildDetails(result));
            HardwareStatusMessage = message;
            IsHardwareSuccess = false;
        }

        HardwareStatusTimestamp = DateTime.Now.ToString("HH:mm:ss");
    }

    private void HandleKernelResult(string source, string successMessage, PowerShellInvocationResult result)
    {
        if (result.IsSuccess)
        {
            var details = BuildDetails(result);
            var primary = details.FirstOrDefault(d => !d.StartsWith("exitCode", StringComparison.OrdinalIgnoreCase))
                          ?? details.FirstOrDefault();
            _activityLog.LogSuccess(source, successMessage, details);
            KernelStatusMessage = primary ?? successMessage;
            IsKernelSuccess = true;
        }
        else
        {
            var message = result.Errors.FirstOrDefault() ?? "Operation failed.";
            _activityLog.LogWarning(source, message, BuildDetails(result));
            KernelStatusMessage = message;
            IsKernelSuccess = false;
        }

        KernelStatusTimestamp = DateTime.Now.ToString("HH:mm:ss");
    }

    private void HandleVbsResult(string source, string successMessage, PowerShellInvocationResult result)
    {
        if (result.IsSuccess)
        {
            var details = BuildDetails(result);
            var primary = details.FirstOrDefault(d => !d.StartsWith("exitCode", StringComparison.OrdinalIgnoreCase))
                          ?? details.FirstOrDefault();
            _activityLog.LogSuccess(source, successMessage, details);
            VbsStatusMessage = primary ?? successMessage;
            IsVbsSuccess = true;
        }
        else
        {
            var message = result.Errors.FirstOrDefault() ?? "Operation failed.";
            _activityLog.LogWarning(source, message, BuildDetails(result));
            VbsStatusMessage = message;
            IsVbsSuccess = false;
        }

        VbsStatusTimestamp = DateTime.Now.ToString("HH:mm:ss");
    }

    private void HandleEtwResult(string source, string successMessage, PowerShellInvocationResult result)
    {
        if (result.IsSuccess)
        {
            var details = BuildDetails(result);
            var primary = details.FirstOrDefault(d => !d.StartsWith("exitCode", StringComparison.OrdinalIgnoreCase))
                          ?? details.FirstOrDefault();
            _activityLog.LogSuccess(source, successMessage, details);
            EtwStatusMessage = primary ?? successMessage;
            IsEtwSuccess = true;
        }
        else
        {
            var message = result.Errors.FirstOrDefault() ?? "Operation failed.";
            _activityLog.LogWarning(source, message, BuildDetails(result));
            EtwStatusMessage = message;
            IsEtwSuccess = false;
        }

        EtwStatusTimestamp = DateTime.Now.ToString("HH:mm:ss");
    }

    private static IReadOnlyList<string> BuildDetails(PowerShellInvocationResult result)
    {
        var raw = (result.Output.Any() ? result.Output : result.Errors)
            .Select(RemoveAnsi)
            .Select(l => l.Trim())
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToList();

        if (raw.Count == 0)
        {
            return new[] { $"exitCode: {result.ExitCode}" };
        }

        var kv = raw.Where(l => l.Contains(':')).ToList();
        var other = raw.Except(kv).ToList();

        var details = new List<string> { $"exitCode: {result.ExitCode}" };
        details.AddRange(kv);
        details.AddRange(other);

        const int max = 20;
        if (details.Count > max)
        {
            details = details.Take(max).Concat(new[] { "..." }).ToList();
        }

        return details;
    }

    private static string RemoveAnsi(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return AnsiRegex.Replace(value, string.Empty).TrimEnd();
    }
}

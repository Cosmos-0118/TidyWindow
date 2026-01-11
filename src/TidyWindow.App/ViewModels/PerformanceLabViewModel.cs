using System;
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

public sealed class PagefilePresetOption
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string DefaultDrive { get; init; } = "C:";
    public int? DefaultInitialMb { get; init; }
    public int? DefaultMaxMb { get; init; }
}

public sealed class SchedulerPresetOption
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string PriorityHint { get; init; } = "Normal";
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

    [ObservableProperty]
    private string pagefileStatusMessage = "Detect pagefile state to view mode.";

    [ObservableProperty]
    private string pagefileStatusTimestamp = "–";

    [ObservableProperty]
    private bool isPagefileSuccess;

    [ObservableProperty]
    private string schedulerStatusMessage = "Detect scheduler state to view affinity masks.";

    [ObservableProperty]
    private string schedulerStatusTimestamp = "–";

    [ObservableProperty]
    private bool isSchedulerSuccess;

    [ObservableProperty]
    private string directStorageStatusMessage = "Detect DirectStorage readiness (NVMe, GPU, driver).";

    [ObservableProperty]
    private string directStorageStatusTimestamp = "–";

    [ObservableProperty]
    private bool isDirectStorageSuccess;

    [ObservableProperty]
    private string autoTuneStatusMessage = "Start the monitoring loop to auto-apply presets.";

    [ObservableProperty]
    private string autoTuneStatusTimestamp = "–";

    [ObservableProperty]
    private bool isAutoTuneSuccess;

    [ObservableProperty]
    private string selectedPagefilePresetId = "SystemManaged";

    [ObservableProperty]
    private string targetPagefileDrive = "C:";

    [ObservableProperty]
    private int pagefileInitialMb = 4096;

    [ObservableProperty]
    private int pagefileMaxMb = 12288;

    [ObservableProperty]
    private bool runWorkingSetSweep = true;

    [ObservableProperty]
    private bool sweepPinnedApps;

    [ObservableProperty]
    private string schedulerProcessNames = "dwm;explorer";

    [ObservableProperty]
    private string selectedSchedulerPresetId = "LatencyBoost";

    [ObservableProperty]
    private bool boostIoPriority = true;

    [ObservableProperty]
    private bool boostThreadPriority = true;

    [ObservableProperty]
    private string autoTuneProcessNames = "steam;epicgameslauncher";

    [ObservableProperty]
    private string autoTunePresetId = "LatencyBoost";

    private static readonly Regex AnsiRegex = new("\\u001B\\[[0-9;]*m", RegexOptions.Compiled);

    public ObservableCollection<PerformanceTemplateOption> Templates { get; }
    public ObservableCollection<PagefilePresetOption> PagefilePresets { get; }
    public ObservableCollection<SchedulerPresetOption> SchedulerPresets { get; }

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
    public IAsyncRelayCommand DetectPagefileCommand { get; }
    public IAsyncRelayCommand ApplyPagefilePresetCommand { get; }
    public IAsyncRelayCommand SweepWorkingSetsCommand { get; }
    public IAsyncRelayCommand DetectSchedulerCommand { get; }
    public IAsyncRelayCommand ApplySchedulerPresetCommand { get; }
    public IAsyncRelayCommand RestoreSchedulerDefaultsCommand { get; }
    public IAsyncRelayCommand DetectDirectStorageCommand { get; }
    public IAsyncRelayCommand ApplyIoBoostCommand { get; }
    public IAsyncRelayCommand RestoreIoDefaultsCommand { get; }
    public IAsyncRelayCommand DetectAutoTuneCommand { get; }
    public IAsyncRelayCommand StartAutoTuneCommand { get; }
    public IAsyncRelayCommand StopAutoTuneCommand { get; }

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

        PagefilePresets = new ObservableCollection<PagefilePresetOption>
        {
            new() { Id = "SystemManaged", Name = "System managed", Description = "Let Windows size the pagefile automatically (recommended for most).", DefaultDrive = "C:" },
            new() { Id = "NVMePerformance", Name = "NVMe performance", Description = "Place a fixed pagefile on the fastest drive with a 3x headroom ceiling.", DefaultDrive = "C:", DefaultInitialMb = 4096, DefaultMaxMb = 16384 },
            new() { Id = "CustomFixed", Name = "Custom fixed", Description = "Use the fields below to define initial and max size explicitly.", DefaultDrive = "C:" }
        };
        SchedulerPresets = new ObservableCollection<SchedulerPresetOption>
        {
            new() { Id = "Balanced", Name = "Balanced", Description = "Normal priority with full-core affinity.", PriorityHint = "Normal" },
            new() { Id = "LatencyBoost", Name = "Latency boost", Description = "High priority across all cores for foreground apps.", PriorityHint = "High" },
            new() { Id = "Efficiency", Name = "Efficiency", Description = "Lower priority on first-half cores to save thermals.", PriorityHint = "BelowNormal" }
        };

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
        DetectPagefileCommand = new AsyncRelayCommand(DetectPagefileAsync, () => !IsBusy);
        ApplyPagefilePresetCommand = new AsyncRelayCommand(ApplyPagefilePresetAsync, () => !IsBusy);
        SweepWorkingSetsCommand = new AsyncRelayCommand(SweepWorkingSetsAsync, () => !IsBusy);
        DetectSchedulerCommand = new AsyncRelayCommand(DetectSchedulerAsync, () => !IsBusy);
        ApplySchedulerPresetCommand = new AsyncRelayCommand(ApplySchedulerPresetAsync, () => !IsBusy);
        RestoreSchedulerDefaultsCommand = new AsyncRelayCommand(RestoreSchedulerDefaultsAsync, () => !IsBusy);
        DetectDirectStorageCommand = new AsyncRelayCommand(DetectDirectStorageAsync, () => !IsBusy);
        ApplyIoBoostCommand = new AsyncRelayCommand(ApplyIoBoostAsync, () => !IsBusy);
        RestoreIoDefaultsCommand = new AsyncRelayCommand(RestoreIoDefaultsAsync, () => !IsBusy);
        DetectAutoTuneCommand = new AsyncRelayCommand(DetectAutoTuneAsync, () => !IsBusy);
        StartAutoTuneCommand = new AsyncRelayCommand(StartAutoTuneAsync, () => !IsBusy);
        StopAutoTuneCommand = new AsyncRelayCommand(StopAutoTuneAsync, () => !IsBusy);
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
        DetectPagefileCommand.NotifyCanExecuteChanged();
        ApplyPagefilePresetCommand.NotifyCanExecuteChanged();
        SweepWorkingSetsCommand.NotifyCanExecuteChanged();
        DetectSchedulerCommand.NotifyCanExecuteChanged();
        ApplySchedulerPresetCommand.NotifyCanExecuteChanged();
        RestoreSchedulerDefaultsCommand.NotifyCanExecuteChanged();
        DetectDirectStorageCommand.NotifyCanExecuteChanged();
        ApplyIoBoostCommand.NotifyCanExecuteChanged();
        RestoreIoDefaultsCommand.NotifyCanExecuteChanged();
        DetectAutoTuneCommand.NotifyCanExecuteChanged();
        StartAutoTuneCommand.NotifyCanExecuteChanged();
        StopAutoTuneCommand.NotifyCanExecuteChanged();
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

            var pagefileResult = await _service.DetectPagefileAsync().ConfigureAwait(true);
            HandlePagefileResult("PerformanceLab", "Pagefile status detected", pagefileResult);

            var schedulerResult = await _service.DetectSchedulerAffinityAsync().ConfigureAwait(true);
            HandleSchedulerResult("PerformanceLab", "Scheduler state captured", schedulerResult);

            var directStorageResult = await _service.DetectDirectStorageAsync().ConfigureAwait(true);
            HandleDirectStorageResult("PerformanceLab", "DirectStorage readiness checked", directStorageResult);

            var autoTuneResult = await _service.DetectAutoTuneAsync().ConfigureAwait(true);
            HandleAutoTuneResult("PerformanceLab", "Auto-tune loop inspected", autoTuneResult);

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

    private async Task DetectPagefileAsync()
    {
        await RunOperationAsync(async () =>
        {
            var result = await _service.DetectPagefileAsync().ConfigureAwait(true);
            HandlePagefileResult("PerformanceLab", "Pagefile status detected", result);
        }).ConfigureAwait(false);
    }

    private async Task ApplyPagefilePresetAsync()
    {
        await RunOperationAsync(async () =>
        {
            var preset = string.IsNullOrWhiteSpace(SelectedPagefilePresetId) ? "SystemManaged" : SelectedPagefilePresetId;
            var drive = string.IsNullOrWhiteSpace(TargetPagefileDrive) ? "C:" : TargetPagefileDrive;
            var result = await _service.ApplyPagefilePresetAsync(preset, drive, PagefileInitialMb, PagefileMaxMb, RunWorkingSetSweep, SweepPinnedApps).ConfigureAwait(true);
            HandlePagefileResult("PerformanceLab", $"Pagefile preset applied ({preset})", result);
        }).ConfigureAwait(false);
    }

    private async Task SweepWorkingSetsAsync()
    {
        await RunOperationAsync(async () =>
        {
            var result = await _service.SweepWorkingSetsAsync(SweepPinnedApps).ConfigureAwait(true);
            HandlePagefileResult("PerformanceLab", "Working sets swept", result);
        }).ConfigureAwait(false);
    }

    private async Task DetectSchedulerAsync()
    {
        await RunOperationAsync(async () =>
        {
            var result = await _service.DetectSchedulerAffinityAsync().ConfigureAwait(true);
            HandleSchedulerResult("PerformanceLab", "Scheduler state captured", result);
        }).ConfigureAwait(false);
    }

    private async Task ApplySchedulerPresetAsync()
    {
        await RunOperationAsync(async () =>
        {
            var preset = string.IsNullOrWhiteSpace(SelectedSchedulerPresetId) ? "Balanced" : SelectedSchedulerPresetId;
            var processes = SchedulerProcessNames ?? string.Empty;
            var result = await _service.ApplySchedulerAffinityAsync(preset, processes).ConfigureAwait(true);
            HandleSchedulerResult("PerformanceLab", $"Scheduler preset applied ({preset})", result);
        }).ConfigureAwait(false);
    }

    private async Task RestoreSchedulerDefaultsAsync()
    {
        await RunOperationAsync(async () =>
        {
            var result = await _service.RestoreSchedulerAffinityAsync().ConfigureAwait(true);
            HandleSchedulerResult("PerformanceLab", "Scheduler defaults restored", result);
        }).ConfigureAwait(false);
    }

    private async Task DetectDirectStorageAsync()
    {
        await RunOperationAsync(async () =>
        {
            var result = await _service.DetectDirectStorageAsync().ConfigureAwait(true);
            HandleDirectStorageResult("PerformanceLab", "DirectStorage readiness checked", result);
        }).ConfigureAwait(false);
    }

    private async Task ApplyIoBoostAsync()
    {
        await RunOperationAsync(async () =>
        {
            var result = await _service.ApplyIoPriorityBoostAsync(BoostIoPriority, BoostThreadPriority).ConfigureAwait(true);
            HandleDirectStorageResult("PerformanceLab", "I/O and thread boosts applied", result);
        }).ConfigureAwait(false);
    }

    private async Task RestoreIoDefaultsAsync()
    {
        await RunOperationAsync(async () =>
        {
            var result = await _service.RestoreIoPriorityAsync().ConfigureAwait(true);
            HandleDirectStorageResult("PerformanceLab", "I/O priorities restored", result);
        }).ConfigureAwait(false);
    }

    private async Task DetectAutoTuneAsync()
    {
        await RunOperationAsync(async () =>
        {
            var result = await _service.DetectAutoTuneAsync().ConfigureAwait(true);
            HandleAutoTuneResult("PerformanceLab", "Auto-tune loop inspected", result);
        }).ConfigureAwait(false);
    }

    private async Task StartAutoTuneAsync()
    {
        await RunOperationAsync(async () =>
        {
            var preset = string.IsNullOrWhiteSpace(AutoTunePresetId) ? "LatencyBoost" : AutoTunePresetId;
            var processes = AutoTuneProcessNames ?? string.Empty;
            var result = await _service.StartAutoTuneAsync(processes, preset).ConfigureAwait(true);
            HandleAutoTuneResult("PerformanceLab", $"Auto-tune loop armed ({preset})", result);
        }).ConfigureAwait(false);
    }

    private async Task StopAutoTuneAsync()
    {
        await RunOperationAsync(async () =>
        {
            var result = await _service.StopAutoTuneAsync().ConfigureAwait(true);
            HandleAutoTuneResult("PerformanceLab", "Auto-tune loop stopped and reverted", result);
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

    private void HandlePagefileResult(string source, string successMessage, PowerShellInvocationResult result)
    {
        if (result.IsSuccess)
        {
            var details = BuildDetails(result);
            var primary = details.FirstOrDefault(d => !d.StartsWith("exitCode", StringComparison.OrdinalIgnoreCase))
                          ?? details.FirstOrDefault();
            _activityLog.LogSuccess(source, successMessage, details);
            PagefileStatusMessage = primary ?? successMessage;
            IsPagefileSuccess = true;
        }
        else
        {
            var message = result.Errors.FirstOrDefault() ?? "Operation failed.";
            _activityLog.LogWarning(source, message, BuildDetails(result));
            PagefileStatusMessage = message;
            IsPagefileSuccess = false;
        }

        PagefileStatusTimestamp = DateTime.Now.ToString("HH:mm:ss");
    }

    private void HandleSchedulerResult(string source, string successMessage, PowerShellInvocationResult result)
    {
        if (result.IsSuccess)
        {
            var details = BuildDetails(result);
            var primary = details.FirstOrDefault(d => !d.StartsWith("exitCode", StringComparison.OrdinalIgnoreCase))
                          ?? details.FirstOrDefault();
            _activityLog.LogSuccess(source, successMessage, details);
            SchedulerStatusMessage = primary ?? successMessage;
            IsSchedulerSuccess = true;
        }
        else
        {
            var message = result.Errors.FirstOrDefault() ?? "Operation failed.";
            _activityLog.LogWarning(source, message, BuildDetails(result));
            SchedulerStatusMessage = message;
            IsSchedulerSuccess = false;
        }

        SchedulerStatusTimestamp = DateTime.Now.ToString("HH:mm:ss");
    }

    private void HandleDirectStorageResult(string source, string successMessage, PowerShellInvocationResult result)
    {
        if (result.IsSuccess)
        {
            var details = BuildDetails(result);
            var primary = details.FirstOrDefault(d => !d.StartsWith("exitCode", StringComparison.OrdinalIgnoreCase))
                          ?? details.FirstOrDefault();
            _activityLog.LogSuccess(source, successMessage, details);
            DirectStorageStatusMessage = primary ?? successMessage;
            IsDirectStorageSuccess = true;
        }
        else
        {
            var message = result.Errors.FirstOrDefault() ?? "Operation failed.";
            _activityLog.LogWarning(source, message, BuildDetails(result));
            DirectStorageStatusMessage = message;
            IsDirectStorageSuccess = false;
        }

        DirectStorageStatusTimestamp = DateTime.Now.ToString("HH:mm:ss");
    }

    private void HandleAutoTuneResult(string source, string successMessage, PowerShellInvocationResult result)
    {
        if (result.IsSuccess)
        {
            var details = BuildDetails(result);
            var primary = details.FirstOrDefault(d => !d.StartsWith("exitCode", StringComparison.OrdinalIgnoreCase))
                          ?? details.FirstOrDefault();
            _activityLog.LogSuccess(source, successMessage, details);
            AutoTuneStatusMessage = primary ?? successMessage;
            IsAutoTuneSuccess = true;
        }
        else
        {
            var message = result.Errors.FirstOrDefault() ?? "Operation failed.";
            _activityLog.LogWarning(source, message, BuildDetails(result));
            AutoTuneStatusMessage = message;
            IsAutoTuneSuccess = false;
        }

        AutoTuneStatusTimestamp = DateTime.Now.ToString("HH:mm:ss");
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

    partial void OnSelectedPagefilePresetIdChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var preset = PagefilePresets.FirstOrDefault(p => string.Equals(p.Id, value, StringComparison.OrdinalIgnoreCase));
        if (preset is null)
        {
            return;
        }

        TargetPagefileDrive = preset.DefaultDrive;
        if (preset.DefaultInitialMb.HasValue)
        {
            PagefileInitialMb = preset.DefaultInitialMb.Value;
        }

        if (preset.DefaultMaxMb.HasValue)
        {
            PagefileMaxMb = preset.DefaultMaxMb.Value;
        }
    }
}

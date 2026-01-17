using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Threading;
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
    private readonly PerformanceLabAutomationRunner _automationRunner;
    private readonly AutoTuneAutomationScheduler _autoTuneAutomation;
    private readonly PerformanceLabProcessListStore _processListStore;
    private readonly Dispatcher _dispatcher;
    private bool _suspendBootAutomationUpdate;
    private bool _suspendSchedulerProcessSync;
    private bool _suspendAutoTuneProcessSync;

    public Action<string>? ShowStatusAction { get; set; }

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
    private bool isInfoDialogVisible;

    [ObservableProperty]
    private string infoDialogTitle = "Step details";

    [ObservableProperty]
    private string infoDialogBody = "More information coming soon.";

    [ObservableProperty]
    private bool isApplyArmed;

    [ObservableProperty]
    private string applyGuardStatus = "Write actions are locked. Arm tweaks to enable apply buttons.";

    [ObservableProperty]
    private bool isBootAutomationEnabled;

    [ObservableProperty]
    private bool autoApplyPowerPlan;

    [ObservableProperty]
    private bool autoApplyServices;

    [ObservableProperty]
    private bool autoApplyHardware;

    [ObservableProperty]
    private bool autoApplyKernel;

    [ObservableProperty]
    private bool autoApplyVbs;

    [ObservableProperty]
    private bool autoApplyEtw;

    [ObservableProperty]
    private bool autoApplyScheduler;

    [ObservableProperty]
    private bool autoApplyAutoTune;

    [ObservableProperty]
    private string bootAutomationStatus = "Boot automation is off.";

    [ObservableProperty]
    private DateTimeOffset? bootAutomationLastRunUtc;

    [ObservableProperty]
    private string powerPlanStatusMessage = "Ultimate Performance is not active.";

    [ObservableProperty]
    private string powerPlanStatusTimestamp = "–";

    [ObservableProperty]
    private bool isPowerPlanSuccess;

    [ObservableProperty]
    private string serviceStatusMessage = "No service actions run yet.";

    [ObservableProperty]
    private string serviceStatusTimestamp = "–";

    [ObservableProperty]
    private bool isServiceSuccess;

    [ObservableProperty]
    private string hardwareStatusMessage = "Detect hardware reserved memory to view status.";

    [ObservableProperty]
    private string hardwareStatusTimestamp = "–";

    [ObservableProperty]
    private bool isHardwareSuccess;

    [ObservableProperty]
    private string kernelStatusMessage = "Detect kernel & boot state to view settings.";

    [ObservableProperty]
    private string kernelStatusTimestamp = "–";

    [ObservableProperty]
    private bool isKernelSuccess;

    [ObservableProperty]
    private string vbsStatusMessage = "Detect Core Isolation / VBS state to view status.";

    [ObservableProperty]
    private string vbsStatusTimestamp = "–";

    [ObservableProperty]
    private bool isVbsSuccess;

    [ObservableProperty]
    private string etwStatusMessage = "Detect ETW sessions to view active traces.";

    [ObservableProperty]
    private string etwStatusTimestamp = "–";

    [ObservableProperty]
    private bool isEtwSuccess;

    [ObservableProperty]
    private string schedulerStatusMessage = "Detect scheduler state to view affinity masks.";

    [ObservableProperty]
    private string schedulerStatusTimestamp = "–";

    [ObservableProperty]
    private bool isSchedulerSuccess;

    [ObservableProperty]
    private string autoTuneStatusMessage = "Start the monitoring loop to auto-apply presets.";

    [ObservableProperty]
    private string autoTuneStatusTimestamp = "–";

    [ObservableProperty]
    private bool isAutoTuneSuccess;

    [ObservableProperty]
    private string lastPowerPlanBackupPath = string.Empty;

    [ObservableProperty]
    private string lastServiceBackupPath = string.Empty;

    [ObservableProperty]
    private bool hasPowerPlanBackup;

    [ObservableProperty]
    private bool hasServiceBackup;

    public string PowerPlanStatusSimple => BuildSimpleStatus(IsUltimateActive, PowerPlanStatusMessage, "Ultimate Performance active");
    public string ServiceStatusSimple => BuildSimpleStatus(IsServiceSuccess, ServiceStatusMessage, "Service template applied");
    public string HardwareStatusSimple => BuildSimpleStatus(IsHardwareSuccess, HardwareStatusMessage, "Hardware reserved fix applied");
    public string KernelStatusSimple => BuildSimpleStatus(IsKernelSuccess, KernelStatusMessage, "Kernel preset applied");
    public string VbsStatusSimple => BuildSimpleStatus(IsVbsSuccess, VbsStatusMessage, "VBS/HVCI disabled");
    public string EtwStatusSimple => BuildSimpleStatus(IsEtwSuccess, EtwStatusMessage, "ETW cleanup applied");
    public string SchedulerStatusSimple => BuildSimpleStatus(IsSchedulerSuccess, SchedulerStatusMessage, "Scheduler preset applied");
    public string AutoTuneStatusSimple => BuildSimpleStatus(IsAutoTuneSuccess, AutoTuneStatusMessage, "Auto-tune monitor active");

    [ObservableProperty]
    private string schedulerProcessNames = "dwm;explorer";

    public ObservableCollection<string> SchedulerProcesses { get; }

    [ObservableProperty]
    private string newSchedulerProcessName = string.Empty;

    [ObservableProperty]
    private string? selectedSchedulerProcess;

    [ObservableProperty]
    private bool isSchedulerPickerVisible;

    [ObservableProperty]
    private SchedulerPresetOption? selectedSchedulerPreset;

    [ObservableProperty]
    private string selectedSchedulerPresetId = "LatencyBoost";

    [ObservableProperty]
    private string autoTuneProcessNames = "steam;epicgameslauncher";

    public ObservableCollection<string> AutoTuneProcesses { get; }

    [ObservableProperty]
    private string newAutoTuneProcessName = string.Empty;

    [ObservableProperty]
    private string? selectedAutoTuneProcess;

    [ObservableProperty]
    private bool isAutoTunePickerVisible;

    [ObservableProperty]
    private string autoTunePresetId = "LatencyBoost";

    private static readonly Regex AnsiRegex = new("\\u001B\\[[0-9;]*m", RegexOptions.Compiled);

    public ObservableCollection<PerformanceTemplateOption> Templates { get; }
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
    public IAsyncRelayCommand DetectSchedulerCommand { get; }
    public IAsyncRelayCommand ApplySchedulerPresetCommand { get; }
    public IAsyncRelayCommand RestoreSchedulerDefaultsCommand { get; }
    public IRelayCommand OpenSchedulerPickerCommand { get; }
    public IRelayCommand CloseSchedulerPickerCommand => closeSchedulerPickerCommand ??= new RelayCommand(() => IsSchedulerPickerVisible = false);
    public IRelayCommand AddSchedulerProcessCommand => addSchedulerProcessCommand ??= new RelayCommand(AddSchedulerProcess, () => !string.IsNullOrWhiteSpace(NewSchedulerProcessName));
    public IRelayCommand RemoveSchedulerProcessCommand => removeSchedulerProcessCommand ??= new RelayCommand(RemoveSelectedSchedulerProcess, () => !string.IsNullOrWhiteSpace(SelectedSchedulerProcess));
    public IAsyncRelayCommand DetectAutoTuneCommand { get; }
    public IAsyncRelayCommand StartAutoTuneCommand { get; }
    public IAsyncRelayCommand StopAutoTuneCommand { get; }
    public IRelayCommand OpenAutoTunePickerCommand { get; }
    public IRelayCommand CloseAutoTunePickerCommand => closeAutoTunePickerCommand ??= new RelayCommand(() => IsAutoTunePickerVisible = false);
    public IRelayCommand AddAutoTuneProcessCommand => addAutoTuneProcessCommand ??= new RelayCommand(AddAutoTuneProcess, () => !string.IsNullOrWhiteSpace(NewAutoTuneProcessName));
    public IRelayCommand RemoveAutoTuneProcessCommand => removeAutoTuneProcessCommand ??= new RelayCommand(RemoveSelectedAutoTuneProcess, () => !string.IsNullOrWhiteSpace(SelectedAutoTuneProcess));
    public IAsyncRelayCommand ApplyBootAutomationCommand { get; }
    public IAsyncRelayCommand RunBootAutomationNowCommand { get; }
    public IAsyncRelayCommand DisableAutomationCommand { get; }
    public IRelayCommand ShowStatusCommand { get; }
    public IRelayCommand ShowStepInfoCommand => showStepInfoCommand ??= new RelayCommand<string?>(ShowStepInfo);
    public IRelayCommand CloseInfoDialogCommand => closeInfoDialogCommand ??= new RelayCommand(CloseInfoDialog);

    private IRelayCommand? showStepInfoCommand;
    private IRelayCommand? closeInfoDialogCommand;
    private IRelayCommand? closeSchedulerPickerCommand;
    private IRelayCommand? addSchedulerProcessCommand;
    private IRelayCommand? removeSchedulerProcessCommand;
    private IRelayCommand? closeAutoTunePickerCommand;
    private IRelayCommand? addAutoTuneProcessCommand;
    private IRelayCommand? removeAutoTuneProcessCommand;

    public PerformanceLabViewModel(IPerformanceLabService service, ActivityLogService activityLog, PerformanceLabAutomationRunner automationRunner, AutoTuneAutomationScheduler autoTuneAutomation)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _activityLog = activityLog ?? throw new ArgumentNullException(nameof(activityLog));
        _automationRunner = automationRunner ?? throw new ArgumentNullException(nameof(automationRunner));
        _autoTuneAutomation = autoTuneAutomation ?? throw new ArgumentNullException(nameof(autoTuneAutomation));
        _dispatcher = Dispatcher.CurrentDispatcher;
        _processListStore = new PerformanceLabProcessListStore();

        SchedulerProcesses = new ObservableCollection<string>();
        SchedulerProcesses.CollectionChanged += OnSchedulerProcessesChanged;

        AutoTuneProcesses = new ObservableCollection<string>();
        AutoTuneProcesses.CollectionChanged += OnAutoTuneProcessesChanged;

        var persistedProcesses = _processListStore.Get();
        if (!string.IsNullOrWhiteSpace(persistedProcesses.SchedulerProcessNames))
        {
            SchedulerProcessNames = persistedProcesses.SchedulerProcessNames;
        }

        if (!string.IsNullOrWhiteSpace(persistedProcesses.AutoTuneProcessNames))
        {
            AutoTuneProcessNames = persistedProcesses.AutoTuneProcessNames;
        }

        Templates = new ObservableCollection<PerformanceTemplateOption>
        {
            new() { Id = "Balanced", Name = "Balanced", Description = "Stops telemetry/Xbox/consumer services; sets them to Manual.", ServiceCount = 6 },
            new() { Id = "Minimal", Name = "Minimal", Description = "Adds CDPSvc/OneSync/Wallet to the balanced set; disables instead of manual.", ServiceCount = 10 }
        };
        SelectedTemplate = Templates.FirstOrDefault();

        SchedulerPresets = new ObservableCollection<SchedulerPresetOption>
        {
            new() { Id = "Balanced", Name = "Balanced", Description = "Normal priority with full-core affinity.", PriorityHint = "Normal" },
            new() { Id = "LatencyBoost", Name = "Latency boost", Description = "High priority across all cores for foreground apps.", PriorityHint = "High" },
            new() { Id = "Efficiency", Name = "Efficiency", Description = "Lower priority on first-half cores to save thermals.", PriorityHint = "BelowNormal" }
        };
        SelectedSchedulerPreset = SchedulerPresets.FirstOrDefault(p => string.Equals(p.Id, SelectedSchedulerPresetId, StringComparison.OrdinalIgnoreCase))
                                  ?? SchedulerPresets.FirstOrDefault();

        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        EnableUltimatePlanCommand = new AsyncRelayCommand(EnableUltimatePlanAsync, CanRunApplyAction);
        RestorePowerPlanCommand = new AsyncRelayCommand(RestorePowerPlanAsync, CanRunApplyAction);
        ApplyServiceTemplateCommand = new AsyncRelayCommand<PerformanceTemplateOption?>(ApplyServiceTemplateAsync, _ => CanRunApplyAction());
        RestoreServicesCommand = new AsyncRelayCommand(RestoreServicesAsync, CanRunApplyAction);
        DetectHardwareReservedCommand = new AsyncRelayCommand(DetectHardwareReservedAsync, () => !IsBusy);
        ApplyHardwareFixCommand = new AsyncRelayCommand(ApplyHardwareFixAsync, CanRunApplyAction);
        RestoreCompressionCommand = new AsyncRelayCommand(RestoreCompressionAsync, CanRunApplyAction);
        ApplyKernelPresetCommand = new AsyncRelayCommand(ApplyKernelPresetAsync, CanRunApplyAction);
        RestoreKernelDefaultsCommand = new AsyncRelayCommand(RestoreKernelDefaultsAsync, CanRunApplyAction);
        DetectVbsHvciCommand = new AsyncRelayCommand(DetectVbsHvciAsync, () => !IsBusy);
        DisableVbsHvciCommand = new AsyncRelayCommand(DisableVbsHvciAsync, CanRunApplyAction);
        RestoreVbsHvciCommand = new AsyncRelayCommand(RestoreVbsHvciAsync, CanRunApplyAction);
        DetectEtwSessionsCommand = new AsyncRelayCommand(DetectEtwTracingAsync, () => !IsBusy);
        CleanupEtwMinimalCommand = new AsyncRelayCommand(() => CleanupEtwAsync("Minimal"), CanRunApplyAction);
        CleanupEtwAggressiveCommand = new AsyncRelayCommand(() => CleanupEtwAsync("Aggressive"), CanRunApplyAction);
        RestoreEtwDefaultsCommand = new AsyncRelayCommand(RestoreEtwDefaultsAsync, CanRunApplyAction);
        DetectSchedulerCommand = new AsyncRelayCommand(DetectSchedulerAsync, () => !IsBusy);
        ApplySchedulerPresetCommand = new AsyncRelayCommand(ApplySchedulerPresetAsync, CanRunApplyAction);
        RestoreSchedulerDefaultsCommand = new AsyncRelayCommand(RestoreSchedulerDefaultsAsync, CanRunApplyAction);
        OpenSchedulerPickerCommand = new RelayCommand(() =>
        {
            IsSchedulerPickerVisible = true;
            NewSchedulerProcessName = string.Empty;
        });
        DetectAutoTuneCommand = new AsyncRelayCommand(DetectAutoTuneAsync, () => !IsBusy);
        StartAutoTuneCommand = new AsyncRelayCommand(StartAutoTuneAsync, CanRunApplyAction);
        StopAutoTuneCommand = new AsyncRelayCommand(StopAutoTuneAsync, () => !IsBusy);
        OpenAutoTunePickerCommand = new RelayCommand(() =>
        {
            IsAutoTunePickerVisible = true;
            NewAutoTuneProcessName = string.Empty;
        });
        ApplyBootAutomationCommand = new AsyncRelayCommand(ApplyBootAutomationAsync, () => !IsBusy);
        RunBootAutomationNowCommand = new AsyncRelayCommand(RunBootAutomationNowAsync, CanRunApplyAction);
        DisableAutomationCommand = new AsyncRelayCommand(DisableAutomationAsync, () => !IsBusy);
        ShowStatusCommand = new RelayCommand(ShowStatus);

        LoadBootAutomationSettings(_automationRunner.CurrentSettings);
        _automationRunner.SettingsChanged += OnBootAutomationSettingsChanged;
        _autoTuneAutomation.SettingsChanged += OnAutoTuneAutomationSettingsChanged;
        UpdateAutoTuneAutomationStatus(_autoTuneAutomation.CurrentSettings);

        // Seed process lists from the initial raw string values.
        SyncSchedulerProcessesFromString(SchedulerProcessNames);
        SyncAutoTuneProcessesFromString(AutoTuneProcessNames);
    }

    private bool CanRunApplyAction() => IsApplyArmed && !IsBusy;

    private bool EnsureApplyArmed(string reason)
    {
        if (IsApplyArmed)
        {
            return true;
        }

        ApplyGuardStatus = reason;
        return false;
    }

    private static string BuildSimpleStatus(bool isSuccess, string detail, string appliedLabel)
    {
        if (isSuccess)
        {
            return string.IsNullOrWhiteSpace(detail) ? appliedLabel : detail;
        }

        if (string.IsNullOrWhiteSpace(detail))
        {
            return "Not applied";
        }

        // If the detail is just a detect/readout, keep it, but still report as not applied.
        return detail;
    }

    private void NotifyCommandStates()
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
        DetectSchedulerCommand.NotifyCanExecuteChanged();
        ApplySchedulerPresetCommand.NotifyCanExecuteChanged();
        RestoreSchedulerDefaultsCommand.NotifyCanExecuteChanged();
        DetectAutoTuneCommand.NotifyCanExecuteChanged();
        StartAutoTuneCommand.NotifyCanExecuteChanged();
        StopAutoTuneCommand.NotifyCanExecuteChanged();
        ApplyBootAutomationCommand.NotifyCanExecuteChanged();
        RunBootAutomationNowCommand.NotifyCanExecuteChanged();
        DisableAutomationCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsBusyChanged(bool value)
    {
        NotifyCommandStates();
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
            var existingServiceMessage = ServiceStatusMessage;
            var existingServiceSuccess = IsServiceSuccess;

            var plan = await _service.GetPowerPlanStatusAsync().ConfigureAwait(true);
            IsUltimateActive = plan.IsUltimateActive;
            PowerPlanHeadline = plan.IsUltimateActive ? "Ultimate Performance active" : "Standard plan active";
            PowerPlanDetails = string.IsNullOrWhiteSpace(plan.ActiveSchemeName)
                ? "Unable to read current scheme."
                : $"{plan.ActiveSchemeName} ({plan.ActiveSchemeId ?? "unknown GUID"})";
            LastPowerPlanBackupPath = plan.LastBackupPath ?? string.Empty;
            HasPowerPlanBackup = !string.IsNullOrWhiteSpace(LastPowerPlanBackupPath);
            PowerPlanStatusMessage = plan.IsUltimateActive
                ? "Ultimate Performance is active"
                : (!string.IsNullOrWhiteSpace(plan.ActiveSchemeName) ? $"Active: {plan.ActiveSchemeName}" : "Active plan detected");
            IsPowerPlanSuccess = plan.IsUltimateActive;
            PowerPlanStatusTimestamp = DateTime.Now.ToString("HH:mm:ss");

            var services = _service.GetServiceSlimmingStatus();
            ServicesHeadline = services.LastBackupPath is null
                ? "No service backups yet"
                : "Service backups available";
            ServicesDetails = services.LastBackupPath is null
                ? "Apply a template to create a baseline backup."
                : $"Latest backup: {Path.GetFileName(services.LastBackupPath)}";
            LastServiceBackupPath = services.LastBackupPath ?? string.Empty;
            HasServiceBackup = !string.IsNullOrWhiteSpace(LastServiceBackupPath);
            if (HasServiceBackup)
            {
                // Preserve the last action message (e.g., the template applied) instead of overwriting it with a generic backup note.
                var hasCustomMessage = !string.IsNullOrWhiteSpace(existingServiceMessage)
                    && !string.Equals(existingServiceMessage, "No service actions run yet.", StringComparison.OrdinalIgnoreCase);
                ServiceStatusMessage = hasCustomMessage
                    ? existingServiceMessage
                    : $"Backup: {Path.GetFileName(LastServiceBackupPath)}";
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

            var vbsResult = await _service.DetectVbsHvciAsync().ConfigureAwait(true);
            HandleVbsResult("PerformanceLab", "VBS/HVCI status captured", vbsResult, markApplied: false);

            var etwResult = await _service.DetectEtwTracingAsync().ConfigureAwait(true);
            HandleEtwResult("PerformanceLab", "ETW sessions inspected", etwResult, markApplied: false);

            var schedulerResult = await _service.DetectSchedulerAffinityAsync().ConfigureAwait(true);
            HandleSchedulerResult("PerformanceLab", "Scheduler state captured", schedulerResult, markApplied: false);

            var autoTuneResult = await _service.DetectAutoTuneAsync().ConfigureAwait(true);
            HandleAutoTuneResult("PerformanceLab", "Auto-tune loop inspected", autoTuneResult, markApplied: false);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task EnableUltimatePlanAsync()
    {
        if (!EnsureApplyArmed("Arm tweaks to apply or restore power plans."))
        {
            return;
        }

        await RunOperationAsync(async () =>
        {
            var result = await _service.EnableUltimatePowerPlanAsync().ConfigureAwait(true);
            HandlePlanResult("PerformanceLab", "Ultimate Performance enabled", result, ultimateActiveOnSuccess: true);
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
        if (!EnsureApplyArmed("Arm tweaks to change hardware reserved memory."))
        {
            return;
        }

        await RunOperationAsync(async () =>
        {
            var result = await _service.ApplyHardwareReservedFixAsync().ConfigureAwait(true);
            HandleHardwareResult("PerformanceLab", "Cleared BCD memory caps and disabled compression", result);
        }).ConfigureAwait(false);
    }

    private async Task RestoreCompressionAsync()
    {
        if (!EnsureApplyArmed("Arm tweaks to restore memory compression."))
        {
            return;
        }

        await RunOperationAsync(async () =>
        {
            var result = await _service.RestoreMemoryCompressionAsync().ConfigureAwait(true);
            HandleHardwareResult("PerformanceLab", "Memory compression restored", result);
        }).ConfigureAwait(false);
    }

    private async Task ApplyKernelPresetAsync()
    {
        if (!EnsureApplyArmed("Arm tweaks to change kernel and boot flags."))
        {
            return;
        }

        await RunOperationAsync(async () =>
        {
            var result = await _service.ApplyKernelBootActionAsync("Recommended").ConfigureAwait(true);
            HandleKernelResult("PerformanceLab", "Kernel preset applied (dynamic tick off, platform clock on, linear57 on)", result);
        }).ConfigureAwait(false);
    }

    private async Task RestoreKernelDefaultsAsync()
    {
        if (!EnsureApplyArmed("Arm tweaks to restore kernel defaults."))
        {
            return;
        }

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
            HandleVbsResult("PerformanceLab", "VBS/HVCI status captured", result, markApplied: false);
        }).ConfigureAwait(false);
    }

    private async Task DisableVbsHvciAsync()
    {
        if (!EnsureApplyArmed("Arm tweaks to change VBS/HVCI."))
        {
            return;
        }

        await RunOperationAsync(async () =>
        {
            var result = await _service.DisableVbsHvciAsync().ConfigureAwait(true);
            HandleVbsResult("PerformanceLab", "VBS/HVCI disabled (hypervisor off, HVCI off)", result);
        }).ConfigureAwait(false);
    }

    private async Task RestoreVbsHvciAsync()
    {
        if (!EnsureApplyArmed("Arm tweaks to change VBS/HVCI."))
        {
            return;
        }

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
            HandleEtwResult("PerformanceLab", "ETW sessions inspected", result, markApplied: false);
        }).ConfigureAwait(false);
    }

    private async Task CleanupEtwAsync(string mode)
    {
        var tier = string.IsNullOrWhiteSpace(mode) ? "Minimal" : mode;

        if (!EnsureApplyArmed("Arm tweaks to stop or restore ETW sessions."))
        {
            return;
        }

        await RunOperationAsync(async () =>
        {
            var result = await _service.CleanupEtwTracingAsync(tier).ConfigureAwait(true);
            HandleEtwResult("PerformanceLab", $"ETW sessions stopped ({tier})", result);
        }).ConfigureAwait(false);
    }

    private async Task RestoreEtwDefaultsAsync()
    {
        if (!EnsureApplyArmed("Arm tweaks to stop or restore ETW sessions."))
        {
            return;
        }

        await RunOperationAsync(async () =>
        {
            var result = await _service.RestoreEtwTracingAsync().ConfigureAwait(true);
            HandleEtwResult("PerformanceLab", "ETW defaults restored", result);
        }).ConfigureAwait(false);
    }

    private async Task RestorePowerPlanAsync()
    {
        if (!EnsureApplyArmed("Arm tweaks to apply or restore power plans."))
        {
            return;
        }

        await RunOperationAsync(async () =>
        {
            var result = await _service.RestorePowerPlanAsync().ConfigureAwait(true);
            HandlePlanResult("PerformanceLab", "Power plan restored", result, ultimateActiveOnSuccess: false);
            await RefreshAsync().ConfigureAwait(true);
        }).ConfigureAwait(false);
    }

    private async Task ApplyServiceTemplateAsync(PerformanceTemplateOption? option)
    {
        if (!EnsureApplyArmed("Arm tweaks to change services."))
        {
            return;
        }

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
        if (!EnsureApplyArmed("Arm tweaks to change services."))
        {
            return;
        }

        await RunOperationAsync(async () =>
        {
            var result = await _service.RestoreServicesAsync(LastServiceBackupPath).ConfigureAwait(true);
            HandleServiceResult("PerformanceLab", "Services restored", result);
            await RefreshAsync().ConfigureAwait(true);
        }).ConfigureAwait(false);
    }

    private async Task DetectSchedulerAsync()
    {
        await RunOperationAsync(async () =>
        {
            var result = await _service.DetectSchedulerAffinityAsync().ConfigureAwait(true);
            HandleSchedulerResult("PerformanceLab", "Scheduler state captured", result, markApplied: false);
        }).ConfigureAwait(false);
    }

    private async Task ApplySchedulerPresetAsync()
    {
        if (!EnsureApplyArmed("Arm tweaks to change scheduler presets."))
        {
            return;
        }

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
        if (!EnsureApplyArmed("Arm tweaks to change scheduler presets."))
        {
            return;
        }

        await RunOperationAsync(async () =>
        {
            var result = await _service.RestoreSchedulerAffinityAsync().ConfigureAwait(true);
            HandleSchedulerResult("PerformanceLab", "Scheduler defaults restored", result);
        }).ConfigureAwait(false);
    }

    private async Task DetectAutoTuneAsync()
    {
        await RunOperationAsync(async () =>
        {
            var result = await _service.DetectAutoTuneAsync().ConfigureAwait(true);
            HandleAutoTuneResult("PerformanceLab", "Auto-tune loop inspected", result, markApplied: false);
        }).ConfigureAwait(false);
    }

    private async Task StartAutoTuneAsync()
    {
        if (!EnsureApplyArmed("Arm tweaks to start the auto-tune monitor."))
        {
            return;
        }

        await RunOperationAsync(async () =>
        {
            var preset = string.IsNullOrWhiteSpace(AutoTunePresetId) ? "LatencyBoost" : AutoTunePresetId;
            var processes = SerializeAutoTuneProcesses();
            AutoTuneProcessNames = processes;
            var settings = new AutoTuneAutomationSettings(true, processes, preset, _autoTuneAutomation.CurrentSettings.LastRunUtc);
            var run = await _autoTuneAutomation.ApplySettingsAsync(settings, queueRunImmediately: true).ConfigureAwait(true);

            if (run?.InvocationResult is { } invocation)
            {
                HandleAutoTuneResult("PerformanceLab", $"Auto-tune automation ran ({preset})", invocation);
            }
            else if (run?.WasSkipped == true)
            {
                AutoTuneStatusMessage = string.IsNullOrWhiteSpace(run.SkipReason)
                    ? "Auto-tune automation armed; waiting for the next matching process."
                    : run.SkipReason;
                AutoTuneStatusTimestamp = DateTime.Now.ToString("HH:mm:ss");
                IsAutoTuneSuccess = false;
            }
            else
            {
                AutoTuneStatusMessage = "Auto-tune automation armed; will trigger immediately on launch.";
                AutoTuneStatusTimestamp = DateTime.Now.ToString("HH:mm:ss");
                IsAutoTuneSuccess = true;
            }
        }).ConfigureAwait(false);
    }

    private async Task StopAutoTuneAsync()
    {
        await RunOperationAsync(async () =>
        {
            var disabled = _autoTuneAutomation.CurrentSettings with { AutomationEnabled = false };
            await _autoTuneAutomation.ApplySettingsAsync(disabled, queueRunImmediately: false).ConfigureAwait(true);

            var result = await _service.StopAutoTuneAsync().ConfigureAwait(true);
            HandleAutoTuneResult("PerformanceLab", "Auto-tune automation stopped", result, markApplied: false);
            IsAutoTuneSuccess = false;
        }).ConfigureAwait(false);
    }

    private async Task DisableAutomationAsync()
    {
        await RunOperationAsync(async () =>
        {
            // Disable boot automation and auto-tune monitoring in one click.
            var bootOff = PerformanceLabAutomationSettings.Default;
            await _automationRunner.ApplySettingsAsync(bootOff, runIfDue: false).ConfigureAwait(true);
            LoadBootAutomationSettings(bootOff);

            var tuneOff = AutoTuneAutomationSettings.Default;
            await _autoTuneAutomation.ApplySettingsAsync(tuneOff, queueRunImmediately: false).ConfigureAwait(true);
            UpdateAutoTuneAutomationStatus(tuneOff);

            var result = await _service.StopAutoTuneAsync().ConfigureAwait(true);
            HandleAutoTuneResult("PerformanceLab", "Auto-tune automation stopped", result, markApplied: false);
            IsAutoTuneSuccess = false;
            AutoTuneStatusMessage = "Auto-tune automation is off.";
            AutoTuneStatusTimestamp = DateTime.Now.ToString("HH:mm:ss");
            ApplyGuardStatus = "All automation disabled. Arm tweaks to re-enable actions.";
        }).ConfigureAwait(false);
    }

    private async Task ApplyBootAutomationAsync()
    {
        var snapshot = BuildAutomationSnapshot();
        var enabled = IsBootAutomationEnabled && snapshot.HasActions;

        if (enabled && !EnsureApplyArmed("Arm tweaks to enable boot automation."))
        {
            return;
        }

        var settings = new PerformanceLabAutomationSettings(enabled, _automationRunner.GetCurrentBootMarker(), BootAutomationLastRunUtc, snapshot).Normalize();

        await _automationRunner.ApplySettingsAsync(settings, runIfDue: false).ConfigureAwait(true);
        LoadBootAutomationSettings(settings);
    }

    private async Task RunBootAutomationNowAsync()
    {
        if (!EnsureApplyArmed("Arm tweaks to run automation now."))
        {
            return;
        }

        var snapshot = BuildAutomationSnapshot();
        var enabled = IsBootAutomationEnabled && snapshot.HasActions;
        var settings = new PerformanceLabAutomationSettings(enabled, _automationRunner.GetCurrentBootMarker(), BootAutomationLastRunUtc, snapshot).Normalize();

        await _automationRunner.ApplySettingsAsync(settings, runIfDue: false).ConfigureAwait(true);
        var result = await _automationRunner.RunNowAsync().ConfigureAwait(true);
        BootAutomationLastRunUtc = result.ExecutedAtUtc;
        UpdateBootAutomationStatus(_automationRunner.CurrentSettings);
    }

    private PerformanceLabAutomationSnapshot BuildAutomationSnapshot()
    {
        return new PerformanceLabAutomationSnapshot(
            ApplyUltimatePlan: AutoApplyPowerPlan,
            ApplyServiceTemplate: AutoApplyServices,
            ApplyHardwareFix: AutoApplyHardware,
            ApplyKernelPreset: AutoApplyKernel,
            ApplyVbsDisable: AutoApplyVbs,
            ApplyEtwCleanup: AutoApplyEtw,
            ApplySchedulerPreset: AutoApplyScheduler,
            ApplyAutoTune: AutoApplyAutoTune,
            ServiceTemplateId: SelectedTemplate?.Id ?? "Balanced",
            SchedulerPresetId: SelectedSchedulerPresetId ?? "Balanced",
            SchedulerProcessNames: SchedulerProcessNames ?? string.Empty,
            AutoTuneProcessNames: AutoTuneProcessNames ?? string.Empty,
            AutoTunePresetId: AutoTunePresetId ?? "LatencyBoost",
            EtwMode: "Minimal").Normalize();
    }

    private void AddSchedulerProcess()
    {
        var value = (NewSchedulerProcessName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var normalized = NormalizeProcessToken(value);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        if (!SchedulerProcesses.Any(p => string.Equals(p, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            SchedulerProcesses.Add(normalized);
        }

        NewSchedulerProcessName = string.Empty;
    }

    private void RemoveSelectedSchedulerProcess()
    {
        if (SelectedSchedulerProcess is null)
        {
            return;
        }

        var existing = SchedulerProcesses.FirstOrDefault(p => string.Equals(p, SelectedSchedulerProcess, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            SchedulerProcesses.Remove(existing);
        }

        SelectedSchedulerProcess = null;
    }

    private void OnSchedulerProcessesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_suspendSchedulerProcessSync)
        {
            return;
        }

        SchedulerProcessNames = SerializeSchedulerProcesses();
        PersistProcessLists();
        addSchedulerProcessCommand?.NotifyCanExecuteChanged();
        removeSchedulerProcessCommand?.NotifyCanExecuteChanged();
    }

    private void SyncSchedulerProcessesFromString(string? raw)
    {
        try
        {
            _suspendSchedulerProcessSync = true;
            SchedulerProcesses.Clear();

            if (!string.IsNullOrWhiteSpace(raw))
            {
                var tokens = raw
                    .Split(new[] { ';', ',', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(NormalizeProcessToken)
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .Distinct(StringComparer.OrdinalIgnoreCase);

                foreach (var token in tokens)
                {
                    SchedulerProcesses.Add(token);
                }
            }

            SchedulerProcessNames = SerializeSchedulerProcesses();
            SelectedSchedulerProcess = SchedulerProcesses.FirstOrDefault();
        }
        finally
        {
            _suspendSchedulerProcessSync = false;
            addSchedulerProcessCommand?.NotifyCanExecuteChanged();
            removeSchedulerProcessCommand?.NotifyCanExecuteChanged();
            PersistProcessLists();
        }
    }

    private string SerializeSchedulerProcesses()
    {
        return string.Join(';', SchedulerProcesses.Where(p => !string.IsNullOrWhiteSpace(p)));
    }

    private void AddAutoTuneProcess()
    {
        var value = (NewAutoTuneProcessName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var normalized = NormalizeProcessToken(value);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        if (!AutoTuneProcesses.Any(p => string.Equals(p, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            AutoTuneProcesses.Add(normalized);
        }

        NewAutoTuneProcessName = string.Empty;
    }

    private void RemoveSelectedAutoTuneProcess()
    {
        if (SelectedAutoTuneProcess is null)
        {
            return;
        }

        var existing = AutoTuneProcesses.FirstOrDefault(p => string.Equals(p, SelectedAutoTuneProcess, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            AutoTuneProcesses.Remove(existing);
        }

        SelectedAutoTuneProcess = null;
    }

    private void OnAutoTuneProcessesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_suspendAutoTuneProcessSync)
        {
            return;
        }

        AutoTuneProcessNames = SerializeAutoTuneProcesses();
        addAutoTuneProcessCommand?.NotifyCanExecuteChanged();
        removeAutoTuneProcessCommand?.NotifyCanExecuteChanged();
        PersistProcessLists();
    }

    private void SyncAutoTuneProcessesFromString(string? raw)
    {
        try
        {
            _suspendAutoTuneProcessSync = true;
            AutoTuneProcesses.Clear();

            if (!string.IsNullOrWhiteSpace(raw))
            {
                var tokens = raw
                    .Split(new[] { ';', ',', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(NormalizeProcessToken)
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .Distinct(StringComparer.OrdinalIgnoreCase);

                foreach (var token in tokens)
                {
                    AutoTuneProcesses.Add(token);
                }
            }

            AutoTuneProcessNames = SerializeAutoTuneProcesses();
            SelectedAutoTuneProcess = AutoTuneProcesses.FirstOrDefault();
        }
        finally
        {
            _suspendAutoTuneProcessSync = false;
            addAutoTuneProcessCommand?.NotifyCanExecuteChanged();
            removeAutoTuneProcessCommand?.NotifyCanExecuteChanged();
            PersistProcessLists();
        }
    }

    private string SerializeAutoTuneProcesses()
    {
        return string.Join(';', AutoTuneProcesses.Where(p => !string.IsNullOrWhiteSpace(p)));
    }

    private void PersistProcessLists()
    {
        _processListStore.Save(SchedulerProcessNames ?? string.Empty, AutoTuneProcessNames ?? string.Empty);
    }

    private static string NormalizeProcessToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return string.Empty;
        }

        var trimmed = token.Trim();
        return trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? trimmed : $"{trimmed}.exe";
    }

    private void LoadBootAutomationSettings(PerformanceLabAutomationSettings settings)
    {
        _suspendBootAutomationUpdate = true;
        IsBootAutomationEnabled = settings.AutomationEnabled;
        BootAutomationLastRunUtc = settings.LastRunUtc;
        var snapshot = settings.Snapshot ?? PerformanceLabAutomationSnapshot.Empty;
        AutoApplyPowerPlan = snapshot.ApplyUltimatePlan;
        AutoApplyServices = snapshot.ApplyServiceTemplate;
        AutoApplyHardware = snapshot.ApplyHardwareFix;
        AutoApplyKernel = snapshot.ApplyKernelPreset;
        AutoApplyVbs = snapshot.ApplyVbsDisable;
        AutoApplyEtw = snapshot.ApplyEtwCleanup;
        AutoApplyScheduler = snapshot.ApplySchedulerPreset;
        AutoApplyAutoTune = snapshot.ApplyAutoTune;
        if (!string.IsNullOrWhiteSpace(snapshot.SchedulerProcessNames))
        {
            SchedulerProcessNames = snapshot.SchedulerProcessNames;
        }

        if (!string.IsNullOrWhiteSpace(snapshot.AutoTuneProcessNames))
        {
            AutoTuneProcessNames = snapshot.AutoTuneProcessNames;
        }
        _suspendBootAutomationUpdate = false;
        UpdateBootAutomationStatus(settings);
    }

    private void OnBootAutomationSettingsChanged(object? sender, PerformanceLabAutomationSettings settings)
    {
        RunOnUi(() => LoadBootAutomationSettings(settings));
    }

    private void UpdateBootAutomationStatus(PerformanceLabAutomationSettings settings)
    {
        var hasActions = settings.Snapshot?.HasActions == true;
        if (!settings.AutomationEnabled)
        {
            BootAutomationStatus = "Boot automation is off.";
            return;
        }

        if (!hasActions)
        {
            BootAutomationStatus = "Select steps to replay, then save automation.";
            return;
        }

        var lastRun = settings.LastRunUtc;
        BootAutomationStatus = lastRun is null
            ? "Will reapply your Performance Lab steps on the next boot."
            : $"Will reapply your Performance Lab steps on the next boot. Last run {FormatRelative(lastRun.Value)}.";
    }

    private void OnAutoTuneAutomationSettingsChanged(object? sender, AutoTuneAutomationSettings settings)
    {
        RunOnUi(() =>
        {
            var rawNames = string.IsNullOrWhiteSpace(settings.ProcessNames)
                ? AutoTuneProcessNames
                : settings.ProcessNames;
            AutoTuneProcessNames = rawNames;
            SyncAutoTuneProcessesFromString(rawNames);
            AutoTunePresetId = string.IsNullOrWhiteSpace(settings.PresetId) ? AutoTunePresetId : settings.PresetId;

            if (settings.AutomationEnabled)
            {
                var lastRun = settings.LastRunUtc;
                var lastLabel = lastRun is null
                    ? "First run pending."
                    : $"Last run {FormatRelative(lastRun.Value)}.";
                AutoTuneStatusMessage = $"Auto-tune automation active (instant on launch). {lastLabel}";
                AutoTuneStatusTimestamp = DateTime.Now.ToString("HH:mm:ss");
                return;
            }

            if (!IsAutoTuneSuccess)
            {
                AutoTuneStatusMessage = "Auto-tune automation is off.";
                AutoTuneStatusTimestamp = DateTime.Now.ToString("HH:mm:ss");
            }
        });

        if (settings.AutomationEnabled)
        {
            var lastRun = settings.LastRunUtc;
            var lastLabel = lastRun is null
                ? "First run pending."
                : $"Last run {FormatRelative(lastRun.Value)}.";
            AutoTuneStatusMessage = $"Auto-tune automation active (instant on launch). {lastLabel}";
            AutoTuneStatusTimestamp = DateTime.Now.ToString("HH:mm:ss");
            return;
        }

        if (!IsAutoTuneSuccess)
        {
            AutoTuneStatusMessage = "Auto-tune automation is off.";
            AutoTuneStatusTimestamp = DateTime.Now.ToString("HH:mm:ss");
        }
    }

    private void UpdateAutoTuneAutomationStatus(AutoTuneAutomationSettings settings)
    {
        OnAutoTuneAutomationSettingsChanged(this, settings);
    }

    private void RunOnUi(Action action)
    {
        if (_dispatcher.CheckAccess())
        {
            action();
            return;
        }

        _dispatcher.Invoke(action);
    }

    partial void OnIsApplyArmedChanged(bool value)
    {
        ApplyGuardStatus = value
            ? "Write actions armed for this session."
            : "Write actions are locked. Arm tweaks to enable apply buttons.";
        NotifyCommandStates();
    }

    partial void OnNewSchedulerProcessNameChanged(string value)
    {
        addSchedulerProcessCommand?.NotifyCanExecuteChanged();
    }

    partial void OnSelectedSchedulerProcessChanged(string? value)
    {
        removeSchedulerProcessCommand?.NotifyCanExecuteChanged();
    }

    partial void OnNewAutoTuneProcessNameChanged(string value)
    {
        addAutoTuneProcessCommand?.NotifyCanExecuteChanged();
    }

    partial void OnSelectedAutoTuneProcessChanged(string? value)
    {
        removeAutoTuneProcessCommand?.NotifyCanExecuteChanged();
    }

    partial void OnIsBootAutomationEnabledChanged(bool value)
    {
        if (_suspendBootAutomationUpdate)
        {
            return;
        }

        var current = _automationRunner.CurrentSettings;
        UpdateBootAutomationStatus(current with { AutomationEnabled = value });
    }

    private static string FormatRelative(DateTimeOffset timestamp)
    {
        var delta = DateTimeOffset.UtcNow - timestamp;
        if (delta < TimeSpan.FromMinutes(1))
        {
            return "just now";
        }

        if (delta < TimeSpan.FromHours(1))
        {
            var minutes = Math.Max(1, (int)Math.Round(delta.TotalMinutes));
            return minutes == 1 ? "1 minute ago" : $"{minutes} minutes ago";
        }

        if (delta < TimeSpan.FromDays(1))
        {
            var hours = Math.Max(1, (int)Math.Round(delta.TotalHours));
            return hours == 1 ? "1 hour ago" : $"{hours} hours ago";
        }

        var days = Math.Max(1, (int)Math.Round(delta.TotalDays));
        return days == 1 ? "1 day ago" : $"{days} days ago";
    }

    private void ShowStatus()
    {
        var sb = new StringBuilder();
        AppendStatus(sb, "Power plan", PowerPlanStatusSimple, PowerPlanStatusMessage);
        AppendStatus(sb, "Services", ServiceStatusSimple, ServiceStatusMessage);
        AppendStatus(sb, "Hardware reserved", HardwareStatusSimple, HardwareStatusMessage);
        AppendStatus(sb, "Kernel & boot", KernelStatusSimple, KernelStatusMessage);
        AppendStatus(sb, "Core Isolation (VBS/HVCI)", VbsStatusSimple, VbsStatusMessage);
        AppendStatus(sb, "ETW tracing", EtwStatusSimple, EtwStatusMessage);
        AppendStatus(sb, "Scheduler & affinity", SchedulerStatusSimple, SchedulerStatusMessage);
        AppendStatus(sb, "Auto-tune", AutoTuneStatusSimple, AutoTuneStatusMessage);

        var message = sb.ToString().TrimEnd();
        if (string.IsNullOrWhiteSpace(message))
        {
            message = "No status available.";
        }

        ShowStatusAction?.Invoke(message);
    }

    private static void AppendStatus(StringBuilder sb, string name, string simple, string detail)
    {
        sb.AppendLine($"{name}: {simple}");
        if (!string.IsNullOrWhiteSpace(detail))
        {
            sb.AppendLine($"  {detail}");
        }
        sb.AppendLine();
    }

    private void ShowStepInfo(string? stepId)
    {
        var (title, body) = stepId switch
        {
            "PowerPlan" => ("Ultimate Performance plan", "Switches to the Ultimate Performance scheme so the CPU stays in higher P-states, parking is disabled, and boost stays aggressive. Expect snappier frame times and fewer downclock dips on AC power. Restore returns your old plan if you want to go back."),
            "Services" => ("Service templates", "Backs up your service state, then disables telemetry/Xbox/updater services that wake disks or steal CPU. This trims background CPU wakeups and I/O chatter, improving game loads and latency. Restore replays the backup to undo changes."),
            "Hardware" => ("Hardware reserved memory", "Clears truncatememory/maxmem flags so Windows can address all installed RAM and optionally disables memory compression to cut latency. More usable RAM reduces paging; disabling compression lowers CPU spikes at the cost of slightly higher memory use. Re-run detect after reboot to confirm."),
            "Kernel" => ("Kernel & boot controls", "Sets BCD flags: disables dynamic tick, enables platform clock, sets stable tscsyncpolicy, and enables linearaddress57 on large-memory systems. These reduce timer jitter and scheduling stalls, helping frametime consistency. Reboot required; Restore removes the BCD tweaks."),
            "Vbs" => ("Core Isolation (VBS/HVCI)", "Turns VBS/HVCI off for maximum performance or restores it for security. Disabling frees CPU cycles and reduces DPC/interrupt overhead, which can improve gaming latency. Changes persist across boots; reboot to take effect."),
            "Etw" => ("ETW tracing cleanup", "Enumerates running ETW sessions and stops noisy loggers that keep disks busy or hit the CPU. This lowers background I/O and context switches, which can smooth frametimes. Restore restarts the allowlisted baseline; you can re-detect to verify."),
            "Scheduler" => ("Scheduler & affinity", "Applies priority/affinity masks to listed processes (dwm, explorer, games) so UI threads stay responsive and workloads stick to intended cores. This reduces contention and stutter; Restore resets masks to Normal on all cores."),
            "AutoTune" => ("Auto-tune loop", "Runs a watcher that detects your listed games/launchers and auto-applies the chosen scheduler preset, then logs before/after. This keeps game processes prioritized the moment they start. Stop & revert ends the watcher and restores defaults."),
            _ => ("Performance Lab", "Quick explanation for this step is not available. Please try again or refresh."),
        };

        InfoDialogTitle = title;
        InfoDialogBody = body;
        IsInfoDialogVisible = true;
    }

    private void CloseInfoDialog()
    {
        IsInfoDialogVisible = false;
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

    private void HandlePlanResult(string source, string successMessage, PowerShellInvocationResult result, bool ultimateActiveOnSuccess)
    {
        if (result.IsSuccess)
        {
            _activityLog.LogSuccess(source, successMessage, BuildDetails(result));
            PowerPlanStatusMessage = successMessage;
            IsPowerPlanSuccess = ultimateActiveOnSuccess;
            IsUltimateActive = ultimateActiveOnSuccess;
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

    private void HandleHardwareResult(string source, string successMessage, PowerShellInvocationResult result, bool markApplied = true)
    {
        if (result.IsSuccess)
        {
            var details = BuildDetails(result);
            var primary = details.FirstOrDefault(d => !d.StartsWith("exitCode", StringComparison.OrdinalIgnoreCase))
                          ?? details.FirstOrDefault();
            _activityLog.LogSuccess(source, successMessage, details);
            HardwareStatusMessage = primary ?? successMessage;
            IsHardwareSuccess = markApplied && result.IsSuccess;
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

    private void HandleKernelResult(string source, string successMessage, PowerShellInvocationResult result, bool markApplied = true)
    {
        if (result.IsSuccess)
        {
            var details = BuildDetails(result);
            var primary = details.FirstOrDefault(d => !d.StartsWith("exitCode", StringComparison.OrdinalIgnoreCase))
                          ?? details.FirstOrDefault();
            _activityLog.LogSuccess(source, successMessage, details);
            KernelStatusMessage = primary ?? successMessage;
            IsKernelSuccess = markApplied && result.IsSuccess;
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

    private void HandleVbsResult(string source, string successMessage, PowerShellInvocationResult result, bool markApplied = true)
    {
        if (result.IsSuccess)
        {
            var details = BuildDetails(result);
            var primary = details.FirstOrDefault(d => !d.StartsWith("exitCode", StringComparison.OrdinalIgnoreCase))
                          ?? details.FirstOrDefault();
            _activityLog.LogSuccess(source, successMessage, details);
            VbsStatusMessage = primary ?? successMessage;
            IsVbsSuccess = markApplied && result.IsSuccess;
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

    private void HandleEtwResult(string source, string successMessage, PowerShellInvocationResult result, bool markApplied = true)
    {
        if (result.IsSuccess)
        {
            var details = BuildDetails(result);
            var primary = details.FirstOrDefault(d => !d.StartsWith("exitCode", StringComparison.OrdinalIgnoreCase))
                          ?? details.FirstOrDefault();
            _activityLog.LogSuccess(source, successMessage, details);
            EtwStatusMessage = primary ?? successMessage;
            IsEtwSuccess = markApplied && result.IsSuccess;
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

    private void HandleSchedulerResult(string source, string successMessage, PowerShellInvocationResult result, bool markApplied = true)
    {
        if (result.IsSuccess)
        {
            var details = BuildDetails(result);
            var primary = details.FirstOrDefault(d => !d.StartsWith("exitCode", StringComparison.OrdinalIgnoreCase))
                          ?? details.FirstOrDefault();
            _activityLog.LogSuccess(source, successMessage, details);
            SchedulerStatusMessage = primary ?? successMessage;
            IsSchedulerSuccess = markApplied && result.IsSuccess;
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

    private void HandleAutoTuneResult(string source, string successMessage, PowerShellInvocationResult result, bool markApplied = true)
    {
        if (result.IsSuccess)
        {
            var details = BuildDetails(result);
            var primary = details.FirstOrDefault(d => !d.StartsWith("exitCode", StringComparison.OrdinalIgnoreCase))
                          ?? details.FirstOrDefault();
            _activityLog.LogSuccess(source, successMessage, details);
            AutoTuneStatusMessage = primary ?? successMessage;
            IsAutoTuneSuccess = markApplied && result.IsSuccess;
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

    partial void OnSelectedSchedulerPresetIdChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var preset = SchedulerPresets.FirstOrDefault(p => string.Equals(p.Id, value, StringComparison.OrdinalIgnoreCase));
        if (preset is null)
        {
            return;
        }

        SelectedSchedulerPreset = preset;
    }

    partial void OnSelectedSchedulerPresetChanged(SchedulerPresetOption? value)
    {
        if (value is null)
        {
            return;
        }

        SelectedSchedulerPresetId = value.Id;
    }

    partial void OnPowerPlanStatusMessageChanged(string value)
    {
        OnPropertyChanged(nameof(PowerPlanStatusSimple));
    }

    partial void OnServiceStatusMessageChanged(string value)
    {
        OnPropertyChanged(nameof(ServiceStatusSimple));
    }

    partial void OnHardwareStatusMessageChanged(string value)
    {
        OnPropertyChanged(nameof(HardwareStatusSimple));
    }

    partial void OnKernelStatusMessageChanged(string value)
    {
        OnPropertyChanged(nameof(KernelStatusSimple));
    }

    partial void OnVbsStatusMessageChanged(string value)
    {
        OnPropertyChanged(nameof(VbsStatusSimple));
    }

    partial void OnEtwStatusMessageChanged(string value)
    {
        OnPropertyChanged(nameof(EtwStatusSimple));
    }

    partial void OnSchedulerStatusMessageChanged(string value)
    {
        OnPropertyChanged(nameof(SchedulerStatusSimple));
    }

    partial void OnAutoTuneStatusMessageChanged(string value)
    {
        OnPropertyChanged(nameof(AutoTuneStatusSimple));
    }

    partial void OnIsUltimateActiveChanged(bool value)
    {
        OnPropertyChanged(nameof(PowerPlanStatusSimple));
    }

    partial void OnIsPowerPlanSuccessChanged(bool value)
    {
        OnPropertyChanged(nameof(PowerPlanStatusSimple));
    }

    partial void OnIsServiceSuccessChanged(bool value)
    {
        OnPropertyChanged(nameof(ServiceStatusSimple));
    }

    partial void OnIsHardwareSuccessChanged(bool value)
    {
        OnPropertyChanged(nameof(HardwareStatusSimple));
    }

    partial void OnIsKernelSuccessChanged(bool value)
    {
        OnPropertyChanged(nameof(KernelStatusSimple));
    }

    partial void OnIsVbsSuccessChanged(bool value)
    {
        OnPropertyChanged(nameof(VbsStatusSimple));
    }

    partial void OnIsEtwSuccessChanged(bool value)
    {
        OnPropertyChanged(nameof(EtwStatusSimple));
    }

    partial void OnIsSchedulerSuccessChanged(bool value)
    {
        OnPropertyChanged(nameof(SchedulerStatusSimple));
    }

    partial void OnIsAutoTuneSuccessChanged(bool value)
    {
        OnPropertyChanged(nameof(AutoTuneStatusSimple));
    }
}

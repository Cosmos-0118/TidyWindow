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
    private readonly PerformanceLabService _service;
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
    private bool isPowerPlanSuccess;

    [ObservableProperty]
    private bool isServiceSuccess;

    [ObservableProperty]
    private string powerPlanStatusTimestamp = "–";

    [ObservableProperty]
    private string serviceStatusTimestamp = "–";

    private static readonly Regex AnsiRegex = new("\\u001B\\[[0-9;]*m", RegexOptions.Compiled);

    public ObservableCollection<PerformanceTemplateOption> Templates { get; }

    [ObservableProperty]
    private PerformanceTemplateOption? selectedTemplate;

    public IAsyncRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand EnableUltimatePlanCommand { get; }
    public IAsyncRelayCommand RestorePowerPlanCommand { get; }
    public IAsyncRelayCommand<PerformanceTemplateOption?> ApplyServiceTemplateCommand { get; }
    public IAsyncRelayCommand RestoreServicesCommand { get; }

    public PerformanceLabViewModel(PerformanceLabService service, ActivityLogService activityLog)
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
    }

    partial void OnIsBusyChanged(bool value)
    {
        EnableUltimatePlanCommand.NotifyCanExecuteChanged();
        RestorePowerPlanCommand.NotifyCanExecuteChanged();
        ApplyServiceTemplateCommand.NotifyCanExecuteChanged();
        RestoreServicesCommand.NotifyCanExecuteChanged();
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
            ServiceStatusMessage = HasServiceBackup
                ? $"Backup: {Path.GetFileName(services.LastBackupPath)}"
                : "No service backup yet";
            IsServiceSuccess = HasServiceBackup;
            ServiceStatusTimestamp = DateTime.Now.ToString("HH:mm:ss");
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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TidyWindow.Core.Maintenance;

namespace TidyWindow.App.Services;

/// <summary>
/// Periodically checks package inventory and applies updates for the configured maintenance targets.
/// </summary>
public sealed class MaintenanceAutoUpdateScheduler : IDisposable
{
    private readonly MaintenanceAutomationSettingsStore _store;
    private readonly PackageInventoryService _inventoryService;
    private readonly PackageMaintenanceService _maintenanceService;
    private readonly UserPreferencesService _preferences;
    private readonly ActivityLogService _activityLog;
    private readonly IAutomationWorkTracker _workTracker;
    private readonly SemaphoreSlim _runLock = new(1, 1);
    private System.Threading.Timer? _timer;
    private MaintenanceAutomationSettings _settings;
    private bool _disposed;

    public MaintenanceAutoUpdateScheduler(
        MaintenanceAutomationSettingsStore store,
        PackageInventoryService inventoryService,
        PackageMaintenanceService maintenanceService,
        UserPreferencesService preferences,
        ActivityLogService activityLog,
        IAutomationWorkTracker workTracker)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _inventoryService = inventoryService ?? throw new ArgumentNullException(nameof(inventoryService));
        _maintenanceService = maintenanceService ?? throw new ArgumentNullException(nameof(maintenanceService));
        _preferences = preferences ?? throw new ArgumentNullException(nameof(preferences));
        _activityLog = activityLog ?? throw new ArgumentNullException(nameof(activityLog));
        _workTracker = workTracker ?? throw new ArgumentNullException(nameof(workTracker));

        _settings = _store.Get().Normalize();
        ConfigureTimer();
    }

    public MaintenanceAutomationSettings CurrentSettings => _settings;

    public event EventHandler<MaintenanceAutomationSettings>? SettingsChanged;

    public async Task<MaintenanceAutomationRunResult?> ApplySettingsAsync(MaintenanceAutomationSettings settings, bool runImmediately, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        var normalized = settings?.Normalize() ?? MaintenanceAutomationSettings.Default;
        _settings = normalized;
        _store.Save(normalized);
        ConfigureTimer();
        OnSettingsChanged();

        if (runImmediately && normalized.AutomationEnabled)
        {
            return await RunOnceInternalAsync(false, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    public Task<MaintenanceAutomationRunResult> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return RunOnceInternalAsync(false, cancellationToken);
    }

    private async Task<MaintenanceAutomationRunResult> RunOnceInternalAsync(bool isBackground, CancellationToken cancellationToken)
    {
        var readinessBlocker = GetReadinessBlocker();
        if (readinessBlocker is not null)
        {
            return LogAndReturnSkip(readinessBlocker);
        }

        var timeout = isBackground ? 0 : Timeout.Infinite;
        if (!await _runLock.WaitAsync(timeout, cancellationToken).ConfigureAwait(false))
        {
            return LogAndReturnSkip("Another run is in progress");
        }

        Guid workToken = Guid.Empty;
        try
        {
            var snapshot = await TryLoadInventoryAsync(cancellationToken).ConfigureAwait(false);
            if (snapshot is null)
            {
                return LogAndReturnSkip("Inventory refresh failed");
            }

            var plan = BuildPlan(snapshot);
            var timestamp = DateTimeOffset.UtcNow;

            if (plan.Count == 0)
            {
                UpdateLastRun(timestamp);
                var emptyResult = MaintenanceAutomationRunResult.Create(timestamp, Array.Empty<MaintenanceAutomationActionResult>());
                LogRunResult(emptyResult);
                return emptyResult;
            }

            workToken = _workTracker.BeginWork(AutomationWorkType.Maintenance, "Maintenance auto-update run");
            var actions = await ExecutePlanAsync(plan, cancellationToken).ConfigureAwait(false);

            UpdateLastRun(timestamp);
            var runResult = MaintenanceAutomationRunResult.Create(timestamp, actions);
            LogRunResult(runResult);
            return runResult;
        }
        finally
        {
            if (workToken != Guid.Empty)
            {
                _workTracker.CompleteWork(workToken);
            }

            _runLock.Release();
        }
    }

    private string? GetReadinessBlocker()
    {
        if (!_settings.AutomationEnabled)
        {
            return "Automation disabled";
        }

        if (!_settings.UpdateAllPackages && (_settings.Targets.IsDefaultOrEmpty || _settings.Targets.Length == 0))
        {
            return "No packages selected";
        }

        if (IsMaintenanceWorkActive())
        {
            return "Maintenance busy";
        }

        return null;
    }

    private bool IsMaintenanceWorkActive()
    {
        return _workTracker.GetActiveWork().Any(item => item.Type == AutomationWorkType.Maintenance);
    }

    private async Task<PackageInventorySnapshot?> TryLoadInventoryAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _inventoryService.GetInventoryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _activityLog.LogError("Maintenance automation", $"Unable to load inventory: {ex.Message}");
            return null;
        }
    }

    private IReadOnlyList<AutomationPlanEntry> BuildPlan(PackageInventorySnapshot snapshot)
    {
        var candidates = snapshot.Packages
            .Where(static package => package.IsUpdateAvailable)
            .ToList();

        if (candidates.Count == 0)
        {
            return Array.Empty<AutomationPlanEntry>();
        }

        HashSet<string> selectedKeys;
        if (_settings.UpdateAllPackages)
        {
            selectedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            selectedKeys = _settings.Targets.IsDefault
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                : _settings.Targets
                    .Where(static target => target.IsValid)
                    .Select(static target => target.Key)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        var list = new List<AutomationPlanEntry>(candidates.Count);
        foreach (var package in candidates)
        {
            var key = MaintenanceAutomationTarget.BuildKey(package.Manager, package.PackageIdentifier);
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            if (!_settings.UpdateAllPackages && selectedKeys.Count > 0 && !selectedKeys.Contains(key))
            {
                continue;
            }

            var suppression = _preferences.GetMaintenanceSuppression(package.Manager, package.PackageIdentifier);
            if (suppression is not null
                && string.Equals(suppression.Reason, MaintenanceSuppressionReasons.ManualUpgradeRequired, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var requiresAdmin = package.Catalog?.RequiresAdmin ?? false;
            var display = string.IsNullOrWhiteSpace(package.Catalog?.DisplayName) ? package.Name : package.Catalog!.DisplayName;
            list.Add(new AutomationPlanEntry(
                package.Manager,
                package.PackageIdentifier,
                display,
                requiresAdmin,
                package.AvailableVersion ?? package.InstalledVersion,
                package.InstalledVersion));
        }

        return list;
    }

    private async Task<IReadOnlyList<MaintenanceAutomationActionResult>> ExecutePlanAsync(IReadOnlyList<AutomationPlanEntry> plan, CancellationToken cancellationToken)
    {
        var actions = new List<MaintenanceAutomationActionResult>(plan.Count);

        foreach (var entry in plan)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var request = new PackageMaintenanceRequest(
                    entry.Manager,
                    entry.PackageId,
                    entry.DisplayName,
                    entry.RequiresAdministrator,
                    entry.TargetVersion);

                var result = await _maintenanceService.UpdateAsync(request, cancellationToken).ConfigureAwait(false);
                var summary = string.IsNullOrWhiteSpace(result.Summary) ? "Update completed." : result.Summary.Trim();
                actions.Add(new MaintenanceAutomationActionResult(entry.Manager, entry.PackageId, entry.DisplayName, result.Attempted, result.Success, summary));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                actions.Add(new MaintenanceAutomationActionResult(entry.Manager, entry.PackageId, entry.DisplayName, false, false, ex.Message));
            }
        }

        return actions;
    }

    private void ConfigureTimer()
    {
        _timer?.Dispose();
        _timer = null;

        if (!_settings.AutomationEnabled)
        {
            return;
        }

        if (!_settings.UpdateAllPackages && (_settings.Targets.IsDefaultOrEmpty || _settings.Targets.Length == 0))
        {
            return;
        }

        var interval = TimeSpan.FromMinutes(Math.Clamp(_settings.IntervalMinutes, MaintenanceAutomationSettings.MinimumIntervalMinutes, MaintenanceAutomationSettings.MaximumIntervalMinutes));
        var dueTime = interval;
        if (_settings.LastRunUtc is { } lastRun)
        {
            var elapsed = DateTimeOffset.UtcNow - lastRun;
            dueTime = elapsed >= interval ? TimeSpan.Zero : interval - elapsed;
        }

        _timer = new System.Threading.Timer(OnTimerTick, null, dueTime, interval);
    }

    private void OnTimerTick(object? state)
    {
        _ = RunOnceInternalAsync(true, CancellationToken.None);
    }

    private void UpdateLastRun(DateTimeOffset timestamp)
    {
        _settings = _settings.WithLastRun(timestamp);
        _store.Save(_settings);
        OnSettingsChanged();
    }

    private MaintenanceAutomationRunResult LogAndReturnSkip(string reason)
    {
        var result = MaintenanceAutomationRunResult.Skipped(DateTimeOffset.UtcNow, reason);
        LogRunResult(result);
        return result;
    }

    private void LogRunResult(MaintenanceAutomationRunResult result)
    {
        if (result.WasSkipped)
        {
            var message = string.IsNullOrWhiteSpace(result.SkipReason)
                ? "Automation run skipped."
                : $"Automation run skipped: {result.SkipReason}.";
            _activityLog.LogInformation("Maintenance automation", message);
            return;
        }

        if (result.Actions.Count == 0)
        {
            _activityLog.LogInformation("Maintenance automation", "Automation ran but no packages required updates.");
            return;
        }

        var failures = result.Actions.Count(action => !action.Success);
        var successCount = result.Actions.Count - failures;
        var details = BuildActionDetails(result.Actions);

        if (failures == 0)
        {
            var message = successCount == 1
                ? "Automation updated 1 package."
                : $"Automation updated {successCount} packages.";
            _activityLog.LogSuccess("Maintenance automation", message, details);
        }
        else
        {
            var message = failures == 1
                ? "Automation completed with 1 failing package."
                : $"Automation completed with {failures} failing packages.";
            _activityLog.LogWarning("Maintenance automation", message, details);
        }
    }

    private static IEnumerable<string> BuildActionDetails(IReadOnlyList<MaintenanceAutomationActionResult> actions)
    {
        foreach (var action in actions)
        {
            var status = action.Success
                ? "Success"
                : action.Attempted
                    ? "Failed"
                    : "Not attempted";

            var detail = string.IsNullOrWhiteSpace(action.Message)
                ? status
                : $"{status} — {action.Message.Trim()}";

            yield return $"{action.DisplayName}: {detail}";
        }
    }

    private void OnSettingsChanged()
    {
        SettingsChanged?.Invoke(this, _settings);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(MaintenanceAutoUpdateScheduler));
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer?.Dispose();
        _runLock.Dispose();
    }

    private sealed record AutomationPlanEntry(
        string Manager,
        string PackageId,
        string DisplayName,
        bool RequiresAdministrator,
        string? TargetVersion,
        string InstalledVersion);
}

public sealed record MaintenanceAutomationActionResult(
    string Manager,
    string PackageId,
    string DisplayName,
    bool Attempted,
    bool Success,
    string Message);

public sealed record MaintenanceAutomationRunResult(
    DateTimeOffset ExecutedAtUtc,
    IReadOnlyList<MaintenanceAutomationActionResult> Actions,
    bool WasSkipped,
    string? SkipReason)
{
    public static MaintenanceAutomationRunResult Create(DateTimeOffset timestamp, IReadOnlyList<MaintenanceAutomationActionResult> actions)
        => new(timestamp, actions, false, null);

    public static MaintenanceAutomationRunResult Skipped(DateTimeOffset timestamp, string reason)
        => new(timestamp, Array.Empty<MaintenanceAutomationActionResult>(), true, reason);
}

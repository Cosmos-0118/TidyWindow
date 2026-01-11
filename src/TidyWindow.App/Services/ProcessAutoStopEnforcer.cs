using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TidyWindow.Core.Processes;

namespace TidyWindow.App.Services;

/// <summary>
/// Periodically enforces auto-stop preferences by stopping their backing Windows services.
/// </summary>
public sealed class ProcessAutoStopEnforcer : IDisposable
{
    private static readonly TimeSpan UpcomingRunLeadTime = TimeSpan.FromMinutes(1);

    private readonly ProcessStateStore _stateStore;
    private readonly ProcessControlService _controlService;
    private readonly TaskControlService _taskControlService;
    private readonly ActivityLogService _activityLog;
    private readonly ServiceResolver _serviceResolver;
    private readonly Lazy<ProcessCatalogSnapshot> _catalogSnapshot;
    private readonly Lazy<IReadOnlyDictionary<string, ProcessCatalogEntry>> _catalogLookup;
    private readonly SemaphoreSlim _runLock = new(1, 1);
    private System.Threading.Timer? _timer;
    private CancellationTokenSource? _nextRunNotificationCts;
    private ProcessAutomationSettings _settings;
    private bool _disposed;

    public ProcessAutoStopEnforcer(ProcessStateStore stateStore, ProcessControlService controlService, TaskControlService taskControlService, ActivityLogService activityLog, ProcessCatalogParser catalogParser, ServiceResolver serviceResolver)
    {
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _controlService = controlService ?? throw new ArgumentNullException(nameof(controlService));
        _taskControlService = taskControlService ?? throw new ArgumentNullException(nameof(taskControlService));
        _activityLog = activityLog ?? throw new ArgumentNullException(nameof(activityLog));
        _serviceResolver = serviceResolver ?? throw new ArgumentNullException(nameof(serviceResolver));
        if (catalogParser is null)
        {
            throw new ArgumentNullException(nameof(catalogParser));
        }

        _catalogSnapshot = new Lazy<ProcessCatalogSnapshot>(catalogParser.LoadSnapshot, isThreadSafe: true);
        _catalogLookup = new Lazy<IReadOnlyDictionary<string, ProcessCatalogEntry>>(
            () => _catalogSnapshot.Value.Entries.ToDictionary(entry => entry.Identifier, StringComparer.OrdinalIgnoreCase),
            isThreadSafe: true);
        _settings = _stateStore.GetAutomationSettings();
        ConfigureTimer();
    }

    public ProcessAutomationSettings CurrentSettings => _settings;

    public event EventHandler<ProcessAutomationSettings>? SettingsChanged;

    public async Task<ProcessAutoStopResult?> ApplySettingsAsync(ProcessAutomationSettings settings, bool enforceImmediately, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var normalized = settings.Normalize();
        _settings = normalized;
        _stateStore.SaveAutomationSettings(normalized);
        ConfigureTimer();
        OnSettingsChanged();

        if (enforceImmediately && normalized.AutoStopEnabled)
        {
            return await RunOnceInternalAsync(false, allowWhenDisabled: false, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    public Task<ProcessAutoStopResult> RunOnceAsync(bool allowWhenDisabled = false, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return RunOnceInternalAsync(false, allowWhenDisabled, cancellationToken);
    }

    private async Task<ProcessAutoStopResult> RunOnceInternalAsync(bool isBackground, bool allowWhenDisabled, CancellationToken cancellationToken)
    {
        if (!_settings.AutoStopEnabled && !allowWhenDisabled)
        {
            return ProcessAutoStopResult.Skipped(DateTimeOffset.UtcNow);
        }

        var timeout = isBackground ? 0 : Timeout.Infinite;
        if (!await _runLock.WaitAsync(timeout, cancellationToken).ConfigureAwait(false))
        {
            return ProcessAutoStopResult.Skipped(DateTimeOffset.UtcNow);
        }

        try
        {
            var (actionableTargets, skippedTargets) = GetAutoStopTargets();

            var timestamp = DateTimeOffset.UtcNow;
            if (actionableTargets.Length == 0)
            {
                UpdateLastRun(timestamp);
                var skippedResult = ProcessAutoStopResult.Create(timestamp, Array.Empty<ProcessAutoStopActionResult>());
                LogRunResult(skippedResult, skippedTargets);
                return skippedResult;
            }

            var actions = new List<ProcessAutoStopActionResult>(actionableTargets.Length);
            foreach (var target in actionableTargets)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (target.IsTask)
                {
                    var taskPattern = target.TaskPattern ?? target.ProcessId;
                    try
                    {
                        var taskResult = await _taskControlService.StopAndDisableAsync(taskPattern).ConfigureAwait(false);

                        var success = taskResult.Success || taskResult.NotFound || taskResult.AccessDenied; // Missing or protected tasks are not treated as issues.
                        var message = taskResult.Success
                            ? (taskResult.Actions.Count == 0 ? "Task disabled." : string.Join("; ", taskResult.Actions))
                            : (taskResult.NotFound
                                ? "No tasks matched this pattern on this PC."
                                : (taskResult.AccessDenied ? "System denied (protected task)." : taskResult.Message));

                        actions.Add(new ProcessAutoStopActionResult(target.Label, success, message));
                    }
                    catch (Exception ex)
                    {
                        actions.Add(new ProcessAutoStopActionResult(target.Label, false, ex.Message));
                    }

                    continue;
                }

                var stopResult = await _controlService.StopAsync(target.ServiceName!, cancellationToken: cancellationToken).ConfigureAwait(false);
                actions.Add(new ProcessAutoStopActionResult(target.Label, stopResult.Success, stopResult.Message));
            }

            UpdateLastRun(timestamp);
            var runResult = ProcessAutoStopResult.Create(timestamp, actions);
            LogRunResult(runResult, skippedTargets);
            return runResult;
        }
        finally
        {
            _runLock.Release();
        }
    }

    private void ConfigureTimer()
    {
        _timer?.Dispose();
        _timer = null;
        CancelUpcomingRunNotification();

        if (!_settings.AutoStopEnabled)
        {
            return;
        }

        var interval = TimeSpan.FromMinutes(Math.Clamp(_settings.AutoStopIntervalMinutes, ProcessAutomationSettings.MinimumIntervalMinutes, ProcessAutomationSettings.MaximumIntervalMinutes));

        var dueTime = interval;
        if (_settings.LastRunUtc is { } lastRunUtc)
        {
            var elapsed = DateTimeOffset.UtcNow - lastRunUtc;
            dueTime = elapsed >= interval
                ? TimeSpan.Zero
                : interval - elapsed;
        }

        _timer = new System.Threading.Timer(OnTimerTick, null, dueTime, interval);
        ScheduleUpcomingRunNotification(dueTime);
    }

    private void OnTimerTick(object? state)
    {
        _ = RunTimerCycleAsync();
    }

    private void UpdateLastRun(DateTimeOffset timestamp)
    {
        _settings = _settings.WithLastRun(timestamp);
        _stateStore.SaveAutomationSettings(_settings);
        OnSettingsChanged();
    }

    private async Task RunTimerCycleAsync()
    {
        await RunOnceInternalAsync(true, allowWhenDisabled: false, CancellationToken.None).ConfigureAwait(false);

        if (!_settings.AutoStopEnabled)
        {
            CancelUpcomingRunNotification();
            return;
        }

        var interval = TimeSpan.FromMinutes(Math.Clamp(_settings.AutoStopIntervalMinutes, ProcessAutomationSettings.MinimumIntervalMinutes, ProcessAutomationSettings.MaximumIntervalMinutes));
        ScheduleUpcomingRunNotification(interval);
    }

    private void ScheduleUpcomingRunNotification(TimeSpan dueTime)
    {
        CancelUpcomingRunNotification();

        if (!_settings.AutoStopEnabled || dueTime <= TimeSpan.Zero)
        {
            return;
        }

        var delay = dueTime - UpcomingRunLeadTime;
        if (delay < TimeSpan.Zero)
        {
            delay = TimeSpan.Zero;
        }

        _nextRunNotificationCts = new CancellationTokenSource();
        var token = _nextRunNotificationCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();

                var (actionableTargets, _) = GetAutoStopTargets();
                if (actionableTargets.Length == 0)
                {
                    return;
                }

                var message = actionableTargets.Length == 1
                    ? "Auto-stop enforcement will run in about a minute for 1 service."
                    : $"Auto-stop enforcement will run in about a minute for {actionableTargets.Length} services.";

                var details = BuildTargetDetails(actionableTargets, 8);
                _activityLog.LogInformation("Auto-stop", message, details);
            }
            catch (TaskCanceledException)
            {
            }
        }, token);
    }

    private void CancelUpcomingRunNotification()
    {
        _nextRunNotificationCts?.Cancel();
        _nextRunNotificationCts?.Dispose();
        _nextRunNotificationCts = null;
    }

    private void LogRunResult(ProcessAutoStopResult result, IReadOnlyList<ProcessAutoStopTarget> skippedTargets)
    {
        if (result.WasSkipped)
        {
            return;
        }

        var details = BuildActionDetails(result.Actions);
        var skippedDetails = BuildSkippedDetails(skippedTargets);

        if (result.Actions.Count == 0)
        {
            var summary = skippedTargets.Count == 0
                ? "Auto-stop enforcement ran but no services required action."
                : "Auto-stop enforcement ran but no services were actionable.";
            var combinedDetails = details.Concat(skippedDetails).ToList();
            _activityLog.LogInformation("Auto-stop", summary, combinedDetails);
            return;
        }

        var failures = result.Actions.Count(action => !action.Success);
        if (failures == 0)
        {
            var message = result.TargetCount == 1
                ? "Auto-stop enforcement stopped 1 service."
                : $"Auto-stop enforcement stopped {result.TargetCount} services.";
            var combinedDetails = details.Concat(skippedDetails).ToList();
            _activityLog.LogSuccess("Auto-stop", message, combinedDetails);
            return;
        }

        var warning = failures == 1
            ? "Auto-stop enforcement completed with 1 issue."
            : $"Auto-stop enforcement completed with {failures} issues.";
        var combined = details.Concat(skippedDetails).ToList();
        _activityLog.LogWarning("Auto-stop", warning, combined);

        var successful = result.Actions.Where(static action => action.Success).ToList();
        if (successful.Count > 0)
        {
            var successMessage = successful.Count == 1
                ? "1 service stopped successfully during this run."
                : $"{successful.Count} services stopped successfully during this run.";
            var successDetails = BuildActionDetails(successful);
            _activityLog.LogSuccess("Auto-stop", successMessage, successDetails);
        }

        if (skippedTargets.Count > 0)
        {
            _activityLog.LogInformation(
                "Auto-stop",
                "Some catalog entries were skipped because service identifiers were unavailable.",
                skippedDetails);
        }
    }

    private (ProcessAutoStopTarget[] Actionable, IReadOnlyList<ProcessAutoStopTarget> Skipped) GetAutoStopTargets()
    {
        var preferences = _stateStore.GetPreferences()
            .Where(static pref => pref.Action == ProcessActionPreference.AutoStop)
            .ToArray();

        if (preferences.Length == 0)
        {
            return (Array.Empty<ProcessAutoStopTarget>(), Array.Empty<ProcessAutoStopTarget>());
        }

        var catalogLookup = _catalogLookup.Value;
        var actionableTargets = new List<ProcessAutoStopTarget>(preferences.Length * 2);
        var skippedTargets = new List<ProcessAutoStopTarget>();

        foreach (var preference in preferences)
        {
            catalogLookup.TryGetValue(preference.ProcessIdentifier, out var entry);

            var displayName = entry?.DisplayName ?? preference.ProcessIdentifier;
            var rawIdentifier = entry?.ServiceIdentifier
                ?? preference.ServiceIdentifier
                ?? entry?.Identifier
                ?? preference.ProcessIdentifier;

            var looksLikeTask = IsTaskIdentifier(rawIdentifier);
            var resolution = looksLikeTask
                ? ServiceResolutionMany.NotInstalled("Resolved as task path; skipping service lookup.")
                : _serviceResolver.ResolveMany(rawIdentifier, displayName);

            switch (resolution.Status)
            {
                case ServiceResolutionStatus.Available when resolution.Candidates.Count > 0:
                    foreach (var candidate in resolution.Candidates)
                    {
                        actionableTargets.Add(new ProcessAutoStopTarget(preference.ProcessIdentifier, displayName, candidate.ServiceName, null, IsTask: false, TaskPattern: null));
                    }
                    break;
                case ServiceResolutionStatus.NotInstalled when looksLikeTask:
                    actionableTargets.Add(new ProcessAutoStopTarget(preference.ProcessIdentifier, displayName, null, null, IsTask: true, TaskPattern: rawIdentifier));
                    break;
                case ServiceResolutionStatus.NotInstalled:
                    skippedTargets.Add(new ProcessAutoStopTarget(preference.ProcessIdentifier, displayName, null, resolution.Reason ?? "Service not installed on this PC.", IsTask: false, TaskPattern: null));
                    break;
                case ServiceResolutionStatus.InvalidName when looksLikeTask:
                    actionableTargets.Add(new ProcessAutoStopTarget(preference.ProcessIdentifier, displayName, null, null, IsTask: true, TaskPattern: rawIdentifier));
                    break;
                case ServiceResolutionStatus.InvalidName:
                    skippedTargets.Add(new ProcessAutoStopTarget(preference.ProcessIdentifier, displayName, null, resolution.Reason ?? "Service identifier is invalid.", IsTask: false, TaskPattern: null));
                    break;
                default:
                    skippedTargets.Add(new ProcessAutoStopTarget(preference.ProcessIdentifier, displayName, null, "Service could not be resolved on this PC.", IsTask: false, TaskPattern: null));
                    break;
            }
        }

        var distinctActionable = actionableTargets
            .GroupBy(target => target.IsTask ? target.TaskPattern ?? target.ProcessId : target.ServiceName ?? target.ProcessId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();

        var distinctSkipped = skippedTargets
            .GroupBy(target => target.ProcessId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();

        return (distinctActionable, distinctSkipped);
    }

    private sealed record ProcessAutoStopTarget(string ProcessId, string DisplayName, string? ServiceName, string? SkipReason, bool IsTask, string? TaskPattern)
    {
        public bool IsActionable => !string.IsNullOrWhiteSpace(ServiceName);

        public string Label => string.IsNullOrWhiteSpace(DisplayName) ? ProcessId : DisplayName;
    }

    private static bool IsTaskIdentifier(string? identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            return false;
        }

        return identifier.Contains("\\", StringComparison.Ordinal) || identifier.Contains('/', StringComparison.Ordinal);
    }

    private static IEnumerable<string> BuildActionDetails(IReadOnlyList<ProcessAutoStopActionResult> actions)
    {
        foreach (var action in actions)
        {
            var message = string.IsNullOrWhiteSpace(action.Message)
                ? (action.Success ? "Stopped" : "Failed")
                : action.Message.Trim();
            yield return $"{action.Identifier}: {message}";
        }
    }

    private static IEnumerable<string> BuildTargetDetails(IEnumerable<ProcessAutoStopTarget> targets, int max)
    {
        var list = targets?.Where(static target => target.IsActionable || target.IsTask).ToList() ?? new List<ProcessAutoStopTarget>();
        if (list.Count == 0)
        {
            return Array.Empty<string>();
        }

        var limit = Math.Min(max, list.Count);
        var lines = new List<string>(limit + 1);
        for (var i = 0; i < limit; i++)
        {
            var label = list[i].IsTask && !string.IsNullOrWhiteSpace(list[i].TaskPattern)
                ? $"{list[i].Label} (task: {list[i].TaskPattern})"
                : list[i].Label;
            lines.Add($"Target: {label}");
        }

        if (list.Count > limit)
        {
            lines.Add($"(+{list.Count - limit} more)");
        }

        return lines;
    }

    private static IEnumerable<string> BuildSkippedDetails(IEnumerable<ProcessAutoStopTarget> targets)
    {
        var list = targets?.Where(static target => !target.IsActionable).ToList() ?? new List<ProcessAutoStopTarget>();
        if (list.Count == 0)
        {
            return Array.Empty<string>();
        }

        var lines = new List<string>(list.Count);
        foreach (var target in list)
        {
            var reason = string.IsNullOrWhiteSpace(target.SkipReason) ? "No service identifier available." : target.SkipReason.Trim();
            lines.Add($"Skipped: {target.Label} - {reason}");
        }

        return lines;
    }

    private void OnSettingsChanged()
    {
        SettingsChanged?.Invoke(this, _settings);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(ProcessAutoStopEnforcer));
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
        CancelUpcomingRunNotification();
    }
}

public sealed record ProcessAutoStopActionResult(string Identifier, bool Success, string Message);

public sealed record ProcessAutoStopResult(DateTimeOffset ExecutedAtUtc, IReadOnlyList<ProcessAutoStopActionResult> Actions, bool WasSkipped)
{
    public static ProcessAutoStopResult Create(DateTimeOffset timestamp, IReadOnlyList<ProcessAutoStopActionResult> actions)
        => new(timestamp, actions, false);

    public static ProcessAutoStopResult Skipped(DateTimeOffset timestamp)
        => new(timestamp, Array.Empty<ProcessAutoStopActionResult>(), true);

    public bool Success => !WasSkipped && Actions.All(static action => action.Success);

    public int TargetCount => Actions.Count;
}

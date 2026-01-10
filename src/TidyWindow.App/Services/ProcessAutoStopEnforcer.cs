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
    private readonly ActivityLogService _activityLog;
    private readonly Lazy<ProcessCatalogSnapshot> _catalogSnapshot;
    private readonly Lazy<IReadOnlyDictionary<string, ProcessCatalogEntry>> _catalogLookup;
    private readonly SemaphoreSlim _runLock = new(1, 1);
    private System.Threading.Timer? _timer;
    private CancellationTokenSource? _nextRunNotificationCts;
    private ProcessAutomationSettings _settings;
    private bool _disposed;

    public ProcessAutoStopEnforcer(ProcessStateStore stateStore, ProcessControlService controlService, ActivityLogService activityLog, ProcessCatalogParser catalogParser)
    {
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _controlService = controlService ?? throw new ArgumentNullException(nameof(controlService));
        _activityLog = activityLog ?? throw new ArgumentNullException(nameof(activityLog));
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
            return await RunOnceInternalAsync(false, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    public Task<ProcessAutoStopResult> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return RunOnceInternalAsync(false, cancellationToken);
    }

    private async Task<ProcessAutoStopResult> RunOnceInternalAsync(bool isBackground, CancellationToken cancellationToken)
    {
        if (!_settings.AutoStopEnabled)
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
            var targets = GetAutoStopTargets();

            var timestamp = DateTimeOffset.UtcNow;
            if (targets.Length == 0)
            {
                UpdateLastRun(timestamp);
                var skippedResult = ProcessAutoStopResult.Create(timestamp, Array.Empty<ProcessAutoStopActionResult>());
                LogRunResult(skippedResult);
                return skippedResult;
            }

            var actions = new List<ProcessAutoStopActionResult>(targets.Length);
            foreach (var target in targets)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!target.IsActionable)
                {
                    actions.Add(new ProcessAutoStopActionResult(target.Label, success: false, target.SkipReason ?? "No service identifier available."));
                    continue;
                }

                var stopResult = await _controlService.StopAsync(target.ServiceName!, cancellationToken).ConfigureAwait(false);
                actions.Add(new ProcessAutoStopActionResult(target.Label, stopResult.Success, stopResult.Message));
            }

            UpdateLastRun(timestamp);
            var runResult = ProcessAutoStopResult.Create(timestamp, actions);
            LogRunResult(runResult);
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
        await RunOnceInternalAsync(true, CancellationToken.None).ConfigureAwait(false);

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

                var targets = GetAutoStopTargets();
                var actionableTargets = targets.Where(static target => target.IsActionable).ToArray();
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

    private void LogRunResult(ProcessAutoStopResult result)
    {
        if (result.WasSkipped)
        {
            return;
        }

        var details = BuildActionDetails(result.Actions);

        if (result.Actions.Count == 0)
        {
            _activityLog.LogInformation("Auto-stop", "Auto-stop enforcement ran but no services required action.", details);
            return;
        }

        var failures = result.Actions.Count(action => !action.Success);
        if (failures == 0)
        {
            var message = result.TargetCount == 1
                ? "Auto-stop enforcement stopped 1 service."
                : $"Auto-stop enforcement stopped {result.TargetCount} services.";
            _activityLog.LogSuccess("Auto-stop", message, details);
            return;
        }

        var warning = failures == 1
            ? "Auto-stop enforcement completed with 1 issue."
            : $"Auto-stop enforcement completed with {failures} issues.";
        _activityLog.LogWarning("Auto-stop", warning, details);

        var successful = result.Actions.Where(static action => action.Success).ToList();
        if (successful.Count > 0)
        {
            var successMessage = successful.Count == 1
                ? "1 service stopped successfully during this run."
                : $"{successful.Count} services stopped successfully during this run.";
            var successDetails = BuildActionDetails(successful);
            _activityLog.LogSuccess("Auto-stop", successMessage, successDetails);
        }
    }

    private ProcessAutoStopTarget[] GetAutoStopTargets()
    {
        var preferences = _stateStore.GetPreferences()
            .Where(static pref => pref.Action == ProcessActionPreference.AutoStop)
            .ToArray();

        if (preferences.Length == 0)
        {
            return Array.Empty<ProcessAutoStopTarget>();
        }

        var catalogLookup = _catalogLookup.Value;
        var targets = new List<ProcessAutoStopTarget>(preferences.Length);

        foreach (var preference in preferences)
        {
            catalogLookup.TryGetValue(preference.ProcessIdentifier, out var entry);

            var displayName = entry?.DisplayName ?? preference.ProcessIdentifier;
            var serviceIdentifier = entry?.ServiceIdentifier
                ?? preference.ServiceIdentifier
                ?? ProcessCatalogEntry.NormalizeServiceIdentifier(preference.ProcessIdentifier);

            var normalizedService = ProcessCatalogEntry.NormalizeServiceIdentifier(serviceIdentifier);
            string? skipReason = null;

            if (string.IsNullOrWhiteSpace(normalizedService))
            {
                skipReason = "No valid service identifier available.";
            }

            targets.Add(new ProcessAutoStopTarget(preference.ProcessIdentifier, displayName, normalizedService, skipReason));
        }

        return targets
            .GroupBy(target => target.ServiceName ?? target.ProcessId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
    }

    private sealed record ProcessAutoStopTarget(string ProcessId, string DisplayName, string? ServiceName, string? SkipReason)
    {
        public bool IsActionable => !string.IsNullOrWhiteSpace(ServiceName);

        public string Label => string.IsNullOrWhiteSpace(DisplayName) ? ProcessId : DisplayName;
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
        var list = targets?.Where(static target => target.IsActionable).ToList() ?? new List<ProcessAutoStopTarget>();
        if (list.Count == 0)
        {
            return Array.Empty<string>();
        }

        var limit = Math.Min(max, list.Count);
        var lines = new List<string>(limit + 1);
        for (var i = 0; i < limit; i++)
        {
            lines.Add($"Target: {list[i].Label}");
        }

        if (list.Count > limit)
        {
            lines.Add($"(+{list.Count - limit} more)");
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

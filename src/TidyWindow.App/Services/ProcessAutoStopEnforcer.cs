using System;
using System.Collections.Generic;
using System.Linq;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;
using TidyWindow.Core.Processes;

namespace TidyWindow.App.Services;

/// <summary>
/// Smart event-driven enforcer that watches for auto-stop target services to start
/// and stops them within seconds. Uses adaptive polling with exponential back-off:
/// fast scans (10s) right after an action, slowing to idle (60s) when all targets
/// are already stopped. Much more efficient than a fixed N-minute timer.
/// </summary>
public sealed class ProcessAutoStopEnforcer : IDisposable
{
    /// <summary>Scan every 10 seconds when we recently stopped something.</summary>
    private static readonly TimeSpan ActiveScanInterval = TimeSpan.FromSeconds(10);

    /// <summary>Maximum idle interval — scan every 60 seconds when everything is quiet.</summary>
    private static readonly TimeSpan MaxIdleScanInterval = TimeSpan.FromSeconds(60);

    /// <summary>Grace period before stopping a newly detected running service (debounce).</summary>
    private static readonly TimeSpan StopDebounceDelay = TimeSpan.FromSeconds(3);

    private readonly ProcessStateStore _stateStore;
    private readonly ProcessControlService _controlService;
    private readonly TaskControlService _taskControlService;
    private readonly ActivityLogService _activityLog;
    private readonly ServiceResolver _serviceResolver;
    private readonly Lazy<ProcessCatalogSnapshot> _catalogSnapshot;
    private readonly Lazy<IReadOnlyDictionary<string, ProcessCatalogEntry>> _catalogLookup;
    /// <summary>Window within which a re-stop of the same service is considered a silent re-enforcement (no new log entry).</summary>
    private static readonly TimeSpan ReStopSilenceWindow = TimeSpan.FromMinutes(10);

    private readonly SemaphoreSlim _runLock = new(1, 1);
    private readonly Dictionary<string, DateTimeOffset> _lastStopTimes = new(StringComparer.OrdinalIgnoreCase);
    private System.Threading.Timer? _guardTimer;
    private TimeSpan _currentScanInterval = ActiveScanInterval;
    private int _consecutiveIdleScans;
    private int _watchedTargetCount;
    private int _runningTargetCount;
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
        ConfigureGuard();
    }

    public ProcessAutomationSettings CurrentSettings => _settings;

    /// <summary>Current adaptive scan interval (for UI display).</summary>
    public TimeSpan CurrentScanInterval => _currentScanInterval;

    /// <summary>Number of target services/tasks being watched.</summary>
    public int WatchedTargetCount => _watchedTargetCount;

    /// <summary>Number of target services currently detected as running.</summary>
    public int RunningTargetCount => _runningTargetCount;

    public event EventHandler<ProcessAutomationSettings>? SettingsChanged;

    /// <summary>
    /// Raised after each smart-guard scan with live status info for the UI.
    /// </summary>
    public event EventHandler<SmartGuardStatus>? GuardStatusUpdated;

    public async Task<ProcessAutoStopResult?> ApplySettingsAsync(ProcessAutomationSettings settings, bool enforceImmediately, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var normalized = settings.Normalize();
        _settings = normalized;
        _stateStore.SaveAutomationSettings(normalized);
        ConfigureGuard();
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
            _watchedTargetCount = actionableTargets.Length;

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

                        var success = taskResult.Success || taskResult.NotFound || taskResult.AccessDenied;
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

                var stopResult = target.IsProcessOnly
                    ? await _controlService.KillProcessByNameAsync(target.ProcessName!, cancellationToken).ConfigureAwait(false)
                    : !string.IsNullOrWhiteSpace(target.ProcessName)
                        ? await _controlService.StopServiceAndProcessAsync(target.ServiceName!, target.ProcessName, cancellationToken: cancellationToken).ConfigureAwait(false)
                        : await _controlService.StopAndDisableAsync(target.ServiceName!, cancellationToken: cancellationToken).ConfigureAwait(false);
                actions.Add(new ProcessAutoStopActionResult(target.Label, stopResult.Success, stopResult.Message));
            }

            UpdateLastRun(timestamp);
            var runResult = ProcessAutoStopResult.Create(timestamp, actions);
            LogRunResult(runResult, skippedTargets);

            // After a manual full-enforcement, reset to active scanning.
            ResetToActiveScan();

            return runResult;
        }
        finally
        {
            _runLock.Release();
        }
    }

    // ── Smart Guard: adaptive scan loop ──────────────────────────────────

    private void ConfigureGuard()
    {
        _guardTimer?.Dispose();
        _guardTimer = null;
        _consecutiveIdleScans = 0;
        _currentScanInterval = ActiveScanInterval;
        ClearAllStopHistory();

        if (!_settings.AutoStopEnabled)
        {
            _watchedTargetCount = 0;
            _runningTargetCount = 0;
            RaiseGuardStatus();
            return;
        }

        // Immediate first scan, then adaptive interval.
        _guardTimer = new System.Threading.Timer(OnGuardTick, null, TimeSpan.Zero, Timeout.InfiniteTimeSpan);
    }

    private void OnGuardTick(object? state)
    {
        _ = RunSmartGuardCycleAsync();
    }

    private async Task RunSmartGuardCycleAsync()
    {
        if (_disposed || !_settings.AutoStopEnabled)
        {
            return;
        }

        if (!await _runLock.WaitAsync(0).ConfigureAwait(false))
        {
            ScheduleNextGuardTick();
            return;
        }

        try
        {
            var (actionableTargets, _) = GetAutoStopTargets();
            _watchedTargetCount = actionableTargets.Length;

            if (actionableTargets.Length == 0)
            {
                _runningTargetCount = 0;
                _consecutiveIdleScans++;
                AdaptInterval(stoppedAny: false);
                RaiseGuardStatus();
                ScheduleNextGuardTick();
                return;
            }

            // Lightweight check: which services are actually running right now?
            var runningServices = DetectRunningServices(actionableTargets);
            // Also check running tasks
            var runningTasks = DetectRunningTasks(actionableTargets);

            var totalRunning = runningServices.Count + runningTasks.Count;
            _runningTargetCount = totalRunning;

            if (totalRunning == 0)
            {
                _consecutiveIdleScans++;
                AdaptInterval(stoppedAny: false);
                RaiseGuardStatus();
                ScheduleNextGuardTick();
                return;
            }

            // Debounce: wait briefly then re-check to avoid stopping a service that's still initializing.
            await Task.Delay(StopDebounceDelay).ConfigureAwait(false);

            var actions = new List<ProcessAutoStopActionResult>();
            var silentReStops = 0;

            // Re-check and stop+disable services/processes that are still running after debounce.
            // Always stop — never skip. But suppress log entries for re-stops within the silence window.
            foreach (var target in runningServices)
            {
                if (target.IsProcessOnly)
                {
                    // Process-only target: kill by executable name.
                    var trackingKey = target.ProcessName!;
                    var isReStop = IsRecentlyStoppedService(trackingKey);
                    var killResult = await _controlService.KillProcessByNameAsync(target.ProcessName!, CancellationToken.None).ConfigureAwait(false);
                    RecordStopTime(trackingKey);

                    if (isReStop && killResult.Success)
                    {
                        silentReStops++;
                    }
                    else
                    {
                        actions.Add(new ProcessAutoStopActionResult(target.Label, killResult.Success, killResult.Message));
                    }

                    continue;
                }

                if (!string.IsNullOrWhiteSpace(target.ServiceName) && !IsServiceStillRunning(target.ServiceName))
                {
                    // Service stopped on its own during debounce — but still try process kill if applicable.
                    if (!string.IsNullOrWhiteSpace(target.ProcessName) && ProcessControlService.IsProcessRunning(target.ProcessName))
                    {
                        var killResult = await _controlService.KillProcessByNameAsync(target.ProcessName, CancellationToken.None).ConfigureAwait(false);
                        actions.Add(new ProcessAutoStopActionResult(target.Label, killResult.Success, killResult.Message));
                    }

                    continue;
                }

                var serviceTrackingKey = target.ServiceName!;
                var isServiceReStop = IsRecentlyStoppedService(serviceTrackingKey);

                // Use StopServiceAndProcessAsync to stop service AND kill process as fallback.
                var stopResult = await _controlService.StopServiceAndProcessAsync(target.ServiceName!, target.ProcessName, cancellationToken: CancellationToken.None).ConfigureAwait(false);
                RecordStopTime(serviceTrackingKey);

                // Also disable the service so Windows doesn't auto-restart it.
                if (stopResult.Success)
                {
                    await _controlService.StopAndDisableAsync(target.ServiceName!, cancellationToken: CancellationToken.None).ConfigureAwait(false);
                }

                if (isServiceReStop && stopResult.Success)
                {
                    // Silently re-enforced — don't create a new log entry.
                    silentReStops++;
                }
                else
                {
                    actions.Add(new ProcessAutoStopActionResult(target.Label, stopResult.Success, stopResult.Message));
                }
            }

            // Stop running tasks.
            foreach (var target in runningTasks)
            {
                var taskPattern = target.TaskPattern ?? target.ProcessId;
                try
                {
                    var taskResult = await _taskControlService.StopAndDisableAsync(taskPattern).ConfigureAwait(false);
                    var success = taskResult.Success || taskResult.NotFound || taskResult.AccessDenied;
                    var message = taskResult.Success
                        ? (taskResult.Actions.Count == 0 ? "Task disabled." : string.Join("; ", taskResult.Actions))
                        : (taskResult.NotFound ? "No tasks matched." : (taskResult.AccessDenied ? "System denied." : taskResult.Message));
                    actions.Add(new ProcessAutoStopActionResult(target.Label, success, message));
                }
                catch (Exception ex)
                {
                    actions.Add(new ProcessAutoStopActionResult(target.Label, false, ex.Message));
                }
            }

            if (actions.Count > 0)
            {
                var timestamp = DateTimeOffset.UtcNow;
                UpdateLastRun(timestamp);

                var stoppedCount = actions.Count(a => a.Success);
                if (stoppedCount > 0)
                {
                    var names = actions.Where(a => a.Success).Select(a => a.Identifier).ToList();
                    _activityLog.LogSuccess("Smart Guard",
                        stoppedCount == 1
                            ? $"Stopped {names[0]} (detected running)."
                            : $"Stopped {stoppedCount} services detected running.",
                        names.Select(n => $"Stopped: {n}").ToList());
                }

                var failedCount = actions.Count(a => !a.Success);
                if (failedCount > 0)
                {
                    var failedDetails = actions.Where(a => !a.Success).Select(a => $"{a.Identifier}: {a.Message}").ToList();
                    _activityLog.LogWarning("Smart Guard", $"{failedCount} service(s) could not be stopped.", failedDetails);
                }

                _runningTargetCount = 0;
                ResetToActiveScan();
            }
            else if (silentReStops > 0)
            {
                // Services were silently re-enforced — keep scanning actively but don't log.
                _runningTargetCount = 0;
                UpdateLastRun(DateTimeOffset.UtcNow);
            }
            else
            {
                _consecutiveIdleScans++;
                AdaptInterval(stoppedAny: false);
            }

            RaiseGuardStatus();
        }
        catch (Exception ex)
        {
            _activityLog.LogWarning("Smart Guard", "Guard scan encountered an error.", new[] { ex.Message });
        }
        finally
        {
            _runLock.Release();
            ScheduleNextGuardTick();
        }
    }

    private void ResetToActiveScan()
    {
        _consecutiveIdleScans = 0;
        _currentScanInterval = ActiveScanInterval;
    }

    // ── Silent re-stop tracking (log dedup, never skip enforcement) ─────

    /// <summary>
    /// Returns true if this service was already stopped within the silence window,
    /// meaning a re-stop should happen silently without a new log entry.
    /// </summary>
    private bool IsRecentlyStoppedService(string serviceName)
    {
        if (!_lastStopTimes.TryGetValue(serviceName, out var lastStop))
        {
            return false;
        }

        return DateTimeOffset.UtcNow - lastStop < ReStopSilenceWindow;
    }

    /// <summary>
    /// Records that we just stopped this service (for log dedup tracking).
    /// </summary>
    private void RecordStopTime(string serviceName)
    {
        _lastStopTimes[serviceName] = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Clears stop-time tracking for a specific service (e.g. when user changes preference).
    /// </summary>
    public void ClearStopHistory(string serviceName)
    {
        _lastStopTimes.Remove(serviceName);
    }

    /// <summary>
    /// Clears all stop-time tracking (e.g. on settings change).
    /// </summary>
    private void ClearAllStopHistory()
    {
        _lastStopTimes.Clear();
    }

    private void AdaptInterval(bool stoppedAny)
    {
        if (stoppedAny)
        {
            ResetToActiveScan();
            return;
        }

        // Exponential back-off: 10s → 20s → 40s → 60s (capped).
        if (_consecutiveIdleScans <= 1)
        {
            _currentScanInterval = ActiveScanInterval;
        }
        else
        {
            var multiplier = Math.Min(1 << (_consecutiveIdleScans - 1), 6); // cap at 6x = 60s
            _currentScanInterval = TimeSpan.FromSeconds(Math.Min(
                ActiveScanInterval.TotalSeconds * multiplier,
                MaxIdleScanInterval.TotalSeconds));
        }
    }

    private void ScheduleNextGuardTick()
    {
        if (_disposed || !_settings.AutoStopEnabled)
        {
            return;
        }

        try
        {
            _guardTimer?.Change(_currentScanInterval, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // Timer was disposed concurrently.
        }
    }

    // ── Lightweight detection helpers ────────────────────────────────────

    private static List<ProcessAutoStopTarget> DetectRunningServices(ProcessAutoStopTarget[] targets)
    {
        var running = new List<ProcessAutoStopTarget>();
        foreach (var target in targets)
        {
            if (target.IsTask)
            {
                continue;
            }

            // Check if the Windows service is running.
            if (!string.IsNullOrWhiteSpace(target.ServiceName) && IsServiceStillRunning(target.ServiceName))
            {
                running.Add(target);
                continue;
            }

            // Fallback: check if the process executable is running (covers UWP apps and user-mode processes).
            if (!string.IsNullOrWhiteSpace(target.ProcessName) && ProcessControlService.IsProcessRunning(target.ProcessName))
            {
                running.Add(target);
            }
        }

        return running;
    }

    private static bool IsServiceStillRunning(string serviceName)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            using var controller = new ServiceController(serviceName);
            controller.Refresh();
            return controller.Status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending;
        }
        catch
        {
            return false;
        }
    }

    private static List<ProcessAutoStopTarget> DetectRunningTasks(ProcessAutoStopTarget[] targets)
    {
        // Tasks are always treated as actionable — we can't cheaply check if they
        // last ran recently, so on the first scan we handle them, then they stay
        // disabled. Subsequent scans will skip because they won't re-enable themselves.
        // We only include task targets during the very first guard cycle.
        return new List<ProcessAutoStopTarget>();
    }

    // ── Status event ─────────────────────────────────────────────────────

    private void RaiseGuardStatus()
    {
        var status = new SmartGuardStatus(
            _settings.AutoStopEnabled,
            _watchedTargetCount,
            _runningTargetCount,
            _currentScanInterval,
            _settings.LastRunUtc);
        GuardStatusUpdated?.Invoke(this, status);
    }

    // ── Persistence ──────────────────────────────────────────────────────

    private void UpdateLastRun(DateTimeOffset timestamp)
    {
        _settings = _settings.WithLastRun(timestamp);
        _stateStore.SaveAutomationSettings(_settings);
        OnSettingsChanged();
    }

    // ── Target resolution ────────────────────────────────────────────────

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
            var processName = entry?.ProcessName;
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
                        actionableTargets.Add(new ProcessAutoStopTarget(preference.ProcessIdentifier, displayName, candidate.ServiceName, processName, null, IsTask: false, TaskPattern: null));
                    }
                    break;
                case ServiceResolutionStatus.NotInstalled when looksLikeTask:
                    actionableTargets.Add(new ProcessAutoStopTarget(preference.ProcessIdentifier, displayName, null, processName, null, IsTask: true, TaskPattern: rawIdentifier));
                    break;
                case ServiceResolutionStatus.NotInstalled when !string.IsNullOrWhiteSpace(processName):
                    // Service not installed, but we have a process name — target it as a process kill.
                    actionableTargets.Add(new ProcessAutoStopTarget(preference.ProcessIdentifier, displayName, null, processName, null, IsTask: false, TaskPattern: null));
                    break;
                case ServiceResolutionStatus.NotInstalled:
                    skippedTargets.Add(new ProcessAutoStopTarget(preference.ProcessIdentifier, displayName, null, null, resolution.Reason ?? "Service not installed on this PC.", IsTask: false, TaskPattern: null));
                    break;
                case ServiceResolutionStatus.InvalidName when looksLikeTask:
                    actionableTargets.Add(new ProcessAutoStopTarget(preference.ProcessIdentifier, displayName, null, processName, null, IsTask: true, TaskPattern: rawIdentifier));
                    break;
                case ServiceResolutionStatus.InvalidName when !string.IsNullOrWhiteSpace(processName):
                    // Invalid service name but we have a process name — target it as a process kill.
                    actionableTargets.Add(new ProcessAutoStopTarget(preference.ProcessIdentifier, displayName, null, processName, null, IsTask: false, TaskPattern: null));
                    break;
                case ServiceResolutionStatus.InvalidName:
                    skippedTargets.Add(new ProcessAutoStopTarget(preference.ProcessIdentifier, displayName, null, null, resolution.Reason ?? "Service identifier is invalid.", IsTask: false, TaskPattern: null));
                    break;
                default:
                    skippedTargets.Add(new ProcessAutoStopTarget(preference.ProcessIdentifier, displayName, null, null, "Service could not be resolved on this PC.", IsTask: false, TaskPattern: null));
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

    private sealed record ProcessAutoStopTarget(string ProcessId, string DisplayName, string? ServiceName, string? ProcessName, string? SkipReason, bool IsTask, string? TaskPattern)
    {
        public bool IsActionable => !string.IsNullOrWhiteSpace(ServiceName) || !string.IsNullOrWhiteSpace(ProcessName);

        public bool IsProcessOnly => string.IsNullOrWhiteSpace(ServiceName) && !string.IsNullOrWhiteSpace(ProcessName);

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

    // ── Logging ──────────────────────────────────────────────────────────

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

    // ── Lifecycle ────────────────────────────────────────────────────────

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
        _guardTimer?.Dispose();
        _runLock.Dispose();
    }
}

/// <summary>
/// Live status snapshot emitted after each smart-guard scan cycle.
/// </summary>
public sealed record SmartGuardStatus(
    bool IsEnabled,
    int WatchedServices,
    int RunningServices,
    TimeSpan CurrentScanInterval,
    DateTimeOffset? LastActionUtc);

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

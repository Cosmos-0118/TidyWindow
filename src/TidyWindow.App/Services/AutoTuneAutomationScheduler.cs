using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TidyWindow.Core.Automation;
using TidyWindow.Core.Performance;

namespace TidyWindow.App.Services;

/// <summary>
/// Runs auto-tune on a fixed cadence (5 minutes) instead of continuous polling.
/// </summary>
public sealed class AutoTuneAutomationScheduler : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    private readonly AutoTuneAutomationSettingsStore _store;
    private readonly IPerformanceLabService _service;
    private readonly ActivityLogService _activityLog;
    private readonly IAutomationWorkTracker _workTracker;
    private readonly SemaphoreSlim _runLock = new(1, 1);
    private AutoTuneAutomationSettings _settings;
    private System.Threading.Timer? _timer;
    private bool _disposed;

    public AutoTuneAutomationScheduler(
        AutoTuneAutomationSettingsStore store,
        IPerformanceLabService service,
        ActivityLogService activityLog,
        IAutomationWorkTracker workTracker)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _activityLog = activityLog ?? throw new ArgumentNullException(nameof(activityLog));
        _workTracker = workTracker ?? throw new ArgumentNullException(nameof(workTracker));

        _settings = _store.Get().Normalize();
        ConfigureTimer();
    }

    public AutoTuneAutomationSettings CurrentSettings => _settings;

    public event EventHandler<AutoTuneAutomationSettings>? SettingsChanged;

    public async Task<AutoTuneAutomationRunResult?> ApplySettingsAsync(AutoTuneAutomationSettings settings, bool queueRunImmediately, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        var normalized = settings.Normalize();
        _settings = normalized;
        _store.Save(normalized);
        ConfigureTimer();
        OnSettingsChanged();

        if (queueRunImmediately && normalized.AutomationEnabled)
        {
            return await RunOnceInternalAsync(isBackground: false, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    public Task<AutoTuneAutomationRunResult> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return RunOnceInternalAsync(isBackground: false, cancellationToken);
    }

    private async Task<AutoTuneAutomationRunResult> RunOnceInternalAsync(bool isBackground, CancellationToken cancellationToken)
    {
        if (!_settings.AutomationEnabled)
        {
            return AutoTuneAutomationRunResult.Skipped(DateTimeOffset.UtcNow, "Automation disabled.");
        }

        if (string.IsNullOrWhiteSpace(_settings.ProcessNames))
        {
            return AutoTuneAutomationRunResult.Skipped(DateTimeOffset.UtcNow, "No process list configured.");
        }

        var timeout = isBackground ? 0 : Timeout.Infinite;
        if (!await _runLock.WaitAsync(timeout, cancellationToken).ConfigureAwait(false))
        {
            return AutoTuneAutomationRunResult.Skipped(DateTimeOffset.UtcNow, "Automation already running.");
        }

        Guid workToken = Guid.Empty;
        try
        {
            workToken = _workTracker.BeginWork(AutomationWorkType.Performance, "Auto-tune automation");

            var preset = string.IsNullOrWhiteSpace(_settings.PresetId) ? "LatencyBoost" : _settings.PresetId;
            var processes = _settings.ProcessNames;
            PowerShellInvocationResult result;
            try
            {
                result = await _service.StartAutoTuneAsync(processes, preset, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                result = new PowerShellInvocationResult(Array.Empty<string>(), new[] { ex.Message }, 1);
            }
            var now = DateTimeOffset.UtcNow;

            UpdateLastRun(now);
            var runResult = AutoTuneAutomationRunResult.Create(now, result);
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

    private void ConfigureTimer()
    {
        _timer?.Dispose();
        _timer = null;

        if (!_settings.AutomationEnabled)
        {
            return;
        }

        var interval = Interval;
        var dueTime = interval;
        if (_settings.LastRunUtc is { } lastRun)
        {
            var elapsed = DateTimeOffset.UtcNow - lastRun;
            dueTime = elapsed >= interval ? TimeSpan.Zero : interval - elapsed;
        }
        else
        {
            dueTime = TimeSpan.Zero;
        }

        _timer = new System.Threading.Timer(OnTimerTick, null, dueTime, interval);
    }

    private void OnTimerTick(object? state)
    {
        _ = RunOnceInternalAsync(isBackground: true, CancellationToken.None);
    }

    private void UpdateLastRun(DateTimeOffset timestamp)
    {
        _settings = _settings.WithLastRun(timestamp);
        _store.Save(_settings);
        OnSettingsChanged();
    }

    private void LogRunResult(AutoTuneAutomationRunResult result)
    {
        if (result.WasSkipped)
        {
            var reason = string.IsNullOrWhiteSpace(result.SkipReason) ? "Skipped." : result.SkipReason!;
            _activityLog.LogInformation("Auto-tune automation", reason);
            return;
        }

        if (result.InvocationResult is null)
        {
            _activityLog.LogInformation("Auto-tune automation", "Run completed without a result.");
            return;
        }

        if (result.InvocationResult.IsSuccess)
        {
            var summary = result.InvocationResult.Output.FirstOrDefault() ?? "Auto-tune run completed.";
            _activityLog.LogInformation("Auto-tune automation", summary, result.InvocationResult.Output);
            return;
        }

        var error = result.InvocationResult.Errors?.FirstOrDefault() ?? "Auto-tune run failed.";
        _activityLog.LogWarning("Auto-tune automation", error, result.InvocationResult.Errors ?? Array.Empty<string>());
    }

    private void OnSettingsChanged()
    {
        SettingsChanged?.Invoke(this, _settings);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(AutoTuneAutomationScheduler));
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
}
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
    private readonly ProcessStateStore _stateStore;
    private readonly ProcessControlService _controlService;
    private readonly SemaphoreSlim _runLock = new(1, 1);
    private System.Threading.Timer? _timer;
    private ProcessAutomationSettings _settings;
    private bool _disposed;

    public ProcessAutoStopEnforcer(ProcessStateStore stateStore, ProcessControlService controlService)
    {
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _controlService = controlService ?? throw new ArgumentNullException(nameof(controlService));
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
            var targets = _stateStore.GetPreferences()
                .Where(static pref => pref.Action == ProcessActionPreference.AutoStop)
                .Select(static pref => pref.ProcessIdentifier)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var timestamp = DateTimeOffset.UtcNow;
            if (targets.Length == 0)
            {
                UpdateLastRun(timestamp);
                return ProcessAutoStopResult.Create(timestamp, Array.Empty<ProcessAutoStopActionResult>());
            }

            var actions = new List<ProcessAutoStopActionResult>(targets.Length);
            foreach (var identifier in targets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = await _controlService.StopAsync(identifier, cancellationToken).ConfigureAwait(false);
                actions.Add(new ProcessAutoStopActionResult(identifier, result.Success, result.Message));
            }

            UpdateLastRun(timestamp);
            return ProcessAutoStopResult.Create(timestamp, actions);
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
    }

    private void OnTimerTick(object? state)
    {
        _ = RunOnceInternalAsync(true, CancellationToken.None);
    }

    private void UpdateLastRun(DateTimeOffset timestamp)
    {
        _settings = _settings.WithLastRun(timestamp);
        _stateStore.SaveAutomationSettings(_settings);
        OnSettingsChanged();
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

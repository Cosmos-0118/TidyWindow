using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using System.Threading;
using System.Threading.Tasks;
using TidyWindow.Core.Automation;
using TidyWindow.Core.Performance;

namespace TidyWindow.App.Services;

/// <summary>
/// Runs auto-tune using process start events (instant) without polling.
/// </summary>
public sealed class AutoTuneAutomationScheduler : IDisposable
{
    private readonly AutoTuneAutomationSettingsStore _store;
    private readonly IPerformanceLabService _service;
    private readonly ActivityLogService _activityLog;
    private readonly IAutomationWorkTracker _workTracker;
    private readonly SemaphoreSlim _runLock = new(1, 1);
    private AutoTuneAutomationSettings _settings;
    private ManagementEventWatcher? _processStartWatcher;
    private IReadOnlyList<string> _normalizedProcessNames = Array.Empty<string>();
    private bool _processWatcherWarningLogged;
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
        ConfigureProcessWatcher();
    }

    public AutoTuneAutomationSettings CurrentSettings => _settings;

    public event EventHandler<AutoTuneAutomationSettings>? SettingsChanged;

    public async Task<AutoTuneAutomationRunResult?> ApplySettingsAsync(AutoTuneAutomationSettings settings, bool queueRunImmediately, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        var normalized = settings.Normalize();
        _settings = normalized;
        _store.Save(normalized);
        ConfigureProcessWatcher();
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

    private void ConfigureProcessWatcher()
    {
        if (_processStartWatcher is not null)
        {
            _processStartWatcher.EventArrived -= OnProcessStarted;
            _processStartWatcher.Dispose();
            _processStartWatcher = null;
        }

        _normalizedProcessNames = NormalizeProcessNames(_settings.ProcessNames);

        if (!_settings.AutomationEnabled || _normalizedProcessNames.Count == 0)
        {
            return;
        }

        var filter = BuildProcessFilter(_normalizedProcessNames);
        var queryText = string.IsNullOrWhiteSpace(filter)
            ? "SELECT ProcessName FROM Win32_ProcessStartTrace"
            : $"SELECT ProcessName FROM Win32_ProcessStartTrace WHERE {filter}";

        try
        {
            var query = new WqlEventQuery(queryText);
            var scope = new ManagementScope("root\\CIMV2");
            scope.Connect();

            _processStartWatcher = new ManagementEventWatcher(scope, query);
            _processStartWatcher.EventArrived += OnProcessStarted;
            _processStartWatcher.Start();
        }
        catch (Exception ex)
        {
            if (!_processWatcherWarningLogged)
            {
                _activityLog.LogWarning("Auto-tune automation", $"Process watcher unavailable: {ex.Message}");
                _processWatcherWarningLogged = true;
            }

            _processStartWatcher = null;
            _normalizedProcessNames = Array.Empty<string>();
        }
    }

    private void OnProcessStarted(object? sender, EventArrivedEventArgs args)
    {
        try
        {
            var rawName = args.NewEvent?.Properties?["ProcessName"]?.Value as string;
            if (string.IsNullOrWhiteSpace(rawName))
            {
                return;
            }

            var normalized = NormalizeProcessName(rawName);
            if (!_normalizedProcessNames.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            {
                return;
            }

            _ = RunOnceInternalAsync(isBackground: true, CancellationToken.None);
        }
        catch
        {
            // Swallow to keep the watcher alive even if a single event fails to parse.
        }
    }

    private static IReadOnlyList<string> NormalizeProcessNames(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Array.Empty<string>();
        }

        var split = raw
            .Split(new[] { ';', ',', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeProcessName)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return split;
    }

    private static string NormalizeProcessName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var trimmed = name.Trim();
        var fileName = Path.GetFileName(trimmed);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return string.Empty;
        }

        var withExtension = fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? fileName
            : $"{fileName}.exe";

        // Preserve internal spaces (e.g., "Wuthering Waves.exe") instead of collapsing/splitting.
        return withExtension;
    }

    private static string BuildProcessFilter(IReadOnlyList<string> processNames)
    {
        if (processNames.Count == 0)
        {
            return string.Empty;
        }

        return string.Join(" OR ", processNames.Select(n => $"ProcessName = '{n.Replace("'", "''")}'"));
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
            var output = result.InvocationResult.Output ?? Array.Empty<string>();
            var hasWarnings = output.Any(line => line.Contains("warnings:", StringComparison.OrdinalIgnoreCase));

            // Stay silent on successful runs (even when matches/actions occurred) unless warnings exist.
            if (!hasWarnings)
            {
                return;
            }

            var summary = output.FirstOrDefault(line => line.Contains("warnings:", StringComparison.OrdinalIgnoreCase))
                         ?? output.FirstOrDefault(line => !string.IsNullOrWhiteSpace(line))
                         ?? "Auto-tune run completed with warnings.";
            _activityLog.LogWarning("Auto-tune automation", summary, output);
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
        if (_processStartWatcher is not null)
        {
            _processStartWatcher.EventArrived -= OnProcessStarted;
            _processStartWatcher.Dispose();
            _processStartWatcher = null;
        }
        _runLock.Dispose();
    }
}
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TidyWindow.Core.Maintenance;

namespace TidyWindow.App.Services;

/// <summary>
/// Queues Essentials tasks on a cadence defined by user automation settings.
/// </summary>
public sealed class EssentialsAutomationScheduler : IDisposable
{
    private readonly EssentialsAutomationSettingsStore _store;
    private readonly EssentialsTaskCatalog _catalog;
    private readonly EssentialsTaskQueue _queue;
    private readonly ActivityLogService _activityLog;
    private readonly IAutomationWorkTracker _workTracker;
    private readonly SemaphoreSlim _runLock = new(1, 1);
    private System.Threading.Timer? _timer;
    private EssentialsAutomationSettings _settings;
    private bool _disposed;

    public EssentialsAutomationScheduler(
        EssentialsAutomationSettingsStore store,
        EssentialsTaskCatalog catalog,
        EssentialsTaskQueue queue,
        ActivityLogService activityLog,
        IAutomationWorkTracker workTracker)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _activityLog = activityLog ?? throw new ArgumentNullException(nameof(activityLog));
        _workTracker = workTracker ?? throw new ArgumentNullException(nameof(workTracker));

        _settings = _store.Get().Normalize();
        ConfigureTimer();
    }

    public EssentialsAutomationSettings CurrentSettings => _settings;

    public event EventHandler<EssentialsAutomationSettings>? SettingsChanged;

    public async Task<EssentialsAutomationRunResult?> ApplySettingsAsync(EssentialsAutomationSettings settings, bool queueRunImmediately, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var normalized = settings.Normalize();
        _settings = normalized;
        _store.Save(normalized);
        ConfigureTimer();
        OnSettingsChanged();

        if (queueRunImmediately && normalized.AutomationEnabled)
        {
            return await RunOnceInternalAsync(false, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    public Task<EssentialsAutomationRunResult> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return RunOnceInternalAsync(false, cancellationToken);
    }

    private async Task<EssentialsAutomationRunResult> RunOnceInternalAsync(bool isBackground, CancellationToken cancellationToken)
    {
        if (!_settings.AutomationEnabled || _settings.TaskIds.IsDefaultOrEmpty || _settings.TaskIds.Length == 0)
        {
            return EssentialsAutomationRunResult.Skipped(DateTimeOffset.UtcNow);
        }

        var timeout = isBackground ? 0 : Timeout.Infinite;
        if (!await _runLock.WaitAsync(timeout, cancellationToken).ConfigureAwait(false))
        {
            return EssentialsAutomationRunResult.Skipped(DateTimeOffset.UtcNow);
        }

        Guid workToken = Guid.Empty;
        try
        {
            var now = DateTimeOffset.UtcNow;
            var actions = QueueSelectedTasks(cancellationToken);
            if (actions.Any(static action => action.Queued))
            {
                workToken = _workTracker.BeginWork(AutomationWorkType.Essentials, "Queued essentials automation run");
            }

            UpdateLastRun(now);
            var result = EssentialsAutomationRunResult.Create(now, actions);
            LogRunResult(result);
            return result;
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

    private IReadOnlyList<EssentialsAutomationTaskResult> QueueSelectedTasks(CancellationToken cancellationToken)
    {
        var actions = new List<EssentialsAutomationTaskResult>();
        var busyTaskLookup = BuildBusyTaskLookup();

        foreach (var taskId in _settings.TaskIds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var definition = _catalog.Tasks.FirstOrDefault(task => string.Equals(task.Id, taskId, StringComparison.OrdinalIgnoreCase));
            if (definition is null)
            {
                actions.Add(EssentialsAutomationTaskResult.Create(taskId, false, "Task not found"));
                continue;
            }

            if (busyTaskLookup.Contains(taskId))
            {
                actions.Add(EssentialsAutomationTaskResult.Create(taskId, false, "Already queued or running"));
                continue;
            }

            _queue.Enqueue(definition);
            busyTaskLookup.Add(taskId);
            actions.Add(EssentialsAutomationTaskResult.Create(taskId, true, $"Queued '{definition.Name}'"));
        }

        return actions;
    }

    private HashSet<string> BuildBusyTaskLookup()
    {
        var busy = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var snapshot in _queue.GetSnapshot())
        {
            if (snapshot.IsActive)
            {
                busy.Add(snapshot.Task.Id);
            }
        }

        return busy;
    }

    private void ConfigureTimer()
    {
        _timer?.Dispose();
        _timer = null;

        if (!_settings.AutomationEnabled || _settings.TaskIds.IsDefaultOrEmpty || _settings.TaskIds.Length == 0)
        {
            return;
        }

        var interval = TimeSpan.FromMinutes(Math.Clamp(_settings.IntervalMinutes, EssentialsAutomationSettings.MinimumIntervalMinutes, EssentialsAutomationSettings.MaximumIntervalMinutes));
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

    private void LogRunResult(EssentialsAutomationRunResult result)
    {
        if (result.WasSkipped)
        {
            _activityLog.LogInformation("Essentials automation", "Automation run skipped (disabled or empty set).");
            return;
        }

        var queued = result.Actions.Count(action => action.Queued);
        if (queued == 0)
        {
            _activityLog.LogInformation("Essentials automation", "Automation run had nothing to queue.", BuildActionDetails(result.Actions));
            return;
        }

        var message = queued == 1
            ? "Automation queued 1 essentials task."
            : $"Automation queued {queued} essentials tasks.";
        _activityLog.LogInformation("Essentials automation", message, BuildActionDetails(result.Actions));
    }

    private static IEnumerable<string> BuildActionDetails(IReadOnlyList<EssentialsAutomationTaskResult> actions)
    {
        foreach (var action in actions)
        {
            yield return string.IsNullOrWhiteSpace(action.Message)
                ? action.TaskId
                : $"{action.TaskId}: {action.Message}";
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
            throw new ObjectDisposedException(nameof(EssentialsAutomationScheduler));
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

public sealed record EssentialsAutomationTaskResult(string TaskId, bool Queued, string Message)
{
    public static EssentialsAutomationTaskResult Create(string taskId, bool queued, string message)
        => new(taskId, queued, message);
}

public sealed record EssentialsAutomationRunResult(DateTimeOffset ExecutedAtUtc, IReadOnlyList<EssentialsAutomationTaskResult> Actions, bool WasSkipped)
{
    public static EssentialsAutomationRunResult Create(DateTimeOffset timestamp, IReadOnlyList<EssentialsAutomationTaskResult> actions)
        => new(timestamp, actions, false);

    public static EssentialsAutomationRunResult Skipped(DateTimeOffset timestamp)
        => new(timestamp, Array.Empty<EssentialsAutomationTaskResult>(), true);

    public int TargetCount => Actions.Count;
}

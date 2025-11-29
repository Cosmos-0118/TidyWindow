using System;
using System.Collections.Generic;
using TidyWindow.Core.Maintenance;

namespace TidyWindow.App.Services;

/// <summary>
/// Mirrors <see cref="EssentialsTaskQueue"/> activity into the automation work tracker so PulseGuard can reason about essentials runs.
/// </summary>
public sealed class EssentialsQueueWorkObserver : IDisposable
{
    private readonly EssentialsTaskQueue _queue;
    private readonly IAutomationWorkTracker _workTracker;
    private readonly Dictionary<Guid, Guid> _activeTokens = new();
    private bool _disposed;

    public EssentialsQueueWorkObserver(EssentialsTaskQueue queue, IAutomationWorkTracker workTracker)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _workTracker = workTracker ?? throw new ArgumentNullException(nameof(workTracker));

        foreach (var snapshot in _queue.GetSnapshot())
        {
            TrackSnapshot(snapshot);
        }

        _queue.OperationChanged += OnOperationChanged;
    }

    private void OnOperationChanged(object? sender, EssentialsQueueChangedEventArgs e)
    {
        if (_disposed || e?.Snapshot is null)
        {
            return;
        }

        TrackSnapshot(e.Snapshot);
    }

    private void TrackSnapshot(EssentialsQueueOperationSnapshot snapshot)
    {
        lock (_activeTokens)
        {
            if (snapshot.IsActive)
            {
                if (_activeTokens.ContainsKey(snapshot.Id))
                {
                    return;
                }

                var description = snapshot.Status switch
                {
                    EssentialsQueueStatus.Running => $"Running essentials task {snapshot.Task.Name}",
                    _ => $"Queued essentials task {snapshot.Task.Name}"
                };

                var workToken = _workTracker.BeginWork(AutomationWorkType.Essentials, description);
                _activeTokens[snapshot.Id] = workToken;
                return;
            }

            if (_activeTokens.TryGetValue(snapshot.Id, out var token))
            {
                _workTracker.CompleteWork(token);
                _activeTokens.Remove(snapshot.Id);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _queue.OperationChanged -= OnOperationChanged;

        lock (_activeTokens)
        {
            foreach (var token in _activeTokens.Values)
            {
                _workTracker.CompleteWork(token);
            }

            _activeTokens.Clear();
        }
    }
}

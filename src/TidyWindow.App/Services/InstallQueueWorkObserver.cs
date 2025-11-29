using System;
using System.Collections.Generic;
using TidyWindow.Core.Install;

namespace TidyWindow.App.Services;

/// <summary>
/// Mirrors <see cref="InstallQueue"/> activity into the automation work tracker so PulseGuard can reason about active installs.
/// </summary>
public sealed class InstallQueueWorkObserver : IDisposable
{
    private readonly InstallQueue _installQueue;
    private readonly IAutomationWorkTracker _workTracker;
    private readonly Dictionary<Guid, Guid> _activeTokens = new();
    private bool _disposed;

    public InstallQueueWorkObserver(InstallQueue installQueue, IAutomationWorkTracker workTracker)
    {
        _installQueue = installQueue ?? throw new ArgumentNullException(nameof(installQueue));
        _workTracker = workTracker ?? throw new ArgumentNullException(nameof(workTracker));

        foreach (var snapshot in _installQueue.GetSnapshot())
        {
            TrackSnapshot(snapshot);
        }

        _installQueue.OperationChanged += OnOperationChanged;
    }

    private void OnOperationChanged(object? sender, InstallQueueChangedEventArgs e)
    {
        if (_disposed || e?.Snapshot is null)
        {
            return;
        }

        TrackSnapshot(e.Snapshot);
    }

    private void TrackSnapshot(InstallQueueOperationSnapshot snapshot)
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
                    InstallQueueStatus.Running => $"Installing {snapshot.Package.Name}",
                    _ => $"Queued install for {snapshot.Package.Name}"
                };

                var token = _workTracker.BeginWork(AutomationWorkType.Install, description);
                _activeTokens[snapshot.Id] = token;
                return;
            }

            if (_activeTokens.TryGetValue(snapshot.Id, out var existingToken))
            {
                _workTracker.CompleteWork(existingToken);
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
        _installQueue.OperationChanged -= OnOperationChanged;

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

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using TidyWindow.Core.Automation;

namespace TidyWindow.Core.Maintenance;

/// <summary>
/// Coordinates sequential execution of essentials automation scripts while notifying listeners of progress.
/// </summary>
public sealed class EssentialsTaskQueue : IDisposable
{
    private readonly PowerShellInvoker _powerShellInvoker;
    private readonly Channel<EssentialsQueueOperation> _channel;
    private readonly List<EssentialsQueueOperation> _operations = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _processingTask;

    public EssentialsTaskQueue(PowerShellInvoker powerShellInvoker)
    {
        _powerShellInvoker = powerShellInvoker ?? throw new ArgumentNullException(nameof(powerShellInvoker));
        _channel = Channel.CreateUnbounded<EssentialsQueueOperation>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
        _processingTask = Task.Run(ProcessQueueAsync, _cts.Token);
    }

    public event EventHandler<EssentialsQueueChangedEventArgs>? OperationChanged;

    public IReadOnlyList<EssentialsQueueOperationSnapshot> GetSnapshot()
    {
        lock (_operations)
        {
            return _operations.Select(op => op.CreateSnapshot()).ToImmutableArray();
        }
    }

    public EssentialsQueueOperationSnapshot Enqueue(EssentialsTaskDefinition task, IReadOnlyDictionary<string, object?>? parameters = null)
    {
        if (task is null)
        {
            throw new ArgumentNullException(nameof(task));
        }

        var operation = new EssentialsQueueOperation(task, parameters);

        lock (_operations)
        {
            _operations.Add(operation);
        }

        _channel.Writer.TryWrite(operation);
        var snapshot = operation.CreateSnapshot();
        RaiseOperationChanged(snapshot);
        return snapshot;
    }

    public EssentialsQueueOperationSnapshot? Cancel(Guid operationId)
    {
        EssentialsQueueOperationSnapshot? snapshot = null;

        lock (_operations)
        {
            var op = _operations.FirstOrDefault(o => o.Id == operationId);
            if (op is null)
            {
                return null;
            }

            op.RequestCancel("Cancellation requested by user.");
            snapshot = op.CreateSnapshot();
        }

        if (snapshot is not null)
        {
            RaiseOperationChanged(snapshot);
        }

        return snapshot;
    }

    public IReadOnlyList<EssentialsQueueOperationSnapshot> RetryFailed()
    {
        var snapshots = new List<EssentialsQueueOperationSnapshot>();

        lock (_operations)
        {
            foreach (var operation in _operations)
            {
                if (!operation.CanRetry)
                {
                    continue;
                }

                operation.ResetForRetry();
                _channel.Writer.TryWrite(operation);
                snapshots.Add(operation.CreateSnapshot());
            }
        }

        foreach (var snapshot in snapshots)
        {
            RaiseOperationChanged(snapshot);
        }

        return snapshots.ToImmutableArray();
    }

    public IReadOnlyList<EssentialsQueueOperationSnapshot> ClearCompleted()
    {
        var removed = new List<EssentialsQueueOperationSnapshot>();

        lock (_operations)
        {
            for (var index = _operations.Count - 1; index >= 0; index--)
            {
                var operation = _operations[index];
                if (operation.IsActive)
                {
                    continue;
                }

                removed.Add(operation.CreateSnapshot());
                _operations.RemoveAt(index);
            }
        }

        return removed.ToImmutableArray();
    }

    public void Dispose()
    {
        _cts.Cancel();
        _channel.Writer.TryComplete();

        try
        {
            _processingTask.Wait(TimeSpan.FromSeconds(5));
        }
        catch
        {
            // ignored
        }

        _cts.Dispose();
    }

    private async Task ProcessQueueAsync()
    {
        while (await _channel.Reader.WaitToReadAsync(_cts.Token).ConfigureAwait(false))
        {
            while (_channel.Reader.TryRead(out var operation))
            {
                var snapshot = operation.CreateSnapshot();

                if (snapshot.Status == EssentialsQueueStatus.Cancelled)
                {
                    RaiseOperationChanged(snapshot);
                    continue;
                }

                if (snapshot.Status != EssentialsQueueStatus.Pending)
                {
                    continue;
                }

                await ExecuteOperationAsync(operation, _cts.Token).ConfigureAwait(false);
            }
        }
    }

    private async Task ExecuteOperationAsync(EssentialsQueueOperation operation, CancellationToken cancellationToken)
    {
        if (operation.IsCancellationRequested)
        {
            operation.MarkCancelled("Cancelled before start.");
            RaiseOperationChanged(operation.CreateSnapshot());
            return;
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        operation.MarkRunning(linkedCts);
        RaiseOperationChanged(operation.CreateSnapshot());

        try
        {
            var scriptPath = operation.ResolveScriptPath();
            var result = await _powerShellInvoker.InvokeScriptAsync(scriptPath, operation.Parameters, linkedCts.Token).ConfigureAwait(false);

            if (result.IsSuccess)
            {
                operation.MarkCompleted(result.Output.ToImmutableArray(), result.Errors.ToImmutableArray());
                RaiseOperationChanged(operation.CreateSnapshot());
                return;
            }

            operation.MarkFailed(result.Output.ToImmutableArray(), result.Errors.ToImmutableArray());
            RaiseOperationChanged(operation.CreateSnapshot());
        }
        catch (OperationCanceledException)
        {
            operation.MarkCancelled("Cancelled.");
            RaiseOperationChanged(operation.CreateSnapshot());
        }
        catch (Exception ex)
        {
            operation.MarkFailed(ImmutableArray<string>.Empty, ImmutableArray.Create(ex.Message));
            RaiseOperationChanged(operation.CreateSnapshot());
        }
    }

    private void RaiseOperationChanged(EssentialsQueueOperationSnapshot snapshot)
    {
        OperationChanged?.Invoke(this, new EssentialsQueueChangedEventArgs(snapshot));
    }
}

public sealed class EssentialsQueueChangedEventArgs : EventArgs
{
    public EssentialsQueueChangedEventArgs(EssentialsQueueOperationSnapshot snapshot)
    {
        Snapshot = snapshot;
    }

    public EssentialsQueueOperationSnapshot Snapshot { get; }
}

public enum EssentialsQueueStatus
{
    Pending,
    Running,
    Succeeded,
    Failed,
    Cancelled
}

public sealed record EssentialsQueueOperationSnapshot(
    Guid Id,
    EssentialsTaskDefinition Task,
    EssentialsQueueStatus Status,
    int AttemptCount,
    DateTimeOffset EnqueuedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? LastMessage,
    ImmutableArray<string> Output,
    ImmutableArray<string> Errors,
    bool IsCancellationRequested)
{
    public bool IsActive => Status is EssentialsQueueStatus.Pending or EssentialsQueueStatus.Running;

    public bool IsSuccessful => Status == EssentialsQueueStatus.Succeeded;

    public bool CanRetry => Status == EssentialsQueueStatus.Failed;
}

internal sealed class EssentialsQueueOperation
{
    private readonly object _lock = new();
    private CancellationTokenSource? _executionCts;
    private EssentialsQueueStatus _status = EssentialsQueueStatus.Pending;
    private DateTimeOffset? _startedAt;
    private DateTimeOffset? _completedAt;
    private ImmutableArray<string> _output = ImmutableArray<string>.Empty;
    private ImmutableArray<string> _errors = ImmutableArray<string>.Empty;
    private string? _lastMessage = "Queued";
    private bool _cancelRequested;
    private int _attemptCount;

    public EssentialsQueueOperation(EssentialsTaskDefinition task, IReadOnlyDictionary<string, object?>? parameters)
    {
        Task = task ?? throw new ArgumentNullException(nameof(task));
        Id = Guid.NewGuid();
        EnqueuedAt = DateTimeOffset.UtcNow;
        Parameters = parameters is null
            ? ImmutableDictionary<string, object?>.Empty
            : parameters.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase);
    }

    public Guid Id { get; }

    public EssentialsTaskDefinition Task { get; }

    public DateTimeOffset EnqueuedAt { get; }

    public ImmutableDictionary<string, object?> Parameters { get; }

    public bool IsActive => _status is EssentialsQueueStatus.Pending or EssentialsQueueStatus.Running;

    public bool IsCancellationRequested => _cancelRequested;

    public bool CanRetry => !IsActive && _status == EssentialsQueueStatus.Failed;

    public EssentialsQueueOperationSnapshot CreateSnapshot()
    {
        lock (_lock)
        {
            return new EssentialsQueueOperationSnapshot(
                Id,
                Task,
                _status,
                _attemptCount,
                EnqueuedAt,
                _startedAt,
                _completedAt,
                _lastMessage,
                _output,
                _errors,
                _cancelRequested);
        }
    }

    public void RequestCancel(string reason)
    {
        lock (_lock)
        {
            _cancelRequested = true;
            _lastMessage = string.IsNullOrWhiteSpace(reason) ? "Cancellation requested." : reason.Trim();
            _executionCts?.Cancel();
        }
    }

    public void MarkRunning(CancellationTokenSource executionCts)
    {
        lock (_lock)
        {
            _attemptCount++;
            _status = EssentialsQueueStatus.Running;
            _startedAt = DateTimeOffset.UtcNow;
            _executionCts = executionCts;
            _lastMessage = "Running";
        }
    }

    public void MarkCompleted(ImmutableArray<string> output, ImmutableArray<string> errors)
    {
        lock (_lock)
        {
            _status = EssentialsQueueStatus.Succeeded;
            _completedAt = DateTimeOffset.UtcNow;
            _executionCts = null;
            _output = output;
            _errors = errors;
            _lastMessage = SelectSummary(output) ?? "Completed successfully.";
        }
    }

    public void MarkFailed(ImmutableArray<string> output, ImmutableArray<string> errors)
    {
        lock (_lock)
        {
            _status = EssentialsQueueStatus.Failed;
            _completedAt = DateTimeOffset.UtcNow;
            _executionCts = null;
            _output = output;
            _errors = errors;
            _lastMessage = SelectSummary(errors) ?? "Execution failed.";
        }
    }

    public void MarkCancelled(string message)
    {
        lock (_lock)
        {
            _status = EssentialsQueueStatus.Cancelled;
            _completedAt = DateTimeOffset.UtcNow;
            _executionCts = null;
            _lastMessage = string.IsNullOrWhiteSpace(message) ? "Cancelled." : message.Trim();
        }
    }

    public void ResetForRetry()
    {
        lock (_lock)
        {
            _status = EssentialsQueueStatus.Pending;
            _startedAt = null;
            _completedAt = null;
            _output = ImmutableArray<string>.Empty;
            _errors = ImmutableArray<string>.Empty;
            _lastMessage = "Retry queued";
            _cancelRequested = false;
            _executionCts = null;
        }
    }

    public string ResolveScriptPath()
    {
        return Task.ResolveScriptPath();
    }

    private static string? SelectSummary(IReadOnlyList<string> lines)
    {
        if (lines is null || lines.Count == 0)
        {
            return null;
        }

        for (var index = lines.Count - 1; index >= 0; index--)
        {
            var line = lines[index];
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (trimmed.StartsWith("at ", StringComparison.Ordinal) || trimmed.StartsWith("---", StringComparison.Ordinal))
            {
                continue;
            }

            if (trimmed.StartsWith("End of stack trace", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (trimmed.StartsWith("System.Management.Automation", StringComparison.Ordinal))
            {
                continue;
            }

            return trimmed;
        }

        return lines[^1]?.Trim();
    }
}

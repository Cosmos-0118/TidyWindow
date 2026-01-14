using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
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
    private const string RestoreTaskId = "restore-manager";

    private readonly PowerShellInvoker _powerShellInvoker;
    private readonly EssentialsTaskCatalog _catalog;
    private readonly IEssentialsQueueStateStore _stateStore;
    private readonly Channel<EssentialsQueueOperation> _channel;
    private readonly List<EssentialsQueueOperation> _operations = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _processingTask;
    private EssentialsTaskDefinition? _restoreTask;

    public EssentialsTaskQueue(PowerShellInvoker powerShellInvoker, EssentialsTaskCatalog catalog, IEssentialsQueueStateStore stateStore)
    {
        _powerShellInvoker = powerShellInvoker ?? throw new ArgumentNullException(nameof(powerShellInvoker));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _channel = Channel.CreateUnbounded<EssentialsQueueOperation>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
        RestorePersistedOperations();
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

    private void RestorePersistedOperations()
    {
        IReadOnlyList<EssentialsQueueOperationRecord> records;
        try
        {
            records = _stateStore.Load();
        }
        catch
        {
            records = Array.Empty<EssentialsQueueOperationRecord>();
        }

        if (records.Count == 0)
        {
            return;
        }

        foreach (var record in records)
        {
            if (string.IsNullOrWhiteSpace(record.TaskId))
            {
                continue;
            }

            EssentialsTaskDefinition? task;
            try
            {
                task = _catalog.GetTask(record.TaskId);
            }
            catch
            {
                continue;
            }

            if (task is null)
            {
                continue;
            }

            var operation = EssentialsQueueOperation.Restore(task, record);
            lock (_operations)
            {
                _operations.Add(operation);
            }

            if (operation.IsPending)
            {
                _channel.Writer.TryWrite(operation);
            }
        }

        RefreshPendingWaitMessages();
    }

    public EssentialsQueueOperationSnapshot Enqueue(EssentialsTaskDefinition task, IReadOnlyDictionary<string, object?>? parameters = null)
    {
        if (task is null)
        {
            throw new ArgumentNullException(nameof(task));
        }

        var restoreSnapshot = EnsureRestoreGuard(task);

        var operation = new EssentialsQueueOperation(task, parameters);

        lock (_operations)
        {
            _operations.Add(operation);
        }

        _channel.Writer.TryWrite(operation);
        var snapshot = operation.CreateSnapshot();
        if (restoreSnapshot is not null)
        {
            RaiseOperationChanged(restoreSnapshot);
        }
        RaiseOperationChanged(snapshot);
        RefreshPendingWaitMessages();
        return snapshot;
    }

    private EssentialsQueueOperationSnapshot? EnsureRestoreGuard(EssentialsTaskDefinition task)
    {
        if (task.Id.Equals(RestoreTaskId, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var restoreTask = _restoreTask ??= TryGetRestoreTask();
        if (restoreTask is null)
        {
            return null;
        }

        EssentialsQueueOperation? guardOperation = null;

        lock (_operations)
        {
            var existing = _operations.FirstOrDefault(op => op.Task.Id.Equals(RestoreTaskId, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                var status = existing.CreateSnapshot().Status;
                if (status is EssentialsQueueStatus.Pending or EssentialsQueueStatus.Running or EssentialsQueueStatus.Succeeded)
                {
                    return null;
                }
            }

            guardOperation = new EssentialsQueueOperation(restoreTask, parameters: null);
            _operations.Add(guardOperation);
        }

        _channel.Writer.TryWrite(guardOperation);
        return guardOperation?.CreateSnapshot();
    }

    private EssentialsTaskDefinition? TryGetRestoreTask()
    {
        try
        {
            return _catalog.GetTask(RestoreTaskId);
        }
        catch
        {
            return null;
        }
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

        RefreshPendingWaitMessages();
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

        PersistState();
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
        RefreshPendingWaitMessages(operation.Task.Name);

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
        finally
        {
            RefreshPendingWaitMessages();
        }
    }

    private void RefreshPendingWaitMessages(string? blockingTaskNameOverride = null)
    {
        var updates = new List<EssentialsQueueOperationSnapshot>();

        lock (_operations)
        {
            var blocker = blockingTaskNameOverride
                ?? _operations.FirstOrDefault(op => op.IsRunning)?.Task.Name;

            foreach (var operation in _operations)
            {
                if (!operation.IsPending)
                {
                    continue;
                }

                var message = blocker is null
                    ? "Queued and ready"
                    : $"Waiting for '{blocker}' to finish";

                if (operation.MarkWaiting(message))
                {
                    updates.Add(operation.CreateSnapshot());
                }
            }
        }

        foreach (var snapshot in updates)
        {
            RaiseOperationChanged(snapshot);
        }
    }

    private void PersistState()
    {
        List<EssentialsQueueOperationRecord> records;

        lock (_operations)
        {
            records = _operations.Select(op => op.CreateRecord()).ToList();
        }

        _stateStore.Save(records);
    }

    private void RaiseOperationChanged(EssentialsQueueOperationSnapshot snapshot)
    {
        OperationChanged?.Invoke(this, new EssentialsQueueChangedEventArgs(snapshot));
        PersistState();
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
        : this(task, parameters, null)
    {
    }

    private EssentialsQueueOperation(EssentialsTaskDefinition task, IReadOnlyDictionary<string, object?>? parameters, EssentialsQueueOperationRecord? record)
    {
        Task = task ?? throw new ArgumentNullException(nameof(task));
        Parameters = parameters is null
            ? ImmutableDictionary<string, object?>.Empty
            : parameters.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase);

        if (record is null)
        {
            Id = Guid.NewGuid();
            EnqueuedAt = DateTimeOffset.UtcNow;
            return;
        }

        Id = record.Id == Guid.Empty ? Guid.NewGuid() : record.Id;
        EnqueuedAt = record.EnqueuedAt == default ? DateTimeOffset.UtcNow : record.EnqueuedAt;
        _status = record.Status == EssentialsQueueStatus.Running ? EssentialsQueueStatus.Pending : record.Status;
        _attemptCount = record.AttemptCount;
        _startedAt = record.StartedAt;
        _completedAt = record.CompletedAt;
        _lastMessage = string.IsNullOrWhiteSpace(record.LastMessage) ? "Queued" : record.LastMessage;
        _output = record.Output is null ? ImmutableArray<string>.Empty : record.Output.ToImmutableArray();
        _errors = record.Errors is null ? ImmutableArray<string>.Empty : record.Errors.ToImmutableArray();
        _cancelRequested = record.IsCancellationRequested;
    }

    public static EssentialsQueueOperation Restore(EssentialsTaskDefinition task, EssentialsQueueOperationRecord record)
    {
        if (record is null)
        {
            throw new ArgumentNullException(nameof(record));
        }

        var parameters = ConvertParameters(record.Parameters);
        return new EssentialsQueueOperation(task, parameters, record);
    }

    public Guid Id { get; }

    public EssentialsTaskDefinition Task { get; }

    public DateTimeOffset EnqueuedAt { get; }

    public ImmutableDictionary<string, object?> Parameters { get; }

    public bool IsActive => _status is EssentialsQueueStatus.Pending or EssentialsQueueStatus.Running;

    public bool IsPending
    {
        get
        {
            lock (_lock)
            {
                return _status == EssentialsQueueStatus.Pending;
            }
        }
    }

    public bool IsRunning
    {
        get
        {
            lock (_lock)
            {
                return _status == EssentialsQueueStatus.Running;
            }
        }
    }

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

    public bool MarkWaiting(string? message)
    {
        lock (_lock)
        {
            if (_status != EssentialsQueueStatus.Pending)
            {
                return false;
            }

            var normalized = string.IsNullOrWhiteSpace(message) ? "Queued and ready" : message.Trim();
            if (string.Equals(_lastMessage, normalized, StringComparison.Ordinal))
            {
                return false;
            }

            _lastMessage = normalized;
            return true;
        }
    }

    public EssentialsQueueOperationRecord CreateRecord()
    {
        lock (_lock)
        {
            var output = _output.IsDefaultOrEmpty ? Array.Empty<string>() : _output.ToArray();
            var errors = _errors.IsDefaultOrEmpty ? Array.Empty<string>() : _errors.ToArray();

            return new EssentialsQueueOperationRecord(
                Id,
                Task.Id,
                _status,
                _attemptCount,
                EnqueuedAt,
                _startedAt,
                _completedAt,
                _lastMessage,
                output,
                errors,
                _cancelRequested,
                SerializeParameters());
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

    private IReadOnlyDictionary<string, JsonElement>? SerializeParameters()
    {
        if (Parameters.Count == 0)
        {
            return null;
        }

        var dictionary = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

        foreach (var kvp in Parameters)
        {
            dictionary[kvp.Key] = ToJsonElement(kvp.Value);
        }

        return dictionary;
    }

    private static JsonElement ToJsonElement(object? value)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(value));
        return document.RootElement.Clone();
    }

    private static IReadOnlyDictionary<string, object?>? ConvertParameters(IReadOnlyDictionary<string, JsonElement>? parameters)
    {
        if (parameters is null || parameters.Count == 0)
        {
            return null;
        }

        var dictionary = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        foreach (var kvp in parameters)
        {
            dictionary[kvp.Key] = ConvertJsonElement(kvp.Value);
        }

        return dictionary;
    }

    private static object? ConvertJsonElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var longValue) ? longValue : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Array => element.EnumerateArray().Select(ConvertJsonElement).ToArray(),
            JsonValueKind.Object => element.ToString(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => element.ToString()
        };
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

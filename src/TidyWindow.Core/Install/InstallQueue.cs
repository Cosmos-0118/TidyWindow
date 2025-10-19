using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using TidyWindow.Core.Automation;

namespace TidyWindow.Core.Install;

public sealed class InstallQueue : IDisposable
{
    private readonly PowerShellInvoker _powerShellInvoker;
    private readonly Channel<InstallQueueOperation> _channel;
    private readonly List<InstallQueueOperation> _operations = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly string _installerScriptPath;
    private readonly Task _processingTask;

    public event EventHandler<InstallQueueChangedEventArgs>? OperationChanged;

    public InstallQueue(PowerShellInvoker powerShellInvoker)
    {
        _powerShellInvoker = powerShellInvoker ?? throw new ArgumentNullException(nameof(powerShellInvoker));
        _channel = Channel.CreateUnbounded<InstallQueueOperation>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
        _installerScriptPath = ResolveInstallerScriptPath();
        _processingTask = Task.Run(ProcessQueueAsync, _cts.Token);
    }

    public IReadOnlyList<InstallQueueOperationSnapshot> GetSnapshot()
    {
        lock (_operations)
        {
            return _operations.Select(op => op.CreateSnapshot()).ToImmutableArray();
        }
    }

    public InstallQueueOperationSnapshot Enqueue(InstallPackageDefinition package)
    {
        if (package is null)
        {
            throw new ArgumentNullException(nameof(package));
        }

        lock (_operations)
        {
            var existing = _operations.FirstOrDefault(op => op.IsMatch(package.Id) && op.IsActive);
            if (existing is not null)
            {
                return existing.CreateSnapshot();
            }

            var operation = new InstallQueueOperation(package);
            _operations.Add(operation);
            _channel.Writer.TryWrite(operation);
            var snapshot = operation.CreateSnapshot();
            RaiseOperationChanged(snapshot);
            return snapshot;
        }
    }

    public IReadOnlyList<InstallQueueOperationSnapshot> EnqueueRange(IEnumerable<InstallPackageDefinition> packages)
    {
        if (packages is null)
        {
            throw new ArgumentNullException(nameof(packages));
        }

        var snapshots = new List<InstallQueueOperationSnapshot>();

        lock (_operations)
        {
            foreach (var package in packages)
            {
                if (package is null)
                {
                    continue;
                }

                var existing = _operations.FirstOrDefault(op => op.IsMatch(package.Id) && op.IsActive);
                if (existing is not null)
                {
                    snapshots.Add(existing.CreateSnapshot());
                    continue;
                }

                var operation = new InstallQueueOperation(package);
                _operations.Add(operation);
                _channel.Writer.TryWrite(operation);
                snapshots.Add(operation.CreateSnapshot());
            }
        }

        if (snapshots.Count > 0)
        {
            foreach (var snapshot in snapshots)
            {
                RaiseOperationChanged(snapshot);
            }
        }

        return snapshots.ToImmutableArray();
    }

    public InstallQueueOperationSnapshot? Cancel(Guid operationId)
    {
        InstallQueueOperationSnapshot? snapshot = null;

        lock (_operations)
        {
            var op = _operations.FirstOrDefault(x => x.Id == operationId);
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

    public IReadOnlyList<InstallQueueOperationSnapshot> RetryFailed()
    {
        var snapshots = new List<InstallQueueOperationSnapshot>();

        lock (_operations)
        {
            foreach (var op in _operations.Where(op => op.CanRetry))
            {
                op.ResetForRetry();
                _channel.Writer.TryWrite(op);
                snapshots.Add(op.CreateSnapshot());
            }
        }

        if (snapshots.Count > 0)
        {
            foreach (var snapshot in snapshots)
            {
                RaiseOperationChanged(snapshot);
            }
        }

        return snapshots.ToImmutableArray();
    }

    public IReadOnlyList<InstallQueueOperationSnapshot> ClearCompleted()
    {
        List<InstallQueueOperationSnapshot> removed = new();

        lock (_operations)
        {
            for (var index = _operations.Count - 1; index >= 0; index--)
            {
                var op = _operations[index];
                if (!op.IsActive)
                {
                    removed.Add(op.CreateSnapshot());
                    _operations.RemoveAt(index);
                }
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

                if (snapshot.Status == InstallQueueStatus.Cancelled)
                {
                    RaiseOperationChanged(snapshot);
                    continue;
                }

                if (snapshot.Status != InstallQueueStatus.Pending)
                {
                    continue;
                }

                await ExecuteOperationAsync(operation, _cts.Token).ConfigureAwait(false);
            }
        }
    }

    private async Task ExecuteOperationAsync(InstallQueueOperation operation, CancellationToken cancellationToken)
    {
        var attempts = 0;

        while (attempts < operation.MaxAttempts)
        {
            attempts++;

            if (operation.IsCancellationRequested)
            {
                operation.MarkCancelled("Cancelled before start.");
                RaiseOperationChanged(operation.CreateSnapshot());
                return;
            }

            var attemptStarted = DateTimeOffset.UtcNow;
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            operation.MarkRunning(linkedCts);
            RaiseOperationChanged(operation.CreateSnapshot());

            try
            {
                var result = await InvokeInstallerAsync(operation.Package, linkedCts.Token).ConfigureAwait(false);
                var attemptCompleted = DateTimeOffset.UtcNow;

                if (result.IsSuccess)
                {
                    var summary = SelectSummary(result.Output) ?? $"{operation.Package.Name} installed.";
                    operation.MarkCompleted(new InstallAttemptResult(attempts, attemptStarted, attemptCompleted, true, result.Output.ToImmutableArray(), result.Errors.ToImmutableArray(), summary));
                    RaiseOperationChanged(operation.CreateSnapshot());
                    return;
                }

                var errorSummary = SelectSummary(result.Errors) ?? "Installation failed.";
                var attemptResult = new InstallAttemptResult(attempts, attemptStarted, attemptCompleted, false, result.Output.ToImmutableArray(), result.Errors.ToImmutableArray(), errorSummary);
                var shouldRetry = attempts < operation.MaxAttempts && !operation.IsCancellationRequested;
                operation.MarkFailedAttempt(attemptResult, shouldRetry);
                RaiseOperationChanged(operation.CreateSnapshot());

                if (!shouldRetry)
                {
                    return;
                }

                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                operation.MarkCancelled("Installation cancelled.");
                RaiseOperationChanged(operation.CreateSnapshot());
                return;
            }
            catch (Exception ex)
            {
                var attemptCompleted = DateTimeOffset.UtcNow;
                var attemptResult = new InstallAttemptResult(attempts, attemptStarted, attemptCompleted, false, ImmutableArray<string>.Empty, ImmutableArray.Create(ex.Message), ex.Message);
                var shouldRetry = attempts < operation.MaxAttempts && !operation.IsCancellationRequested;
                operation.MarkFailedAttempt(attemptResult, shouldRetry);
                RaiseOperationChanged(operation.CreateSnapshot());

                if (!shouldRetry)
                {
                    return;
                }

                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<PowerShellInvocationResult> InvokeInstallerAsync(InstallPackageDefinition package, CancellationToken cancellationToken)
    {
        var parameters = new Dictionary<string, object?>
        {
            ["PackageId"] = package.Id,
            ["DisplayName"] = package.Name,
            ["Manager"] = package.Manager,
            ["Command"] = package.Command
        };

        if (package.RequiresAdmin)
        {
            parameters["RequiresAdmin"] = true;
        }

        if (!package.Buckets.IsDefaultOrEmpty)
        {
            parameters["Buckets"] = package.Buckets.ToArray();
        }

        return await _powerShellInvoker.InvokeScriptAsync(_installerScriptPath, parameters, cancellationToken).ConfigureAwait(false);
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

    private static string ResolveInstallerScriptPath()
    {
        var relative = Path.Combine("automation", "scripts", "install-catalog-package.ps1");
        var baseDirectory = AppContext.BaseDirectory;
        var candidate = Path.Combine(baseDirectory, relative);

        if (File.Exists(candidate))
        {
            return candidate;
        }

        var directory = new DirectoryInfo(baseDirectory);
        while (directory is not null)
        {
            candidate = Path.Combine(directory.FullName, relative);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Unable to locate '{relative}'.", relative);
    }

    private void RaiseOperationChanged(InstallQueueOperationSnapshot snapshot)
    {
        OperationChanged?.Invoke(this, new InstallQueueChangedEventArgs(snapshot));
    }
}

public sealed class InstallQueueOperation
{
    private readonly object _lock = new();
    private readonly List<InstallAttemptResult> _attempts = new();
    private CancellationTokenSource? _executionCts;
    private InstallQueueStatus _status = InstallQueueStatus.Pending;
    private DateTimeOffset? _startedAt;
    private DateTimeOffset? _completedAt;
    private string? _lastMessage;
    private bool _cancelRequested;

    internal InstallQueueOperation(InstallPackageDefinition package)
    {
        Package = package ?? throw new ArgumentNullException(nameof(package));
        Id = Guid.NewGuid();
        EnqueuedAt = DateTimeOffset.UtcNow;
        _lastMessage = "Queued";
    }

    public Guid Id { get; }

    public InstallPackageDefinition Package { get; }

    public DateTimeOffset EnqueuedAt { get; }

    public int MaxAttempts { get; } = 3;

    public bool IsCancellationRequested
    {
        get
        {
            lock (_lock)
            {
                return _cancelRequested;
            }
        }
    }

    public bool CanRetry
    {
        get
        {
            lock (_lock)
            {
                return _status is InstallQueueStatus.Failed || (_status == InstallQueueStatus.Cancelled && _attempts.Count > 0);
            }
        }
    }

    public bool IsActive
    {
        get
        {
            lock (_lock)
            {
                return _status is InstallQueueStatus.Pending or InstallQueueStatus.Running;
            }
        }
    }

    internal bool IsMatch(string packageId) => string.Equals(Package.Id, packageId, StringComparison.OrdinalIgnoreCase);

    internal void MarkRunning(CancellationTokenSource executionCts)
    {
        lock (_lock)
        {
            _executionCts?.Dispose();
            _executionCts = executionCts;
            _status = InstallQueueStatus.Running;
            _startedAt ??= DateTimeOffset.UtcNow;
            _lastMessage = "Installing...";
            _cancelRequested = false;
        }
    }

    internal void MarkCompleted(InstallAttemptResult attempt)
    {
        lock (_lock)
        {
            _attempts.Add(attempt);
            _status = InstallQueueStatus.Succeeded;
            _completedAt = attempt.CompletedAt;
            _lastMessage = attempt.Summary;
            DisposeExecutionCts_NoLock();
        }
    }

    internal void MarkFailedAttempt(InstallAttemptResult attempt, bool willRetry)
    {
        lock (_lock)
        {
            _attempts.Add(attempt);
            _lastMessage = attempt.Summary;
            if (willRetry)
            {
                _status = InstallQueueStatus.Pending;
            }
            else
            {
                _status = InstallQueueStatus.Failed;
                _completedAt = attempt.CompletedAt;
            }
            DisposeExecutionCts_NoLock();
        }
    }

    internal void MarkCancelled(string reason)
    {
        lock (_lock)
        {
            _cancelRequested = true;
            _status = InstallQueueStatus.Cancelled;
            _completedAt = DateTimeOffset.UtcNow;
            _lastMessage = string.IsNullOrWhiteSpace(reason) ? "Cancelled." : reason;
            DisposeExecutionCts_NoLock();
        }
    }

    internal void ResetForRetry()
    {
        lock (_lock)
        {
            if (_status is not (InstallQueueStatus.Failed or InstallQueueStatus.Cancelled))
            {
                return;
            }

            _status = InstallQueueStatus.Pending;
            _cancelRequested = false;
            _completedAt = null;
            _lastMessage = "Retry queued.";
        }
    }

    internal void RequestCancel(string reason)
    {
        lock (_lock)
        {
            _cancelRequested = true;
            if (_status == InstallQueueStatus.Pending)
            {
                _status = InstallQueueStatus.Cancelled;
                _completedAt = DateTimeOffset.UtcNow;
                _lastMessage = reason;
            }
            _executionCts?.Cancel();
        }
    }

    public InstallQueueOperationSnapshot CreateSnapshot()
    {
        lock (_lock)
        {
            var attempts = _attempts.ToImmutableArray();
            var lastAttempt = attempts.LastOrDefault();
            var canRetry = _status is InstallQueueStatus.Failed || (_status == InstallQueueStatus.Cancelled && attempts.Length > 0);
            var isActive = _status is InstallQueueStatus.Pending or InstallQueueStatus.Running;
            return new InstallQueueOperationSnapshot(
                Id,
                Package,
                _status,
                EnqueuedAt,
                _startedAt,
                _completedAt,
                attempts.Length,
                _lastMessage,
                lastAttempt?.Output ?? ImmutableArray<string>.Empty,
                lastAttempt?.Errors ?? ImmutableArray<string>.Empty,
                canRetry,
                isActive);
        }
    }

    private void DisposeExecutionCts_NoLock()
    {
        var cts = _executionCts;
        _executionCts = null;
        cts?.Dispose();
    }
}

public sealed record InstallQueueOperationSnapshot(
    Guid Id,
    InstallPackageDefinition Package,
    InstallQueueStatus Status,
    DateTimeOffset EnqueuedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    int AttemptCount,
    string? LastMessage,
    ImmutableArray<string> Output,
    ImmutableArray<string> Errors,
    bool CanRetry,
    bool IsActive);

public sealed class InstallQueueChangedEventArgs : EventArgs
{
    public InstallQueueChangedEventArgs(InstallQueueOperationSnapshot snapshot)
    {
        Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
    }

    public InstallQueueOperationSnapshot Snapshot { get; }
}

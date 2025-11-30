using System;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace TidyWindow.App.Services;

/// <summary>
/// Provides helpers for inspecting System Restore checkpoints and broadcasting prompts when safeguards are missing.
/// </summary>
public sealed class SystemRestoreGuardService : ISystemRestoreGuardService
{
    private readonly object _promptLock = new();
    private readonly Func<CancellationToken, Task<DateTimeOffset?>> _checkpointProvider;
    private SystemRestoreGuardPrompt? _pendingPrompt;

    public event EventHandler<SystemRestoreGuardPromptEventArgs>? PromptRequested;

    public SystemRestoreGuardService(Func<CancellationToken, Task<DateTimeOffset?>>? checkpointProvider = null)
    {
        _checkpointProvider = checkpointProvider ?? QueryLatestRestorePointUtcAsync;
    }

    public async Task<SystemRestoreGuardCheckResult> CheckAsync(TimeSpan freshnessThreshold, CancellationToken cancellationToken = default)
    {
        if (freshnessThreshold <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(freshnessThreshold), "Threshold must be positive.");
        }

        DateTimeOffset? latest = null;
        string? error = null;

        try
        {
            latest = await _checkpointProvider(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            error = ex.Message;
        }

        if (latest is null)
        {
            var reason = string.IsNullOrWhiteSpace(error)
                ? "No System Restore checkpoints were found."
                : error;
            return new SystemRestoreGuardCheckResult(false, null, reason);
        }

        var isFresh = DateTimeOffset.UtcNow - latest.Value <= freshnessThreshold;
        return new SystemRestoreGuardCheckResult(isFresh, latest, null);
    }

    public void RequestPrompt(SystemRestoreGuardPrompt prompt)
    {
        if (prompt is null)
        {
            throw new ArgumentNullException(nameof(prompt));
        }

        SystemRestoreGuardPrompt captured;
        lock (_promptLock)
        {
            _pendingPrompt = prompt;
            captured = prompt;
        }

        PromptRequested?.Invoke(this, new SystemRestoreGuardPromptEventArgs(captured));
    }

    public bool TryConsumePendingPrompt(out SystemRestoreGuardPrompt prompt)
    {
        lock (_promptLock)
        {
            if (_pendingPrompt is not null)
            {
                prompt = _pendingPrompt;
                _pendingPrompt = null;
                return true;
            }
        }

        prompt = null!;
        return false;
    }

    private static Task<DateTimeOffset?> QueryLatestRestorePointUtcAsync(CancellationToken cancellationToken)
    {
        return Task.Run(QueryLatestRestorePointUtc, cancellationToken);
    }

    private static DateTimeOffset? QueryLatestRestorePointUtc()
    {
        using var searcher = new ManagementObjectSearcher("root/default", "SELECT CreationTime FROM SystemRestore");
        using var collection = searcher.Get();

        DateTimeOffset? latest = null;

        foreach (ManagementObject instance in collection)
        {
            using (instance)
            {
                var raw = instance["CreationTime"] as string;
                if (string.IsNullOrWhiteSpace(raw))
                {
                    continue;
                }

                DateTime parsed;
                try
                {
                    parsed = ManagementDateTimeConverter.ToDateTime(raw);
                }
                catch
                {
                    continue;
                }

                var normalized = DateTime.SpecifyKind(parsed, DateTimeKind.Local);
                var candidate = new DateTimeOffset(normalized).ToUniversalTime();
                if (latest is null || candidate > latest.Value)
                {
                    latest = candidate;
                }
            }
        }

        return latest;
    }

    private static bool IsRecoverable(Exception exception)
    {
        return exception is ManagementException
            or UnauthorizedAccessException
            or COMException
            or InvalidOperationException;
    }
}

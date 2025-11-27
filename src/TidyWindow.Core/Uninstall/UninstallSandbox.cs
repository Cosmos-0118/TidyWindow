using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace TidyWindow.Core.Uninstall;

public interface IUninstallTelemetrySink
{
    Task PublishAsync(UninstallTelemetryRecord record, CancellationToken cancellationToken = default);
}

public sealed record UninstallTelemetryRecord(
    string OperationId,
    string DisplayName,
    bool DryRun,
    string Publisher,
    string Version,
    IReadOnlyDictionary<string, string> Metadata,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    IReadOnlyList<UninstallCommandSnapshot> Steps);

public sealed record UninstallCommandSnapshot(
    UninstallCommandPlan Plan,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    int? ExitCode,
    IReadOnlyList<string> Output,
    IReadOnlyList<string> Errors,
    bool DryRun,
    string Message)
{
    public TimeSpan Duration => CompletedAt - StartedAt;

    public bool IsSuccess => ExitCode is null || ExitCode == 0;
}

public sealed record UninstallExecutionResult(
    int ExitCode,
    IReadOnlyList<string> Output,
    IReadOnlyList<string> Errors,
    string? Notes = null)
{
    public bool IsSuccess => ExitCode == 0;
}

public sealed record UninstallOperationResult(
    UninstallOperationPlan Plan,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    IReadOnlyList<UninstallCommandSnapshot> Steps)
{
    public bool IsDryRun => Plan.DryRun;

    public bool IsSuccess => Steps.All(static snapshot => snapshot.IsSuccess);

    public TimeSpan Duration => CompletedAt - StartedAt;
}

public sealed class NoOpUninstallTelemetrySink : IUninstallTelemetrySink
{
    public Task PublishAsync(UninstallTelemetryRecord record, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }
}

public sealed class UninstallSandbox
{
    private readonly IUninstallTelemetrySink _telemetrySink;

    public UninstallSandbox(IUninstallTelemetrySink? telemetrySink = null)
    {
        _telemetrySink = telemetrySink ?? new NoOpUninstallTelemetrySink();
    }

    public async Task<UninstallOperationResult> ExecuteAsync(
        UninstallOperationPlan plan,
        Func<UninstallCommandPlan, CancellationToken, Task<UninstallExecutionResult>> executor,
        CancellationToken cancellationToken = default)
    {
        if (plan is null)
        {
            throw new ArgumentNullException(nameof(plan));
        }

        if (executor is null)
        {
            throw new ArgumentNullException(nameof(executor));
        }

        var snapshots = new List<UninstallCommandSnapshot>(plan.Steps.Count);
        var startedAt = DateTimeOffset.UtcNow;

        foreach (var step in plan.Steps)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (plan.DryRun)
            {
                var timestamp = DateTimeOffset.UtcNow;
                snapshots.Add(new UninstallCommandSnapshot(
                    step,
                    timestamp,
                    timestamp,
                    null,
                    Array.Empty<string>(),
                    Array.Empty<string>(),
                    true,
                    $"Dry run: would execute {step.CommandLine}"));
                continue;
            }

            var stepStart = DateTimeOffset.UtcNow;
            UninstallExecutionResult execution;
            try
            {
                execution = await executor(step, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                var now = DateTimeOffset.UtcNow;
                snapshots.Add(new UninstallCommandSnapshot(
                    step,
                    stepStart,
                    now,
                    null,
                    Array.Empty<string>(),
                    new[] { ex.Message },
                    false,
                    "Executor threw an exception."));
                var partial = new ReadOnlyCollection<UninstallCommandSnapshot>(snapshots);
                await EmitTelemetryAsync(plan, startedAt, now, partial, cancellationToken).ConfigureAwait(false);
                throw;
            }

            var stepEnd = DateTimeOffset.UtcNow;
            snapshots.Add(new UninstallCommandSnapshot(
                step,
                stepStart,
                stepEnd,
                execution.ExitCode,
                execution.Output ?? Array.Empty<string>(),
                execution.Errors ?? Array.Empty<string>(),
                false,
                execution.Notes ?? string.Empty));
        }

        var completedAt = DateTimeOffset.UtcNow;
        var readOnly = new ReadOnlyCollection<UninstallCommandSnapshot>(snapshots);
        var result = new UninstallOperationResult(plan, startedAt, completedAt, readOnly);
        await EmitTelemetryAsync(plan, startedAt, completedAt, readOnly, cancellationToken).ConfigureAwait(false);
        return result;
    }

    private Task EmitTelemetryAsync(
        UninstallOperationPlan plan,
        DateTimeOffset startedAt,
        DateTimeOffset completedAt,
        IReadOnlyList<UninstallCommandSnapshot> steps,
        CancellationToken cancellationToken)
    {
        var record = new UninstallTelemetryRecord(
            plan.OperationId,
            plan.DisplayName,
            plan.DryRun,
            plan.Publisher,
            plan.Version,
            plan.Metadata,
            startedAt,
            completedAt,
            steps);

        return _telemetrySink.PublishAsync(record, cancellationToken);
    }
}

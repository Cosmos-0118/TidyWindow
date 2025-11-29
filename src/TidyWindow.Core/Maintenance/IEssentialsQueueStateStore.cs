using System;
using System.Collections.Generic;
using System.Text.Json;

namespace TidyWindow.Core.Maintenance;

public interface IEssentialsQueueStateStore
{
    IReadOnlyList<EssentialsQueueOperationRecord> Load();

    void Save(IReadOnlyList<EssentialsQueueOperationRecord> operations);
}

public sealed record EssentialsQueueOperationRecord(
    Guid Id,
    string TaskId,
    EssentialsQueueStatus Status,
    int AttemptCount,
    DateTimeOffset EnqueuedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? LastMessage,
    IReadOnlyList<string>? Output,
    IReadOnlyList<string>? Errors,
    bool IsCancellationRequested,
    IReadOnlyDictionary<string, JsonElement>? Parameters);

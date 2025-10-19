using System;
using System.Collections.Generic;
using System.Linq;

namespace TidyWindow.Core.Cleanup;

/// <summary>
/// Represents the outcome of a cleanup delete operation.
/// </summary>
public sealed class CleanupDeletionResult
{
    public CleanupDeletionResult(int deletedCount, int skippedCount, IReadOnlyList<string> errors)
    {
        if (deletedCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(deletedCount));
        }

        if (skippedCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(skippedCount));
        }

        DeletedCount = deletedCount;
        SkippedCount = skippedCount;
        Errors = errors ?? Array.Empty<string>();
    }

    public int DeletedCount { get; }

    public int SkippedCount { get; }

    public IReadOnlyList<string> Errors { get; }

    public bool HasErrors => Errors.Count > 0;

    public string ToStatusMessage()
    {
        var parts = new List<string>
        {
            $"Deleted {DeletedCount:N0} file" + (DeletedCount == 1 ? string.Empty : "s")
        };

        if (SkippedCount > 0)
        {
            parts.Add($"skipped {SkippedCount:N0}");
        }

        if (HasErrors)
        {
            parts.Add("errors: " + string.Join("; ", Errors.Take(3)) + (Errors.Count > 3 ? "..." : string.Empty));
        }

        return string.Join(", ", parts);
    }
}

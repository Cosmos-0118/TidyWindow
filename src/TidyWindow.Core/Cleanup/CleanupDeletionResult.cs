using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace TidyWindow.Core.Cleanup;

/// <summary>
/// Represents the action taken for a cleanup item.
/// </summary>
public enum CleanupDeletionDisposition
{
    Deleted,
    Skipped,
    Failed
}

/// <summary>
/// Tracks the outcome of deleting a single cleanup candidate.
/// </summary>
public sealed record CleanupDeletionEntry(
    string Path,
    long SizeBytes,
    bool IsDirectory,
    CleanupDeletionDisposition Disposition,
    string? Reason = null,
    Exception? Exception = null)
{
    public string EffectiveReason => string.IsNullOrWhiteSpace(Reason)
        ? Disposition switch
        {
            CleanupDeletionDisposition.Deleted => "Deleted successfully",
            CleanupDeletionDisposition.Skipped => "Skipped by policy",
            CleanupDeletionDisposition.Failed => Exception?.Message ?? "Deletion failed",
            _ => string.Empty
        }
        : Reason!;
}

/// <summary>
/// Represents the outcome of a cleanup delete operation.
/// </summary>
public sealed class CleanupDeletionResult
{
    public CleanupDeletionResult(IEnumerable<CleanupDeletionEntry> entries)
    {
        if (entries is null)
        {
            throw new ArgumentNullException(nameof(entries));
        }

        var list = entries.ToList();
        Entries = new ReadOnlyCollection<CleanupDeletionEntry>(list);

        DeletedCount = list.Count(static entry => entry.Disposition == CleanupDeletionDisposition.Deleted);
        SkippedCount = list.Count(static entry => entry.Disposition == CleanupDeletionDisposition.Skipped);
        FailedCount = list.Count(static entry => entry.Disposition == CleanupDeletionDisposition.Failed);

        TotalBytesDeleted = list.Where(static entry => entry.Disposition == CleanupDeletionDisposition.Deleted)
            .Sum(static entry => Math.Max(entry.SizeBytes, 0));
        TotalBytesSkipped = list.Where(static entry => entry.Disposition == CleanupDeletionDisposition.Skipped)
            .Sum(static entry => Math.Max(entry.SizeBytes, 0));
        TotalBytesFailed = list.Where(static entry => entry.Disposition == CleanupDeletionDisposition.Failed)
            .Sum(static entry => Math.Max(entry.SizeBytes, 0));

        Errors = list.Where(static entry => entry.Disposition == CleanupDeletionDisposition.Failed)
            .Select(static entry => string.IsNullOrWhiteSpace(entry.Reason)
                ? entry.Path + ": " + (entry.Exception?.Message ?? "Deletion failed")
                : entry.Path + ": " + entry.Reason)
            .ToArray();
    }

    public IReadOnlyList<CleanupDeletionEntry> Entries { get; }

    public int DeletedCount { get; }

    public int SkippedCount { get; }

    public int FailedCount { get; }

    public long TotalBytesDeleted { get; }

    public long TotalBytesSkipped { get; }

    public long TotalBytesFailed { get; }

    public IReadOnlyList<string> Errors { get; }

    public bool HasErrors => Errors.Count > 0;

    public string ToStatusMessage()
    {
        var parts = new List<string>();

        var deletedLabel = DeletedCount == 1 ? "item" : "items";
        var deletedSummary = $"Deleted {DeletedCount:N0} {deletedLabel}";
        if (TotalBytesDeleted > 0)
        {
            deletedSummary += $" • {FormatMegabytes(TotalBytesDeleted):F2} MB freed";
        }

        parts.Add(deletedSummary);

        if (SkippedCount > 0)
        {
            parts.Add($"skipped {SkippedCount:N0}");
        }

        if (FailedCount > 0)
        {
            parts.Add($"failed {FailedCount:N0}");
        }

        if (HasErrors)
        {
            parts.Add("errors: " + string.Join("; ", Errors.Take(3)) + (Errors.Count > 3 ? "..." : string.Empty));
        }

        return string.Join(", ", parts);
    }

    private static double FormatMegabytes(long bytes)
    {
        if (bytes <= 0)
        {
            return 0d;
        }

        return bytes / 1_048_576d;
    }
}

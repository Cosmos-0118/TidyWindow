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
    /// <summary>
    /// The item was successfully deleted and the space is now freed.
    /// </summary>
    Deleted,

    /// <summary>
    /// The item was skipped due to policy or filter settings.
    /// </summary>
    Skipped,

    /// <summary>
    /// The deletion failed and the item still exists on disk.
    /// </summary>
    Failed,

    /// <summary>
    /// The item could not be deleted immediately and has been scheduled for removal on next reboot.
    /// The space is NOT freed until after a restart.
    /// </summary>
    PendingReboot
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
    /// <summary>
    /// Gets the actual bytes that were freed by this deletion.
    /// For <see cref="CleanupDeletionDisposition.PendingReboot"/>, this is 0 because the space isn't freed until reboot.
    /// </summary>
    public long ActualBytesFreed => Disposition == CleanupDeletionDisposition.Deleted ? Math.Max(SizeBytes, 0) : 0;

    public string EffectiveReason => string.IsNullOrWhiteSpace(Reason)
        ? Disposition switch
        {
            CleanupDeletionDisposition.Deleted => "Deleted successfully",
            CleanupDeletionDisposition.Skipped => "Skipped by policy",
            CleanupDeletionDisposition.Failed => Exception?.Message ?? "Deletion failed",
            CleanupDeletionDisposition.PendingReboot => "Scheduled for removal after restart",
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
        PendingRebootCount = list.Count(static entry => entry.Disposition == CleanupDeletionDisposition.PendingReboot);

        // Only count bytes that were ACTUALLY freed (verified deleted items only)
        TotalBytesDeleted = list.Where(static entry => entry.Disposition == CleanupDeletionDisposition.Deleted)
            .Sum(static entry => entry.ActualBytesFreed);
        TotalBytesSkipped = list.Where(static entry => entry.Disposition == CleanupDeletionDisposition.Skipped)
            .Sum(static entry => Math.Max(entry.SizeBytes, 0));
        TotalBytesFailed = list.Where(static entry => entry.Disposition == CleanupDeletionDisposition.Failed)
            .Sum(static entry => Math.Max(entry.SizeBytes, 0));
        // PendingReboot items haven't freed any space yet - they still consume disk space until reboot
        TotalBytesPendingReboot = list.Where(static entry => entry.Disposition == CleanupDeletionDisposition.PendingReboot)
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

    /// <summary>
    /// Number of items scheduled for deletion on next reboot.
    /// </summary>
    public int PendingRebootCount { get; }

    /// <summary>
    /// Bytes that were actually freed immediately. Does not include pending reboot items.
    /// </summary>
    public long TotalBytesDeleted { get; }

    public long TotalBytesSkipped { get; }

    public long TotalBytesFailed { get; }

    /// <summary>
    /// Bytes that will be freed after the next reboot. Not included in <see cref="TotalBytesDeleted"/>.
    /// </summary>
    public long TotalBytesPendingReboot { get; }

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

        // Show pending reboot items separately so users know space isn't freed yet
        if (PendingRebootCount > 0)
        {
            var pendingLabel = PendingRebootCount == 1 ? "item" : "items";
            var pendingSummary = $"{PendingRebootCount:N0} {pendingLabel} pending reboot";
            if (TotalBytesPendingReboot > 0)
            {
                pendingSummary += $" ({FormatMegabytes(TotalBytesPendingReboot):F2} MB)";
            }
            parts.Add(pendingSummary);
        }

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

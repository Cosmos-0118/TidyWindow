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
    /// Indicates the item was moved to the Recycle Bin rather than permanently deleted.
    /// The space is NOT freed from disk until the Recycle Bin is emptied.
    /// </summary>
    public bool WasRecycled { get; init; }

    /// <summary>
    /// Gets the actual bytes that were permanently freed by this deletion.
    /// Returns 0 for recycled items (still on disk) and pending reboot items.
    /// </summary>
    public long ActualBytesFreed => Disposition == CleanupDeletionDisposition.Deleted && !WasRecycled ? Math.Max(SizeBytes, 0) : 0;

    /// <summary>
    /// Gets the bytes moved to the Recycle Bin (still consuming disk space until emptied).
    /// </summary>
    public long BytesMovedToRecycleBin => Disposition == CleanupDeletionDisposition.Deleted && WasRecycled ? Math.Max(SizeBytes, 0) : 0;

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
        RecycledCount = list.Count(static entry => entry.Disposition == CleanupDeletionDisposition.Deleted && entry.WasRecycled);
        SkippedCount = list.Count(static entry => entry.Disposition == CleanupDeletionDisposition.Skipped);
        FailedCount = list.Count(static entry => entry.Disposition == CleanupDeletionDisposition.Failed);
        PendingRebootCount = list.Count(static entry => entry.Disposition == CleanupDeletionDisposition.PendingReboot);

        // Only count bytes that were permanently freed (not recycled items that still consume disk space)
        TotalBytesDeleted = list.Where(static entry => entry.Disposition == CleanupDeletionDisposition.Deleted)
            .Sum(static entry => entry.ActualBytesFreed);
        // Bytes moved to Recycle Bin - still consuming disk space until bin is emptied
        TotalBytesRecycled = list.Where(static entry => entry.Disposition == CleanupDeletionDisposition.Deleted)
            .Sum(static entry => entry.BytesMovedToRecycleBin);
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

    /// <summary>
    /// Number of items moved to the Recycle Bin (subset of <see cref="DeletedCount"/>).
    /// </summary>
    public int RecycledCount { get; }

    public int SkippedCount { get; }

    public int FailedCount { get; }

    /// <summary>
    /// Number of items scheduled for deletion on next reboot.
    /// </summary>
    public int PendingRebootCount { get; }

    /// <summary>
    /// Bytes that were actually freed immediately. Does not include recycled or pending reboot items.
    /// </summary>
    public long TotalBytesDeleted { get; }

    /// <summary>
    /// Bytes moved to the Recycle Bin. Still consuming disk space until the bin is emptied.
    /// </summary>
    public long TotalBytesRecycled { get; }

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

        var permanentlyDeleted = DeletedCount - RecycledCount;
        if (permanentlyDeleted > 0)
        {
            var deletedLabel = permanentlyDeleted == 1 ? "item" : "items";
            var deletedSummary = $"Deleted {permanentlyDeleted:N0} {deletedLabel}";
            if (TotalBytesDeleted > 0)
            {
                deletedSummary += $" • {FormatMegabytes(TotalBytesDeleted):F2} MB freed";
            }

            parts.Add(deletedSummary);
        }

        // Show recycled items separately so users know space isn't freed until bin is emptied
        if (RecycledCount > 0)
        {
            var recycledLabel = RecycledCount == 1 ? "item" : "items";
            var recycledSummary = $"{RecycledCount:N0} {recycledLabel} moved to Recycle Bin";
            if (TotalBytesRecycled > 0)
            {
                recycledSummary += $" ({FormatMegabytes(TotalBytesRecycled):F2} MB — empty bin to free space)";
            }
            parts.Add(recycledSummary);
        }

        if (permanentlyDeleted == 0 && RecycledCount == 0)
        {
            parts.Add($"Deleted {DeletedCount:N0} {(DeletedCount == 1 ? "item" : "items")}");
        }

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

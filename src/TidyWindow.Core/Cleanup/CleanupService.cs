using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

namespace TidyWindow.Core.Cleanup;

/// <summary>
/// Provides high-performance cleanup preview and deletion operations without external scripting.
/// </summary>
public sealed class CleanupService
{
    private readonly CleanupScanner _scanner;

    public CleanupService()
        : this(new CleanupScanner(new CleanupDefinitionProvider()))
    {
    }

    internal CleanupService(CleanupScanner scanner)
    {
        _scanner = scanner ?? throw new ArgumentNullException(nameof(scanner));
    }

    public Task<CleanupReport> PreviewAsync(bool includeDownloads, bool includeBrowserHistory, int previewCount, CleanupItemKind itemKind = CleanupItemKind.Files, CancellationToken cancellationToken = default)
    {
        return _scanner.ScanAsync(includeDownloads, includeBrowserHistory, previewCount, itemKind, cancellationToken);
    }

    public Task<CleanupReport> PreviewAsync(bool includeDownloads, bool includeBrowserHistory, int previewCount, CleanupItemKind itemKind, IProgress<CleanupScanProgress>? progress, CancellationToken cancellationToken = default)
    {
        return _scanner.ScanAsync(includeDownloads, includeBrowserHistory, previewCount, itemKind, progress, cancellationToken);
    }

    public Task<CleanupDeletionResult> DeleteAsync(
        IEnumerable<CleanupPreviewItem> items,
        IProgress<CleanupDeletionProgress>? progress = null,
        CleanupDeletionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (items is null)
        {
            throw new ArgumentNullException(nameof(items));
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalizedItems = new List<CleanupPreviewItem>();

        foreach (var item in items)
        {
            if (item is null)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(item.FullName))
            {
                continue;
            }

            var candidate = item.FullName.Trim();
            if (candidate.Length == 0)
            {
                continue;
            }

            if (seen.Add(candidate))
            {
                normalizedItems.Add(item);
            }
        }

        if (normalizedItems.Count == 0)
        {
            return Task.FromResult(new CleanupDeletionResult(Array.Empty<CleanupDeletionEntry>()));
        }

        // Remove child items when their parent directory is also selected to prevent double-counting
        normalizedItems = DeduplicateOverlappingPaths(normalizedItems);

        progress?.Report(new CleanupDeletionProgress(0, normalizedItems.Count, string.Empty));

        var sanitizedOptions = (options ?? CleanupDeletionOptions.Default).Sanitize();
        return Task.Run(() => DeleteInternal(normalizedItems, progress, sanitizedOptions, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// Removes items whose paths are children of another selected directory.
    /// When a parent directory is selected for deletion (recursive), any child files or
    /// subdirectories within it are redundant and would cause double-counting of freed bytes.
    /// </summary>
    internal static List<CleanupPreviewItem> DeduplicateOverlappingPaths(List<CleanupPreviewItem> items)
    {
        if (items.Count <= 1)
        {
            return items;
        }

        // Collect all selected directory paths, normalized with trailing separator
        var directoryPaths = new List<string>();
        foreach (var item in items)
        {
            if (item.IsDirectory)
            {
                var path = item.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                           + Path.DirectorySeparatorChar;
                directoryPaths.Add(path);
            }
        }

        if (directoryPaths.Count == 0)
        {
            return items;
        }

        var result = new List<CleanupPreviewItem>(items.Count);
        foreach (var item in items)
        {
            var normalizedPath = item.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var isChildOfSelected = false;

            foreach (var dirPath in directoryPaths)
            {
                // Don't compare a directory against itself
                if (item.IsDirectory &&
                    normalizedPath.Equals(dirPath.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Check if this item lives inside a selected directory
                if ((normalizedPath + Path.DirectorySeparatorChar).StartsWith(dirPath, StringComparison.OrdinalIgnoreCase))
                {
                    isChildOfSelected = true;
                    break;
                }
            }

            if (!isChildOfSelected)
            {
                result.Add(item);
            }
        }

        return result;
    }

    private static CleanupDeletionResult DeleteInternal(
        IReadOnlyList<CleanupPreviewItem> items,
        IProgress<CleanupDeletionProgress>? progress,
        CleanupDeletionOptions options,
        CancellationToken cancellationToken)
    {
        var entries = new ConcurrentBag<CleanupDeletionEntry>();
        var total = items.Count;
        var completed = 0;
        var lastReportedIndex = 0;
        var progressLock = new object();

        void ReportProgress(int current, string path)
        {
            if (progress is null) return;
            // Throttle progress: report every ~2% or at least every 20 items
            var threshold = Math.Max(1, total / 50);
            if (current - Volatile.Read(ref lastReportedIndex) >= threshold || current == total)
            {
                lock (progressLock)
                {
                    if (current > lastReportedIndex)
                    {
                        lastReportedIndex = current;
                        progress.Report(new CleanupDeletionProgress(current, total, path));
                    }
                }
            }
        }

        var parallelism = Math.Clamp(Environment.ProcessorCount, 2, 8);
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = parallelism,
            CancellationToken = cancellationToken,
        };

        Parallel.ForEach(items, parallelOptions, item =>
        {
            var path = item.FullName;
            var normalizedPath = NormalizeFullPath(path);
            if (normalizedPath.Length == 0)
            {
                return;
            }

            var idx = Interlocked.Increment(ref completed);
            ReportProgress(idx, normalizedPath);

            if (!options.AllowProtectedSystemPaths && CleanupSystemPathSafety.IsSystemManagedPath(normalizedPath))
            {
                entries.Add(new CleanupDeletionEntry(
                    normalizedPath,
                    Math.Max(item.SizeBytes, 0),
                    item.IsDirectory,
                    CleanupDeletionDisposition.Skipped,
                    "System-managed location skipped (enable Allow protected system locations to continue)."));
                return;
            }

            var isDirectory = item.IsDirectory || Directory.Exists(normalizedPath);
            var fileExists = !isDirectory && File.Exists(normalizedPath);
            if (!fileExists && !isDirectory)
            {
                isDirectory = Directory.Exists(normalizedPath);
            }

            if (!fileExists && !isDirectory)
            {
                entries.Add(new CleanupDeletionEntry(normalizedPath, 0, item.IsDirectory, CleanupDeletionDisposition.Skipped, "Item not found"));
                return;
            }

            // For files, grab the actual current size (fast single stat call);
            // for directories, enumerate recursively to get accurate total
            var actualSizeBytes = GetActualSize(normalizedPath, isDirectory);

            var attributes = TryGetAttributes(normalizedPath);
            if (options.SkipHiddenItems && (item.IsHidden || attributes is not null && attributes.Value.HasFlag(FileAttributes.Hidden)))
            {
                entries.Add(new CleanupDeletionEntry(normalizedPath, actualSizeBytes, isDirectory, CleanupDeletionDisposition.Skipped, "Hidden item skipped"));
                return;
            }

            if (options.SkipSystemItems && (item.IsSystem || attributes is not null && attributes.Value.HasFlag(FileAttributes.System)))
            {
                entries.Add(new CleanupDeletionEntry(normalizedPath, actualSizeBytes, isDirectory, CleanupDeletionDisposition.Skipped, "System item skipped"));
                return;
            }

            if (options.SkipRecentItems)
            {
                var lastModified = item.LastModifiedUtc;
                if (lastModified == DateTime.MinValue)
                {
                    lastModified = TryGetLastWriteUtc(normalizedPath, isDirectory) ?? DateTime.MinValue;
                }

                if (lastModified != DateTime.MinValue)
                {
                    var age = DateTime.UtcNow - lastModified;
                    if (age < TimeSpan.Zero || age <= options.RecentItemThreshold)
                    {
                        entries.Add(new CleanupDeletionEntry(normalizedPath, actualSizeBytes, isDirectory, CleanupDeletionDisposition.Skipped, "Recently modified item skipped"));
                        return;
                    }
                }
            }

            // Attempt standard deletion
            if (TryDeletePath(normalizedPath, isDirectory, options, cancellationToken, out var failure, out var wasRecycled))
            {
                var verifiedSize = VerifyDeletionAndGetSize(normalizedPath, isDirectory, actualSizeBytes);
                entries.Add(new CleanupDeletionEntry(normalizedPath, verifiedSize, isDirectory, CleanupDeletionDisposition.Deleted) { WasRecycled = wasRecycled });
                return;
            }

            var repairedPermissions = false;
            if (OperatingSystem.IsWindows() && IsUnauthorizedAccessError(failure) && options.TakeOwnershipOnAccessDenied)
            {
                repairedPermissions = TryRepairPermissions(normalizedPath, isDirectory);
                if (repairedPermissions && TryDeletePath(normalizedPath, isDirectory, options, cancellationToken, out failure, out wasRecycled))
                {
                    var verifiedSize = VerifyDeletionAndGetSize(normalizedPath, isDirectory, actualSizeBytes);
                    entries.Add(new CleanupDeletionEntry(
                        normalizedPath,
                        verifiedSize,
                        isDirectory,
                        CleanupDeletionDisposition.Deleted,
                        "Deleted after preparing for force delete.")
                    { WasRecycled = wasRecycled });
                    return;
                }

                if (repairedPermissions && TryForceDeleteWithoutReboot(normalizedPath, isDirectory, cancellationToken, out failure))
                {
                    var verifiedSize = VerifyDeletionAndGetSize(normalizedPath, isDirectory, actualSizeBytes);
                    entries.Add(new CleanupDeletionEntry(
                        normalizedPath,
                        verifiedSize,
                        isDirectory,
                        CleanupDeletionDisposition.Deleted,
                        "Deleted using force cleanup."));
                    return;
                }
            }

            if (IsInUseError(failure))
            {
                if (options.TakeOwnershipOnAccessDenied && TryForceDeleteWithoutReboot(normalizedPath, isDirectory, cancellationToken, out failure))
                {
                    var verifiedSize = VerifyDeletionAndGetSize(normalizedPath, isDirectory, actualSizeBytes);
                    entries.Add(new CleanupDeletionEntry(
                        normalizedPath,
                        verifiedSize,
                        isDirectory,
                        CleanupDeletionDisposition.Deleted,
                        "Deleted after releasing locks."));
                    return;
                }

                if (!options.SkipLockedItems)
                {
                    if ((options.AllowDeleteOnReboot || options.TakeOwnershipOnAccessDenied) && TryScheduleDeleteOnReboot(normalizedPath))
                    {
                        entries.Add(new CleanupDeletionEntry(
                            normalizedPath,
                            actualSizeBytes,
                            isDirectory,
                            CleanupDeletionDisposition.PendingReboot,
                            BuildDeleteOnRebootMessage(options.TakeOwnershipOnAccessDenied)));
                        return;
                    }

                    entries.Add(new CleanupDeletionEntry(
                        normalizedPath,
                        actualSizeBytes,
                        isDirectory,
                        CleanupDeletionDisposition.Failed,
                        "Deletion blocked because another process is using the item.",
                        failure));
                    return;
                }

                entries.Add(new CleanupDeletionEntry(
                    normalizedPath,
                    actualSizeBytes,
                    isDirectory,
                    CleanupDeletionDisposition.Skipped,
                    "Skipped because another process is using the item.",
                    failure));
                return;
            }

            var reason = failure?.Message ?? "Deletion failed";
            if (repairedPermissions && IsUnauthorizedAccessError(failure))
            {
                reason = "Permission repair failed — delete still blocked.";
            }

            if ((options.AllowDeleteOnReboot || options.TakeOwnershipOnAccessDenied) && TryScheduleDeleteOnReboot(normalizedPath))
            {
                entries.Add(new CleanupDeletionEntry(
                    normalizedPath,
                    actualSizeBytes,
                    isDirectory,
                    CleanupDeletionDisposition.PendingReboot,
                    BuildDeleteOnRebootMessage(options.TakeOwnershipOnAccessDenied)));
                return;
            }

            entries.Add(new CleanupDeletionEntry(normalizedPath, actualSizeBytes, isDirectory, CleanupDeletionDisposition.Failed, reason, failure));
        });

        return new CleanupDeletionResult(entries.ToList());
    }

    /// <summary>
    /// Gets the actual current size of a file or directory.
    /// </summary>
    private static long GetActualSize(string path, bool isDirectory)
    {
        try
        {
            if (isDirectory)
            {
                return GetDirectorySize(path);
            }

            var fileInfo = new FileInfo(path);
            return fileInfo.Exists ? fileInfo.Length : 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Gets the total size of all files in a directory recursively.
    /// </summary>
    private static long GetDirectorySize(string path)
    {
        if (!Directory.Exists(path))
        {
            return 0;
        }

        long totalSize = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Offline
            }))
            {
                try
                {
                    var info = new FileInfo(file);
                    if (info.Exists)
                    {
                        totalSize += info.Length;
                    }
                }
                catch
                {
                    // Skip inaccessible files
                }
            }
        }
        catch
        {
            // Return whatever we accumulated
        }

        return totalSize;
    }

    /// <summary>
    /// Verifies that a file or directory was actually deleted and returns the size that was freed.
    /// Returns 0 if the item still exists (deletion didn't actually work).
    /// </summary>
    private static long VerifyDeletionAndGetSize(string path, bool isDirectory, long expectedSize)
    {
        try
        {
            if (isDirectory)
            {
                if (Directory.Exists(path))
                {
                    // Directory still exists - report zero since it wasn't fully removed
                    return 0;
                }
            }
            else
            {
                if (File.Exists(path))
                {
                    // File still exists - no space was freed
                    return 0;
                }
            }

            // Item no longer exists, return the full size
            return Math.Max(expectedSize, 0);
        }
        catch
        {
            // If we can't verify, assume the expected size was freed
            return Math.Max(expectedSize, 0);
        }
    }

    /// <summary>
    /// Attempts force delete using all available methods EXCEPT scheduling for reboot.
    /// This ensures we only use reboot scheduling as an absolute last resort.
    /// Uses the most aggressive deletion strategies available on Windows.
    /// </summary>
    private static bool TryForceDeleteWithoutReboot(string path, bool isDirectory, CancellationToken cancellationToken, out Exception? failure)
    {
        failure = null;
        if (string.IsNullOrWhiteSpace(path))
        {
            return true;
        }

        // On Windows, use the full aggressive deletion helper which includes:
        // 1. Clearing all restrictive attributes (readonly, hidden, system)
        // 2. Taking ownership and granting full control permissions
        // 3. Using Restart Manager to close processes holding file handles
        // 4. Renaming to tombstone to bypass filename locks
        // 5. Depth-first aggressive directory purge
        if (OperatingSystem.IsWindows())
        {
            return ForceDeleteHelper.TryAggressiveDelete(path, isDirectory, cancellationToken, out failure);
        }

        // Fallback for non-Windows: basic force delete
        TryClearAttributes(path);
        if (TryDeletePath(path, isDirectory, out failure))
        {
            return true;
        }

        // For directories, try aggressive recursive cleanup
        if (isDirectory && Directory.Exists(path))
        {
            TryAggressiveDirectoryCleanup(path, cancellationToken);
            if (TryDeletePath(path, isDirectory, out failure))
            {
                return true;
            }
        }

        // Try renaming to tombstone and deleting
        var tombstone = TryRenameToTombstone(path, isDirectory, cancellationToken);
        if (!string.IsNullOrWhiteSpace(tombstone))
        {
            if (TryDeletePath(tombstone!, isDirectory, out failure))
            {
                return true;
            }
            return !PathExists(path, isDirectory);
        }

        return false;
    }

    /// <summary>
    /// Checks if a path exists as a file or directory.
    /// </summary>
    private static bool PathExists(string path, bool isDirectory)
    {
        return isDirectory ? Directory.Exists(path) : File.Exists(path);
    }

    private static bool TryDeletePath(string path, bool isDirectory, CleanupDeletionOptions options, CancellationToken cancellationToken, out Exception? failure, out bool usedRecycleBin)
    {
        failure = null;
        usedRecycleBin = false;

        if (options.PreferRecycleBin)
        {
            if (TrySendToRecycleBin(path, isDirectory, out failure))
            {
                usedRecycleBin = true;
                return true;
            }

            if (!options.AllowPermanentDeleteFallback)
            {
                return false;
            }

            failure = null;
        }

        var maxAttempts = Math.Max(0, options.MaxRetryCount) + 1;

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (isDirectory)
                {
                    Directory.Delete(path, recursive: true);
                }
                else
                {
                    File.Delete(path);
                }

                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failure = ex;
            }
            catch (Exception ex)
            {
                failure = ex;
                break;
            }

            if (attempt < maxAttempts - 1 && options.RetryDelay > TimeSpan.Zero)
            {
                Delay(options.RetryDelay, cancellationToken);
            }
        }

        return false;
    }

    private static bool TrySendToRecycleBin(string path, bool isDirectory, out Exception? failure)
    {
        failure = null;

        if (CleanupNativeMethods.TrySendToRecycleBin(path, out failure))
        {
            return true;
        }

        return false;
    }

    private static bool IsInUseError(Exception? exception)
    {
        if (exception is null)
        {
            return false;
        }

        if (exception is IOException ioException)
        {
            const int ERROR_SHARING_VIOLATION = 32;
            const int ERROR_LOCK_VIOLATION = 33;

            var win32Code = ioException.HResult & 0xFFFF;
            if (win32Code == ERROR_SHARING_VIOLATION || win32Code == ERROR_LOCK_VIOLATION)
            {
                return true;
            }

            var message = ioException.Message;
            if (!string.IsNullOrWhiteSpace(message))
            {
                var normalized = message.ToLowerInvariant();
                if (normalized.Contains("being used by another process") || normalized.Contains("in use by another process"))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsUnauthorizedAccessError(Exception? exception) => exception is UnauthorizedAccessException;

    /// <summary>
    /// Legacy force delete method - now delegates to the non-reboot version.
    /// Kept for backwards compatibility but no longer schedules reboot internally.
    /// </summary>
    private static bool TryForceDelete(string path, bool isDirectory, CancellationToken cancellationToken, out Exception? failure)
    {
        // Delegate to the non-reboot version - reboot scheduling is now handled separately
        // by the caller with proper PendingReboot disposition tracking
        return TryForceDeleteWithoutReboot(path, isDirectory, cancellationToken, out failure);
    }

    private static bool TryDeletePath(string path, bool isDirectory, out Exception? failure)
    {
        failure = null;

        try
        {
            if (isDirectory)
            {
                if (!Directory.Exists(path))
                {
                    return true;
                }

                Directory.Delete(path, recursive: true);
            }
            else
            {
                if (!File.Exists(path))
                {
                    return true;
                }

                File.Delete(path);
            }

            return true;
        }
        catch (Exception ex)
        {
            failure = ex;
            return false;
        }
    }

    private static void TryAggressiveDirectoryCleanup(string root, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Pop();

            string[] directories;
            try
            {
                directories = Directory.GetDirectories(current);
            }
            catch
            {
                directories = Array.Empty<string>();
            }

            foreach (var directory in directories)
            {
                TryClearAttributes(directory);
                pending.Push(directory);
            }

            string[] files;
            try
            {
                files = Directory.GetFiles(current);
            }
            catch
            {
                files = Array.Empty<string>();
            }

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                TryClearAttributes(file);
                try
                {
                    File.Delete(file);
                }
                catch
                {
                    if (OperatingSystem.IsWindows())
                    {
                        TryScheduleDeleteOnReboot(file);
                    }
                }
            }
        }
    }

    private static string? TryRenameToTombstone(string path, bool isDirectory, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var trimmed = Path.TrimEndingDirectorySeparator(path);
            var parent = Path.GetDirectoryName(trimmed);
            if (string.IsNullOrWhiteSpace(parent))
            {
                return null;
            }

            var tombstone = Path.Combine(parent, $".tidywindow-deleting-{Guid.NewGuid():N}");
            TryClearAttributes(tombstone);

            if (isDirectory)
            {
                Directory.Move(path, tombstone);
            }
            else
            {
                File.Move(path, tombstone);
            }

            return tombstone;
        }
        catch
        {
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool TryRepairPermissions(string path, bool isDirectory)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            NormalizeAttributes(path, isDirectory);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryScheduleDeleteOnReboot(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            return NativeMethods.MoveFileEx(path, null, NativeMethods.MoveFileFlags.MOVEFILE_DELAY_UNTIL_REBOOT);
        }
        catch
        {
            return false;
        }
    }

    private static string BuildDeleteOnRebootMessage(bool forceDeleteRequested)
    {
        return forceDeleteRequested
            ? "Scheduled for removal after restart (force delete fallback)."
            : "Scheduled for removal after restart.";
    }

    private static void NormalizeAttributes(string path, bool isDirectory)
    {
        TryClearAttributes(path);
        if (!isDirectory)
        {
            return;
        }

        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories))
            {
                TryClearAttributes(entry);
            }
        }
        catch
        {
        }
    }

    private static void TryClearAttributes(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            var normalized = attributes & ~(FileAttributes.ReadOnly | FileAttributes.System);
            if (normalized == attributes)
            {
                return;
            }

            if (normalized == 0)
            {
                normalized = FileAttributes.Normal;
            }

            File.SetAttributes(path, normalized);
        }
        catch
        {
        }
    }

    private static string NormalizeFullPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(path);
            return Path.GetFullPath(expanded);
        }
        catch
        {
            return path.Trim();
        }
    }

    private static FileAttributes? TryGetAttributes(string path)
    {
        try
        {
            return File.GetAttributes(path);
        }
        catch
        {
            return null;
        }
    }

    private static DateTime? TryGetLastWriteUtc(string path, bool isDirectory)
    {
        try
        {
            return isDirectory ? Directory.GetLastWriteTimeUtc(path) : File.GetLastWriteTimeUtc(path);
        }
        catch
        {
            return null;
        }
    }

    private static void Delay(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            Task.Delay(delay, cancellationToken).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private static class NativeMethods
    {
        [Flags]
        public enum MoveFileFlags : uint
        {
            MOVEFILE_REPLACE_EXISTING = 0x1,
            MOVEFILE_COPY_ALLOWED = 0x2,
            MOVEFILE_DELAY_UNTIL_REBOOT = 0x4,
            MOVEFILE_WRITE_THROUGH = 0x8,
            MOVEFILE_CREATE_HARDLINK = 0x10,
            MOVEFILE_FAIL_IF_NOT_TRACKABLE = 0x20
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool MoveFileEx(string lpExistingFileName, string? lpNewFileName, MoveFileFlags dwFlags);
    }
}

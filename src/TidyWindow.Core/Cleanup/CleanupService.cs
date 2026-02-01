using System;
using System.Collections.Generic;
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

        progress?.Report(new CleanupDeletionProgress(0, normalizedItems.Count, string.Empty));

        var sanitizedOptions = (options ?? CleanupDeletionOptions.Default).Sanitize();
        return Task.Run(() => DeleteInternal(normalizedItems, progress, sanitizedOptions, cancellationToken), cancellationToken);
    }

    private static CleanupDeletionResult DeleteInternal(
        IReadOnlyList<CleanupPreviewItem> items,
        IProgress<CleanupDeletionProgress>? progress,
        CleanupDeletionOptions options,
        CancellationToken cancellationToken)
    {
        var entries = new List<CleanupDeletionEntry>(items.Count);
        var index = 0;
        var total = items.Count;

        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var path = item.FullName;
            var normalizedPath = NormalizeFullPath(path);
            if (normalizedPath.Length == 0)
            {
                continue;
            }

            index++;
            progress?.Report(new CleanupDeletionProgress(index, total, normalizedPath));

            if (!options.AllowProtectedSystemPaths && CleanupSystemPathSafety.IsSystemCriticalPath(normalizedPath))
            {
                entries.Add(new CleanupDeletionEntry(
                    normalizedPath,
                    Math.Max(item.SizeBytes, 0),
                    item.IsDirectory,
                    CleanupDeletionDisposition.Skipped,
                    "Protected system location skipped"));
                continue;
            }

            var isDirectory = item.IsDirectory || Directory.Exists(normalizedPath);
            var fileExists = !isDirectory && File.Exists(normalizedPath);
            if (!fileExists && !isDirectory)
            {
                isDirectory = Directory.Exists(normalizedPath);
            }

            if (!fileExists && !isDirectory)
            {
                // Item no longer exists - report as skipped with zero size (it's already gone, no space to reclaim)
                entries.Add(new CleanupDeletionEntry(normalizedPath, 0, item.IsDirectory, CleanupDeletionDisposition.Skipped, "Item not found"));
                continue;
            }

            // Get the actual current size of the item (not the stale preview size)
            // This ensures accurate reporting of freed space
            var actualSizeBytes = GetActualSize(normalizedPath, isDirectory);

            var attributes = TryGetAttributes(normalizedPath);
            if (options.SkipHiddenItems && (item.IsHidden || attributes is not null && attributes.Value.HasFlag(FileAttributes.Hidden)))
            {
                entries.Add(new CleanupDeletionEntry(normalizedPath, actualSizeBytes, isDirectory, CleanupDeletionDisposition.Skipped, "Hidden item skipped"));
                continue;
            }

            if (options.SkipSystemItems && (item.IsSystem || attributes is not null && attributes.Value.HasFlag(FileAttributes.System)))
            {
                entries.Add(new CleanupDeletionEntry(normalizedPath, actualSizeBytes, isDirectory, CleanupDeletionDisposition.Skipped, "System item skipped"));
                continue;
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
                        continue;
                    }
                }
            }

            // Attempt standard deletion
            if (TryDeletePath(normalizedPath, isDirectory, options, cancellationToken, out var failure))
            {
                // CRITICAL: Verify the item was actually deleted before counting the size
                var verifiedSize = VerifyDeletionAndGetSize(normalizedPath, isDirectory, actualSizeBytes);
                entries.Add(new CleanupDeletionEntry(normalizedPath, verifiedSize, isDirectory, CleanupDeletionDisposition.Deleted));
                continue;
            }

            var repairedPermissions = false;
            if (OperatingSystem.IsWindows() && IsUnauthorizedAccessError(failure) && options.TakeOwnershipOnAccessDenied)
            {
                repairedPermissions = TryRepairPermissions(normalizedPath, isDirectory);
                if (repairedPermissions && TryDeletePath(normalizedPath, isDirectory, options, cancellationToken, out failure))
                {
                    var verifiedSize = VerifyDeletionAndGetSize(normalizedPath, isDirectory, actualSizeBytes);
                    entries.Add(new CleanupDeletionEntry(
                        normalizedPath,
                        verifiedSize,
                        isDirectory,
                        CleanupDeletionDisposition.Deleted,
                        "Deleted after preparing for force delete."));
                    continue;
                }

                // Try force delete with aggressive cleanup (no reboot fallback yet)
                if (repairedPermissions && TryForceDeleteWithoutReboot(normalizedPath, isDirectory, cancellationToken, out failure))
                {
                    var verifiedSize = VerifyDeletionAndGetSize(normalizedPath, isDirectory, actualSizeBytes);
                    entries.Add(new CleanupDeletionEntry(
                        normalizedPath,
                        verifiedSize,
                        isDirectory,
                        CleanupDeletionDisposition.Deleted,
                        "Deleted using force cleanup."));
                    continue;
                }
            }

            if (IsInUseError(failure))
            {
                // Try force delete without reboot fallback first
                if (options.TakeOwnershipOnAccessDenied && TryForceDeleteWithoutReboot(normalizedPath, isDirectory, cancellationToken, out failure))
                {
                    var verifiedSize = VerifyDeletionAndGetSize(normalizedPath, isDirectory, actualSizeBytes);
                    entries.Add(new CleanupDeletionEntry(
                        normalizedPath,
                        verifiedSize,
                        isDirectory,
                        CleanupDeletionDisposition.Deleted,
                        "Deleted after releasing locks."));
                    continue;
                }

                if (!options.SkipLockedItems)
                {
                    // Only schedule for reboot as last resort - mark as PendingReboot, NOT Deleted
                    if ((options.AllowDeleteOnReboot || options.TakeOwnershipOnAccessDenied) && TryScheduleDeleteOnReboot(normalizedPath))
                    {
                        entries.Add(new CleanupDeletionEntry(
                            normalizedPath,
                            actualSizeBytes,
                            isDirectory,
                            CleanupDeletionDisposition.PendingReboot,
                            BuildDeleteOnRebootMessage(options.TakeOwnershipOnAccessDenied)));
                        continue;
                    }

                    entries.Add(new CleanupDeletionEntry(
                        normalizedPath,
                        actualSizeBytes,
                        isDirectory,
                        CleanupDeletionDisposition.Failed,
                        "Deletion blocked because another process is using the item.",
                        failure));
                    continue;
                }

                entries.Add(new CleanupDeletionEntry(
                    normalizedPath,
                    actualSizeBytes,
                    isDirectory,
                    CleanupDeletionDisposition.Skipped,
                    "Skipped because another process is using the item.",
                    failure));
                continue;
            }

            var reason = failure?.Message ?? "Deletion failed";
            if (repairedPermissions && IsUnauthorizedAccessError(failure))
            {
                reason = "Permission repair failed — delete still blocked.";
            }

            // Last resort: schedule for reboot - mark as PendingReboot, NOT Deleted
            if ((options.AllowDeleteOnReboot || options.TakeOwnershipOnAccessDenied) && TryScheduleDeleteOnReboot(normalizedPath))
            {
                entries.Add(new CleanupDeletionEntry(
                    normalizedPath,
                    actualSizeBytes,
                    isDirectory,
                    CleanupDeletionDisposition.PendingReboot,
                    BuildDeleteOnRebootMessage(options.TakeOwnershipOnAccessDenied)));
                continue;
            }

            entries.Add(new CleanupDeletionEntry(normalizedPath, actualSizeBytes, isDirectory, CleanupDeletionDisposition.Failed, reason, failure));
        }

        return new CleanupDeletionResult(entries);
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
            // Give the filesystem a moment to sync
            Thread.Sleep(5);

            if (isDirectory)
            {
                if (Directory.Exists(path))
                {
                    // Directory still exists - check if it's empty or partial deletion
                    var remainingSize = GetDirectorySize(path);
                    // Return only the portion that was actually deleted
                    return Math.Max(0, expectedSize - remainingSize);
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

    private static bool TryDeletePath(string path, bool isDirectory, CleanupDeletionOptions options, CancellationToken cancellationToken, out Exception? failure)
    {
        failure = null;

        if (options.PreferRecycleBin)
        {
            if (TrySendToRecycleBin(path, isDirectory, out failure))
            {
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

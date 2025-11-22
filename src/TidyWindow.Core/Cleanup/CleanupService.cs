using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
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
            var normalizedPath = path.Trim();
            if (normalizedPath.Length == 0)
            {
                continue;
            }

            index++;
            progress?.Report(new CleanupDeletionProgress(index, total, normalizedPath));

            var isDirectory = item.IsDirectory || Directory.Exists(normalizedPath);
            var fileExists = !isDirectory && File.Exists(normalizedPath);
            if (!fileExists && !isDirectory)
            {
                isDirectory = Directory.Exists(normalizedPath);
            }

            if (!fileExists && !isDirectory)
            {
                entries.Add(new CleanupDeletionEntry(normalizedPath, Math.Max(item.SizeBytes, 0), item.IsDirectory, CleanupDeletionDisposition.Skipped, "Item not found"));
                continue;
            }

            var attributes = TryGetAttributes(normalizedPath);
            if (options.SkipHiddenItems && (item.IsHidden || attributes is not null && attributes.Value.HasFlag(FileAttributes.Hidden)))
            {
                entries.Add(new CleanupDeletionEntry(normalizedPath, Math.Max(item.SizeBytes, 0), isDirectory, CleanupDeletionDisposition.Skipped, "Hidden item skipped"));
                continue;
            }

            if (options.SkipSystemItems && (item.IsSystem || attributes is not null && attributes.Value.HasFlag(FileAttributes.System)))
            {
                entries.Add(new CleanupDeletionEntry(normalizedPath, Math.Max(item.SizeBytes, 0), isDirectory, CleanupDeletionDisposition.Skipped, "System item skipped"));
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
                        entries.Add(new CleanupDeletionEntry(normalizedPath, Math.Max(item.SizeBytes, 0), isDirectory, CleanupDeletionDisposition.Skipped, "Recently modified item skipped"));
                        continue;
                    }
                }
            }

            if (TryDeletePath(normalizedPath, isDirectory, options, cancellationToken, out var failure))
            {
                entries.Add(new CleanupDeletionEntry(normalizedPath, Math.Max(item.SizeBytes, 0), isDirectory, CleanupDeletionDisposition.Deleted));
                continue;
            }

            var repairedPermissions = false;
            if (IsUnauthorizedAccessError(failure) && options.TakeOwnershipOnAccessDenied)
            {
                repairedPermissions = TryRepairPermissions(normalizedPath, isDirectory);
                if (repairedPermissions && TryDeletePath(normalizedPath, isDirectory, options, cancellationToken, out failure))
                {
                    entries.Add(new CleanupDeletionEntry(
                        normalizedPath,
                        Math.Max(item.SizeBytes, 0),
                        isDirectory,
                        CleanupDeletionDisposition.Deleted,
                        "Deleted after repairing permissions."));
                    continue;
                }
            }

            if (IsInUseError(failure))
            {
                if (!options.SkipLockedItems)
                {
                    if (options.AllowDeleteOnReboot && TryScheduleDeleteOnReboot(normalizedPath))
                    {
                        entries.Add(new CleanupDeletionEntry(
                            normalizedPath,
                            Math.Max(item.SizeBytes, 0),
                            isDirectory,
                            CleanupDeletionDisposition.Deleted,
                            "Scheduled for removal after restart."));
                        continue;
                    }

                    entries.Add(new CleanupDeletionEntry(
                        normalizedPath,
                        Math.Max(item.SizeBytes, 0),
                        isDirectory,
                        CleanupDeletionDisposition.Failed,
                        "Deletion blocked because another process is using the item.",
                        failure));
                    continue;
                }

                entries.Add(new CleanupDeletionEntry(
                    normalizedPath,
                    Math.Max(item.SizeBytes, 0),
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

            if (options.AllowDeleteOnReboot && TryScheduleDeleteOnReboot(normalizedPath))
            {
                entries.Add(new CleanupDeletionEntry(
                    normalizedPath,
                    Math.Max(item.SizeBytes, 0),
                    isDirectory,
                    CleanupDeletionDisposition.Deleted,
                    "Scheduled for removal after restart."));
                continue;
            }

            entries.Add(new CleanupDeletionEntry(normalizedPath, Math.Max(item.SizeBytes, 0), isDirectory, CleanupDeletionDisposition.Failed, reason, failure));
        }

        return new CleanupDeletionResult(entries);
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

    private static bool TryRepairPermissions(string path, bool isDirectory)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var user = identity?.User;
            if (user is null)
            {
                return false;
            }

            if (isDirectory)
            {
                var directoryInfo = new DirectoryInfo(path);
                var security = directoryInfo.GetAccessControl(AccessControlSections.All);
                security.SetOwner(user);
                var rule = new FileSystemAccessRule(
                    user,
                    FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None,
                    AccessControlType.Allow);
                security.AddAccessRule(rule);
                directoryInfo.SetAccessControl(security);
            }
            else
            {
                var fileInfo = new FileInfo(path);
                var security = fileInfo.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
                security.SetOwner(user);
                var rule = new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow);
                security.AddAccessRule(rule);
                fileInfo.SetAccessControl(security);
            }

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

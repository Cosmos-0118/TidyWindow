using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualBasic.FileIO;

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

    public Task<CleanupReport> PreviewAsync(bool includeDownloads, int previewCount, CleanupItemKind itemKind = CleanupItemKind.Files, CancellationToken cancellationToken = default)
    {
        return _scanner.ScanAsync(includeDownloads, previewCount, itemKind, cancellationToken);
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

            if (IsInUseError(failure))
            {
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

        try
        {
            if (isDirectory)
            {
                FileSystem.DeleteDirectory(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            }
            else
            {
                FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException)
        {
            failure = ex;
            return false;
        }
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
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

    public Task<CleanupReport> PreviewAsync(bool includeDownloads, int previewCount, CleanupItemKind itemKind = CleanupItemKind.Files, CancellationToken cancellationToken = default)
    {
        return _scanner.ScanAsync(includeDownloads, previewCount, itemKind, cancellationToken);
    }

    public Task<CleanupDeletionResult> DeleteAsync(
        IEnumerable<CleanupPreviewItem> items,
        IProgress<CleanupDeletionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (items is null)
        {
            throw new ArgumentNullException(nameof(items));
        }

        var targets = items
            .Where(static item => !string.IsNullOrWhiteSpace(item.FullName))
            .Select(static item => item.FullName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (targets.Count == 0)
        {
            return Task.FromResult(new CleanupDeletionResult(0, 0, Array.Empty<string>()));
        }

        progress?.Report(new CleanupDeletionProgress(0, targets.Count, string.Empty));

        return Task.Run(() => DeleteInternal(targets, progress, cancellationToken), cancellationToken);
    }

    private static CleanupDeletionResult DeleteInternal(
        IReadOnlyCollection<string> paths,
        IProgress<CleanupDeletionProgress>? progress,
        CancellationToken cancellationToken)
    {
        var deleted = 0;
        var skipped = 0;
        var errors = new List<string>();
        var index = 0;

        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            index++;
            progress?.Report(new CleanupDeletionProgress(index, paths.Count, path));

            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                    deleted++;
                }
                else if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                    deleted++;
                }
                else
                {
                    skipped++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add($"{path}: {ex.Message}");
            }
            catch (Exception ex)
            {
                errors.Add($"{path}: {ex.Message}");
            }
        }

        return new CleanupDeletionResult(deleted, skipped, errors);
    }
}

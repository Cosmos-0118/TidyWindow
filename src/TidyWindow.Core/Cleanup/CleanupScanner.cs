using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Enumeration;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace TidyWindow.Core.Cleanup;

internal sealed class CleanupScanner
{
    private readonly CleanupDefinitionProvider _definitionProvider;

    public CleanupScanner(CleanupDefinitionProvider definitionProvider)
    {
        _definitionProvider = definitionProvider ?? throw new ArgumentNullException(nameof(definitionProvider));
    }

    public Task<CleanupReport> ScanAsync(bool includeDownloads, int previewCount, CleanupItemKind itemKind, CancellationToken cancellationToken)
    {
        var definitions = _definitionProvider.GetDefinitions(includeDownloads);
        if (definitions.Count == 0)
        {
            return Task.FromResult(CleanupReport.Empty);
        }

        previewCount = Math.Max(0, previewCount);

        var results = new ConcurrentBag<CleanupTargetReport>();
        var parallelOptions = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = Math.Max(Environment.ProcessorCount - 1, 1)
        };

        Parallel.ForEach(definitions, parallelOptions, definition =>
        {
            var report = BuildReport(definition, previewCount, itemKind, cancellationToken);
            if (report is not null)
            {
                results.Add(report);
            }
        });

        if (results.IsEmpty)
        {
            return Task.FromResult(CleanupReport.Empty);
        }

        var ordered = results
            .OrderBy(static report => report.Classification, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(static report => report.TotalSizeBytes)
            .ToList();

        return Task.FromResult(new CleanupReport(ordered));
    }

    private static CleanupTargetReport BuildReport(CleanupTargetDefinition definition, int previewCount, CleanupItemKind itemKind, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var resolvedPath = ResolvePath(definition.RawPath);
        if (string.IsNullOrWhiteSpace(resolvedPath))
        {
            return new CleanupTargetReport(definition.Category, definition.RawPath ?? string.Empty, false, 0, 0, Array.Empty<CleanupPreviewItem>(), definition.Notes, true, definition.Classification);
        }

        if (!Directory.Exists(resolvedPath))
        {
            return new CleanupTargetReport(definition.Category, resolvedPath, false, 0, 0, Array.Empty<CleanupPreviewItem>(), "No directory located.", true, definition.Classification);
        }

        var fileEnumerationOptions = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Offline,
            ReturnSpecialDirectories = false
        };

        var immediateEnumerationOptions = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Offline,
            ReturnSpecialDirectories = false
        };

        var directoryStats = InitializeDirectoryStats(resolvedPath, immediateEnumerationOptions);
        var filesCount = 0;
        long totalSize = 0;

        var topFiles = new TopN<CleanupPreviewItem>(previewCount);

        try
        {
            foreach (var file in EnumerateFiles(resolvedPath, fileEnumerationOptions, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();

                filesCount++;
                totalSize += file.SizeBytes;

                if (previewCount > 0 && itemKind != CleanupItemKind.Folders)
                {
                    var previewItem = new CleanupPreviewItem(file.Name, file.FullPath, file.SizeBytes, file.LastModifiedUtc, isDirectory: false, file.Extension);
                    topFiles.TryAdd(previewItem, file.SizeBytes);
                }

                if (itemKind != CleanupItemKind.Files && directoryStats.Count > 0)
                {
                    var accumulator = FindImmediateDirectory(file.DirectoryPath, resolvedPath, directoryStats);
                    accumulator?.Add(file.SizeBytes, file.LastModifiedUtc);
                }
            }
        }
        catch (IOException ex)
        {
            return BuildErrorReport(definition, resolvedPath, ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            return BuildErrorReport(definition, resolvedPath, ex.Message);
        }

        var directoriesCount = directoryStats.Count;
        var topDirectories = new TopN<CleanupPreviewItem>(previewCount);

        if (previewCount > 0 && itemKind != CleanupItemKind.Files)
        {
            foreach (var stat in directoryStats.Values)
            {
                if (stat.SizeBytes <= 0)
                {
                    continue;
                }

                var directoryItem = new CleanupPreviewItem(stat.Name, stat.FullPath, stat.SizeBytes, stat.LastModifiedUtc, isDirectory: true, extension: string.Empty);
                topDirectories.TryAdd(directoryItem, stat.SizeBytes);
            }
        }

        var combinedPreview = CombinePreviews(topFiles, topDirectories, previewCount);

        var itemCount = itemKind switch
        {
            CleanupItemKind.Folders => directoriesCount,
            CleanupItemKind.Both => filesCount + directoriesCount,
            _ => filesCount
        };

        return new CleanupTargetReport(
            definition.Category,
            resolvedPath,
            exists: true,
            itemCount,
            totalSize,
            combinedPreview,
            definition.Notes,
            dryRun: true,
            definition.Classification);
    }

    private static IReadOnlyList<CleanupPreviewItem> CombinePreviews(TopN<CleanupPreviewItem> files, TopN<CleanupPreviewItem> directories, int previewCount)
    {
        if (previewCount <= 0)
        {
            return Array.Empty<CleanupPreviewItem>();
        }

        var items = new List<CleanupPreviewItem>(previewCount * 2);
        items.AddRange(files.ToDescendingList());
        items.AddRange(directories.ToDescendingList());

        if (items.Count == 0)
        {
            return Array.Empty<CleanupPreviewItem>();
        }

        return items
            .OrderByDescending(static item => item.SizeBytes)
            .Take(previewCount)
            .ToList();
    }

    private static Dictionary<string, DirectoryAccumulator> InitializeDirectoryStats(string rootPath, EnumerationOptions options)
    {
        var stats = new Dictionary<string, DirectoryAccumulator>(StringComparer.OrdinalIgnoreCase);

        foreach (var directory in SafeEnumerateDirectories(rootPath, options))
        {
            var name = Path.GetFileName(directory);
            if (string.IsNullOrWhiteSpace(name))
            {
                name = directory;
            }

            stats[directory] = new DirectoryAccumulator(directory, name, Directory.GetLastWriteTimeUtc(directory));
        }

        return stats;
    }

    private static IEnumerable<EnumeratedFile> EnumerateFiles(string rootPath, EnumerationOptions options, CancellationToken cancellationToken)
    {
        var enumerable = new FileSystemEnumerable<EnumeratedFile>(
            rootPath,
            static (ref FileSystemEntry entry) =>
            {
                var fullPath = entry.ToFullPath();
                var name = entry.FileName.ToString();
                var directoryPath = Path.GetDirectoryName(fullPath);
                var extension = Path.GetExtension(fullPath);

                return new EnumeratedFile
                {
                    FullPath = fullPath,
                    Name = name,
                    DirectoryPath = directoryPath,
                    SizeBytes = entry.Length,
                    LastModifiedUtc = entry.LastWriteTimeUtc.UtcDateTime,
                    Extension = extension
                };
            },
            options)
        {
            ShouldIncludePredicate = static (ref FileSystemEntry entry) => !entry.IsDirectory
        };

        foreach (var file in enumerable)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return file;
        }
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string path, EnumerationOptions options)
    {
        try
        {
            return Directory.EnumerateDirectories(path, "*", options);
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static DirectoryAccumulator? FindImmediateDirectory(string? directoryPath, string rootPath, IDictionary<string, DirectoryAccumulator> stats)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            return null;
        }

        var current = directoryPath;
        while (!string.IsNullOrWhiteSpace(current) && !string.Equals(current, rootPath, StringComparison.OrdinalIgnoreCase))
        {
            if (stats.TryGetValue(current, out var accumulator))
            {
                return accumulator;
            }

            current = Path.GetDirectoryName(current);
        }

        return null;
    }

    private static CleanupTargetReport BuildErrorReport(CleanupTargetDefinition definition, string resolvedPath, string message)
    {
        var preview = Array.Empty<CleanupPreviewItem>();
        var notes = string.IsNullOrWhiteSpace(message)
            ? definition.Notes
            : $"Enumeration failed: {message}";

        return new CleanupTargetReport(
            definition.Category,
            resolvedPath,
            exists: true,
            itemCount: 0,
            totalSizeBytes: 0,
            preview,
            notes,
            dryRun: true,
            definition.Classification);
    }

    private static string? ResolvePath(string? rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return null;
        }

        var expanded = Environment.ExpandEnvironmentVariables(rawPath);
        try
        {
            return Path.GetFullPath(expanded);
        }
        catch
        {
            return null;
        }
    }

    private sealed class TopN<T>
    {
        private readonly int _capacity;
        private readonly PriorityQueue<(T Item, long Weight), long> _queue;

        public TopN(int capacity)
        {
            _capacity = capacity;
            _queue = new PriorityQueue<(T, long), long>();
        }

        public void TryAdd(T item, long weight)
        {
            if (_capacity <= 0)
            {
                return;
            }

            if (_queue.Count < _capacity)
            {
                _queue.Enqueue((item, weight), weight);
                return;
            }

            if (_queue.TryPeek(out _, out var smallest) && weight > smallest)
            {
                _queue.Dequeue();
                _queue.Enqueue((item, weight), weight);
            }
        }

        public IReadOnlyList<T> ToDescendingList()
        {
            if (_queue.Count == 0)
            {
                return Array.Empty<T>();
            }

            return _queue.UnorderedItems
                .OrderByDescending(static tuple => tuple.Element.Weight)
                .Select(static tuple => tuple.Element.Item)
                .ToList();
        }
    }

    private sealed class DirectoryAccumulator
    {
        public DirectoryAccumulator(string fullPath, string name, DateTime lastModifiedUtc)
        {
            FullPath = fullPath;
            Name = name;
            LastModifiedUtc = lastModifiedUtc;
        }

        public string FullPath { get; }

        public string Name { get; }

        public long SizeBytes { get; private set; }

        public DateTime LastModifiedUtc { get; private set; }

        public void Add(long sizeBytes, DateTime lastModifiedUtc)
        {
            SizeBytes += sizeBytes;
            if (lastModifiedUtc > LastModifiedUtc)
            {
                LastModifiedUtc = lastModifiedUtc;
            }
        }
    }

    private sealed class EnumeratedFile
    {
        public string FullPath { get; set; } = string.Empty;

        public string? Name { get; set; }

        public string? DirectoryPath { get; set; }

        public long SizeBytes { get; set; }

        public DateTime LastModifiedUtc { get; set; }

        public string? Extension { get; set; }
    }
}

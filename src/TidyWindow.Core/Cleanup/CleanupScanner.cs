using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.IO.Enumeration;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace TidyWindow.Core.Cleanup;

internal sealed class CleanupScanner
{
    private readonly CleanupDefinitionProvider _definitionProvider;
    private static readonly TimeSpan RecentActivityThreshold = TimeSpan.FromHours(12);

    public CleanupScanner(CleanupDefinitionProvider definitionProvider)
    {
        _definitionProvider = definitionProvider ?? throw new ArgumentNullException(nameof(definitionProvider));
    }

    public Task<CleanupReport> ScanAsync(bool includeDownloads, bool includeBrowserHistory, int previewCount, CleanupItemKind itemKind, CancellationToken cancellationToken)
    {
        return ScanAsync(includeDownloads, includeBrowserHistory, previewCount, itemKind, progress: null, cancellationToken);
    }

    public Task<CleanupReport> ScanAsync(bool includeDownloads, bool includeBrowserHistory, int previewCount, CleanupItemKind itemKind, IProgress<CleanupScanProgress>? progress, CancellationToken cancellationToken)
    {
        return Task.Run(() => ScanInternal(includeDownloads, includeBrowserHistory, previewCount, itemKind, progress, cancellationToken), cancellationToken);
    }

    private CleanupReport ScanInternal(bool includeDownloads, bool includeBrowserHistory, int previewCount, CleanupItemKind itemKind, IProgress<CleanupScanProgress>? progress, CancellationToken cancellationToken)
    {
        var definitions = _definitionProvider.GetDefinitions(includeDownloads, includeBrowserHistory);
        if (definitions.Count == 0)
        {
            return CleanupReport.Empty;
        }

        previewCount = Math.Max(0, previewCount);

        var results = new ConcurrentBag<CleanupTargetReport>();
        var completedCount = 0;
        var totalBytes = 0L;
        var totalFiles = 0;
        var totalTargets = definitions.Count;

        // Report initial progress
        progress?.Report(new CleanupScanProgress(0, totalTargets, "Initializing scan…", 0, 0));

        var parallelOptions = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            // Limit parallelism more conservatively for better UI responsiveness on weaker machines
            MaxDegreeOfParallelism = Math.Max(Math.Min(Environment.ProcessorCount / 2, 4), 1)
        };

        Parallel.ForEach(definitions, parallelOptions, definition =>
        {
            var report = BuildReport(definition, previewCount, itemKind, cancellationToken);
            if (report is not null)
            {
                results.Add(report);

                // Update progress atomically
                var completed = Interlocked.Increment(ref completedCount);
                Interlocked.Add(ref totalBytes, report.TotalSizeBytes);
                Interlocked.Add(ref totalFiles, report.ItemCount);

                progress?.Report(new CleanupScanProgress(
                    completed,
                    totalTargets,
                    definition.Category,
                    Interlocked.Read(ref totalBytes),
                    totalFiles));
            }
        });

        if (results.IsEmpty)
        {
            return CleanupReport.Empty;
        }

        var ordered = results
            .OrderBy(static report => report.Classification, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(static report => report.TotalSizeBytes)
            .ToList();

        return new CleanupReport(ordered);
    }

    private static CleanupTargetReport BuildReport(CleanupTargetDefinition definition, int previewCount, CleanupItemKind itemKind, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var warnings = new List<string>();
        var resolvedPath = ResolvePath(definition.RawPath);
        if (string.IsNullOrWhiteSpace(resolvedPath))
        {
            warnings.Add("Unable to resolve cleanup path.");
            return new CleanupTargetReport(definition.Category, definition.RawPath ?? string.Empty, false, 0, 0, Array.Empty<CleanupPreviewItem>(), definition.Notes, true, definition.Classification, warnings);
        }

        if (definition.TargetType == CleanupTargetType.File)
        {
            return BuildFileReport(definition, resolvedPath, itemKind, warnings);
        }

        if (!Directory.Exists(resolvedPath))
        {
            warnings.Add("Directory not found when scanning for cleanup items.");
            return new CleanupTargetReport(definition.Category, resolvedPath, false, 0, 0, Array.Empty<CleanupPreviewItem>(), "No directory located.", true, definition.Classification, warnings);
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

        var nowUtc = DateTime.UtcNow;
        List<CleanupFileContext>? fileCandidates = previewCount > 0 && itemKind != CleanupItemKind.Folders
            ? new List<CleanupFileContext>()
            : null;

        try
        {
            foreach (var file in EnumerateFiles(resolvedPath, fileEnumerationOptions, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();

                filesCount++;
                totalSize += file.SizeBytes;

                var wasRecentlyModified = IsRecentlyModified(file.LastModifiedUtc, nowUtc);

                if (fileCandidates is not null)
                {
                    var context = new CleanupFileContext(
                        file.Name,
                        file.FullPath,
                        file.Extension,
                        file.SizeBytes,
                        file.LastModifiedUtc,
                        file.IsHidden,
                        file.IsSystem,
                        wasRecentlyModified,
                        file.LastAccessUtc,
                        file.CreationUtc);

                    if (CleanupIntelligence.ShouldCheckActiveLock(definition, context))
                    {
                        context = context.WithLockState(HasActiveLock(file.FullPath));
                    }

                    fileCandidates.Add(context);
                }

                if (itemKind != CleanupItemKind.Files && directoryStats.Count > 0)
                {
                    var accumulator = FindImmediateDirectory(file.DirectoryPath, resolvedPath, directoryStats);
                    accumulator?.Add(file.SizeBytes, file.LastModifiedUtc, file.Extension, file.IsHidden, file.IsSystem, wasRecentlyModified);
                }
            }
        }
        catch (IOException ex)
        {
            warnings.Add(ex.Message);
            return BuildErrorReport(definition, resolvedPath, ex.Message, warnings);
        }
        catch (UnauthorizedAccessException ex)
        {
            warnings.Add(ex.Message);
            return BuildErrorReport(definition, resolvedPath, ex.Message, warnings);
        }

        var lockedSkipCount = 0;
        if (fileCandidates is not null && fileCandidates.Count > 0)
        {
            IReadOnlySet<string>? protectedCrashPaths = null;
            if (ShouldApplyCrashRetention(definition))
            {
                var candidateSet = CleanupCrashRetentionPolicy.GetPathsToProtect(fileCandidates);
                if (candidateSet.Count > 0)
                {
                    protectedCrashPaths = candidateSet;
                }
            }

            foreach (var context in fileCandidates)
            {
                if (context.IsLocked)
                {
                    lockedSkipCount++;
                    continue;
                }

                if (protectedCrashPaths is not null && !string.IsNullOrWhiteSpace(context.FullPath) && protectedCrashPaths.Contains(context.FullPath))
                {
                    continue;
                }

                var evaluation = CleanupIntelligence.EvaluateFile(definition, context, nowUtc);
                if (!evaluation.ShouldInclude)
                {
                    continue;
                }

                var previewItem = new CleanupPreviewItem(
                    context.Name,
                    context.FullPath,
                    context.SizeBytes,
                    context.LastModifiedUtc,
                    isDirectory: false,
                    context.Extension,
                    context.IsHidden,
                    context.IsSystem,
                    context.WasRecentlyModified,
                    evaluation.Confidence,
                    evaluation.Signals,
                    context.LastAccessUtc,
                    context.CreationUtc);
                topFiles.TryAdd(previewItem, evaluation.Weight);
            }
        }

        var directoriesCount = directoryStats.Count;
        var topDirectories = new TopN<CleanupPreviewItem>(previewCount);

        if (previewCount > 0 && itemKind != CleanupItemKind.Files)
        {
            foreach (var stat in directoryStats.Values)
            {
                var snapshot = stat.ToSnapshot();
                var evaluation = CleanupIntelligence.EvaluateDirectory(definition, snapshot, nowUtc);
                if (!evaluation.ShouldInclude)
                {
                    continue;
                }

                var directoryItem = new CleanupPreviewItem(
                    stat.Name,
                    stat.FullPath,
                    stat.SizeBytes,
                    stat.LastModifiedUtc,
                    isDirectory: true,
                    extension: string.Empty,
                    stat.IsHidden,
                    stat.IsSystem,
                    IsRecentlyModified(stat.LastModifiedUtc, nowUtc),
                    evaluation.Confidence,
                    evaluation.Signals);
                topDirectories.TryAdd(directoryItem, evaluation.Weight);
            }
        }

        var combinedPreview = CombinePreviews(topFiles, topDirectories, previewCount);

        if (lockedSkipCount > 0)
        {
            warnings.Add($"Skipped {lockedSkipCount:N0} files because they are still in use.");
        }

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
            definition.Classification,
            warnings);
    }

    private static CleanupTargetReport BuildFileReport(CleanupTargetDefinition definition, string resolvedPath, CleanupItemKind itemKind, List<string> warnings)
    {
        if (!File.Exists(resolvedPath))
        {
            warnings.Add("File not found when scanning for cleanup items.");
            return new CleanupTargetReport(definition.Category, resolvedPath, false, 0, 0, Array.Empty<CleanupPreviewItem>(), definition.Notes, true, definition.Classification, warnings);
        }

        if (itemKind == CleanupItemKind.Folders)
        {
            return new CleanupTargetReport(definition.Category, resolvedPath, true, 0, 0, Array.Empty<CleanupPreviewItem>(), definition.Notes, true, definition.Classification, warnings);
        }

        var info = new FileInfo(resolvedPath);
        var lastModifiedUtc = info.LastWriteTimeUtc;
        var nowUtc = DateTime.UtcNow;
        var previewItem = new CleanupPreviewItem(
            info.Name,
            info.FullName,
            info.Length,
            lastModifiedUtc,
            isDirectory: false,
            Path.GetExtension(info.FullName),
            info.Attributes.HasFlag(FileAttributes.Hidden),
            info.Attributes.HasFlag(FileAttributes.System),
            IsRecentlyModified(lastModifiedUtc, nowUtc),
            confidence: 0.65,
            signals: Array.Empty<string>(),
            info.LastAccessTimeUtc,
            info.CreationTimeUtc);

        return new CleanupTargetReport(
            definition.Category,
            resolvedPath,
            exists: true,
            itemCount: 1,
            totalSizeBytes: info.Length,
            preview: new[] { previewItem },
            notes: definition.Notes,
            dryRun: true,
            classification: definition.Classification,
            warnings: warnings);
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
            .OrderByDescending(static item => item.SizeBytes + item.Confidence * 25_000_000d)
            .ThenByDescending(static item => item.Confidence)
            .ThenByDescending(static item => item.SizeBytes)
            .Take(previewCount)
            .ToList();
    }

    private static bool ShouldApplyCrashRetention(CleanupTargetDefinition definition)
    {
        var classification = definition.Classification ?? string.Empty;
        var normalized = classification.Trim().ToLowerInvariant();
        return normalized is "orphaned" or "logs";
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

            var attributes = GetFileAttributes(directory);
            var lastWrite = Directory.GetLastWriteTimeUtc(directory);
            stats[directory] = new DirectoryAccumulator(
                directory,
                name,
                lastWrite,
                attributes.HasValue && attributes.Value.HasFlag(FileAttributes.Hidden),
                attributes.HasValue && attributes.Value.HasFlag(FileAttributes.System));
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
                    LastAccessUtc = entry.LastAccessTimeUtc.UtcDateTime,
                    CreationUtc = entry.CreationTimeUtc.UtcDateTime,
                    Extension = extension,
                    IsHidden = entry.Attributes.HasFlag(FileAttributes.Hidden),
                    IsSystem = entry.Attributes.HasFlag(FileAttributes.System)
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

    private static bool HasActiveLock(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
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

    private static CleanupTargetReport BuildErrorReport(CleanupTargetDefinition definition, string resolvedPath, string message, IReadOnlyList<string>? warnings = null)
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
            definition.Classification,
            warnings);
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

    private static bool IsRecentlyModified(DateTime timestampUtc, DateTime referenceUtc)
    {
        if (timestampUtc == DateTime.MinValue)
        {
            return false;
        }

        var delta = referenceUtc - timestampUtc;
        if (delta < TimeSpan.Zero)
        {
            return true;
        }

        return delta <= RecentActivityThreshold;
    }

    private static FileAttributes? GetFileAttributes(string path)
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

    private sealed class DirectoryAccumulator
    {
        private Dictionary<string, int>? _extensionCounts;

        public DirectoryAccumulator(string fullPath, string name, DateTime lastModifiedUtc, bool isHidden, bool isSystem)
        {
            FullPath = fullPath;
            Name = name;
            LastModifiedUtc = lastModifiedUtc;
            IsHidden = isHidden;
            IsSystem = isSystem;
        }

        public string FullPath { get; }

        public string Name { get; }

        public long SizeBytes { get; private set; }

        public DateTime LastModifiedUtc { get; private set; }

        public bool IsHidden { get; }

        public bool IsSystem { get; }

        public int FileCount { get; private set; }

        public int HiddenFileCount { get; private set; }

        public int SystemFileCount { get; private set; }

        public int RecentFileCount { get; private set; }

        public int TempFileCount { get; private set; }

        public void Add(long sizeBytes, DateTime lastModifiedUtc, string? extension, bool isHidden, bool isSystem, bool wasRecentlyModified)
        {
            SizeBytes += sizeBytes;
            if (lastModifiedUtc > LastModifiedUtc)
            {
                LastModifiedUtc = lastModifiedUtc;
            }

            FileCount++;
            if (isHidden)
            {
                HiddenFileCount++;
            }

            if (isSystem)
            {
                SystemFileCount++;
            }

            if (wasRecentlyModified)
            {
                RecentFileCount++;
            }

            if (!string.IsNullOrWhiteSpace(extension))
            {
                var normalized = extension.Trim();
                if (!normalized.StartsWith(".", StringComparison.Ordinal))
                {
                    normalized = "." + normalized;
                }

                normalized = normalized.ToLowerInvariant();

                _extensionCounts ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                if (_extensionCounts.TryGetValue(normalized, out var count))
                {
                    _extensionCounts[normalized] = count + 1;
                }
                else
                {
                    _extensionCounts[normalized] = 1;
                }

                if (CleanupIntelligence.IsTempExtension(normalized))
                {
                    TempFileCount++;
                }
            }
        }

        public CleanupDirectorySnapshot ToSnapshot()
        {
            IReadOnlyDictionary<string, int>? histogram = null;
            if (_extensionCounts is not null && _extensionCounts.Count > 0)
            {
                histogram = new ReadOnlyDictionary<string, int>(_extensionCounts);
            }

            return new CleanupDirectorySnapshot(
                FullPath,
                Name,
                SizeBytes,
                LastModifiedUtc,
                IsHidden,
                IsSystem,
                FileCount,
                HiddenFileCount,
                SystemFileCount,
                RecentFileCount,
                TempFileCount,
                histogram);
        }
    }

    private sealed class EnumeratedFile
    {
        public string FullPath { get; set; } = string.Empty;

        public string? Name { get; set; }

        public string? DirectoryPath { get; set; }

        public long SizeBytes { get; set; }

        public DateTime LastModifiedUtc { get; set; }

        public DateTime LastAccessUtc { get; set; }

        public DateTime CreationUtc { get; set; }

        public string? Extension { get; set; }

        public bool IsHidden { get; set; }

        public bool IsSystem { get; set; }
    }
}

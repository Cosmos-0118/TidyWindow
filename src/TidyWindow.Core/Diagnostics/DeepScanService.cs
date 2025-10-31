using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.IO.Enumeration;
using System.Linq;
using System.Security;
using System.Threading;
using System.Threading.Tasks;

namespace TidyWindow.Core.Diagnostics;

public enum DeepScanNameMatchMode
{
    Contains,
    StartsWith,
    EndsWith,
    Exact
}

internal static class DeepScanAggregation
{
    public static long CalculateUniqueSize(IEnumerable<DeepScanFinding> findings)
    {
        if (findings is null)
        {
            return 0;
        }

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var directories = new List<string>();
        var total = 0L;

        foreach (var finding in findings.OrderByDescending(static item => item.SizeBytes))
        {
            var size = Math.Max(0L, finding.SizeBytes);
            if (size == 0)
            {
                continue;
            }

            if (finding.IsDirectory)
            {
                var normalizedDirectory = NormalizeDirectoryPath(finding.Path);
                if (IsUnderExistingParent(normalizedDirectory, directories, comparison))
                {
                    continue;
                }

                directories.Add(normalizedDirectory);
                total += size;
                continue;
            }

            var filePath = NormalizeFilePath(finding.Path);
            if (IsUnderExistingParent(filePath, directories, comparison))
            {
                continue;
            }

            total += size;
        }

        return total;
    }

    public static IReadOnlyDictionary<string, long> CalculateCategoryTotals(IEnumerable<DeepScanFinding> findings)
    {
        var totals = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        if (findings is null)
        {
            return new ReadOnlyDictionary<string, long>(totals);
        }

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var directories = new List<string>();

        foreach (var finding in findings.OrderByDescending(static item => item.SizeBytes))
        {
            var size = Math.Max(0L, finding.SizeBytes);
            if (size == 0)
            {
                continue;
            }

            var category = string.IsNullOrWhiteSpace(finding.Category) ? "Other" : finding.Category;

            if (finding.IsDirectory)
            {
                var normalizedDirectory = NormalizeDirectoryPath(finding.Path);
                if (IsUnderExistingParent(normalizedDirectory, directories, comparison))
                {
                    continue;
                }

                directories.Add(normalizedDirectory);
                totals[category] = totals.TryGetValue(category, out var current) ? current + size : size;
                continue;
            }

            var filePath = NormalizeFilePath(finding.Path);
            if (IsUnderExistingParent(filePath, directories, comparison))
            {
                continue;
            }

            totals[category] = totals.TryGetValue(category, out var existing) ? existing + size : size;
        }

        return new ReadOnlyDictionary<string, long>(totals);
    }

    private static bool IsUnderExistingParent(string candidatePath, IReadOnlyList<string> directories, StringComparison comparison)
    {
        if (candidatePath.Length == 0)
        {
            return false;
        }

        for (var i = 0; i < directories.Count; i++)
        {
            if (candidatePath.StartsWith(directories[i], comparison))
            {
                return true;
            }
        }

        return false;
    }

    private static string NormalizeDirectoryPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            var normalized = Path.GetFullPath(path);
            if (!normalized.EndsWith(Path.DirectorySeparatorChar) && !normalized.EndsWith(Path.AltDirectorySeparatorChar))
            {
                normalized += Path.DirectorySeparatorChar;
            }

            return normalized;
        }
        catch (Exception)
        {
            return path;
        }
    }

    private static string NormalizeFilePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception)
        {
            return path;
        }
    }
}

/// <summary>
/// Executes deep scan automation to surface the largest files and folders under a target path.
/// </summary>
public sealed class DeepScanService
{
    public Task<DeepScanResult> RunScanAsync(DeepScanRequest request, CancellationToken cancellationToken = default)
    {
        return RunScanAsync(request, progress: null, cancellationToken);
    }

    public async Task<DeepScanResult> RunScanAsync(DeepScanRequest request, IProgress<DeepScanProgressUpdate>? progress, CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        var resolvedRoot = ResolveRootPath(request.RootPath);
        return await Task.Run(
            () => ExecuteScan(resolvedRoot, request, progress, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    private static DeepScanResult ExecuteScan(string resolvedRoot, DeepScanRequest request, IProgress<DeepScanProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        var context = new ScanContext(
            request.MaxItems,
            Math.Max(0L, (long)request.MinimumSizeInMegabytes) * 1024L * 1024L,
            request.IncludeHiddenFiles,
            request.NameFilters,
            request.NameMatchMode,
            request.IsCaseSensitiveNameMatch,
            request.IncludeDirectories,
            progress);

        if (context.Limit <= 0)
        {
            return DeepScanResult.FromFindings(resolvedRoot, Array.Empty<DeepScanFinding>());
        }

        if (File.Exists(resolvedRoot))
        {
            var queue = new PriorityQueue<DeepScanFinding, long>(1);
            var rootFile = CreateFileEntry(resolvedRoot);
            if (rootFile is not null && TryProcessFileEntry(rootFile.Value, context, out var rootFinding, out var rootSize))
            {
                context.RecordProcessed(rootSize, rootFile.Value.FullPath, queue);
                if (rootFinding is not null)
                {
                    AddCandidate(queue, rootFinding, context);
                }
            }

            context.Emit(queue, rootFile?.FullPath, isFinal: true, latestFinding: null, force: true);
            var single = DrainQueue(queue);
            return BuildResult(resolvedRoot, single);
        }

        var results = new PriorityQueue<DeepScanFinding, long>(context.Limit);
        var totalSize = ProcessDirectory(resolvedRoot, results, context, cancellationToken);

        if (context.IncludeDirectories && totalSize >= context.MinSizeBytes)
        {
            if (CreateDirectoryEntry(resolvedRoot) is { } rootDirectory
                && !ShouldSkipDirectory(rootDirectory, context.IncludeHidden)
                && MatchesName(rootDirectory.Name, context))
            {
                var rootFinding = new DeepScanFinding(
                    path: rootDirectory.FullPath,
                    name: rootDirectory.Name,
                    directory: rootDirectory.Directory,
                    sizeBytes: totalSize,
                    modifiedUtc: rootDirectory.LastWriteUtc,
                    extension: string.Empty,
                    isDirectory: true);
                AddCandidate(results, rootFinding, context);
            }
        }

        context.Emit(results, resolvedRoot, isFinal: true, latestFinding: null, force: true);
        var findings = DrainQueue(results);
        return BuildResult(resolvedRoot, findings);
    }

    private static long ProcessDirectory(string directoryPath, PriorityQueue<DeepScanFinding, long> queue, ScanContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        long directorySize = 0;
        List<FileEntry>? subdirectories = null;

        foreach (var entry in EnumerateEntries(directoryPath, context))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!entry.IsDirectory)
            {
                if (!TryProcessFileEntry(entry, context, out var fileFinding, out var fileSize))
                {
                    continue;
                }

                directorySize += fileSize;
                context.RecordProcessed(fileSize, entry.FullPath, queue);

                if (fileFinding is not null)
                {
                    AddCandidate(queue, fileFinding, context);
                }

                continue;
            }

            if (ShouldSkipDirectory(entry, context.IncludeHidden))
            {
                continue;
            }

            subdirectories ??= new List<FileEntry>();
            subdirectories.Add(entry);
        }

        if (subdirectories is null || subdirectories.Count == 0)
        {
            return directorySize;
        }

        if (subdirectories.Count == 1 || context.MaxDegreeOfParallelism <= 1)
        {
            foreach (var entry in subdirectories)
            {
                var childSize = ProcessDirectory(entry.FullPath, queue, context, cancellationToken);
                directorySize += childSize;

                if (!context.IncludeDirectories || childSize < context.MinSizeBytes)
                {
                    continue;
                }

                if (!MatchesName(entry.Name, context))
                {
                    continue;
                }

                var directoryFinding = new DeepScanFinding(
                    path: entry.FullPath,
                    name: entry.Name,
                    directory: entry.Directory,
                    sizeBytes: childSize,
                    modifiedUtc: entry.LastWriteUtc,
                    extension: string.Empty,
                    isDirectory: true);

                AddCandidate(queue, directoryFinding, context);
            }

            return directorySize;
        }

        var childSizes = new long[subdirectories.Count];

        Parallel.For(0, subdirectories.Count, new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = context.MaxDegreeOfParallelism
        }, index =>
        {
            var entry = subdirectories[index];
            var childSize = ProcessDirectory(entry.FullPath, queue, context, cancellationToken);
            childSizes[index] = childSize;
        });

        for (var i = 0; i < subdirectories.Count; i++)
        {
            var entry = subdirectories[i];
            var childSize = childSizes[i];
            directorySize += childSize;

            if (!context.IncludeDirectories || childSize < context.MinSizeBytes)
            {
                continue;
            }

            if (!MatchesName(entry.Name, context))
            {
                continue;
            }

            var directoryFinding = new DeepScanFinding(
                path: entry.FullPath,
                name: entry.Name,
                directory: entry.Directory,
                sizeBytes: childSize,
                modifiedUtc: entry.LastWriteUtc,
                extension: string.Empty,
                isDirectory: true);

            AddCandidate(queue, directoryFinding, context);
        }

        return directorySize;
    }

    private static void AddCandidate(PriorityQueue<DeepScanFinding, long> queue, DeepScanFinding candidate, ScanContext context)
    {
        context.TryEnqueueCandidate(queue, candidate);
    }

    private static List<DeepScanFinding> DrainQueue(PriorityQueue<DeepScanFinding, long> queue)
    {
        var results = new List<DeepScanFinding>(queue.Count);
        while (queue.TryDequeue(out var item, out _))
        {
            results.Add(item);
        }

        results.Sort(static (left, right) => right.SizeBytes.CompareTo(left.SizeBytes));
        return results;
    }

    private static bool MatchesName(string name, ScanContext context)
    {
        if (!context.HasNameFilters)
        {
            return true;
        }

        foreach (var filter in context.NameFilters)
        {
            if (filter.Length == 0)
            {
                continue;
            }

            var isMatch = context.NameMatchMode switch
            {
                DeepScanNameMatchMode.Contains => name.IndexOf(filter, context.NameComparison) >= 0,
                DeepScanNameMatchMode.StartsWith => name.StartsWith(filter, context.NameComparison),
                DeepScanNameMatchMode.EndsWith => name.EndsWith(filter, context.NameComparison),
                DeepScanNameMatchMode.Exact => string.Equals(name, filter, context.NameComparison),
                _ => false
            };

            if (isMatch)
            {
                return true;
            }
        }

        return false;
    }

    private static string ResolveRootPath(string? rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        try
        {
            var fullPath = Path.GetFullPath(rootPath);
            if (Directory.Exists(fullPath) || File.Exists(fullPath))
            {
                return fullPath;
            }

            throw new DirectoryNotFoundException($"The specified root path '{rootPath}' does not exist.");
        }
        catch (Exception ex)
        {
            throw new ArgumentException("The specified root path could not be resolved: " + ex.Message, nameof(rootPath));
        }
    }

    private static IEnumerable<FileEntry> EnumerateEntries(string directoryPath, ScanContext context)
    {
        FileSystemEnumerable<FileEntry>? enumerable;
        try
        {
            enumerable = new FileSystemEnumerable<FileEntry>(
                directoryPath,
                static (ref FileSystemEntry entry) =>
                {
                    var fullPath = entry.ToFullPath();
                    var name = entry.FileName.ToString();
                    var directory = Path.GetDirectoryName(fullPath) ?? string.Empty;
                    var extension = entry.IsDirectory ? string.Empty : Path.GetExtension(name);
                    return new FileEntry(
                        fullPath: fullPath,
                        name: name,
                        directory: directory,
                        sizeBytes: entry.IsDirectory ? 0 : entry.Length,
                        lastWriteUtc: entry.LastWriteTimeUtc,
                        attributes: entry.Attributes,
                        isDirectory: entry.IsDirectory,
                        extension: extension);
                },
                context.DirectoryEnumerationOptions)
            {
                ShouldIncludePredicate = static (ref FileSystemEntry entry) => true,
                ShouldRecursePredicate = static (ref FileSystemEntry entry) => false
            };
        }
        catch (Exception ex) when (IsNonCriticalFileSystemException(ex))
        {
            yield break;
        }

        using var enumerator = enumerable.GetEnumerator();
        while (true)
        {
            FileEntry current;
            try
            {
                if (!enumerator.MoveNext())
                {
                    yield break;
                }

                current = enumerator.Current;
            }
            catch (Exception ex) when (IsNonCriticalFileSystemException(ex))
            {
                yield break;
            }

            yield return current;
        }
    }

    private static bool TryProcessFileEntry(FileEntry entry, ScanContext context, out DeepScanFinding? finding, out long fileSize)
    {
        finding = null;
        fileSize = 0;

        if (ShouldSkipFile(entry.Attributes, entry.Name, context.IncludeHidden))
        {
            return false;
        }

        fileSize = entry.SizeBytes;

        if (entry.SizeBytes < context.MinSizeBytes)
        {
            return true;
        }

        if (!MatchesName(entry.Name, context))
        {
            return true;
        }

        finding = new DeepScanFinding(
            path: entry.FullPath,
            name: entry.Name,
            directory: entry.Directory,
            sizeBytes: entry.SizeBytes,
            modifiedUtc: entry.LastWriteUtc,
            extension: entry.Extension,
            isDirectory: false);

        return true;
    }

    private static bool ShouldSkipDirectory(FileEntry entry, bool includeHidden)
    {
        if (IsReparsePoint(entry.Attributes))
        {
            return true;
        }

        if (!includeHidden && IsHidden(entry.Name, entry.Attributes))
        {
            return true;
        }

        return false;
    }

    private static bool ShouldSkipFile(FileAttributes attributes, string name, bool includeHidden)
    {
        if (!includeHidden && IsHidden(name, attributes))
        {
            return true;
        }

        if (!includeHidden && (attributes & FileAttributes.System) == FileAttributes.System)
        {
            return true;
        }

        return false;
    }

    private static bool IsHidden(string name, FileAttributes attributes)
    {
        if ((attributes & FileAttributes.Hidden) == FileAttributes.Hidden)
        {
            return true;
        }

        if (!OperatingSystem.IsWindows() && name.Length > 0 && name[0] == '.')
        {
            return true;
        }

        return false;
    }

    private static bool IsReparsePoint(FileAttributes attributes)
        => (attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint;

    private static bool IsNonCriticalFileSystemException(Exception ex)
        => ex is UnauthorizedAccessException
            or PathTooLongException
            or DirectoryNotFoundException
            or FileNotFoundException
            or IOException
            or SecurityException;

    private static DeepScanResult BuildResult(string rootPath, List<DeepScanFinding> findings)
    {
        var readOnly = new ReadOnlyCollection<DeepScanFinding>(findings);
        var totalSize = DeepScanAggregation.CalculateUniqueSize(findings);
        return new DeepScanResult(readOnly, rootPath, DateTimeOffset.UtcNow, findings.Count, totalSize);
    }

    private static FileEntry? CreateFileEntry(string filePath)
    {
        try
        {
            var fileInfo = new FileInfo(filePath);
            if (!fileInfo.Exists)
            {
                return null;
            }

            return new FileEntry(
                fullPath: fileInfo.FullName,
                name: fileInfo.Name,
                directory: fileInfo.DirectoryName ?? string.Empty,
                sizeBytes: fileInfo.Length,
                lastWriteUtc: ToUtcOffset(fileInfo.LastWriteTimeUtc),
                attributes: fileInfo.Attributes,
                isDirectory: false,
                extension: fileInfo.Extension);
        }
        catch (Exception ex) when (IsNonCriticalFileSystemException(ex))
        {
            return null;
        }
    }

    private static FileEntry? CreateDirectoryEntry(string directoryPath)
    {
        try
        {
            var directoryInfo = new DirectoryInfo(directoryPath);
            if (!directoryInfo.Exists)
            {
                return null;
            }

            return new FileEntry(
                fullPath: directoryInfo.FullName,
                name: directoryInfo.Name,
                directory: directoryInfo.Parent?.FullName ?? string.Empty,
                sizeBytes: 0,
                lastWriteUtc: ToUtcOffset(directoryInfo.LastWriteTimeUtc),
                attributes: directoryInfo.Attributes,
                isDirectory: true,
                extension: string.Empty);
        }
        catch (Exception ex) when (IsNonCriticalFileSystemException(ex))
        {
            return null;
        }
    }

    private static DateTimeOffset ToUtcOffset(DateTime value)
    {
        var specified = DateTime.SpecifyKind(value, DateTimeKind.Utc);
        return new DateTimeOffset(specified);
    }

    private readonly struct FileEntry
    {
        public FileEntry(string fullPath, string name, string directory, long sizeBytes, DateTimeOffset lastWriteUtc, FileAttributes attributes, bool isDirectory, string extension)
        {
            FullPath = fullPath;
            Name = name;
            Directory = directory;
            SizeBytes = sizeBytes;
            LastWriteUtc = lastWriteUtc;
            Attributes = attributes;
            IsDirectory = isDirectory;
            Extension = extension ?? string.Empty;
        }

        public string FullPath { get; }

        public string Name { get; }

        public string Directory { get; }

        public long SizeBytes { get; }

        public DateTimeOffset LastWriteUtc { get; }

        public FileAttributes Attributes { get; }

        public bool IsDirectory { get; }

        public string Extension { get; }
    }

    private sealed class ScanContext
    {
        private const int ReportItemThreshold = 1500;
        private const int LargeQueueSnapshotThreshold = 200_000;
        private const int ProgressPreviewLimit = 500;
        private static readonly TimeSpan ReportInterval = TimeSpan.FromMilliseconds(600);
        private static readonly TimeSpan CandidateReportInterval = TimeSpan.FromMilliseconds(220);
        private static readonly IReadOnlyDictionary<string, long> EmptyCategoryTotals = new ReadOnlyDictionary<string, long>(new Dictionary<string, long>());

        private readonly object _syncRoot = new();
        private int _itemsSinceLastReport;
        private DateTime _lastReportTimestamp = DateTime.UtcNow - ReportInterval;
        private bool _hasPendingCandidate;

        public ScanContext(
            int limit,
            long minSizeBytes,
            bool includeHidden,
            IReadOnlyList<string> nameFilters,
            DeepScanNameMatchMode nameMatchMode,
            bool isCaseSensitive,
            bool includeDirectories,
            IProgress<DeepScanProgressUpdate>? progress)
        {
            Limit = limit;
            MinSizeBytes = minSizeBytes;
            IncludeHidden = includeHidden;
            NameFilters = nameFilters ?? Array.Empty<string>();
            NameMatchMode = nameMatchMode;
            NameComparison = isCaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            IncludeDirectories = includeDirectories;
            Progress = progress;

            var processorCount = Environment.ProcessorCount > 0 ? Environment.ProcessorCount : 1;
            var suggestedDegree = processorCount <= 2 ? processorCount : Math.Min(processorCount, 8);
            MaxDegreeOfParallelism = Math.Max(1, suggestedDegree);

            var attributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System;
            if (!includeHidden)
            {
                attributesToSkip |= FileAttributes.Hidden;
            }

            DirectoryEnumerationOptions = new EnumerationOptions
            {
                AttributesToSkip = attributesToSkip,
                IgnoreInaccessible = true,
                RecurseSubdirectories = false,
                ReturnSpecialDirectories = false,
                BufferSize = 131072
            };
            HasNameFilters = NameFilters.Count > 0;
        }

        public int Limit { get; }

        public int MaxDegreeOfParallelism { get; }

        public long MinSizeBytes { get; }

        public bool IncludeHidden { get; }

        public IReadOnlyList<string> NameFilters { get; }

        public DeepScanNameMatchMode NameMatchMode { get; }

        public StringComparison NameComparison { get; }

        public bool IncludeDirectories { get; }

        public EnumerationOptions DirectoryEnumerationOptions { get; }

        public bool HasNameFilters { get; }

        public long ProcessedEntries { get; private set; }

        public long ProcessedSizeBytes { get; private set; }

        public IProgress<DeepScanProgressUpdate>? Progress { get; }

        public void RecordProcessed(long sizeBytes, string currentPath, PriorityQueue<DeepScanFinding, long> queue)
        {
            lock (_syncRoot)
            {
                ProcessedEntries++;
                if (sizeBytes > 0)
                {
                    ProcessedSizeBytes += sizeBytes;
                }

                if (Progress is null)
                {
                    return;
                }

                _itemsSinceLastReport++;
                TryEmitLocked(queue, currentPath, isFinal: false, latestFinding: null, force: false);
            }
        }

        public void Emit(PriorityQueue<DeepScanFinding, long> queue, string? currentPath, bool isFinal, DeepScanFinding? latestFinding, bool force)
        {
            lock (_syncRoot)
            {
                TryEmitLocked(queue, currentPath, isFinal, latestFinding, force);
            }
        }

        public bool TryEnqueueCandidate(PriorityQueue<DeepScanFinding, long> queue, DeepScanFinding candidate)
        {
            if (Limit <= 0)
            {
                return false;
            }

            lock (_syncRoot)
            {
                if (queue.Count >= Limit && queue.TryPeek(out _, out var smallestPriority) && candidate.SizeBytes <= smallestPriority)
                {
                    return false;
                }

                queue.Enqueue(candidate, candidate.SizeBytes);

                if (queue.Count > Limit)
                {
                    queue.Dequeue();
                }

                if (Progress is not null)
                {
                    _hasPendingCandidate = true;
                    TryEmitLocked(queue, candidate.Path, isFinal: false, latestFinding: candidate, force: false);
                }

                return true;
            }
        }

        private void TryEmitLocked(PriorityQueue<DeepScanFinding, long> queue, string? currentPath, bool isFinal, DeepScanFinding? latestFinding, bool force)
        {
            if (Progress is null)
            {
                return;
            }

            var now = DateTime.UtcNow;
            if (!force)
            {
                var elapsed = now - _lastReportTimestamp;
                if (_hasPendingCandidate)
                {
                    if (elapsed < CandidateReportInterval)
                    {
                        return;
                    }
                }
                else if (_itemsSinceLastReport < ReportItemThreshold && elapsed < ReportInterval)
                {
                    return;
                }
            }

            if (!force && !isFinal && queue.Count > LargeQueueSnapshotThreshold)
            {
                _itemsSinceLastReport = 0;
                _hasPendingCandidate = false;
                _lastReportTimestamp = now;
                Progress.Report(new DeepScanProgressUpdate(
                    Array.Empty<DeepScanFinding>(),
                    ProcessedEntries,
                    ProcessedSizeBytes,
                    currentPath,
                    latestFinding,
                    EmptyCategoryTotals,
                    false));
                return;
            }

            EmitSnapshotLocked(queue, currentPath, isFinal, latestFinding, now);
        }

        private void EmitSnapshotLocked(PriorityQueue<DeepScanFinding, long> queue, string? currentPath, bool isFinal, DeepScanFinding? latestFinding, DateTime timestampUtc)
        {
            var snapshot = BuildSnapshot(queue, isFinal);
            IReadOnlyDictionary<string, long> categories;
            if (snapshot.Count == 0)
            {
                categories = EmptyCategoryTotals;
            }
            else
            {
                var dict = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                for (var index = 0; index < snapshot.Count; index++)
                {
                    var finding = snapshot[index];
                    var category = string.IsNullOrWhiteSpace(finding.Category) ? "Other" : finding.Category;
                    var size = Math.Max(0L, finding.SizeBytes);
                    if (size == 0)
                    {
                        continue;
                    }

                    dict[category] = dict.TryGetValue(category, out var current)
                        ? current + size
                        : size;
                }

                categories = dict.Count == 0
                    ? EmptyCategoryTotals
                    : new ReadOnlyDictionary<string, long>(dict);
            }

            IReadOnlyList<DeepScanFinding> readOnlySnapshot = snapshot.Count == 0
                ? Array.Empty<DeepScanFinding>()
                : snapshot;

            var update = new DeepScanProgressUpdate(readOnlySnapshot, ProcessedEntries, ProcessedSizeBytes, currentPath, latestFinding, categories, isFinal);
            Progress!.Report(update);

            _itemsSinceLastReport = 0;
            _hasPendingCandidate = false;
            _lastReportTimestamp = timestampUtc;
        }

        private static List<DeepScanFinding> BuildSnapshot(PriorityQueue<DeepScanFinding, long> queue, bool isFinal)
        {
            if (queue.Count == 0)
            {
                return new List<DeepScanFinding>(0);
            }

            var target = isFinal || queue.Count <= ProgressPreviewLimit ? queue.Count : ProgressPreviewLimit;

            if (target >= queue.Count)
            {
                var listAll = new List<DeepScanFinding>(queue.Count);
                foreach (var item in queue.UnorderedItems)
                {
                    listAll.Add(item.Element);
                }

                listAll.Sort(static (left, right) => right.SizeBytes.CompareTo(left.SizeBytes));
                return listAll;
            }

            var previewHeap = new PriorityQueue<DeepScanFinding, long>(target);
            foreach (var item in queue.UnorderedItems)
            {
                var element = item.Element;
                if (previewHeap.Count < target)
                {
                    previewHeap.Enqueue(element, element.SizeBytes);
                    continue;
                }

                if (previewHeap.TryPeek(out _, out var smallest) && element.SizeBytes > smallest)
                {
                    previewHeap.Dequeue();
                    previewHeap.Enqueue(element, element.SizeBytes);
                }
            }

            var list = new List<DeepScanFinding>(previewHeap.Count);
            while (previewHeap.TryDequeue(out var element, out _))
            {
                list.Add(element);
            }

            list.Sort(static (left, right) => right.SizeBytes.CompareTo(left.SizeBytes));
            return list;
        }
    }
}

public sealed class DeepScanRequest
{
    private static readonly char[] _filterSeparators = { ';', ',', '|' };

    public DeepScanRequest(
        string? rootPath,
        int maxItems,
        int minimumSizeInMegabytes,
        bool includeHiddenFiles,
        IEnumerable<string>? nameFilters = null,
        DeepScanNameMatchMode nameMatchMode = DeepScanNameMatchMode.Contains,
        bool isCaseSensitiveNameMatch = false,
        bool includeDirectories = false)
    {
        if (maxItems <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxItems), "Max items must be greater than zero.");
        }

        if (minimumSizeInMegabytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumSizeInMegabytes), "Minimum size cannot be negative.");
        }

        if (!Enum.IsDefined(typeof(DeepScanNameMatchMode), nameMatchMode))
        {
            throw new ArgumentOutOfRangeException(nameof(nameMatchMode));
        }

        RootPath = rootPath ?? string.Empty;
        MaxItems = maxItems;
        MinimumSizeInMegabytes = minimumSizeInMegabytes;
        IncludeHiddenFiles = includeHiddenFiles;
        NameMatchMode = nameMatchMode;
        IsCaseSensitiveNameMatch = isCaseSensitiveNameMatch;
        IncludeDirectories = includeDirectories;
        NameFilters = NormalizeNameFilters(nameFilters, isCaseSensitiveNameMatch);
    }

    public string RootPath { get; }

    public int MaxItems { get; }

    public int MinimumSizeInMegabytes { get; }

    public bool IncludeHiddenFiles { get; }

    public IReadOnlyList<string> NameFilters { get; }

    public DeepScanNameMatchMode NameMatchMode { get; }

    public bool IsCaseSensitiveNameMatch { get; }

    public bool IncludeDirectories { get; }

    private static IReadOnlyList<string> NormalizeNameFilters(IEnumerable<string>? filters, bool isCaseSensitive)
    {
        if (filters is null)
        {
            return Array.Empty<string>();
        }

        var comparer = isCaseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        var unique = new HashSet<string>(comparer);
        var ordered = new List<string>();

        foreach (var candidate in filters)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            var tokens = candidate.Split(_filterSeparators, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0)
            {
                continue;
            }

            foreach (var token in tokens)
            {
                if (unique.Add(token))
                {
                    ordered.Add(token);
                }
            }
        }

        return ordered.Count == 0
            ? Array.Empty<string>()
            : new ReadOnlyCollection<string>(ordered);
    }
}

public sealed class DeepScanProgressUpdate
{
    public DeepScanProgressUpdate(
        IReadOnlyList<DeepScanFinding> findings,
        long processedEntries,
        long processedSizeBytes,
        string? currentPath,
        DeepScanFinding? latestFinding,
        IReadOnlyDictionary<string, long> categoryTotals,
        bool isFinal)
    {
        Findings = findings ?? Array.Empty<DeepScanFinding>();
        ProcessedEntries = processedEntries < 0 ? 0 : processedEntries;
        ProcessedSizeBytes = processedSizeBytes < 0 ? 0 : processedSizeBytes;
        CurrentPath = string.IsNullOrWhiteSpace(currentPath) ? string.Empty : currentPath;
        LatestFinding = latestFinding;
        CategoryTotals = categoryTotals ?? new ReadOnlyDictionary<string, long>(new Dictionary<string, long>());
        IsFinal = isFinal;
    }

    public IReadOnlyList<DeepScanFinding> Findings { get; }

    public long ProcessedEntries { get; }

    public long ProcessedSizeBytes { get; }

    public string CurrentPath { get; }

    public DeepScanFinding? LatestFinding { get; }

    public IReadOnlyDictionary<string, long> CategoryTotals { get; }

    public bool IsFinal { get; }

    public string ProcessedSizeDisplay => DeepScanFormatting.FormatBytes(ProcessedSizeBytes);
}

public sealed class DeepScanResult
{
    public DeepScanResult(IReadOnlyList<DeepScanFinding> findings, string rootPath, DateTimeOffset generatedAt, int totalCandidates, long totalSizeBytes)
    {
        Findings = findings ?? throw new ArgumentNullException(nameof(findings));
        RootPath = rootPath ?? string.Empty;
        GeneratedAt = generatedAt;
        TotalCandidates = totalCandidates;
        TotalSizeBytes = totalSizeBytes;
        CategoryTotals = DeepScanAggregation.CalculateCategoryTotals(findings);
    }

    public IReadOnlyList<DeepScanFinding> Findings { get; }

    public string RootPath { get; }

    public DateTimeOffset GeneratedAt { get; }

    public int TotalCandidates { get; }

    public long TotalSizeBytes { get; }

    public string TotalSizeDisplay => DeepScanFormatting.FormatBytes(TotalSizeBytes);

    public IReadOnlyDictionary<string, long> CategoryTotals { get; }

    public static DeepScanResult FromFindings(string rootPath, IReadOnlyList<DeepScanFinding> findings)
    {
        var list = findings ?? Array.Empty<DeepScanFinding>();
        var totalSize = DeepScanAggregation.CalculateUniqueSize(list);
        return new DeepScanResult(list, rootPath, DateTimeOffset.UtcNow, list.Count, totalSize);
    }
}

internal static class DeepScanClassifier
{
    private static readonly string[] GameMarkers =
    {
        "\\steamapps\\",
        "\\steam library",
        "\\epic games\\",
        "\\gog galaxy",
        "\\riot games",
        "\\league of legends",
        "\\battle.net",
        "\\blizzard\\",
        "\\origin\\",
        "\\ea games",
        "\\uplay",
        "\\ubisoft",
        "\\rockstar games",
        "\\xboxgames",
        "\\windowsapps\\",
        "\\microsoft flight simulator"
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".mov", ".avi", ".wmv", ".flv", ".webm"
    };

    private static readonly HashSet<string> PictureExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tiff", ".heic", ".raw"
    };

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac", ".wav", ".aac", ".ogg", ".wma"
    };

    private static readonly HashSet<string> DocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".doc", ".docx", ".ppt", ".pptx", ".xls", ".xlsx", ".pdf", ".txt", ".rtf", ".md"
    };

    private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".rar", ".7z", ".tar", ".gz", ".xz", ".bz2"
    };

    private static readonly HashSet<string> DatabaseExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mdb", ".accdb", ".sqlite", ".db", ".ndf", ".ldf", ".mdf"
    };

    private static readonly HashSet<string> LogExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".log", ".etl", ".blg"
    };

    public static string Resolve(string path, bool isDirectory, string? extension)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "Other";
        }

        var normalized = path.Replace('/', '\\');
        var lower = normalized.ToLowerInvariant();

        if (lower.Contains("\\windows\\") || lower.Contains("\\system32") || lower.Contains("\\$recycle.bin\\") || lower.Contains("\\system volume information"))
        {
            return "System";
        }

        if (lower.Contains("\\program files") || lower.Contains("\\program files (x86)") || lower.Contains("\\windowsapps\\"))
        {
            return ContainsAny(lower, GameMarkers) ? "Games" : "Applications";
        }

        if (ContainsAny(lower, GameMarkers) || lower.Contains("\\saved games\\"))
        {
            return "Games";
        }

        if (lower.Contains("\\appdata\\") || lower.Contains("\\programdata\\"))
        {
            return "App Data";
        }

        if (lower.Contains("\\onedrive\\") || lower.Contains("\\dropbox\\") || lower.Contains("\\google drive\\"))
        {
            return "Cloud Sync";
        }

        if (lower.Contains("\\downloads\\"))
        {
            return "Downloads";
        }

        if (lower.Contains("\\documents\\") || lower.Contains("\\my documents\\"))
        {
            return "Documents";
        }

        if (lower.Contains("\\desktop\\"))
        {
            return "Desktop";
        }

        if (lower.Contains("\\pictures\\") || lower.Contains("\\photos\\") || lower.Contains("\\dcim\\"))
        {
            return "Pictures";
        }

        if (lower.Contains("\\videos\\") || lower.Contains("\\movies\\"))
        {
            return "Videos";
        }

        if (lower.Contains("\\music\\") || lower.Contains("\\audio\\"))
        {
            return "Music";
        }

        if (isDirectory && (lower.EndsWith("\\cache") || lower.Contains("\\cache\\") || lower.Contains("\\temp\\") || lower.Contains("\\tmp\\")))
        {
            return "Cache";
        }

        var ext = string.IsNullOrWhiteSpace(extension) ? string.Empty : extension;
        if (VideoExtensions.Contains(ext))
        {
            return "Videos";
        }

        if (PictureExtensions.Contains(ext))
        {
            return "Pictures";
        }

        if (AudioExtensions.Contains(ext))
        {
            return "Music";
        }

        if (DocumentExtensions.Contains(ext))
        {
            return "Documents";
        }

        if (ArchiveExtensions.Contains(ext))
        {
            return "Archives";
        }

        if (DatabaseExtensions.Contains(ext))
        {
            return "Databases";
        }

        if (LogExtensions.Contains(ext))
        {
            return "Logs";
        }

        return "Other";
    }

    private static bool ContainsAny(string value, IEnumerable<string> markers)
    {
        foreach (var marker in markers)
        {
            if (value.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

public sealed class DeepScanFinding
{
    public DeepScanFinding(string path, string name, string directory, long sizeBytes, DateTimeOffset modifiedUtc, string extension, bool isDirectory = false, string? category = null)
    {
        Path = path ?? throw new ArgumentNullException(nameof(path));
        Name = string.IsNullOrWhiteSpace(name) ? System.IO.Path.GetFileName(path) : name;
        Directory = directory ?? string.Empty;
        SizeBytes = sizeBytes;
        ModifiedUtc = modifiedUtc;
        Extension = extension ?? string.Empty;
        IsDirectory = isDirectory;
        Category = string.IsNullOrWhiteSpace(category)
            ? DeepScanClassifier.Resolve(path, isDirectory, extension)
            : category;
    }

    public string Path { get; }

    public string Name { get; }

    public string Directory { get; }

    public long SizeBytes { get; }

    public DateTimeOffset ModifiedUtc { get; }

    public string Extension { get; }

    public bool IsDirectory { get; }

    public string Category { get; }

    public string SizeDisplay => DeepScanFormatting.FormatBytes(SizeBytes);

    public string ModifiedDisplay => ModifiedUtc.ToLocalTime().ToString("g");

    public string KindDisplay => IsDirectory ? "Folder" : "File";
}

internal static class DeepScanFormatting
{
    public static string FormatBytes(long bytes)
    {
        if (bytes < 0)
        {
            return "0 B";
        }

        var size = (double)bytes;
        string[] units = { "B", "KB", "MB", "GB", "TB", "PB" };
        var unitIndex = 0;

        while (size >= 1024 && unitIndex < units.Length - 1)
        {
            size /= 1024;
            unitIndex++;
        }

        return unitIndex == 0 ? $"{bytes} {units[unitIndex]}" : $"{size:0.0} {units[unitIndex]}";
    }
}

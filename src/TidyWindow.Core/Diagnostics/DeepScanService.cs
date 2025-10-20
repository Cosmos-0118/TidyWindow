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

/// <summary>
/// Executes deep scan automation to surface the largest files and folders under a target path.
/// </summary>
public sealed class DeepScanService
{
    public async Task<DeepScanResult> RunScanAsync(DeepScanRequest request, CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        var resolvedRoot = ResolveRootPath(request.RootPath);
        return await Task.Run(
            () => ExecuteScan(resolvedRoot, request, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    private static DeepScanResult ExecuteScan(string resolvedRoot, DeepScanRequest request, CancellationToken cancellationToken)
    {
        var context = new ScanContext(
            request.MaxItems,
            Math.Max(0L, (long)request.MinimumSizeInMegabytes) * 1024L * 1024L,
            request.IncludeHiddenFiles,
            request.NameFilters,
            request.NameMatchMode,
            request.IsCaseSensitiveNameMatch,
            request.IncludeDirectories);

        if (context.Limit <= 0)
        {
            return DeepScanResult.FromFindings(resolvedRoot, Array.Empty<DeepScanFinding>());
        }

        if (File.Exists(resolvedRoot))
        {
            var queue = new PriorityQueue<DeepScanFinding, long>(1);
            var rootFile = CreateFileEntry(resolvedRoot);
            if (rootFile is not null && TryProcessFileEntry(rootFile.Value, context, out var rootFinding, out _))
            {
                if (rootFinding is not null)
                {
                    AddCandidate(queue, rootFinding, context.Limit);
                }
            }

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
                AddCandidate(results, rootFinding, context.Limit);
            }
        }

        var findings = DrainQueue(results);
        return BuildResult(resolvedRoot, findings);
    }

    private static long ProcessDirectory(string directoryPath, PriorityQueue<DeepScanFinding, long> queue, ScanContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        long directorySize = 0;

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

                if (fileFinding is not null)
                {
                    AddCandidate(queue, fileFinding, context.Limit);
                }

                continue;
            }

            if (ShouldSkipDirectory(entry, context.IncludeHidden))
            {
                continue;
            }

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

            AddCandidate(queue, directoryFinding, context.Limit);
        }

        return directorySize;
    }

    private static void AddCandidate(PriorityQueue<DeepScanFinding, long> queue, DeepScanFinding candidate, int limit)
    {
        queue.Enqueue(candidate, candidate.SizeBytes);
        TrimQueue(queue, limit);
    }

    private static void TrimQueue(PriorityQueue<DeepScanFinding, long> queue, int limit)
    {
        while (queue.Count > limit)
        {
            queue.Dequeue();
        }
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
        var totalSize = findings.Sum(static item => item.SizeBytes);
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
        public ScanContext(
            int limit,
            long minSizeBytes,
            bool includeHidden,
            IReadOnlyList<string> nameFilters,
            DeepScanNameMatchMode nameMatchMode,
            bool isCaseSensitive,
            bool includeDirectories)
        {
            Limit = limit;
            MinSizeBytes = minSizeBytes;
            IncludeHidden = includeHidden;
            NameFilters = nameFilters ?? Array.Empty<string>();
            NameMatchMode = nameMatchMode;
            NameComparison = isCaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            IncludeDirectories = includeDirectories;
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
                ReturnSpecialDirectories = false
            };
            HasNameFilters = NameFilters.Count > 0;
        }

        public int Limit { get; }

        public long MinSizeBytes { get; }

        public bool IncludeHidden { get; }

        public IReadOnlyList<string> NameFilters { get; }

        public DeepScanNameMatchMode NameMatchMode { get; }

        public StringComparison NameComparison { get; }

        public bool IncludeDirectories { get; }

        public EnumerationOptions DirectoryEnumerationOptions { get; }

        public bool HasNameFilters { get; }
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

public sealed class DeepScanResult
{
    public DeepScanResult(IReadOnlyList<DeepScanFinding> findings, string rootPath, DateTimeOffset generatedAt, int totalCandidates, long totalSizeBytes)
    {
        Findings = findings ?? throw new ArgumentNullException(nameof(findings));
        RootPath = rootPath ?? string.Empty;
        GeneratedAt = generatedAt;
        TotalCandidates = totalCandidates;
        TotalSizeBytes = totalSizeBytes;
    }

    public IReadOnlyList<DeepScanFinding> Findings { get; }

    public string RootPath { get; }

    public DateTimeOffset GeneratedAt { get; }

    public int TotalCandidates { get; }

    public long TotalSizeBytes { get; }

    public string TotalSizeDisplay => DeepScanFormatting.FormatBytes(TotalSizeBytes);

    public static DeepScanResult FromFindings(string rootPath, IReadOnlyList<DeepScanFinding> findings)
    {
        var list = findings ?? Array.Empty<DeepScanFinding>();
        var totalSize = list.Sum(static item => item.SizeBytes);
        return new DeepScanResult(list, rootPath, DateTimeOffset.UtcNow, list.Count, totalSize);
    }
}

public sealed class DeepScanFinding
{
    public DeepScanFinding(string path, string name, string directory, long sizeBytes, DateTimeOffset modifiedUtc, string extension, bool isDirectory = false)
    {
        Path = path ?? throw new ArgumentNullException(nameof(path));
        Name = string.IsNullOrWhiteSpace(name) ? System.IO.Path.GetFileName(path) : name;
        Directory = directory ?? string.Empty;
        SizeBytes = sizeBytes;
        ModifiedUtc = modifiedUtc;
        Extension = extension ?? string.Empty;
        IsDirectory = isDirectory;
    }

    public string Path { get; }

    public string Name { get; }

    public string Directory { get; }

    public long SizeBytes { get; }

    public DateTimeOffset ModifiedUtc { get; }

    public string Extension { get; }

    public bool IsDirectory { get; }

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

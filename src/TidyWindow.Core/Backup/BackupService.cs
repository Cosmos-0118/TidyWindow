using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace TidyWindow.Core.Backup;

/// <summary>
/// Conflict behavior used when restoring items.
/// </summary>
public enum BackupConflictStrategy
{
    Overwrite,
    Rename,
    Skip,
    BackupExisting
}

public sealed class BackupRequest
{
    public IReadOnlyList<string> SourcePaths { get; init; } = Array.Empty<string>();
    public string DestinationArchivePath { get; init; } = string.Empty;
    public int ChunkSizeBytes { get; init; } = 4 * 1024 * 1024;
    public CompressionLevel PayloadCompressionLevel { get; init; } = CompressionLevel.Fastest;
    public bool AutoDetectPrecompressedFiles { get; init; } = true;
    public bool IncludeChunkHashes { get; init; }
    public string? Generator { get; init; }
    public BackupPolicies Policies { get; init; } = BackupPolicies.Default;
    public IReadOnlyList<string> RegistryKeys { get; init; } = Array.Empty<string>();
}

public sealed class BackupPolicies
{
    public static BackupPolicies Default { get; } = new()
    {
        ConflictStrategy = BackupConflictStrategy.Rename,
        LongPathAware = true,
        OneDriveHandling = "metadata",
        VssRequired = false
    };

    public BackupConflictStrategy ConflictStrategy { get; init; } = BackupConflictStrategy.Rename;
    public bool LongPathAware { get; init; } = true;
    public string OneDriveHandling { get; init; } = "metadata";
    public bool VssRequired { get; init; }
}

public sealed class BackupManifest
{
    public int ManifestVersion { get; init; } = 1;
    public DateTime CreatedUtc { get; init; }
    public string ArchiveFormat { get; init; } = "rrarchive";
    public string Generator { get; init; } = string.Empty;
    public BackupPolicies Policies { get; init; } = BackupPolicies.Default;
    public BackupHashInfo Hash { get; init; } = BackupHashInfo.Default;
    public IReadOnlyList<BackupProfile> Profiles { get; init; } = Array.Empty<BackupProfile>();
    public IReadOnlyList<BackupApp> Apps { get; init; } = Array.Empty<BackupApp>();
    public IReadOnlyList<BackupEntry> Entries { get; init; } = Array.Empty<BackupEntry>();
    public IReadOnlyList<RegistrySnapshot> Registry { get; init; } = Array.Empty<RegistrySnapshot>();
}

public sealed class RegistrySnapshot
{
    public string Root { get; init; } = "HKCU";
    public string Path { get; init; } = string.Empty; // relative to root
    public IReadOnlyList<RegistryValueSnapshot> Values { get; init; } = Array.Empty<RegistryValueSnapshot>();
    public IReadOnlyList<RegistrySnapshot> SubKeys { get; init; } = Array.Empty<RegistrySnapshot>();
}

public sealed class RegistryValueSnapshot
{
    public string Name { get; init; } = string.Empty; // empty = default value
    public string Kind { get; init; } = string.Empty;
    public object? Data { get; init; }
}

public sealed class BackupHashInfo
{
    public static BackupHashInfo Default { get; } = new()
    {
        Algorithm = "SHA256",
        ChunkSizeBytes = 4 * 1024 * 1024
    };

    public string Algorithm { get; init; } = "SHA256";
    public int ChunkSizeBytes { get; init; }
}

public sealed class BackupProfile
{
    public string Sid { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Root { get; init; } = string.Empty;
    public IReadOnlyList<string> KnownFolders { get; init; } = Array.Empty<string>();
}

public sealed class BackupApp
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string? InstallLocation { get; init; }
    public IReadOnlyList<string> DataPaths { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> RegistryKeys { get; init; } = Array.Empty<string>();
}

public sealed class BackupEntry
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Type { get; init; } = "file";
    public string SourcePath { get; init; } = string.Empty;
    public string TargetPath { get; init; } = string.Empty;
    public long SizeBytes { get; init; }
    public DateTime LastWriteTimeUtc { get; init; }
    public BackupHashValue Hash { get; init; } = new();
    public BackupAcl? Acl { get; init; }
    public string? Attributes { get; init; }
    public string? AppId { get; init; }
    public string? VssSnapshotId { get; init; }
}

public sealed class BackupHashValue
{
    public IReadOnlyList<string> Chunks { get; init; } = Array.Empty<string>();
    public string? Full { get; init; }
}

public sealed class BackupAcl
{
    public string? Owner { get; init; }
    public string? Sddl { get; init; }
    public bool Preserve { get; init; }
}

public sealed class BackupProgress
{
    public BackupProgress(long processedEntries, long totalEntries, string? currentPath, long processedBytes = 0, long totalBytes = 0)
    {
        ProcessedEntries = processedEntries;
        TotalEntries = totalEntries;
        CurrentPath = currentPath;
        ProcessedBytes = Math.Max(0, processedBytes);
        TotalBytes = Math.Max(0, totalBytes);
    }

    public long ProcessedEntries { get; }
    public long TotalEntries { get; }
    public string? CurrentPath { get; }
    public long ProcessedBytes { get; }
    public long TotalBytes { get; }
}

public sealed class BackupResult
{
    public BackupResult(string archivePath, BackupManifest manifest, long totalEntries, long totalBytes)
    {
        ArchivePath = archivePath;
        Manifest = manifest;
        TotalEntries = totalEntries;
        TotalBytes = totalBytes;
    }

    public string ArchivePath { get; }
    public BackupManifest Manifest { get; }
    public long TotalEntries { get; }
    public long TotalBytes { get; }
}

public interface IFileSnapshotProvider
{
    Stream OpenRead(string path);
}

internal sealed class DefaultFileSnapshotProvider : IFileSnapshotProvider
{
    public Stream OpenRead(string path)
    {
        // Placeholder for future VSS support; currently returns a shared read stream.
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.SequentialScan);
    }
}

/// <summary>
/// Creates rrarchive packages with manifest + payload using chunked hashing.
/// </summary>
public sealed class BackupService
{
    private static readonly TimeSpan ProgressReportInterval = TimeSpan.FromMilliseconds(250);
    private const long LargeFileCompressionThresholdBytes = 32L * 1024L * 1024L;
    private static readonly HashSet<string> PrecompressedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".7z", ".rar", ".gz", ".bz2", ".xz",
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".heic", ".avif",
        ".mp3", ".aac", ".ogg", ".flac", ".m4a", ".wav",
        ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".webm",
        ".pdf",
        ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx",
        ".cab", ".msi", ".nupkg", ".jar", ".war", ".apk"
    };

    private static readonly HashSet<string> HighCompressibilityExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".log", ".csv", ".json", ".xml", ".yaml", ".yml", ".ini", ".config",
        ".md", ".sql", ".ps1", ".psm1", ".cmd", ".bat", ".reg",
        ".cs", ".vb", ".ts", ".js", ".jsx", ".tsx", ".html", ".htm", ".css", ".scss", ".less"
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IFileSnapshotProvider _snapshotProvider;

    public BackupService()
        : this(new DefaultFileSnapshotProvider())
    {
    }

    public BackupService(IFileSnapshotProvider snapshotProvider)
    {
        _snapshotProvider = snapshotProvider ?? throw new ArgumentNullException(nameof(snapshotProvider));
    }

    public Task<BackupResult> CreateAsync(BackupRequest request, IProgress<BackupProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        return Task.Run(() => CreateInternal(request, progress, cancellationToken), cancellationToken);
    }

    private BackupResult CreateInternal(BackupRequest request, IProgress<BackupProgress>? progress, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.DestinationArchivePath))
        {
            throw new ArgumentException("DestinationArchivePath is required", nameof(request));
        }

        var normalizedChunkSize = Math.Max(64 * 1024, request.ChunkSizeBytes);
        var entries = new List<BackupEntry>();
        var totalBytes = 0L;
        var normalizedDest = Path.GetFullPath(request.DestinationArchivePath);
        Directory.CreateDirectory(Path.GetDirectoryName(normalizedDest)!);

        var uniqueSources = NormalizeSources(request.SourcePaths)
            .Where(source => !StringComparer.OrdinalIgnoreCase.Equals(source, normalizedDest))
            .ToList();

        var filesToBackup = new List<(string FilePath, string? BaseDirectory, long SizeBytes)>();
        var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var totalPlannedBytes = 0L;

        foreach (var source in uniqueSources)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (File.Exists(source))
            {
                if (StringComparer.OrdinalIgnoreCase.Equals(source, normalizedDest))
                {
                    continue;
                }

                if (seenFiles.Add(source))
                {
                    var fileSize = TryGetFileLength(source);
                    filesToBackup.Add((source, null, fileSize));
                    totalPlannedBytes += fileSize;
                }

                continue;
            }

            if (!Directory.Exists(source))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(source, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (StringComparer.OrdinalIgnoreCase.Equals(file, normalizedDest))
                {
                    continue;
                }

                if (seenFiles.Add(file))
                {
                    var fileSize = TryGetFileLength(file);
                    filesToBackup.Add((file, source, fileSize));
                    totalPlannedBytes += fileSize;
                }
            }
        }

        var totalCount = filesToBackup.Count;
        var processed = 0L;
        var processedBytes = 0L;

        using var archive = ZipFile.Open(normalizedDest, ZipArchiveMode.Create);

        foreach (var (filePath, baseDirectory, knownSizeBytes) in filesToBackup)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var entry = AddFile(
                filePath,
                archive,
                normalizedChunkSize,
                request.PayloadCompressionLevel,
                request.AutoDetectPrecompressedFiles,
                request.IncludeChunkHashes,
                cancellationToken,
                progress,
                processed,
                totalCount,
                processedBytes,
                totalPlannedBytes,
                knownSizeBytes,
                baseDirectory);

            entries.Add(entry);
            totalBytes += entry.SizeBytes;
            processed++;
            processedBytes = Math.Max(0, processedBytes + Math.Max(0, entry.SizeBytes));
            progress?.Report(new BackupProgress(processed, totalCount, filePath, processedBytes, totalPlannedBytes));
        }

        var registry = ExportRegistry(request.RegistryKeys);

        var manifest = new BackupManifest
        {
            CreatedUtc = DateTime.UtcNow,
            Generator = request.Generator ?? "TidyWindow",
            Policies = request.Policies,
            Hash = new BackupHashInfo { Algorithm = "SHA256", ChunkSizeBytes = normalizedChunkSize },
            Entries = entries.ToArray(),
            Registry = registry
        };

        var manifestEntry = archive.CreateEntry("manifest.json", CompressionLevel.Optimal);
        using (var stream = manifestEntry.Open())
        {
            JsonSerializer.Serialize(stream, manifest, JsonOptions);
        }

        return new BackupResult(normalizedDest, manifest, entries.Count, totalBytes);
    }

    private BackupEntry AddFile(
        string path,
        ZipArchive archive,
        int chunkSize,
        CompressionLevel payloadCompressionLevel,
        bool autoDetectPrecompressedFiles,
        bool includeChunkHashes,
        CancellationToken cancellationToken,
        IProgress<BackupProgress>? progress,
        long processedEntries,
        long totalEntries,
        long processedBytes,
        long totalBytes,
        long knownSizeBytes,
        string? baseDirectory = null)
    {
        var normalizedPath = Path.GetFullPath(path);
        var relativeTarget = BuildTargetPath(normalizedPath, baseDirectory);
        var targetEntryName = $"payload/{relativeTarget.Replace('\\', '/')}";

        var sizeBytes = knownSizeBytes >= 0 ? knownSizeBytes : TryGetFileLength(normalizedPath);
        var compressionLevel = ResolvePayloadCompressionLevel(normalizedPath, sizeBytes, payloadCompressionLevel, autoDetectPrecompressedFiles);
        var entry = archive.CreateEntry(targetEntryName, compressionLevel);

        List<string>? chunkHashes = includeChunkHashes ? new List<string>() : null;
        string? fullHash = null;

        progress?.Report(new BackupProgress(processedEntries, totalEntries, normalizedPath, processedBytes, totalBytes));

        using (var source = _snapshotProvider.OpenRead(normalizedPath))
        using (var target = entry.Open())
        {
            fullHash = CopyWithHash(source, target, chunkSize, chunkHashes, cancellationToken, progress, processedEntries, totalEntries, processedBytes, totalBytes, normalizedPath);
        }

        var hashValue = new BackupHashValue
        {
            Chunks = chunkHashes?.ToArray() ?? Array.Empty<string>(),
            Full = fullHash
        };

        var lastWriteTimeUtc = File.GetLastWriteTimeUtc(normalizedPath);
        var attributes = File.GetAttributes(normalizedPath).ToString();

        return new BackupEntry
        {
            Type = "file",
            SourcePath = normalizedPath,
            TargetPath = relativeTarget.Replace('\\', '/'),
            SizeBytes = Math.Max(0L, sizeBytes),
            LastWriteTimeUtc = lastWriteTimeUtc,
            Hash = hashValue,
            Attributes = attributes
        };
    }

    private static IReadOnlyList<RegistrySnapshot> ExportRegistry(IReadOnlyList<string> registryKeys)
    {
        if (registryKeys is null || registryKeys.Count == 0)
        {
            return Array.Empty<RegistrySnapshot>();
        }

        var results = new List<RegistrySnapshot>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var full in registryKeys)
        {
            if (string.IsNullOrWhiteSpace(full))
            {
                continue;
            }

            if (!full.StartsWith("HKEY_CURRENT_USER", StringComparison.OrdinalIgnoreCase))
            {
                continue; // only HKCU is allowed for safety
            }

            var relative = full["HKEY_CURRENT_USER".Length..].TrimStart('\\');
            if (!seen.Add(relative))
            {
                continue;
            }

            using var root = Microsoft.Win32.Registry.CurrentUser;
            using var key = root.OpenSubKey(relative);
            if (key is null)
            {
                continue;
            }

            var snapshot = SnapshotKey("HKCU", relative, key);
            if (snapshot != null)
            {
                results.Add(snapshot);
            }
        }

        return results;
    }

    private static RegistrySnapshot? SnapshotKey(string root, string relativePath, Microsoft.Win32.RegistryKey key)
    {
        try
        {
            var values = new List<RegistryValueSnapshot>();
            foreach (var name in key.GetValueNames())
            {
                var kind = key.GetValueKind(name);
                var data = key.GetValue(name);

                object? serialized = data;
                switch (kind)
                {
                    case Microsoft.Win32.RegistryValueKind.Binary:
                        serialized = data is byte[] bytes ? Convert.ToBase64String(bytes) : null;
                        break;
                    case Microsoft.Win32.RegistryValueKind.MultiString:
                        serialized = data as string[] ?? Array.Empty<string>();
                        break;
                    default:
                        serialized = data;
                        break;
                }

                values.Add(new RegistryValueSnapshot
                {
                    Name = name ?? string.Empty,
                    Kind = kind.ToString(),
                    Data = serialized
                });
            }

            var subKeys = new List<RegistrySnapshot>();
            foreach (var sub in key.GetSubKeyNames())
            {
                using var child = key.OpenSubKey(sub);
                if (child is null)
                {
                    continue;
                }
                var childSnapshot = SnapshotKey(root, Path.Combine(relativePath, sub), child);
                if (childSnapshot != null)
                {
                    subKeys.Add(childSnapshot);
                }
            }

            return new RegistrySnapshot
            {
                Root = root,
                Path = relativePath,
                Values = values.ToArray(),
                SubKeys = subKeys.ToArray()
            };
        }
        catch
        {
            return null;
        }
    }

    private static string CopyWithHash(
        Stream source,
        Stream destination,
        int chunkSize,
        IList<string>? chunkHashes,
        CancellationToken cancellationToken,
        IProgress<BackupProgress>? progress,
        long processedEntries,
        long totalEntries,
        long processedBytes,
        long totalBytes,
        string currentPath)
    {
        using var fullHasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var chunkHasher = chunkHashes is not null ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256) : null;
        var buffer = new byte[Math.Max(64 * 1024, chunkSize)];
        var lastReportUtc = DateTime.MinValue;
        var copiedBytes = 0L;
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            destination.Write(buffer, 0, read);
            copiedBytes += read;

            fullHasher.AppendData(buffer, 0, read);
            if (chunkHasher is not null)
            {
                chunkHasher.AppendData(buffer, 0, read);
                chunkHashes!.Add(Convert.ToHexString(chunkHasher.GetHashAndReset()));
            }

            if (progress is not null)
            {
                var now = DateTime.UtcNow;
                if (now - lastReportUtc >= ProgressReportInterval)
                {
                    progress.Report(new BackupProgress(processedEntries, totalEntries, currentPath, processedBytes + copiedBytes, totalBytes));
                    lastReportUtc = now;
                }
            }
        }

        // Rewind destination for potential callers (not needed for Zip entry but kept consistent)
        destination.Flush();

        progress?.Report(new BackupProgress(processedEntries, totalEntries, currentPath, processedBytes + copiedBytes, totalBytes));
        return Convert.ToHexString(fullHasher.GetHashAndReset());
    }

    private static CompressionLevel ResolvePayloadCompressionLevel(string filePath, long fileLength, CompressionLevel configured, bool autoDetectPrecompressedFiles)
    {
        if (!autoDetectPrecompressedFiles)
        {
            return configured;
        }

        var extension = Path.GetExtension(filePath);
        if (PrecompressedExtensions.Contains(extension))
        {
            return CompressionLevel.NoCompression;
        }

        if (HighCompressibilityExtensions.Contains(extension))
        {
            return CompressionLevel.SmallestSize;
        }

        if (fileLength >= LargeFileCompressionThresholdBytes)
        {
            return CompressionLevel.Fastest;
        }

        return configured;
    }

    private static long TryGetFileLength(string filePath)
    {
        try
        {
            return new FileInfo(filePath).Length;
        }
        catch
        {
            return 0;
        }
    }

    private static List<string> NormalizeSources(IReadOnlyList<string> sources)
    {
        var results = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources)
        {
            if (string.IsNullOrWhiteSpace(source))
            {
                continue;
            }

            try
            {
                var full = Path.GetFullPath(source);
                if (seen.Add(full))
                {
                    results.Add(full);
                }
            }
            catch (Exception)
            {
                // Ignore invalid paths.
            }
        }

        return results;
    }

    private static string BuildTargetPath(string fullPath, string? baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(baseDirectory))
        {
            return Path.GetFileName(fullPath);
        }

        try
        {
            var baseFull = Path.GetFullPath(baseDirectory);
            var relative = Path.GetRelativePath(baseFull, fullPath);
            return relative;
        }
        catch (Exception)
        {
            return Path.GetFileName(fullPath);
        }
    }
}

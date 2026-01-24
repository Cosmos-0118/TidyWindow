using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
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
    public string? Generator { get; init; }
    public BackupPolicies Policies { get; init; } = BackupPolicies.Default;
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
    public BackupProgress(long processedEntries, long totalEntries, string? currentPath)
    {
        ProcessedEntries = processedEntries;
        TotalEntries = totalEntries;
        CurrentPath = currentPath;
    }

    public long ProcessedEntries { get; }
    public long TotalEntries { get; }
    public string? CurrentPath { get; }
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
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
    }
}

/// <summary>
/// Creates rrarchive packages with manifest + payload using chunked hashing.
/// </summary>
public sealed class BackupService
{
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

        var uniqueSources = NormalizeSources(request.SourcePaths);
        var totalCount = uniqueSources.Count;
        var processed = 0L;

        using var archive = ZipFile.Open(normalizedDest, ZipArchiveMode.Create);

        foreach (var source in uniqueSources)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (File.Exists(source))
            {
                var entry = AddFile(source, archive, normalizedChunkSize, cancellationToken);
                entries.Add(entry);
                totalBytes += entry.SizeBytes;
                processed++;
                progress?.Report(new BackupProgress(processed, totalCount, source));
                continue;
            }

            if (Directory.Exists(source))
            {
                foreach (var file in Directory.EnumerateFiles(source, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var entry = AddFile(file, archive, normalizedChunkSize, cancellationToken, baseDirectory: source);
                    entries.Add(entry);
                    totalBytes += entry.SizeBytes;
                    processed++;
                    progress?.Report(new BackupProgress(processed, totalCount, file));
                }
            }
        }

        var manifest = new BackupManifest
        {
            CreatedUtc = DateTime.UtcNow,
            Generator = request.Generator ?? "TidyWindow",
            Policies = request.Policies,
            Hash = new BackupHashInfo { Algorithm = "SHA256", ChunkSizeBytes = normalizedChunkSize },
            Entries = entries.ToArray()
        };

        var manifestEntry = archive.CreateEntry("manifest.json", CompressionLevel.Optimal);
        using (var stream = manifestEntry.Open())
        {
            JsonSerializer.Serialize(stream, manifest, JsonOptions);
        }

        return new BackupResult(normalizedDest, manifest, entries.Count, totalBytes);
    }

    private BackupEntry AddFile(string path, ZipArchive archive, int chunkSize, CancellationToken cancellationToken, string? baseDirectory = null)
    {
        var normalizedPath = Path.GetFullPath(path);
        var relativeTarget = BuildTargetPath(normalizedPath, baseDirectory);
        var targetEntryName = $"payload/{relativeTarget.Replace('\\', '/')}";

        var fileInfo = new FileInfo(normalizedPath);
        var entry = archive.CreateEntry(targetEntryName, CompressionLevel.Optimal);

        var chunkHashes = new List<string>();
        string? fullHash = null;

        using (var source = _snapshotProvider.OpenRead(normalizedPath))
        using (var target = entry.Open())
        {
            fullHash = CopyWithHash(source, target, chunkSize, chunkHashes, cancellationToken);
        }

        var hashValue = new BackupHashValue
        {
            Chunks = chunkHashes.ToArray(),
            Full = fullHash
        };

        return new BackupEntry
        {
            Type = "file",
            SourcePath = normalizedPath,
            TargetPath = relativeTarget.Replace('\\', '/'),
            SizeBytes = Math.Max(0L, fileInfo.Length),
            LastWriteTimeUtc = fileInfo.LastWriteTimeUtc,
            Hash = hashValue,
            Attributes = fileInfo.Attributes.ToString()
        };
    }

    private static string CopyWithHash(Stream source, Stream destination, int chunkSize, IList<string> chunkHashes, CancellationToken cancellationToken)
    {
        using var fullHasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[chunkSize];
        var fullBuffer = new byte[8192];
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            destination.Write(buffer, 0, read);

            fullHasher.AppendData(buffer, 0, read);

            using var chunkHasher = SHA256.Create();
            var chunk = read == buffer.Length ? buffer : buffer.AsSpan(0, read).ToArray();
            var chunkHash = chunkHasher.ComputeHash(chunk);
            chunkHashes.Add(Convert.ToHexString(chunkHash));
        }

        // Rewind destination for potential callers (not needed for Zip entry but kept consistent)
        destination.Flush();

        fullHasher.AppendData(Array.Empty<byte>());
        return Convert.ToHexString(fullHasher.GetHashAndReset());
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

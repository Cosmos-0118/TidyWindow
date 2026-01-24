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

public sealed class RestoreRequest
{
    public string ArchivePath { get; init; } = string.Empty;
    public string? DestinationRoot { get; init; }
    public IReadOnlyDictionary<string, string>? PathRemapping { get; init; }
    public BackupConflictStrategy ConflictStrategy { get; init; } = BackupConflictStrategy.Rename;
    public bool VerifyHashes { get; init; } = true;
}

public sealed class RestoreResult
{
    public RestoreResult(long restoredEntries, IReadOnlyList<RestoreIssue> issues, long renamedCount, long backupCount, long overwrittenCount, long skippedCount)
    {
        RestoredEntries = restoredEntries;
        Issues = issues;
        RenamedCount = renamedCount;
        BackupCount = backupCount;
        OverwrittenCount = overwrittenCount;
        SkippedCount = skippedCount;
    }

    public long RestoredEntries { get; }
    public IReadOnlyList<RestoreIssue> Issues { get; }
    public long RenamedCount { get; }
    public long BackupCount { get; }
    public long OverwrittenCount { get; }
    public long SkippedCount { get; }
}

public sealed class RestoreIssue
{
    public RestoreIssue(string path, string message)
    {
        Path = path;
        Message = message;
    }

    public string Path { get; }
    public string Message { get; }
}

public sealed class RestoreProgress
{
    public RestoreProgress(long processedEntries, long totalEntries, string? currentPath)
    {
        ProcessedEntries = processedEntries;
        TotalEntries = totalEntries;
        CurrentPath = currentPath;
    }

    public long ProcessedEntries { get; }
    public long TotalEntries { get; }
    public string? CurrentPath { get; }
}

/// <summary>
/// Restores rrarchive payloads to their reconciled paths, honoring conflict strategy.
/// </summary>
public sealed class RestoreService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public Task<RestoreResult> RestoreAsync(RestoreRequest request, IProgress<RestoreProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        return Task.Run(() => RestoreInternal(request, progress, cancellationToken), cancellationToken);
    }

    private RestoreResult RestoreInternal(RestoreRequest request, IProgress<RestoreProgress>? progress, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ArchivePath))
        {
            throw new ArgumentException("ArchivePath is required", nameof(request));
        }

        var normalizedArchive = Path.GetFullPath(request.ArchivePath);
        if (!File.Exists(normalizedArchive))
        {
            throw new FileNotFoundException("Archive not found", normalizedArchive);
        }

        var issues = new List<RestoreIssue>();
        BackupManifest? manifest;

        using (var archive = ZipFile.OpenRead(normalizedArchive))
        {
            manifest = ReadManifest(archive);
            if (manifest is null)
            {
                throw new InvalidDataException("manifest.json missing or invalid in archive");
            }

            var total = manifest.Entries.Count;
            var processed = 0L;
            var renamedCount = 0L;
            var backupCount = 0L;
            var overwrittenCount = 0L;
            var skippedCount = 0L;

            foreach (var entry in manifest.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!string.Equals(entry.Type, "file", StringComparison.OrdinalIgnoreCase))
                {
                    processed++;
                    progress?.Report(new RestoreProgress(processed, total, entry.SourcePath));
                    continue;
                }

                var targetPath = ResolveTargetPath(entry, request);
                if (string.IsNullOrWhiteSpace(targetPath))
                {
                    processed++;
                    issues.Add(new RestoreIssue(entry.SourcePath, "Unable to map target path"));
                    progress?.Report(new RestoreProgress(processed, total, entry.SourcePath));
                    continue;
                }

                var payloadEntryName = $"payload/{entry.TargetPath}".Replace('\\', '/');
                var payload = archive.GetEntry(payloadEntryName);
                if (payload is null)
                {
                    processed++;
                    issues.Add(new RestoreIssue(targetPath, "Payload missing in archive"));
                    progress?.Report(new RestoreProgress(processed, total, targetPath));
                    continue;
                }

                try
                {
                    var existed = File.Exists(targetPath);
                    if (existed)
                    {
                        switch (request.ConflictStrategy)
                        {
                            case BackupConflictStrategy.Overwrite:
                                File.Delete(targetPath);
                                overwrittenCount++;
                                break;
                            case BackupConflictStrategy.Skip:
                                skippedCount++;
                                issues.Add(new RestoreIssue(targetPath, "Skipped: target exists"));
                                processed++;
                                progress?.Report(new RestoreProgress(processed, total, targetPath));
                                continue;
                            case BackupConflictStrategy.BackupExisting:
                                var backupPath = BuildUniqueName(targetPath, ".bak");
                                File.Move(targetPath, backupPath);
                                backupCount++;
                                break;
                            case BackupConflictStrategy.Rename:
                            default:
                                var renamed = BuildUniqueName(targetPath, "-backup");
                                File.Move(targetPath, renamed);
                                renamedCount++;
                                break;
                        }
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                    using var payloadStream = payload.Open();
                    using var destination = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.SequentialScan);
                    var verified = CopyAndVerify(payloadStream, destination, manifest.Hash.ChunkSizeBytes, entry.Hash, request.VerifyHashes, cancellationToken);
                    if (!verified)
                    {
                        issues.Add(new RestoreIssue(targetPath, "Hash mismatch after restore"));
                    }

                    if (entry.LastWriteTimeUtc != default)
                    {
                        File.SetLastWriteTimeUtc(targetPath, entry.LastWriteTimeUtc);
                    }
                }
                catch (Exception ex)
                {
                    issues.Add(new RestoreIssue(targetPath, ex.Message));
                }

                processed++;
                progress?.Report(new RestoreProgress(processed, total, targetPath));
            }

            return new RestoreResult(manifest.Entries.Count, issues, renamedCount, backupCount, overwrittenCount, skippedCount);
        }
    }

    private static BackupManifest? ReadManifest(ZipArchive archive)
    {
        var manifestEntry = archive.GetEntry("manifest.json");
        if (manifestEntry is null)
        {
            return null;
        }

        using var stream = manifestEntry.Open();
        return JsonSerializer.Deserialize<BackupManifest>(stream, JsonOptions);
    }

    private static string? ResolveTargetPath(BackupEntry entry, RestoreRequest request)
    {
        var source = entry.SourcePath;
        var mapped = ApplyMapping(source, request.PathRemapping);
        if (!string.IsNullOrWhiteSpace(mapped))
        {
            return NormalizePath(mapped!);
        }

        if (!string.IsNullOrWhiteSpace(request.DestinationRoot))
        {
            var relative = StripDrive(source);
            return NormalizePath(Path.Combine(request.DestinationRoot!, relative));
        }

        return NormalizePath(source);
    }

    private static string? ApplyMapping(string source, IReadOnlyDictionary<string, string>? mapping)
    {
        if (mapping is null || mapping.Count == 0)
        {
            return null;
        }

        var matches = mapping
            .OrderByDescending(pair => pair.Key.Length)
            .FirstOrDefault(pair => source.StartsWith(pair.Key, StringComparison.OrdinalIgnoreCase));

        if (string.IsNullOrWhiteSpace(matches.Key))
        {
            return null;
        }

        var remainder = source[matches.Key.Length..];
        return Path.Combine(matches.Value, remainder.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    }

    private static void ApplyConflictPolicy(string targetPath, BackupConflictStrategy strategy)
    {
        var fileExists = File.Exists(targetPath);
        if (!fileExists)
        {
            return;
        }

        switch (strategy)
        {
            case BackupConflictStrategy.Overwrite:
                File.Delete(targetPath);
                return;
            case BackupConflictStrategy.Skip:
                throw new IOException("Target exists and strategy is Skip.");
            case BackupConflictStrategy.BackupExisting:
                var backupPath = BuildUniqueName(targetPath, suffix: ".bak");
                File.Move(targetPath, backupPath);
                return;
            case BackupConflictStrategy.Rename:
            default:
                var renamed = BuildUniqueName(targetPath, suffix: "-backup");
                File.Move(targetPath, renamed);
                return;
        }
    }

    private static string BuildUniqueName(string path, string suffix)
    {
        var directory = Path.GetDirectoryName(path) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        var counter = 1;
        while (true)
        {
            var candidate = Path.Combine(directory, $"{name}{suffix}{(counter > 1 ? counter.ToString() : string.Empty)}{extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }

            counter++;
        }
    }

    private static bool CopyAndVerify(Stream source, Stream destination, int chunkSize, BackupHashValue manifestHash, bool verify, CancellationToken cancellationToken)
    {
        var chunkHashes = manifestHash.Chunks ?? Array.Empty<string>();
        var buffer = new byte[Math.Max(64 * 1024, chunkSize)];
        var chunkBuffer = verify ? new byte[chunkSize] : Array.Empty<byte>();
        var chunkFill = 0;
        var chunkIndex = 0;
        using var fullHasher = verify ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256) : null;

        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            destination.Write(buffer, 0, read);

            if (!verify)
            {
                continue;
            }

            fullHasher!.AppendData(buffer, 0, read);

            var remaining = read;
            var offset = 0;
            while (remaining > 0)
            {
                var toCopy = Math.Min(chunkSize - chunkFill, remaining);
                Buffer.BlockCopy(buffer, offset, chunkBuffer, chunkFill, toCopy);
                chunkFill += toCopy;
                offset += toCopy;
                remaining -= toCopy;

                if (chunkFill == chunkSize)
                {
                    if (chunkIndex < chunkHashes.Count)
                    {
                        using var chunkHasher = SHA256.Create();
                        var computed = Convert.ToHexString(chunkHasher.ComputeHash(chunkBuffer, 0, chunkFill));
                        if (!computed.Equals(chunkHashes[chunkIndex], StringComparison.OrdinalIgnoreCase))
                        {
                            return false;
                        }
                    }
                    chunkIndex++;
                    chunkFill = 0;
                }
            }
        }

        if (!verify)
        {
            return true;
        }

        if (chunkFill > 0)
        {
            if (chunkIndex < chunkHashes.Count)
            {
                using var chunkHasher = SHA256.Create();
                var computed = Convert.ToHexString(chunkHasher.ComputeHash(chunkBuffer, 0, chunkFill));
                if (!computed.Equals(chunkHashes[chunkIndex], StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
        }

        var final = Convert.ToHexString(fullHasher!.GetHashAndReset());
        if (!string.IsNullOrWhiteSpace(manifestHash.Full) && !final.Equals(manifestHash.Full, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private static string NormalizePath(string path)
    {
        return Path.GetFullPath(path);
    }

    private static string StripDrive(string path)
    {
        try
        {
            var root = Path.GetPathRoot(path);
            if (string.IsNullOrWhiteSpace(root))
            {
                return path.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }

            return path[root.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception)
        {
            return path.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }
}

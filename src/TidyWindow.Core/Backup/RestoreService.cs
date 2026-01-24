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
    public string? VolumeRootOverride { get; init; }
    public BackupConflictStrategy ConflictStrategy { get; init; } = BackupConflictStrategy.Rename;
    public bool VerifyHashes { get; init; } = true;
    public bool RestoreRegistry { get; init; }
}

public sealed class RestoreResult
{
    public RestoreResult(long restoredEntries, IReadOnlyList<RestoreIssue> issues, long renamedCount, long backupCount, long overwrittenCount, long skippedCount, IReadOnlyList<string> logs)
    {
        RestoredEntries = restoredEntries;
        Issues = issues;
        RenamedCount = renamedCount;
        BackupCount = backupCount;
        OverwrittenCount = overwrittenCount;
        SkippedCount = skippedCount;
        Logs = logs;
    }

    public long RestoredEntries { get; }
    public IReadOnlyList<RestoreIssue> Issues { get; }
    public long RenamedCount { get; }
    public long BackupCount { get; }
    public long OverwrittenCount { get; }
    public long SkippedCount { get; }
    public IReadOnlyList<string> Logs { get; }
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
            var traces = new List<string>();
            manifest = ReadManifest(archive);
            if (manifest is null)
            {
                throw new InvalidDataException("manifest.json missing or invalid in archive");
            }

            if (request.RestoreRegistry && manifest.Registry.Count > 0)
            {
                ApplyRegistry(manifest.Registry, traces, issues);
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
                    traces.Add($"MAP-FAIL source={entry.SourcePath}");
                    progress?.Report(new RestoreProgress(processed, total, entry.SourcePath));
                    continue;
                }

                var payloadEntryName = $"payload/{entry.TargetPath}".Replace('\\', '/');
                var payload = archive.GetEntry(payloadEntryName);
                if (payload is null)
                {
                    processed++;
                    issues.Add(new RestoreIssue(targetPath, "Payload missing in archive"));
                    traces.Add($"PAYLOAD-MISSING target={targetPath} source={entry.SourcePath}");
                    progress?.Report(new RestoreProgress(processed, total, targetPath));
                    continue;
                }

                try
                {
                    var existed = File.Exists(targetPath);
                    var conflictAction = "none";
                    if (existed)
                    {
                        switch (request.ConflictStrategy)
                        {
                            case BackupConflictStrategy.Overwrite:
                                File.Delete(targetPath);
                                overwrittenCount++;
                                conflictAction = "overwrite";
                                break;
                            case BackupConflictStrategy.Skip:
                                skippedCount++;
                                issues.Add(new RestoreIssue(targetPath, "Skipped: target exists"));
                                traces.Add($"SKIP target={targetPath} reason=exists");
                                processed++;
                                progress?.Report(new RestoreProgress(processed, total, targetPath));
                                continue;
                            case BackupConflictStrategy.BackupExisting:
                                var backupPath = BuildUniqueName(targetPath, ".bak");
                                File.Move(targetPath, backupPath);
                                backupCount++;
                                conflictAction = $"backup->{backupPath}";
                                break;
                            case BackupConflictStrategy.Rename:
                            default:
                                var renamed = BuildUniqueName(targetPath, "-backup");
                                File.Move(targetPath, renamed);
                                renamedCount++;
                                conflictAction = $"rename->{renamed}";
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
                        traces.Add($"VERIFY-FAIL target={targetPath}");
                    }

                    traces.Add($"RESTORED source={entry.SourcePath} target={targetPath} existed={existed} conflictAction={conflictAction} size={payload.Length}");

                    if (entry.LastWriteTimeUtc != default)
                    {
                        File.SetLastWriteTimeUtc(targetPath, entry.LastWriteTimeUtc);
                    }
                }
                catch (Exception ex)
                {
                    issues.Add(new RestoreIssue(targetPath, ex.Message));
                    traces.Add($"ERROR target={targetPath} error={ex.Message}");
                }

                processed++;
                progress?.Report(new RestoreProgress(processed, total, targetPath));
            }

            return new RestoreResult(manifest.Entries.Count, issues, renamedCount, backupCount, overwrittenCount, skippedCount, traces);
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

    private static void ApplyRegistry(IReadOnlyList<RegistrySnapshot> registry, List<string> traces, List<RestoreIssue> issues)
    {
        foreach (var root in registry)
        {
            if (!string.Equals(root.Root, "HKCU", StringComparison.OrdinalIgnoreCase))
            {
                continue; // safety: only HKCU
            }

            try
            {
                ApplyRegistrySubTree(Microsoft.Win32.Registry.CurrentUser, root.Path, root, traces);
            }
            catch (Exception ex)
            {
                issues.Add(new RestoreIssue(root.Path, $"Registry restore failed: {ex.Message}"));
                traces.Add($"REG-ERROR path={root.Path} error={ex.Message}");
            }
        }
    }

    private static void ApplyRegistrySubTree(Microsoft.Win32.RegistryKey root, string relativePath, RegistrySnapshot snapshot, List<string> traces)
    {
        using var key = root.CreateSubKey(relativePath, writable: true);
        if (key is null)
        {
            traces.Add($"REG-SKIP path={relativePath} reason=create-failed");
            return;
        }

        foreach (var value in snapshot.Values)
        {
            try
            {
                var kind = ParseKind(value.Kind);
                var data = DeserializeValue(value) ?? string.Empty;
                key.SetValue(string.IsNullOrEmpty(value.Name) ? string.Empty : value.Name, data, kind);
                traces.Add($"REG-WRITE path={relativePath} name={value.Name} kind={kind}");
            }
            catch (Exception ex)
            {
                traces.Add($"REG-WRITE-FAIL path={relativePath} name={value.Name} error={ex.Message}");
            }
        }

        foreach (var child in snapshot.SubKeys)
        {
            var childPath = string.IsNullOrEmpty(relativePath) ? child.Path : child.Path;
            ApplyRegistrySubTree(root, childPath, child, traces);
        }
    }

    private static Microsoft.Win32.RegistryValueKind ParseKind(string kind)
    {
        if (Enum.TryParse<Microsoft.Win32.RegistryValueKind>(kind, ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        return Microsoft.Win32.RegistryValueKind.String;
    }

    private static object? DeserializeValue(RegistryValueSnapshot value)
    {
        switch (value.Kind?.ToUpperInvariant())
        {
            case "BINARY":
                return value.Data is string s ? Convert.FromBase64String(s) : Array.Empty<byte>();
            case "MULTISTRING":
                if (value.Data is System.Text.Json.JsonElement je && je.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    var list = new List<string>();
                    foreach (var item in je.EnumerateArray())
                    {
                        list.Add(item.GetString() ?? string.Empty);
                    }
                    return list.ToArray();
                }
                return value.Data as string[] ?? Array.Empty<string>();
            case "DWORD":
                if (value.Data is JsonElement dwordElement && dwordElement.ValueKind == JsonValueKind.Number)
                {
                    return dwordElement.GetInt32();
                }
                if (value.Data is string dStr && int.TryParse(dStr, out var dParsed))
                {
                    return dParsed;
                }
                return 0;
            case "QWORD":
                if (value.Data is JsonElement qwordElement && qwordElement.ValueKind == JsonValueKind.Number)
                {
                    return qwordElement.GetInt64();
                }
                if (value.Data is string qStr && long.TryParse(qStr, out var qParsed))
                {
                    return qParsed;
                }
                return 0L;
            default:
                return value.Data?.ToString() ?? string.Empty;
        }
    }

    private static string? ResolveTargetPath(BackupEntry entry, RestoreRequest request)
    {
        var source = entry.SourcePath;

        if (!string.IsNullOrWhiteSpace(request.VolumeRootOverride))
        {
            var relative = StripDrive(source);
            var root = NormalizeDriveRoot(request.VolumeRootOverride!);
            return NormalizePath(Path.Combine(root, relative));
        }

        if (!string.IsNullOrWhiteSpace(request.DestinationRoot))
        {
            var relative = StripDrive(source);
            return NormalizePath(Path.Combine(request.DestinationRoot!, relative));
        }

        return NormalizePath(source);
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

    private static string NormalizeDriveRoot(string root)
    {
        var full = Path.GetFullPath(root);
        return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
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

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace TidyWindow.Core.Startup;

public sealed class StartupBackupStore
{
    private const string BackupFileName = "startup-backups.json";
    private static readonly TimeSpan InterprocessLockTimeout = TimeSpan.FromSeconds(10);

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _filePath;
    private readonly string _baseDirectory;
    private readonly string _mutexName;
    private readonly object _lock = new();

    public StartupBackupStore(string? rootDirectory = null)
    {
        _baseDirectory = string.IsNullOrWhiteSpace(rootDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TidyWindow", "StartupBackups")
            : rootDirectory;

        Directory.CreateDirectory(_baseDirectory);
        _filePath = Path.Combine(_baseDirectory, BackupFileName);
        _mutexName = BuildMutexName(_filePath);
    }

    /// <summary>
    /// Gets the directory path where backup files are stored.
    /// </summary>
    public string BackupDirectory => _baseDirectory;

    public StartupEntryBackup? Get(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        return ExecuteLocked(() =>
        {
            var map = ReadAll();
            return map.TryGetValue(id.Trim(), out var backup) ? backup : null;
        });
    }

    public IReadOnlyCollection<StartupEntryBackup> GetAll()
    {
        return ExecuteLocked(() =>
        {
            var map = ReadAll();
            return map.Values.ToList();
        });
    }

    public StartupEntryBackup? FindLatestByValueName(string valueName)
    {
        if (string.IsNullOrWhiteSpace(valueName))
        {
            return null;
        }

        var normalizedValueName = valueName.Trim();
        return ExecuteLocked(() =>
        {
            var map = ReadAll();
            return map.Values
                .Where(IsRunBackup)
                .Where(b => string.Equals(b.RegistryValueName, normalizedValueName, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(b => b.CreatedAtUtc)
                .FirstOrDefault();
        });
    }

    public StartupEntryBackup? FindLatestRunBackup(string registryRoot, string registrySubKey, string valueName)
    {
        if (string.IsNullOrWhiteSpace(registryRoot) || string.IsNullOrWhiteSpace(registrySubKey) || string.IsNullOrWhiteSpace(valueName))
        {
            return null;
        }

        var normalizedRoot = NormalizeRootName(registryRoot);
        var normalizedSubKey = NormalizeRegistrySubKey(registrySubKey);
        var normalizedValueName = valueName.Trim();
        if (string.IsNullOrWhiteSpace(normalizedRoot) || string.IsNullOrWhiteSpace(normalizedSubKey))
        {
            return null;
        }

        return ExecuteLocked(() =>
        {
            var map = ReadAll();
            return map.Values
                .Where(IsRunBackup)
                .Where(b => string.Equals(NormalizeRootName(b.RegistryRoot), normalizedRoot, StringComparison.OrdinalIgnoreCase))
                .Where(b => string.Equals(NormalizeRegistrySubKey(b.RegistrySubKey), normalizedSubKey, StringComparison.OrdinalIgnoreCase))
                .Where(b => string.Equals(b.RegistryValueName, normalizedValueName, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(b => b.CreatedAtUtc)
                .FirstOrDefault();
        });
    }

    public void Save(StartupEntryBackup backup)
    {
        if (backup is null)
        {
            throw new ArgumentNullException(nameof(backup));
        }

        if (!IsValidBackup(backup))
        {
            throw new ArgumentException("Backup entry is missing required identifying data.", nameof(backup));
        }

        var normalizedBackup = NormalizeBackup(backup);

        ExecuteLocked(() =>
        {
            var map = ReadAll();
            map[normalizedBackup.Id] = normalizedBackup;
            Persist(map);
        });
    }

    public void Remove(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return;
        }

        ExecuteLocked(() =>
        {
            var map = ReadAll();
            if (map.Remove(id.Trim()))
            {
                Persist(map);
            }
        });
    }

    /// <summary>
    /// Removes backup entries that reference a backup file which no longer exists.
    /// This cleans up "ghost" entries caused by external file deletion.
    /// </summary>
    /// <returns>Number of stale entries removed.</returns>
    public int CleanupStaleBackups()
    {
        return ExecuteLocked(() =>
        {
            var map = ReadAll();
            var staleIds = new List<string>();

            foreach (var (id, backup) in map)
            {
                // For StartupFolder entries, check if backup file still exists
                if (backup.SourceKind == StartupItemSourceKind.StartupFolder &&
                    !string.IsNullOrWhiteSpace(backup.FileBackupPath) &&
                    !File.Exists(backup.FileBackupPath))
                {
                    // Also check that original file doesn't exist (truly stale)
                    if (string.IsNullOrWhiteSpace(backup.FileOriginalPath) || !File.Exists(backup.FileOriginalPath))
                    {
                        staleIds.Add(id);
                    }
                }
            }

            if (staleIds.Count > 0)
            {
                foreach (var id in staleIds)
                {
                    map.Remove(id);
                }
                Persist(map);
            }

            return staleIds.Count;
        });
    }

    /// <summary>
    /// Validates a backup entry to ensure it has the minimum required data.
    /// </summary>
    public static bool IsValidBackup(StartupEntryBackup? backup)
    {
        if (backup is null || string.IsNullOrWhiteSpace(backup.Id))
            return false;

        // Must have at least some identifying information
        return !string.IsNullOrWhiteSpace(backup.RegistryValueName) ||
               !string.IsNullOrWhiteSpace(backup.ServiceName) ||
               !string.IsNullOrWhiteSpace(backup.TaskPath) ||
               !string.IsNullOrWhiteSpace(backup.FileOriginalPath);
    }

    private Dictionary<string, StartupEntryBackup> ReadAll()
    {
        // Try primary file first, then fall back to .bak if primary is missing/corrupt.
        var result = TryReadFile(_filePath);
        if (result is not null)
        {
            return result;
        }

        var bakPath = _filePath + ".bak";
        result = TryReadFile(bakPath);
        if (result is not null)
        {
            // Self-heal primary file from fallback if possible.
            TryRestorePrimaryFromFallback(result);
            return result;
        }

        return result ?? new Dictionary<string, StartupEntryBackup>(StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, StartupEntryBackup>? TryReadFile(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json))
            {
                return new Dictionary<string, StartupEntryBackup>(StringComparer.OrdinalIgnoreCase);
            }

            var items = JsonSerializer.Deserialize<List<StartupEntryBackup?>>(json, SerializerOptions) ?? new List<StartupEntryBackup?>();
            var map = new Dictionary<string, StartupEntryBackup>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in items)
            {
                if (!IsValidBackup(item))
                {
                    continue;
                }

                var normalized = NormalizeBackup(item!);
                if (map.TryGetValue(normalized.Id, out var existing))
                {
                    if (normalized.CreatedAtUtc >= existing.CreatedAtUtc)
                    {
                        map[normalized.Id] = normalized;
                    }
                }
                else
                {
                    map[normalized.Id] = normalized;
                }
            }

            return map;
        }
        catch
        {
            // Corrupted — return null to try fallback.
        }

        return null;
    }

    private void Persist(IDictionary<string, StartupEntryBackup> map)
    {
        var list = map.Values
            .Where(IsValidBackup)
            .Select(NormalizeBackup)
            .OrderBy(item => item.CreatedAtUtc)
            .ToList();

        var json = JsonSerializer.Serialize(list, SerializerOptions);
        WriteAtomically(json, createBackup: true);
    }

    private void TryRestorePrimaryFromFallback(IDictionary<string, StartupEntryBackup> map)
    {
        try
        {
            var list = map.Values
                .Where(IsValidBackup)
                .Select(NormalizeBackup)
                .OrderBy(item => item.CreatedAtUtc)
                .ToList();

            var json = JsonSerializer.Serialize(list, SerializerOptions);
            WriteAtomically(json, createBackup: false);
        }
        catch
        {
            // Non-fatal: keep serving from fallback.
        }
    }

    private void WriteAtomically(string json, bool createBackup)
    {
        var tempPath = _filePath + ".tmp";
        var bakPath = _filePath + ".bak";
        var tempConsumed = false;

        try
        {
            File.WriteAllText(tempPath, json);

            if (File.Exists(_filePath))
            {
                if (createBackup)
                {
                    try
                    {
                        File.Replace(tempPath, _filePath, bakPath, ignoreMetadataErrors: true);
                        tempConsumed = true;
                        return;
                    }
                    catch
                    {
                        TryCopyFile(_filePath, bakPath);
                    }
                }

                // Fallback path if Replace fails or backup rotation is not requested.
                File.Copy(tempPath, _filePath, overwrite: true);
                return;
            }

            File.Move(tempPath, _filePath, overwrite: true);
            tempConsumed = true;

            if (createBackup)
            {
                TryCopyFile(_filePath, bakPath);
            }
        }
        finally
        {
            if (!tempConsumed)
            {
                TryDeleteFile(tempPath);
            }
        }
    }

    private static StartupEntryBackup NormalizeBackup(StartupEntryBackup backup)
    {
        var normalizedId = backup.Id.Trim();
        var normalizedCreatedAt = backup.CreatedAtUtc == default ? DateTimeOffset.UtcNow : backup.CreatedAtUtc;

        return backup with
        {
            Id = normalizedId,
            RegistryRoot = NormalizeRootName(backup.RegistryRoot),
            RegistrySubKey = NormalizeRegistrySubKey(backup.RegistrySubKey),
            RegistryValueName = NormalizeOptional(backup.RegistryValueName),
            RegistryValueData = NormalizeOptional(backup.RegistryValueData),
            FileOriginalPath = NormalizeOptional(backup.FileOriginalPath),
            FileBackupPath = NormalizeOptional(backup.FileBackupPath),
            TaskPath = NormalizeOptional(backup.TaskPath),
            ServiceName = NormalizeOptional(backup.ServiceName),
            CreatedAtUtc = normalizedCreatedAt
        };
    }

    private static bool IsRunBackup(StartupEntryBackup backup)
    {
        return backup.SourceKind is StartupItemSourceKind.RunKey or StartupItemSourceKind.RunOnce;
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string? NormalizeRootName(string? rootName)
    {
        if (string.IsNullOrWhiteSpace(rootName))
        {
            return null;
        }

        return rootName.Trim().ToUpperInvariant() switch
        {
            "HKEY_CURRENT_USER" => "HKCU",
            "HKEY_LOCAL_MACHINE" => "HKLM",
            var normalized => normalized
        };
    }

    private static string? NormalizeRegistrySubKey(string? subKey)
    {
        if (string.IsNullOrWhiteSpace(subKey))
        {
            return null;
        }

        return subKey.Trim().Replace('/', '\\').Trim('\\');
    }

    private void ExecuteLocked(Action action)
    {
        ExecuteLocked(() =>
        {
            action();
            return 0;
        });
    }

    private T ExecuteLocked<T>(Func<T> action)
    {
        lock (_lock)
        {
            using var mutex = new Mutex(initiallyOwned: false, _mutexName);
            var hasHandle = false;

            try
            {
                try
                {
                    hasHandle = mutex.WaitOne(InterprocessLockTimeout);
                }
                catch (AbandonedMutexException)
                {
                    hasHandle = true;
                }

                if (!hasHandle)
                {
                    throw new IOException("Timed out waiting for startup backup store lock.");
                }

                return action();
            }
            finally
            {
                if (hasHandle)
                {
                    mutex.ReleaseMutex();
                }
            }
        }
    }

    private static string BuildMutexName(string filePath)
    {
        var normalizedPath = Path.GetFullPath(filePath).ToUpperInvariant();
        using var sha = SHA256.Create();
        var hash = Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(normalizedPath)));
        return $@"Local\TidyWindow.StartupBackupStore.{hash}";
    }

    private static void TryCopyFile(string sourcePath, string destinationPath)
    {
        try
        {
            if (File.Exists(sourcePath))
            {
                File.Copy(sourcePath, destinationPath, overwrite: true);
            }
        }
        catch
        {
            // Non-fatal.
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Non-fatal.
        }
    }
}

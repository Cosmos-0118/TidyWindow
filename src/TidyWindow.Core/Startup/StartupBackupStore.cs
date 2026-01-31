using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace TidyWindow.Core.Startup;

public sealed class StartupBackupStore
{
    private const string BackupFileName = "startup-backups.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _filePath;
    private readonly string _baseDirectory;
    private readonly object _lock = new();

    public StartupBackupStore(string? rootDirectory = null)
    {
        _baseDirectory = string.IsNullOrWhiteSpace(rootDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TidyWindow", "StartupBackups")
            : rootDirectory;

        Directory.CreateDirectory(_baseDirectory);
        _filePath = Path.Combine(_baseDirectory, BackupFileName);
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

        lock (_lock)
        {
            var map = ReadAll();
            return map.TryGetValue(id, out var backup) ? backup : null;
        }
    }

    public IReadOnlyCollection<StartupEntryBackup> GetAll()
    {
        lock (_lock)
        {
            var map = ReadAll();
            return map.Values.ToList();
        }
    }

    public StartupEntryBackup? FindLatestByValueName(string valueName)
    {
        if (string.IsNullOrWhiteSpace(valueName))
        {
            return null;
        }

        lock (_lock)
        {
            var map = ReadAll();
            return map.Values
                .Where(b => string.Equals(b.RegistryValueName, valueName, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(b => b.CreatedAtUtc)
                .FirstOrDefault();
        }
    }

    public void Save(StartupEntryBackup backup)
    {
        if (backup is null)
        {
            throw new ArgumentNullException(nameof(backup));
        }

        lock (_lock)
        {
            var map = ReadAll();
            map[backup.Id] = backup;
            Persist(map);
        }
    }

    public void Remove(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return;
        }

        lock (_lock)
        {
            var map = ReadAll();
            if (map.Remove(id))
            {
                Persist(map);
            }
        }
    }

    /// <summary>
    /// Removes backup entries that reference a backup file which no longer exists.
    /// This cleans up "ghost" entries caused by external file deletion.
    /// </summary>
    /// <returns>Number of stale entries removed.</returns>
    public int CleanupStaleBackups()
    {
        lock (_lock)
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
        }
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
        if (!File.Exists(_filePath))
        {
            return new Dictionary<string, StartupEntryBackup>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            var json = File.ReadAllText(_filePath);
            var items = JsonSerializer.Deserialize<List<StartupEntryBackup>>(json, SerializerOptions);
            return items?.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase) ?? new Dictionary<string, StartupEntryBackup>(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, StartupEntryBackup>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void Persist(IDictionary<string, StartupEntryBackup> map)
    {
        var list = map.Values.ToList();
        var json = JsonSerializer.Serialize(list, SerializerOptions);
        File.WriteAllText(_filePath, json);
    }
}

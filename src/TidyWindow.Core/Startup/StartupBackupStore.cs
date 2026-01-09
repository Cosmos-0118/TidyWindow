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
    private readonly object _lock = new();

    public StartupBackupStore(string? rootDirectory = null)
    {
        var baseDirectory = string.IsNullOrWhiteSpace(rootDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TidyWindow", "StartupBackups")
            : rootDirectory;

        Directory.CreateDirectory(baseDirectory);
        _filePath = Path.Combine(baseDirectory, BackupFileName);
    }

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

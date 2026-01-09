using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace TidyWindow.App.Services;

public sealed class StartupGuardService
{
    private readonly string _filePath;
    private readonly object _lock = new();

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public StartupGuardService(string? storageDirectory = null)
    {
        var baseDirectory = string.IsNullOrWhiteSpace(storageDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TidyWindow", "StartupGuards")
            : storageDirectory;

        Directory.CreateDirectory(baseDirectory);
        _filePath = Path.Combine(baseDirectory, "guards.json");
    }

    public bool IsGuarded(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        lock (_lock)
        {
            var set = Read();
            return set.Contains(id);
        }
    }

    public IReadOnlyCollection<string> GetAll()
    {
        lock (_lock)
        {
            return Read().ToArray();
        }
    }

    public void SetGuard(string id, bool enabled)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return;
        }

        lock (_lock)
        {
            var set = Read();
            if (enabled)
            {
                set.Add(id);
            }
            else
            {
                set.Remove(id);
            }

            Persist(set);
        }
    }

    private HashSet<string> Read()
    {
        if (!File.Exists(_filePath))
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            var json = File.ReadAllText(_filePath);
            var items = JsonSerializer.Deserialize<List<string>>(json, Options);
            return items is null
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(items, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void Persist(HashSet<string> ids)
    {
        var list = ids.ToList();
        var json = JsonSerializer.Serialize(list, Options);
        File.WriteAllText(_filePath, json);
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace TidyWindow.Core.Startup;

public sealed class StartupDelayPlanStore
{
    private const string FileName = "startup-delays.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _filePath;
    private readonly object _lock = new();

    public StartupDelayPlanStore(string? rootDirectory = null)
    {
        var baseDirectory = string.IsNullOrWhiteSpace(rootDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TidyWindow", "StartupBackups")
            : rootDirectory;

        Directory.CreateDirectory(baseDirectory);
        _filePath = Path.Combine(baseDirectory, FileName);
    }

    public IReadOnlyList<StartupDelayPlan> GetAll()
    {
        lock (_lock)
        {
            return ReadAll().Values.ToList();
        }
    }

    public StartupDelayPlan? Get(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        lock (_lock)
        {
            var map = ReadAll();
            return map.TryGetValue(id, out var plan) ? plan : null;
        }
    }

    public void Save(StartupDelayPlan plan)
    {
        if (plan is null)
        {
            throw new ArgumentNullException(nameof(plan));
        }

        lock (_lock)
        {
            var map = ReadAll();
            map[plan.Id] = plan;
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

    private Dictionary<string, StartupDelayPlan> ReadAll()
    {
        if (!File.Exists(_filePath))
        {
            return new Dictionary<string, StartupDelayPlan>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            var json = File.ReadAllText(_filePath);
            var items = JsonSerializer.Deserialize<List<StartupDelayPlan>>(json, SerializerOptions);
            return items?.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase) ?? new Dictionary<string, StartupDelayPlan>(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, StartupDelayPlan>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void Persist(IDictionary<string, StartupDelayPlan> map)
    {
        var list = map.Values.ToList();
        var json = JsonSerializer.Serialize(list, SerializerOptions);
        File.WriteAllText(_filePath, json);
    }
}

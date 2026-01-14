using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TidyWindow.App.Services;

/// <summary>
/// Persists scheduler and auto-tune process lists locally so they survive app restarts.
/// </summary>
public sealed class PerformanceLabProcessListStore
{
    private readonly string _filePath;
    private readonly JsonSerializerOptions _serializerOptions;
    private readonly object _syncRoot = new();

    public PerformanceLabProcessListStore()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TidyWindow");
        Directory.CreateDirectory(root);
        _filePath = Path.Combine(root, "performance-lab-processes.json");
        _serializerOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
    }

    public PerformanceLabProcessListState Get()
    {
        lock (_syncRoot)
        {
            try
            {
                if (!File.Exists(_filePath))
                {
                    return PerformanceLabProcessListState.Empty;
                }

                using var stream = File.OpenRead(_filePath);
                var payload = JsonSerializer.Deserialize<StatePayload>(stream, _serializerOptions);
                return payload?.ToState() ?? PerformanceLabProcessListState.Empty;
            }
            catch
            {
                return PerformanceLabProcessListState.Empty;
            }
        }
    }

    public void Save(string schedulerProcessNames, string autoTuneProcessNames)
    {
        lock (_syncRoot)
        {
            try
            {
                var payload = StatePayload.FromState(new PerformanceLabProcessListState(schedulerProcessNames ?? string.Empty, autoTuneProcessNames ?? string.Empty));
                var tempPath = _filePath + ".tmp";

                using (var stream = File.Create(tempPath))
                {
                    JsonSerializer.Serialize(stream, payload, _serializerOptions);
                    stream.Flush(true);
                }

                File.Copy(tempPath, _filePath, overwrite: true);
                File.Delete(tempPath);
            }
            catch
            {
                // Local persistence failures should not break the UI.
            }
        }
    }

    public sealed record PerformanceLabProcessListState(string SchedulerProcessNames, string AutoTuneProcessNames)
    {
        public static PerformanceLabProcessListState Empty => new(string.Empty, string.Empty);
    }

    private sealed class StatePayload
    {
        public string? SchedulerProcessNames { get; set; }
        public string? AutoTuneProcessNames { get; set; }

        public PerformanceLabProcessListState ToState()
        {
            return new PerformanceLabProcessListState(
                SchedulerProcessNames ?? string.Empty,
                AutoTuneProcessNames ?? string.Empty);
        }

        public static StatePayload FromState(PerformanceLabProcessListState state)
        {
            return new StatePayload
            {
                SchedulerProcessNames = state.SchedulerProcessNames,
                AutoTuneProcessNames = state.AutoTuneProcessNames
            };
        }
    }
}

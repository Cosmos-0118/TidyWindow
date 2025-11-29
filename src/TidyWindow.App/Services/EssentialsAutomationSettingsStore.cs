using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.Immutable;
using TidyWindow.Core.Maintenance;

namespace TidyWindow.App.Services;

/// <summary>
/// Persists essentials automation settings to disk.
/// </summary>
public sealed class EssentialsAutomationSettingsStore
{
    private readonly string _filePath;
    private readonly JsonSerializerOptions _serializerOptions;
    private readonly object _syncRoot = new();

    public EssentialsAutomationSettingsStore()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TidyWindow");
        Directory.CreateDirectory(root);
        _filePath = Path.Combine(root, "essentials-automation.json");
        _serializerOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
    }

    public EssentialsAutomationSettings Get()
    {
        lock (_syncRoot)
        {
            try
            {
                if (!File.Exists(_filePath))
                {
                    return EssentialsAutomationSettings.Default;
                }

                using var stream = File.OpenRead(_filePath);
                var payload = JsonSerializer.Deserialize<StatePayload>(stream, _serializerOptions);
                return payload?.ToSettings() ?? EssentialsAutomationSettings.Default;
            }
            catch
            {
                return EssentialsAutomationSettings.Default;
            }
        }
    }

    public void Save(EssentialsAutomationSettings settings)
    {
        if (settings is null)
        {
            throw new ArgumentNullException(nameof(settings));
        }

        lock (_syncRoot)
        {
            try
            {
                var payload = StatePayload.FromSettings(settings);
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
                // Persistence failures should not bubble up into the UI layer.
            }
        }
    }

    private sealed class StatePayload
    {
        public bool AutomationEnabled { get; set; }

        public int IntervalMinutes { get; set; }

        public DateTimeOffset? LastRunUtc { get; set; }

        public List<string> TaskIds { get; set; } = new();

        public EssentialsAutomationSettings ToSettings()
        {
            var tasks = TaskIds?.Where(static id => !string.IsNullOrWhiteSpace(id)).ToImmutableArray() ?? ImmutableArray<string>.Empty;
            return new EssentialsAutomationSettings(AutomationEnabled, IntervalMinutes, LastRunUtc, tasks);
        }

        public static StatePayload FromSettings(EssentialsAutomationSettings settings)
        {
            return new StatePayload
            {
                AutomationEnabled = settings.AutomationEnabled,
                IntervalMinutes = settings.IntervalMinutes,
                LastRunUtc = settings.LastRunUtc,
                TaskIds = settings.TaskIds.IsDefault ? new List<string>() : settings.TaskIds.ToList()
            };
        }
    }
}

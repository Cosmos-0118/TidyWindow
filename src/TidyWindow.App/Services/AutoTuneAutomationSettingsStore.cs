using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TidyWindow.App.Services;

/// <summary>
/// Persists auto-tune automation settings to disk.
/// </summary>
public sealed class AutoTuneAutomationSettingsStore
{
    private readonly string _filePath;
    private readonly JsonSerializerOptions _serializerOptions;
    private readonly object _syncRoot = new();

    public AutoTuneAutomationSettingsStore()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TidyWindow");
        Directory.CreateDirectory(root);
        _filePath = Path.Combine(root, "auto-tune-automation.json");
        _serializerOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
    }

    public AutoTuneAutomationSettings Get()
    {
        lock (_syncRoot)
        {
            try
            {
                if (!File.Exists(_filePath))
                {
                    return AutoTuneAutomationSettings.Default;
                }

                using var stream = File.OpenRead(_filePath);
                var payload = JsonSerializer.Deserialize<StatePayload>(stream, _serializerOptions);
                return payload?.ToSettings() ?? AutoTuneAutomationSettings.Default;
            }
            catch
            {
                return AutoTuneAutomationSettings.Default;
            }
        }
    }

    public void Save(AutoTuneAutomationSettings settings)
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
                // Persistence failures should not block the UI.
            }
        }
    }

    private sealed class StatePayload
    {
        public bool AutomationEnabled { get; set; }
        public string? ProcessNames { get; set; }
        public string? PresetId { get; set; }
        public DateTimeOffset? LastRunUtc { get; set; }

        public AutoTuneAutomationSettings ToSettings()
        {
            return new AutoTuneAutomationSettings(
                AutomationEnabled,
                ProcessNames ?? string.Empty,
                PresetId ?? "LatencyBoost",
                LastRunUtc);
        }

        public static StatePayload FromSettings(AutoTuneAutomationSettings settings)
        {
            return new StatePayload
            {
                AutomationEnabled = settings.AutomationEnabled,
                ProcessNames = settings.ProcessNames,
                PresetId = settings.PresetId,
                LastRunUtc = settings.LastRunUtc
            };
        }
    }
}
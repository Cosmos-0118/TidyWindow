using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace TidyWindow.Core.Maintenance;

/// <summary>
/// Persists per-tweak registry preferences such as custom value overrides.
/// </summary>
public sealed class RegistryPreferenceService
{
    private const string PreferencesFileName = "registry-preferences.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _filePath;
    private readonly object _syncRoot = new();
    private Dictionary<string, string> _customValues;

    public RegistryPreferenceService()
        : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TidyWindow", PreferencesFileName))
    {
    }

    internal RegistryPreferenceService(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("Value cannot be null or whitespace.", nameof(filePath));
        }

        _filePath = filePath;
        _customValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        LoadFromDisk();
    }

    /// <summary>
    /// Returns the persisted custom value for the specified tweak, if any.
    /// </summary>
    public string? GetCustomValue(string tweakId)
    {
        if (string.IsNullOrWhiteSpace(tweakId))
        {
            return null;
        }

        lock (_syncRoot)
        {
            return _customValues.TryGetValue(tweakId, out var value) ? value : null;
        }
    }

    /// <summary>
    /// Updates or clears the persisted custom value for the specified tweak.
    /// </summary>
    public void SetCustomValue(string tweakId, string? value)
    {
        if (string.IsNullOrWhiteSpace(tweakId))
        {
            return;
        }

        var normalizedId = tweakId.Trim();

        lock (_syncRoot)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                if (_customValues.Remove(normalizedId))
                {
                    SaveToDiskLocked();
                }

                return;
            }

            if (_customValues.TryGetValue(normalizedId, out var existing) && string.Equals(existing, value, StringComparison.Ordinal))
            {
                return;
            }

            _customValues[normalizedId] = value;
            SaveToDiskLocked();
        }
    }

    private void LoadFromDisk()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return;
            }

            using var stream = File.OpenRead(_filePath);
            var model = JsonSerializer.Deserialize<RegistryPreferenceModel>(stream, SerializerOptions);
            if (model?.CustomValues is null)
            {
                return;
            }

            _customValues = new Dictionary<string, string>(model.CustomValues, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            _customValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void SaveToDiskLocked()
    {
        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var model = new RegistryPreferenceModel
            {
                CustomValues = new Dictionary<string, string>(_customValues, StringComparer.OrdinalIgnoreCase)
            };

            using var stream = File.Create(_filePath);
            JsonSerializer.Serialize(stream, model, SerializerOptions);
        }
        catch
        {
            // Persistence is best-effort; swallow and continue.
        }
    }

    private sealed class RegistryPreferenceModel
    {
        public Dictionary<string, string>? CustomValues { get; set; }
    }
}

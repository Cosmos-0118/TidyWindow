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
    private Dictionary<string, bool> _tweakStates;
    private string? _selectedPresetId;

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
        _tweakStates = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

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

    public bool TryGetTweakState(string tweakId, out bool value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(tweakId))
        {
            return false;
        }

        lock (_syncRoot)
        {
            return _tweakStates.TryGetValue(tweakId.Trim(), out value);
        }
    }

    public void SetTweakState(string tweakId, bool value)
    {
        if (string.IsNullOrWhiteSpace(tweakId))
        {
            return;
        }

        var normalizedId = tweakId.Trim();

        lock (_syncRoot)
        {
            if (_tweakStates.TryGetValue(normalizedId, out var existing) && existing == value)
            {
                return;
            }

            _tweakStates[normalizedId] = value;
            SaveToDiskLocked();
        }
    }

    public string? GetSelectedPresetId()
    {
        lock (_syncRoot)
        {
            return _selectedPresetId;
        }
    }

    public void SetSelectedPresetId(string? presetId)
    {
        lock (_syncRoot)
        {
            var normalized = string.IsNullOrWhiteSpace(presetId) ? null : presetId!.Trim();
            if (string.Equals(_selectedPresetId, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _selectedPresetId = normalized;
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
            if (model is null)
            {
                return;
            }

            if (model.CustomValues is not null)
            {
                _customValues = new Dictionary<string, string>(model.CustomValues, StringComparer.OrdinalIgnoreCase);
            }

            if (model.TweakStates is not null)
            {
                _tweakStates = new Dictionary<string, bool>(model.TweakStates, StringComparer.OrdinalIgnoreCase);
            }

            if (!string.IsNullOrWhiteSpace(model.SelectedPresetId))
            {
                _selectedPresetId = model.SelectedPresetId;
            }
        }
        catch
        {
            _customValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _tweakStates = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            _selectedPresetId = null;
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
                CustomValues = new Dictionary<string, string>(_customValues, StringComparer.OrdinalIgnoreCase),
                TweakStates = new Dictionary<string, bool>(_tweakStates, StringComparer.OrdinalIgnoreCase),
                SelectedPresetId = _selectedPresetId
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

        public Dictionary<string, bool>? TweakStates { get; set; }

        public string? SelectedPresetId { get; set; }
    }
}

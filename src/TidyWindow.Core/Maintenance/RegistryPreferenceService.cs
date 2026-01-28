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
    private Dictionary<string, bool> _appliedStates;
    private Dictionary<string, string> _appliedCustomValues;
    private DateTimeOffset? _lastAppliedUtc;
    private string? _appliedPresetId;
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
        _appliedStates = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        _appliedCustomValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

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

    public bool TryGetAppliedTweakState(string tweakId, out bool value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(tweakId))
        {
            return false;
        }

        lock (_syncRoot)
        {
            return _appliedStates.TryGetValue(tweakId.Trim(), out value);
        }
    }

    public string? GetAppliedCustomValue(string tweakId)
    {
        if (string.IsNullOrWhiteSpace(tweakId))
        {
            return null;
        }

        lock (_syncRoot)
        {
            return _appliedCustomValues.TryGetValue(tweakId.Trim(), out var value) ? value : null;
        }
    }

    public DateTimeOffset? GetLastAppliedUtc()
    {
        lock (_syncRoot)
        {
            return _lastAppliedUtc;
        }
    }

    public void SetAppliedStates(IEnumerable<RegistryAppliedState> appliedStates, DateTimeOffset appliedAtUtc)
    {
        SetAppliedStates(appliedStates, appliedAtUtc, null);
    }

    public void SetAppliedStates(IEnumerable<RegistryAppliedState> appliedStates, DateTimeOffset appliedAtUtc, string? appliedPresetId)
    {
        if (appliedStates is null)
        {
            return;
        }

        lock (_syncRoot)
        {
            _appliedStates.Clear();
            _appliedCustomValues.Clear();

            foreach (var state in appliedStates)
            {
                if (string.IsNullOrWhiteSpace(state.TweakId))
                {
                    continue;
                }

                var id = state.TweakId.Trim();
                _appliedStates[id] = state.State;

                if (!string.IsNullOrWhiteSpace(state.CustomValue))
                {
                    _appliedCustomValues[id] = state.CustomValue.Trim();
                }
            }

            _lastAppliedUtc = appliedAtUtc;
            _appliedPresetId = string.IsNullOrWhiteSpace(appliedPresetId) ? null : appliedPresetId.Trim();
            SaveToDiskLocked();
        }
    }

    public bool HasAppliedSnapshot()
    {
        lock (_syncRoot)
        {
            return _appliedStates.Count > 0 || _lastAppliedUtc.HasValue;
        }
    }

    public string? GetAppliedPresetId()
    {
        lock (_syncRoot)
        {
            return _appliedPresetId;
        }
    }

    public bool AppliedStatesMatch(IReadOnlyDictionary<string, bool> targetStates)
    {
        if (targetStates is null)
        {
            return false;
        }

        lock (_syncRoot)
        {
            if (_appliedStates.Count == 0)
            {
                return false;
            }

            foreach (var pair in targetStates)
            {
                if (!_appliedStates.TryGetValue(pair.Key, out var appliedState) || appliedState != pair.Value)
                {
                    return false;
                }
            }

            return true;
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

            if (model.AppliedStates is not null)
            {
                _appliedStates = new Dictionary<string, bool>(model.AppliedStates, StringComparer.OrdinalIgnoreCase);
            }

            if (model.AppliedCustomValues is not null)
            {
                _appliedCustomValues = new Dictionary<string, string>(model.AppliedCustomValues, StringComparer.OrdinalIgnoreCase);
            }

            if (model.LastAppliedUtc.HasValue)
            {
                _lastAppliedUtc = model.LastAppliedUtc.Value;
            }

            if (!string.IsNullOrWhiteSpace(model.AppliedPresetId))
            {
                _appliedPresetId = model.AppliedPresetId;
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
            _appliedStates = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            _appliedCustomValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _lastAppliedUtc = null;
            _appliedPresetId = null;
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
                AppliedStates = new Dictionary<string, bool>(_appliedStates, StringComparer.OrdinalIgnoreCase),
                AppliedCustomValues = new Dictionary<string, string>(_appliedCustomValues, StringComparer.OrdinalIgnoreCase),
                LastAppliedUtc = _lastAppliedUtc,
                AppliedPresetId = _appliedPresetId,
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

        public Dictionary<string, bool>? AppliedStates { get; set; }

        public Dictionary<string, string>? AppliedCustomValues { get; set; }

        public DateTimeOffset? LastAppliedUtc { get; set; }

        public string? AppliedPresetId { get; set; }

        public string? SelectedPresetId { get; set; }
    }
}

public readonly record struct RegistryAppliedState(string TweakId, bool State, string? CustomValue);

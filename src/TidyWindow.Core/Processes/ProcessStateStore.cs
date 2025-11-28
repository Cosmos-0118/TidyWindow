using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TidyWindow.Core.Processes;

/// <summary>
/// Persists UI for Processes questionnaire answers, preferences, and detection history.
/// </summary>
public sealed class ProcessStateStore
{
    private const string StateOverrideEnvironmentVariable = "TIDYWINDOW_PROCESS_STATE_PATH";
    private const string DefaultFileName = "uiforprocesses-state.json";
    internal const int LatestSchemaVersion = 1;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly object _syncRoot = new();
    private readonly string _filePath;
    private ProcessStateSnapshot _snapshot;

    public ProcessStateStore()
        : this(ResolveDefaultPath())
    {
    }

    internal ProcessStateStore(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("Value cannot be null or whitespace.", nameof(filePath));
        }

        _filePath = filePath;
        _snapshot = LoadFromDisk();
    }

    public ProcessStateSnapshot GetSnapshot()
    {
        lock (_syncRoot)
        {
            return _snapshot;
        }
    }

    public IReadOnlyCollection<ProcessPreference> GetPreferences()
    {
        lock (_syncRoot)
        {
            return _snapshot.Preferences.Values.ToArray();
        }
    }

    public bool TryGetPreference(string processIdentifier, out ProcessPreference preference)
    {
        var key = ProcessPreference.NormalizeProcessIdentifier(processIdentifier);
        lock (_syncRoot)
        {
            return _snapshot.Preferences.TryGetValue(key, out preference!);
        }
    }

    public void UpsertPreference(ProcessPreference preference)
    {
        if (preference is null)
        {
            throw new ArgumentNullException(nameof(preference));
        }

        lock (_syncRoot)
        {
            var updatedPreferences = _snapshot.Preferences.SetItem(preference.ProcessIdentifier, preference);
            _snapshot = _snapshot with
            {
                Preferences = updatedPreferences,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };

            SaveSnapshotLocked();
        }
    }

    public bool RemovePreference(string processIdentifier)
    {
        if (string.IsNullOrWhiteSpace(processIdentifier))
        {
            return false;
        }

        var key = ProcessPreference.NormalizeProcessIdentifier(processIdentifier);
        lock (_syncRoot)
        {
            if (!_snapshot.Preferences.ContainsKey(key))
            {
                return false;
            }

            var updatedPreferences = _snapshot.Preferences.Remove(key);
            _snapshot = _snapshot with
            {
                Preferences = updatedPreferences,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };

            SaveSnapshotLocked();
            return true;
        }
    }

    public IReadOnlyCollection<SuspiciousProcessHit> GetSuspiciousHits()
    {
        lock (_syncRoot)
        {
            return _snapshot.SuspiciousHits.Values
                .OrderByDescending(static hit => hit.ObservedAtUtc)
                .ToArray();
        }
    }

    public void RecordSuspiciousHit(SuspiciousProcessHit hit)
    {
        if (hit is null)
        {
            throw new ArgumentNullException(nameof(hit));
        }

        var normalizedId = SuspiciousProcessHit.NormalizeId(hit.Id);
        var normalizedHit = hit with
        {
            Id = normalizedId,
            ObservedAtUtc = hit.ObservedAtUtc == default ? DateTimeOffset.UtcNow : hit.ObservedAtUtc
        };

        lock (_syncRoot)
        {
            var updatedHits = _snapshot.SuspiciousHits.SetItem(normalizedId, normalizedHit);
            _snapshot = _snapshot with
            {
                SuspiciousHits = updatedHits,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };

            SaveSnapshotLocked();
        }
    }

    public bool RemoveSuspiciousHit(string hitId)
    {
        if (string.IsNullOrWhiteSpace(hitId))
        {
            return false;
        }

        var key = SuspiciousProcessHit.NormalizeId(hitId);
        lock (_syncRoot)
        {
            if (!_snapshot.SuspiciousHits.ContainsKey(key))
            {
                return false;
            }

            var updatedHits = _snapshot.SuspiciousHits.Remove(key);
            _snapshot = _snapshot with
            {
                SuspiciousHits = updatedHits,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };

            SaveSnapshotLocked();
            return true;
        }
    }

    private ProcessStateSnapshot LoadFromDisk()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return ProcessStateSnapshot.CreateEmpty(LatestSchemaVersion);
            }

            using var stream = File.OpenRead(_filePath);
            var model = JsonSerializer.Deserialize<ProcessStateModel>(stream, SerializerOptions);
            if (model is null)
            {
                return ProcessStateSnapshot.CreateEmpty(LatestSchemaVersion);
            }

            var schemaVersion = model.SchemaVersion <= 0 ? 1 : model.SchemaVersion;
            if (schemaVersion > LatestSchemaVersion)
            {
                schemaVersion = LatestSchemaVersion;
            }
            else if (schemaVersion < LatestSchemaVersion)
            {
                model = UpgradeModel(model);
                schemaVersion = LatestSchemaVersion;
            }

            var preferences = model.Preferences is null
                ? ImmutableDictionary.Create<string, ProcessPreference>(StringComparer.OrdinalIgnoreCase)
                : model.Preferences
                    .Select(ToProcessPreference)
                    .Where(static pref => pref is not null)
                    .Cast<ProcessPreference>()
                    .ToImmutableDictionary(static pref => pref.ProcessIdentifier, StringComparer.OrdinalIgnoreCase);

            var hits = model.SuspiciousHits is null
                ? ImmutableDictionary.Create<string, SuspiciousProcessHit>(StringComparer.OrdinalIgnoreCase)
                : model.SuspiciousHits
                    .Select(ToSuspiciousProcessHit)
                    .Where(static hit => hit is not null)
                    .Cast<SuspiciousProcessHit>()
                    .ToImmutableDictionary(static hit => hit.Id, StringComparer.OrdinalIgnoreCase);

            var updatedAt = model.UpdatedAtUtc == default ? DateTimeOffset.UtcNow : model.UpdatedAtUtc;
            return new ProcessStateSnapshot(schemaVersion, updatedAt, preferences, hits);
        }
        catch
        {
            return ProcessStateSnapshot.CreateEmpty(LatestSchemaVersion);
        }
    }

    private void SaveSnapshotLocked()
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempFile = _filePath + ".tmp";
        var model = ProcessStateModel.FromSnapshot(_snapshot);

        using (var stream = File.Create(tempFile))
        {
            JsonSerializer.Serialize(stream, model, SerializerOptions);
        }

        File.Copy(tempFile, _filePath, overwrite: true);
        File.Delete(tempFile);
    }

    private static ProcessPreference? ToProcessPreference(ProcessPreferenceModel? model)
    {
        if (model is null || string.IsNullOrWhiteSpace(model.ProcessId))
        {
            return null;
        }

        var updatedAt = model.UpdatedAtUtc == default ? DateTimeOffset.UtcNow : model.UpdatedAtUtc;
        return new ProcessPreference(model.ProcessId, model.Action, model.Source, updatedAt, model.Notes);
    }

    private static SuspiciousProcessHit? ToSuspiciousProcessHit(SuspiciousProcessHitModel? model)
    {
        if (model is null || string.IsNullOrWhiteSpace(model.Id) || string.IsNullOrWhiteSpace(model.ProcessName) || string.IsNullOrWhiteSpace(model.FilePath))
        {
            return null;
        }

        var observedAt = model.ObservedAtUtc == default ? DateTimeOffset.UtcNow : model.ObservedAtUtc;
        return new SuspiciousProcessHit(
            model.Id,
            model.ProcessName,
            model.FilePath,
            model.Level,
            model.MatchedRules,
            observedAt,
            model.Hash,
            model.Source,
            model.Notes);
    }

    private static ProcessStateModel UpgradeModel(ProcessStateModel model)
    {
        model.SchemaVersion = LatestSchemaVersion;
        return model;
    }

    private static string ResolveDefaultPath()
    {
        var overridePath = Environment.GetEnvironmentVariable(StateOverrideEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return overridePath;
        }

        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var defaultDirectory = string.IsNullOrWhiteSpace(roaming)
            ? Path.GetTempPath()
            : Path.Combine(roaming, "TidyWindow");

        return Path.Combine(defaultDirectory, DefaultFileName);
    }

    private sealed class ProcessStateModel
    {
        public int SchemaVersion { get; set; } = LatestSchemaVersion;

        public DateTimeOffset UpdatedAtUtc { get; set; }

        public List<ProcessPreferenceModel>? Preferences { get; set; }

        public List<SuspiciousProcessHitModel>? SuspiciousHits { get; set; }

        public static ProcessStateModel FromSnapshot(ProcessStateSnapshot snapshot)
        {
            return new ProcessStateModel
            {
                SchemaVersion = snapshot.SchemaVersion,
                UpdatedAtUtc = snapshot.UpdatedAtUtc,
                Preferences = snapshot.Preferences.Values
                    .Select(static pref => new ProcessPreferenceModel
                    {
                        ProcessId = pref.ProcessIdentifier,
                        Action = pref.Action,
                        Source = pref.Source,
                        UpdatedAtUtc = pref.UpdatedAtUtc,
                        Notes = pref.Notes
                    })
                    .ToList(),
                SuspiciousHits = snapshot.SuspiciousHits.Values
                    .Select(static hit => new SuspiciousProcessHitModel
                    {
                        Id = hit.Id,
                        ProcessName = hit.ProcessName,
                        FilePath = hit.FilePath,
                        Level = hit.Level,
                        MatchedRules = hit.MatchedRules?.ToList(),
                        ObservedAtUtc = hit.ObservedAtUtc,
                        Hash = hit.Hash,
                        Source = hit.Source,
                        Notes = hit.Notes
                    })
                    .ToList()
            };
        }
    }

    private sealed class ProcessPreferenceModel
    {
        public string? ProcessId { get; set; }

        public ProcessActionPreference Action { get; set; }

        public ProcessPreferenceSource Source { get; set; }

        public DateTimeOffset UpdatedAtUtc { get; set; }

        public string? Notes { get; set; }
    }

    private sealed class SuspiciousProcessHitModel
    {
        public string? Id { get; set; }

        public string? ProcessName { get; set; }

        public string? FilePath { get; set; }

        public SuspicionLevel Level { get; set; }

        public List<string>? MatchedRules { get; set; }

        public DateTimeOffset ObservedAtUtc { get; set; }

        public string? Hash { get; set; }

        public string? Source { get; set; }

        public string? Notes { get; set; }
    }
}

/// <summary>
/// Aggregate snapshot representing persisted UI state for processes.
/// </summary>
public sealed record ProcessStateSnapshot(
    int SchemaVersion,
    DateTimeOffset UpdatedAtUtc,
    IImmutableDictionary<string, ProcessPreference> Preferences,
    IImmutableDictionary<string, SuspiciousProcessHit> SuspiciousHits)
{
    public static ProcessStateSnapshot CreateEmpty(int schemaVersion)
    {
        return new ProcessStateSnapshot(
            schemaVersion,
            DateTimeOffset.MinValue,
            ImmutableDictionary.Create<string, ProcessPreference>(StringComparer.OrdinalIgnoreCase),
            ImmutableDictionary.Create<string, SuspiciousProcessHit>(StringComparer.OrdinalIgnoreCase));
    }
}

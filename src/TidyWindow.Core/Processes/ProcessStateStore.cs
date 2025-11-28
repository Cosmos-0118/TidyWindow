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
    internal const int LatestSchemaVersion = 3;

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

    public IReadOnlyCollection<AntiSystemWhitelistEntry> GetWhitelistEntries()
    {
        lock (_syncRoot)
        {
            return _snapshot.WhitelistEntries.Values.ToArray();
        }
    }

    public void UpsertWhitelistEntry(AntiSystemWhitelistEntry entry)
    {
        if (entry is null)
        {
            throw new ArgumentNullException(nameof(entry));
        }

        var normalized = entry.Normalize();

        lock (_syncRoot)
        {
            var updated = _snapshot.WhitelistEntries.SetItem(normalized.Id, normalized);
            _snapshot = _snapshot with
            {
                WhitelistEntries = updated,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };

            SaveSnapshotLocked();
        }
    }

    public bool RemoveWhitelistEntry(string entryId)
    {
        if (string.IsNullOrWhiteSpace(entryId))
        {
            return false;
        }

        var key = entryId.Trim();
        lock (_syncRoot)
        {
            if (!_snapshot.WhitelistEntries.ContainsKey(key))
            {
                return false;
            }

            var updated = _snapshot.WhitelistEntries.Remove(key);
            _snapshot = _snapshot with
            {
                WhitelistEntries = updated,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };

            SaveSnapshotLocked();
            return true;
        }
    }

    public bool TryMatchWhitelist(string? filePath, string? sha256, string? processName, out AntiSystemWhitelistEntry? entry)
    {
        lock (_syncRoot)
        {
            foreach (var candidate in _snapshot.WhitelistEntries.Values)
            {
                if (candidate.Matches(filePath, sha256, processName))
                {
                    entry = candidate;
                    return true;
                }
            }
        }

        entry = null;
        return false;
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

    public ProcessQuestionnaireSnapshot GetQuestionnaireSnapshot()
    {
        lock (_syncRoot)
        {
            return _snapshot.Questionnaire;
        }
    }

    public void SaveQuestionnaireSnapshot(ProcessQuestionnaireSnapshot snapshot)
    {
        if (snapshot is null)
        {
            throw new ArgumentNullException(nameof(snapshot));
        }

        lock (_syncRoot)
        {
            _snapshot = _snapshot with
            {
                Questionnaire = snapshot,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };

            SaveSnapshotLocked();
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

            var whitelistEntries = model.WhitelistEntries is null
                ? ImmutableDictionary.Create<string, AntiSystemWhitelistEntry>(StringComparer.OrdinalIgnoreCase)
                : model.WhitelistEntries
                    .Select(ToWhitelistEntry)
                    .Where(static entry => entry is not null)
                    .Cast<AntiSystemWhitelistEntry>()
                    .ToImmutableDictionary(static entry => entry.Id, StringComparer.OrdinalIgnoreCase);

            var questionnaire = ToQuestionnaireSnapshot(model.Questionnaire);

            var updatedAt = model.UpdatedAtUtc == default ? DateTimeOffset.UtcNow : model.UpdatedAtUtc;
            return new ProcessStateSnapshot(schemaVersion, updatedAt, preferences, hits, questionnaire, whitelistEntries);
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

    private static AntiSystemWhitelistEntry? ToWhitelistEntry(AntiSystemWhitelistEntryModel? model)
    {
        if (model is null || string.IsNullOrWhiteSpace(model.Value))
        {
            return null;
        }

        try
        {
            return new AntiSystemWhitelistEntry(
                model.Id ?? string.Empty,
                model.Kind,
                model.Value,
                model.Notes,
                model.AddedBy,
                model.AddedAtUtc);
        }
        catch
        {
            return null;
        }
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

    private static ProcessQuestionnaireSnapshot ToQuestionnaireSnapshot(ProcessQuestionnaireModel? model)
    {
        if (model is null)
        {
            return ProcessQuestionnaireSnapshot.Empty;
        }

        var answers = model.Answers is null
            ? ImmutableDictionary.Create<string, string>(StringComparer.OrdinalIgnoreCase)
            : model.Answers
                .Where(static pair => !string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
                .ToImmutableDictionary(
                    static pair => pair.Key.Trim().ToLowerInvariant(),
                    static pair => pair.Value.Trim().ToLowerInvariant(),
                    StringComparer.OrdinalIgnoreCase);

        var processes = model.AutoStopProcessIds is null
            ? ImmutableHashSet.Create<string>(StringComparer.OrdinalIgnoreCase)
            : model.AutoStopProcessIds
                .Where(static id => !string.IsNullOrWhiteSpace(id))
                .Select(ProcessCatalogEntry.NormalizeIdentifier)
                .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);

        return new ProcessQuestionnaireSnapshot(model.CompletedAtUtc, answers, processes);
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

        public List<AntiSystemWhitelistEntryModel>? WhitelistEntries { get; set; }

        public ProcessQuestionnaireModel? Questionnaire { get; set; }

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
                WhitelistEntries = snapshot.WhitelistEntries.Values
                    .Select(static entry => new AntiSystemWhitelistEntryModel
                    {
                        Id = entry.Id,
                        Kind = entry.Kind,
                        Value = entry.Value,
                        Notes = entry.Notes,
                        AddedBy = entry.AddedBy,
                        AddedAtUtc = entry.AddedAtUtc
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
                    .ToList(),
                Questionnaire = ProcessQuestionnaireModel.FromSnapshot(snapshot.Questionnaire)
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

    private sealed class AntiSystemWhitelistEntryModel
    {
        public string? Id { get; set; }

        public AntiSystemWhitelistEntryKind Kind { get; set; }

        public string? Value { get; set; }

        public string? Notes { get; set; }

        public string? AddedBy { get; set; }

        public DateTimeOffset AddedAtUtc { get; set; }
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

    private sealed class ProcessQuestionnaireModel
    {
        public DateTimeOffset? CompletedAtUtc { get; set; }

        public Dictionary<string, string>? Answers { get; set; }

        public List<string>? AutoStopProcessIds { get; set; }

        public static ProcessQuestionnaireModel FromSnapshot(ProcessQuestionnaireSnapshot snapshot)
        {
            return new ProcessQuestionnaireModel
            {
                CompletedAtUtc = snapshot.CompletedAtUtc,
                Answers = snapshot.Answers.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.OrdinalIgnoreCase),
                AutoStopProcessIds = snapshot.AutoStopProcessIds.ToList()
            };
        }
    }
}

/// <summary>
/// Aggregate snapshot representing persisted UI state for processes.
/// </summary>
public sealed record ProcessStateSnapshot(
    int SchemaVersion,
    DateTimeOffset UpdatedAtUtc,
    IImmutableDictionary<string, ProcessPreference> Preferences,
    IImmutableDictionary<string, SuspiciousProcessHit> SuspiciousHits,
    ProcessQuestionnaireSnapshot Questionnaire,
    IImmutableDictionary<string, AntiSystemWhitelistEntry> WhitelistEntries)
{
    public static ProcessStateSnapshot CreateEmpty(int schemaVersion)
    {
        return new ProcessStateSnapshot(
            schemaVersion,
            DateTimeOffset.MinValue,
            ImmutableDictionary.Create<string, ProcessPreference>(StringComparer.OrdinalIgnoreCase),
            ImmutableDictionary.Create<string, SuspiciousProcessHit>(StringComparer.OrdinalIgnoreCase),
            ProcessQuestionnaireSnapshot.Empty,
            ImmutableDictionary.Create<string, AntiSystemWhitelistEntry>(StringComparer.OrdinalIgnoreCase));
    }
}

/// <summary>
/// Stores questionnaire answers and derived recommendations.
/// </summary>
public sealed record ProcessQuestionnaireSnapshot(
    DateTimeOffset? CompletedAtUtc,
    IImmutableDictionary<string, string> Answers,
    IImmutableSet<string> AutoStopProcessIds)
{
    public static ProcessQuestionnaireSnapshot Empty { get; } = new(
        null,
        ImmutableDictionary.Create<string, string>(StringComparer.OrdinalIgnoreCase),
        ImmutableHashSet.Create<string>(StringComparer.OrdinalIgnoreCase));
}

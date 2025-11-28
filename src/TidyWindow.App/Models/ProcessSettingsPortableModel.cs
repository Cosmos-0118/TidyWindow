using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json.Serialization;
using TidyWindow.Core.Processes;

namespace TidyWindow.App.Models;

internal sealed class ProcessSettingsPortableModel
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; }

    [JsonPropertyName("exportedAtUtc")]
    public DateTimeOffset ExportedAtUtc { get; set; }

    [JsonPropertyName("preferences")]
    public List<PortablePreferenceModel> Preferences { get; set; } = new();

    [JsonPropertyName("questionnaire")]
    public PortableQuestionnaireModel? Questionnaire { get; set; }

    public static ProcessSettingsPortableModel FromSnapshot(ProcessStateSnapshot snapshot)
    {
        if (snapshot is null)
        {
            throw new ArgumentNullException(nameof(snapshot));
        }

        return new ProcessSettingsPortableModel
        {
            SchemaVersion = snapshot.SchemaVersion,
            ExportedAtUtc = DateTimeOffset.UtcNow,
            Preferences = snapshot.Preferences.Values
                .Select(pref => new PortablePreferenceModel
                {
                    ProcessIdentifier = pref.ProcessIdentifier,
                    Action = pref.Action,
                    Source = pref.Source,
                    UpdatedAtUtc = pref.UpdatedAtUtc,
                    Notes = pref.Notes
                })
                .ToList(),
            Questionnaire = PortableQuestionnaireModel.FromSnapshot(snapshot.Questionnaire)
        };
    }

    public IReadOnlyCollection<ProcessPreference> ToPreferences()
    {
        if (Preferences.Count == 0)
        {
            return Array.Empty<ProcessPreference>();
        }

        var list = new List<ProcessPreference>(Preferences.Count);
        foreach (var model in Preferences)
        {
            if (string.IsNullOrWhiteSpace(model.ProcessIdentifier))
            {
                continue;
            }

            try
            {
                var preference = new ProcessPreference(
                    ProcessCatalogEntry.NormalizeIdentifier(model.ProcessIdentifier),
                    model.Action,
                    model.Source,
                    model.UpdatedAtUtc == default ? DateTimeOffset.UtcNow : model.UpdatedAtUtc,
                    model.Notes);
                list.Add(preference);
            }
            catch
            {
                // Skip invalid entries silently to keep import resilient.
            }
        }

        return list;
    }

    public ProcessQuestionnaireSnapshot ToQuestionnaireSnapshot()
    {
        if (Questionnaire is null)
        {
            return ProcessQuestionnaireSnapshot.Empty;
        }

        var answers = Questionnaire.Answers is null
            ? ImmutableDictionary.Create<string, string>(StringComparer.OrdinalIgnoreCase)
            : Questionnaire.Answers
                .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
                .Select(pair => new KeyValuePair<string, string>(
                    ProcessCatalogEntry.NormalizeIdentifier(pair.Key),
                    pair.Value.Trim().ToLowerInvariant()))
                .ToImmutableDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.OrdinalIgnoreCase);

        var processIds = Questionnaire.AutoStopProcessIds is null
            ? ImmutableHashSet.Create<string>(StringComparer.OrdinalIgnoreCase)
            : Questionnaire.AutoStopProcessIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(ProcessCatalogEntry.NormalizeIdentifier)
                .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);

        return new ProcessQuestionnaireSnapshot(Questionnaire.CompletedAtUtc, answers, processIds);
    }

    internal sealed class PortablePreferenceModel
    {
        [JsonPropertyName("id")]
        public string? ProcessIdentifier { get; set; }

        [JsonPropertyName("action")]
        public ProcessActionPreference Action { get; set; }

        [JsonPropertyName("source")]
        public ProcessPreferenceSource Source { get; set; }

        [JsonPropertyName("updatedAtUtc")]
        public DateTimeOffset UpdatedAtUtc { get; set; }

        [JsonPropertyName("notes")]
        public string? Notes { get; set; }
    }

    internal sealed class PortableQuestionnaireModel
    {
        [JsonPropertyName("completedAtUtc")]
        public DateTimeOffset? CompletedAtUtc { get; set; }

        [JsonPropertyName("answers")]
        public Dictionary<string, string>? Answers { get; set; }

        [JsonPropertyName("autoStopProcessIds")]
        public List<string>? AutoStopProcessIds { get; set; }

        public static PortableQuestionnaireModel FromSnapshot(ProcessQuestionnaireSnapshot snapshot)
        {
            return new PortableQuestionnaireModel
            {
                CompletedAtUtc = snapshot.CompletedAtUtc,
                Answers = snapshot.Answers.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.OrdinalIgnoreCase),
                AutoStopProcessIds = snapshot.AutoStopProcessIds.ToList()
            };
        }
    }
}

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace TidyWindow.Core.Processes;

/// <summary>
/// Evaluates questionnaire answers and synchronizes derived preferences.
/// </summary>
public sealed class ProcessQuestionnaireEngine
{
    private static readonly ProcessQuestionnaireDefinition Definition;
    private static readonly QuestionnaireRule[] Rules;

    private readonly ProcessCatalogParser _catalogParser;
    private readonly ProcessStateStore _stateStore;
    private readonly Lazy<ProcessCatalogSnapshot> _catalogSnapshot;

    static ProcessQuestionnaireEngine()
    {
        Definition = BuildDefinition();
        Rules = BuildRules();
    }

    public ProcessQuestionnaireEngine(ProcessCatalogParser catalogParser, ProcessStateStore stateStore)
    {
        _catalogParser = catalogParser ?? throw new ArgumentNullException(nameof(catalogParser));
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _catalogSnapshot = new Lazy<ProcessCatalogSnapshot>(_catalogParser.LoadSnapshot, isThreadSafe: true);
    }

    public ProcessQuestionnaireDefinition GetDefinition() => Definition;

    public ProcessQuestionnaireSnapshot GetSnapshot() => _stateStore.GetQuestionnaireSnapshot();

    public ProcessQuestionnaireResult EvaluateAndApply(IDictionary<string, string> answers)
    {
        var normalizedAnswers = NormalizeAnswers(answers);
        ValidateAnswers(normalizedAnswers);

        var recommendedIds = DeriveProcessIdentifiers(normalizedAnswers);
        var questionnaireSnapshot = new ProcessQuestionnaireSnapshot(
            DateTimeOffset.UtcNow,
            normalizedAnswers.ToImmutableDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase),
            recommendedIds.ToImmutableHashSet(StringComparer.OrdinalIgnoreCase));

        var appliedPreferences = SynchronizePreferences(recommendedIds);
        _stateStore.SaveQuestionnaireSnapshot(questionnaireSnapshot);

        return new ProcessQuestionnaireResult(questionnaireSnapshot, recommendedIds, appliedPreferences);
    }

    private static Dictionary<string, string> NormalizeAnswers(IDictionary<string, string> answers)
    {
        if (answers is null)
        {
            throw new ArgumentNullException(nameof(answers));
        }

        var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in answers)
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value))
            {
                continue;
            }

            normalized[ProcessCatalogEntry.NormalizeIdentifier(pair.Key)] = pair.Value.Trim().ToLowerInvariant();
        }

        return normalized;
    }

    private static void ValidateAnswers(IReadOnlyDictionary<string, string> answers)
    {
        var missing = Definition.Questions
            .Where(question => question.Required && !answers.ContainsKey(question.Id))
            .Select(question => question.Id)
            .ToArray();

        if (missing.Length > 0)
        {
            throw new InvalidOperationException($"Missing answers for: {string.Join(", ", missing)}");
        }
    }

    private IReadOnlyCollection<string> DeriveProcessIdentifiers(IReadOnlyDictionary<string, string> answers)
    {
        var selectedRules = Rules
            .Where(rule => answers.TryGetValue(rule.QuestionId, out var optionId) && string.Equals(optionId, rule.OptionId, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (selectedRules.Length == 0)
        {
            return Array.Empty<string>();
        }

        var categoryKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var explicitProcessIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rule in selectedRules)
        {
            foreach (var category in rule.CategoryKeys)
            {
                categoryKeys.Add(category);
            }

            foreach (var processId in rule.ProcessIdentifiers)
            {
                explicitProcessIds.Add(processId);
            }
        }

        var identifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var snapshot = _catalogSnapshot.Value;

        if (categoryKeys.Count > 0)
        {
            foreach (var entry in snapshot.Entries)
            {
                if (!categoryKeys.Contains(entry.CategoryKey))
                {
                    continue;
                }

                if (entry.RecommendedAction != ProcessActionPreference.AutoStop)
                {
                    continue;
                }

                identifiers.Add(entry.Identifier);
            }
        }

        if (explicitProcessIds.Count > 0)
        {
            var catalogLookup = snapshot.Entries.ToDictionary(entry => entry.Identifier, StringComparer.OrdinalIgnoreCase);
            foreach (var explicitId in explicitProcessIds)
            {
                var normalized = ProcessCatalogEntry.NormalizeIdentifier(explicitId);
                if (catalogLookup.ContainsKey(normalized))
                {
                    identifiers.Add(normalized);
                }
            }
        }

        return identifiers.ToArray();
    }

    private IReadOnlyCollection<ProcessPreference> SynchronizePreferences(IReadOnlyCollection<string> recommendedProcessIds)
    {
        var applied = new List<ProcessPreference>();
        var now = DateTimeOffset.UtcNow;

        var recommendedSet = recommendedProcessIds.ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
        var existingPreferences = _stateStore.GetPreferences();

        foreach (var preference in existingPreferences)
        {
            if (preference.Source != ProcessPreferenceSource.Questionnaire)
            {
                continue;
            }

            if (!recommendedSet.Contains(preference.ProcessIdentifier))
            {
                _stateStore.RemovePreference(preference.ProcessIdentifier);
            }
        }

        foreach (var processId in recommendedSet)
        {
            if (_stateStore.TryGetPreference(processId, out var existing) && existing is not null)
            {
                if (existing.Source == ProcessPreferenceSource.UserOverride)
                {
                    continue;
                }

                if (existing.Source == ProcessPreferenceSource.Questionnaire && existing.Action == ProcessActionPreference.AutoStop)
                {
                    applied.Add(existing);
                    continue;
                }
            }

            var preference = new ProcessPreference(
                processId,
                ProcessActionPreference.AutoStop,
                ProcessPreferenceSource.Questionnaire,
                now,
                "Derived from questionnaire responses");

            _stateStore.UpsertPreference(preference);
            applied.Add(preference);
        }

        return applied;
    }

    private static ProcessQuestionnaireDefinition BuildDefinition()
    {
        var yesOption = new ProcessQuestionOption("yes", "Yes", null);
        var noOption = new ProcessQuestionOption("no", "No", null);

        var questions = new List<ProcessQuestion>
        {
            new("usage.gaming", "Gaming features", "Do you actively use Xbox Game Bar or Microsoft Store games on this PC?", new[] { yesOption, noOption }),
            new("usage.vr", "Mixed Reality", "Do you use any Mixed Reality or VR headsets on this device?", new[] { yesOption, noOption }),
            new("usage.printer", "Printing & fax", "Do you need local printers or fax devices connected to this PC?", new[] { yesOption, noOption }),
            new("usage.phone", "Phone Link", "Do you rely on Phone Link notifications or cross-device experiences?", new[] { yesOption, noOption }),
            new("usage.location", "Location services", "Do you need location/Maps functionality on this PC?", new[] { yesOption, noOption }),
            new("device.touch", "Touch & pen input", "Is this a touchscreen or pen-enabled device?", new[] { yesOption, noOption }),
            new("usage.telemetry", "Diagnostics & telemetry", "Choose how aggressively TidyWindow should disable diagnostics/telemetry services.", new[]
            {
                new ProcessQuestionOption("standard", "Standard", "Disable only obvious optional diagnostics."),
                new ProcessQuestionOption("balanced", "Balanced", "Disable telemetry + error reporting services."),
                new ProcessQuestionOption("aggressive", "Aggressive", "Also disable networking helpers used for telemetry."),
            }),
            new("usage.performance", "Performance helpers", "Do you want to disable background caching/prefetching helpers (SysMain)?", new[]
            {
                new ProcessQuestionOption("standard", "Keep defaults", "Leave performance helpers enabled."),
                new ProcessQuestionOption("aggressive", "Disable helpers", "Disable SysMain and similar services."),
            }),
        };

        return new ProcessQuestionnaireDefinition(questions);
    }

    private static QuestionnaireRule[] BuildRules()
    {
        return new[]
        {
            new QuestionnaireRule("usage.gaming", "no", new[] { "A" }, Array.Empty<string>()),
            new QuestionnaireRule("usage.vr", "no", new[] { "B" }, Array.Empty<string>()),
            new QuestionnaireRule("usage.printer", "no", new[] { "C" }, Array.Empty<string>()),
            new QuestionnaireRule("usage.phone", "no", new[] { "E" }, Array.Empty<string>()),
            new QuestionnaireRule("usage.location", "no", new[] { "F" }, Array.Empty<string>()),
            new QuestionnaireRule("device.touch", "no", new[] { "G" }, Array.Empty<string>()),
            new QuestionnaireRule("usage.performance", "aggressive", new[] { "I" }, new[] { "sysmain" }),
            new QuestionnaireRule("usage.telemetry", "balanced", Array.Empty<string>(), new[] { "diagtrack", "wersvc", "werfault" }),
            new QuestionnaireRule("usage.telemetry", "aggressive", Array.Empty<string>(), new[] { "diagtrack", "wersvc", "werfault", "bits", "iphlpsvc" })
        };
    }

    private sealed record QuestionnaireRule(string QuestionId, string OptionId, IReadOnlyList<string> CategoryKeys, IReadOnlyList<string> ProcessIdentifiers);
}

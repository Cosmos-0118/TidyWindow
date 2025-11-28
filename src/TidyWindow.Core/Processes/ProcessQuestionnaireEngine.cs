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
    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, QuestionnaireRule>> RuleLookup;

    private readonly ProcessCatalogParser _catalogParser;
    private readonly ProcessStateStore _stateStore;
    private readonly Lazy<ProcessCatalogSnapshot> _catalogSnapshot;

    static ProcessQuestionnaireEngine()
    {
        Definition = BuildDefinition();
        RuleLookup = BuildRules();
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

        var plan = BuildAutoStopPlan(normalizedAnswers);
        var questionnaireSnapshot = new ProcessQuestionnaireSnapshot(
            DateTimeOffset.UtcNow,
            normalizedAnswers.ToImmutableDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase),
            plan.ProcessIdentifiers.ToImmutableHashSet(StringComparer.OrdinalIgnoreCase));

        var appliedPreferences = SynchronizePreferences(plan.ProcessIdentifiers);
        _stateStore.SaveQuestionnaireSnapshot(questionnaireSnapshot);

        return new ProcessQuestionnaireResult(questionnaireSnapshot, plan.ProcessIdentifiers, appliedPreferences);
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

    private AutoStopPlan BuildAutoStopPlan(IReadOnlyDictionary<string, string> answers)
    {
        if (answers.Count == 0)
        {
            return AutoStopPlan.Empty;
        }

        var categoryKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var explicitProcessIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pair in answers)
        {
            if (!RuleLookup.TryGetValue(pair.Key, out var optionRules))
            {
                continue;
            }

            if (!optionRules.TryGetValue(pair.Value, out var rule))
            {
                continue;
            }

            foreach (var category in rule.CategoryKeys)
            {
                if (!string.IsNullOrWhiteSpace(category))
                {
                    categoryKeys.Add(category);
                }
            }

            foreach (var processId in rule.ProcessIdentifiers)
            {
                if (!string.IsNullOrWhiteSpace(processId))
                {
                    explicitProcessIds.Add(ProcessCatalogEntry.NormalizeIdentifier(processId));
                }
            }
        }

        if (categoryKeys.Count == 0 && explicitProcessIds.Count == 0)
        {
            return AutoStopPlan.Empty;
        }

        var identifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var snapshot = _catalogSnapshot.Value;

        if (categoryKeys.Count > 0)
        {
            foreach (var entry in snapshot.Entries)
            {
                if (categoryKeys.Contains(entry.CategoryKey) && entry.RecommendedAction == ProcessActionPreference.AutoStop)
                {
                    identifiers.Add(entry.Identifier);
                }
            }
        }

        if (explicitProcessIds.Count > 0)
        {
            var catalogLookup = snapshot.Entries.ToDictionary(entry => entry.Identifier, StringComparer.OrdinalIgnoreCase);
            foreach (var explicitId in explicitProcessIds)
            {
                if (catalogLookup.ContainsKey(explicitId))
                {
                    identifiers.Add(explicitId);
                }
            }
        }

        return new AutoStopPlan(identifiers.ToArray());
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
        var yesNoOptions = new[] { yesOption, noOption };

        var questions = new List<ProcessQuestion>
        {
            new("usage.gaming", "Gaming features", "Do you actively use Xbox Game Bar or Microsoft Store games on this PC?", yesNoOptions),
            new("usage.vr", "Mixed Reality", "Do you use any Mixed Reality or VR headsets on this device?", yesNoOptions),
            new("usage.printer", "Printing & fax", "Do you need local printers or fax devices connected to this PC?", yesNoOptions),
            new("usage.phone", "Phone Link & notifications", "Do you rely on Phone Link notifications or cross-device experiences?", yesNoOptions),
            new("usage.location", "Location & Maps", "Do you need location/Maps functionality on this PC?", yesNoOptions),
            new("device.touch", "Touch & pen input", "Is this a touchscreen or pen-enabled device?", yesNoOptions),
            new("usage.developer", "Developer helpers", "Do you rely on Remote Registry, Diagnostics Hub, or other developer diagnostics services on this PC?", yesNoOptions),
            new("usage.telemetrycore", "Telemetry & diagnostics", "Do you want Windows telemetry/error reporting/messaging services to stay enabled?", yesNoOptions),
            new("usage.telemetryadvanced", "BITS / IP Helper", "Do you rely on Background Intelligent Transfer Service (BITS) or IP Helper/IPv6 networking features?", yesNoOptions),
            new("usage.performance", "Background caching (SysMain)", "Do you want Windows caching/prefetching helpers like SysMain to stay enabled for faster launches?", yesNoOptions),
            new("usage.misc", "Legacy Xbox / Phone helpers", "Do you need legacy GameInput/Xbox helper processes or the Windows Phone Service?", yesNoOptions),
            new("usage.store", "Store / OneDrive / Bluetooth / Remote Desktop", "Do you actively use Windows Store apps, Bluetooth accessories, OneDrive sync, or Remote Desktop?", yesNoOptions),
            new("usage.scheduledtasks", "Telemetry scheduled tasks", "Do you want Windows telemetry & diagnostics scheduled tasks (CEIP, DiskDiagnostic, etc.) to stay enabled?", yesNoOptions),
        };

        return new ProcessQuestionnaireDefinition(questions);
    }

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, QuestionnaireRule>> BuildRules()
    {
        var lookup = new Dictionary<string, Dictionary<string, QuestionnaireRule>>(StringComparer.OrdinalIgnoreCase);

        static void RegisterRule(
            Dictionary<string, Dictionary<string, QuestionnaireRule>> target,
            QuestionnaireRule rule)
        {
            if (!target.TryGetValue(rule.QuestionId, out var optionMap))
            {
                optionMap = new Dictionary<string, QuestionnaireRule>(StringComparer.OrdinalIgnoreCase);
                target[rule.QuestionId] = optionMap;
            }

            optionMap[rule.OptionId] = rule;
        }

        var rules = new[]
        {
            new QuestionnaireRule("usage.gaming", "no", new[] { "A" }, Array.Empty<string>()),
            new QuestionnaireRule("usage.vr", "no", new[] { "B" }, Array.Empty<string>()),
            new QuestionnaireRule("usage.printer", "no", new[] { "C" }, Array.Empty<string>()),
            new QuestionnaireRule("usage.phone", "no", new[] { "E" }, Array.Empty<string>()),
            new QuestionnaireRule("usage.location", "no", new[] { "F" }, Array.Empty<string>()),
            new QuestionnaireRule("device.touch", "no", new[] { "G" }, Array.Empty<string>()),
            new QuestionnaireRule("usage.developer", "no", new[] { "H" }, Array.Empty<string>()),
            new QuestionnaireRule("usage.telemetrycore", "no", new[] { "D", "K" }, Array.Empty<string>()),
            new QuestionnaireRule("usage.telemetryadvanced", "no", Array.Empty<string>(), new[] { "bits", "iphlpsvc" }),
            new QuestionnaireRule("usage.performance", "no", new[] { "I" }, new[] { "sysmain" }),
            new QuestionnaireRule("usage.misc", "no", new[] { "J" }, Array.Empty<string>()),
            new QuestionnaireRule("usage.store", "no", new[] { "L" }, Array.Empty<string>()),
            new QuestionnaireRule("usage.scheduledtasks", "no", new[] { "M" }, Array.Empty<string>())
        };

        foreach (var rule in rules)
        {
            RegisterRule(lookup, rule);
        }

        return lookup.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyDictionary<string, QuestionnaireRule>)pair.Value,
            StringComparer.OrdinalIgnoreCase);
    }

    private sealed record QuestionnaireRule(string QuestionId, string OptionId, IReadOnlyList<string> CategoryKeys, IReadOnlyList<string> ProcessIdentifiers);

    private sealed record AutoStopPlan(IReadOnlyCollection<string> ProcessIdentifiers)
    {
        public static AutoStopPlan Empty { get; } = new(Array.Empty<string>());
    }
}

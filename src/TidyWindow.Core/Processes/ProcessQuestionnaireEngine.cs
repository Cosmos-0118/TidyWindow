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
        var catalogLookup = _catalogSnapshot.Value.Entries.ToDictionary(entry => entry.Identifier, StringComparer.OrdinalIgnoreCase);

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

            catalogLookup.TryGetValue(processId, out var entry);

            var preference = new ProcessPreference(
                processId,
                ProcessActionPreference.AutoStop,
                ProcessPreferenceSource.Questionnaire,
                now,
                "Derived from questionnaire responses",
                entry?.ServiceIdentifier);

            _stateStore.UpsertPreference(preference);
            applied.Add(preference);
        }

        return applied;
    }

    private static ProcessQuestionnaireDefinition BuildDefinition()
    {
        var yesOption = new ProcessQuestionOption(
            "yes",
            "Yes, I use this",
            "Keep the related Windows helpers running.");
        var noOption = new ProcessQuestionOption(
            "no",
            "No, turn it down",
            "Pause or auto-stop the extra background services.");
        var yesNoOptions = new[] { yesOption, noOption };

        var questions = new List<ProcessQuestion>
        {
            new("usage.gaming", "Gaming & Game Bar", "Do you actually use Xbox Game Bar or play Microsoft Store games here? Choose no to silence Xbox background helpers when you do not.", yesNoOptions),
            new("usage.vr", "Mixed Reality / VR", "Do you plug in VR or Mixed Reality headsets on this PC?", yesNoOptions),
            new("usage.printer", "Printing & fax", "Do you print or fax from this PC? Pick yes if a printer or fax is connected.", yesNoOptions),
            new("usage.phone", "Phone Link & notifications", "Do you rely on Phone Link for calls, messaging, or notifications?", yesNoOptions),
            new("usage.location", "Location & Maps", "Do you want location and Maps apps to keep working on this device?", yesNoOptions),
            new("device.touch", "Touch & pen input", "Is this a touchscreen or pen-enabled device you tap or write on?", yesNoOptions),
            new("usage.developer", "Developer diagnostics", "Do you use Remote Registry, Diagnostics Hub, or similar developer diagnostics tools?", yesNoOptions),
            new("usage.telemetrycore", "Diagnostics & telemetry", "Do you want Windows diagnostics, Connected User Experiences, and error reporting to stay enabled?", yesNoOptions),
            new("usage.telemetryadvanced", "BITS / IP Helper", "Do you depend on Background Intelligent Transfer Service (BITS) downloads or IPv6/advanced networking features?", yesNoOptions),
            new("usage.performance", "Background caching (SysMain)", "Do you want Windows caching/prefetching helpers like SysMain to speed up app launches?", yesNoOptions),
            new("usage.edgeupdates", "Microsoft Edge background updates", "Should Microsoft Edge keep updating itself in the background automatically?", yesNoOptions),
            new("usage.cellular", "Cellular / Phone Service", "Does this PC use Phone Service or cellular data/SMS features?", yesNoOptions),
            new("usage.appreadiness", "Store app prep (AppReadiness)", "Do you regularly install Microsoft Store apps that need the App Readiness service to stay running?", yesNoOptions),
            new("usage.remotedesktop", "Remote Desktop & VPN host", "Do you host Remote Desktop sessions or use Routing and Remote Access/VPN on this device?", yesNoOptions),
            new("usage.cloudsync", "OneDrive / Mail / People sync", "Should OneDrive, Work Folders, Mail/Calendar/People, and account sync stay active?", yesNoOptions),
            new("usage.bluetooth", "Bluetooth accessories", "Do you pair Bluetooth audio, controllers, or pens with this PC?", yesNoOptions),
            new("usage.hotspot", "Mobile hotspot / sharing", "Do you share this PC's internet connection with other devices (Mobile Hotspot/ICS)?", yesNoOptions),
            new("usage.storeapps", "Microsoft Store apps", "Do you run Microsoft Store / UWP apps that need their platform services in the background?", yesNoOptions),
            new("usage.sharedexperience", "Nearby sharing & Wallet", "Do you use Nearby sharing, cross-device experiences, or Microsoft Wallet connectors?", yesNoOptions),
            new("usage.searchindexing", "Windows Search indexing", "Do you rely on Windows Search indexing for fast file and email results?", yesNoOptions),
            new("usage.deliveryoptimization", "Delivery Optimization sharing", "Do you want Delivery Optimization or peer sharing for updates to stay on?", yesNoOptions),
            new("usage.helloface", "Windows Hello Face", "Do you sign in with Windows Hello Face or other camera biometrics on this PC?", yesNoOptions),
            new("usage.scheduledtasks", "Telemetry & updater tasks", "Should Windows telemetry, Edge update, and support tasks stay scheduled?", yesNoOptions),
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
            new QuestionnaireRule("usage.developer", "no", new[] { "H" }, new[] { "p9rdrservice_*" }),
            new QuestionnaireRule("usage.telemetrycore", "no", new[] { "D", "K" }, Array.Empty<string>()),
            new QuestionnaireRule("usage.telemetryadvanced", "no", Array.Empty<string>(), new[] { "bits", "iphlpsvc" }),
            new QuestionnaireRule("usage.performance", "no", new[] { "I" }, new[] { "sysmain" }),
            new QuestionnaireRule("usage.edgeupdates", "no", Array.Empty<string>(), new[] { "edgeupdate", "edgeupdateservice" }),
            new QuestionnaireRule("usage.cellular", "no", Array.Empty<string>(), new[] { "phonesvc" }),
            new QuestionnaireRule("usage.appreadiness", "no", Array.Empty<string>(), new[] { "appreadiness" }),
            new QuestionnaireRule("usage.remotedesktop", "no", Array.Empty<string>(), new[] { "remoteaccess", "termservice", "umrdpservice" }),
            new QuestionnaireRule("usage.cloudsync", "no", Array.Empty<string>(), new[] { "onesyncsvc", @"\microsoft\office\onedrive standalone update task", "workfolderssvc" }),
            new QuestionnaireRule("usage.bluetooth", "no", Array.Empty<string>(), new[] { "bthserv", "btagservice", "bluetoothuserservice_*" }),
            new QuestionnaireRule("usage.hotspot", "no", Array.Empty<string>(), new[] { "icssvc", "sharedaccess" }),
            new QuestionnaireRule("usage.storeapps", "no", Array.Empty<string>(), new[] { "wsservice", "appxsvc", "staterepository" }),
            new QuestionnaireRule("usage.sharedexperience", "no", Array.Empty<string>(), new[] { "pimindexmaintenancesvc", "userdatasvc_*", "walletservice" }),
            new QuestionnaireRule("usage.searchindexing", "no", Array.Empty<string>(), new[] { "wsearch" }),
            new QuestionnaireRule("usage.deliveryoptimization", "no", Array.Empty<string>(), new[] { "dosvc" }),
            new QuestionnaireRule("usage.helloface", "no", Array.Empty<string>(), new[] { "facesvc" }),
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

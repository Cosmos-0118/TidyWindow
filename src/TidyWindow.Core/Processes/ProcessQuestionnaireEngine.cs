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
            "Yes, keep it running",
            "I use this feature — keep related services active.");
        var noOption = new ProcessQuestionOption(
            "no",
            "No, I don't need this",
            "I don't use this — stop background services to save resources.");
        var yesNoOptions = new[] { yesOption, noOption };

        var questions = new List<ProcessQuestion>
        {
            // Gaming
            new("usage.gaming", "🎮 Xbox & Game Bar",
                "Do you play Xbox/PC Game Pass games or use Game Bar for screenshots, recordings, or FPS overlay?\n\n" +
                "• Yes = Keep Xbox services, Game Bar, and game capture running\n" +
                "• No = Stop all Xbox/gaming background helpers (saves RAM & CPU)",
                yesNoOptions),

            // VR
            new("usage.vr", "🥽 Virtual Reality Headsets",
                "Do you own and use a VR headset (Oculus, Valve Index, Windows Mixed Reality, etc.) with this PC?\n\n" +
                "• Yes = Keep Mixed Reality Portal and VR drivers active\n" +
                "• No = Stop VR/spatial audio background services",
                yesNoOptions),

            // Printing
            new("usage.printer", "🖨️ Printers & Fax",
                "Is a printer, scanner, or fax machine connected to this computer (USB, Wi-Fi, or network)?\n\n" +
                "• Yes = Keep print spooler and related services running\n" +
                "• No = Stop printing services (you can re-enable anytime)",
                yesNoOptions),

            // Phone Link
            new("usage.phone", "📱 Phone Link & Mobile Sync",
                "Do you use the Phone Link app to sync your Android/iPhone for calls, texts, or notifications?\n\n" +
                "• Yes = Keep device sync and push notification services active\n" +
                "• No = Stop phone-related background services",
                yesNoOptions),

            // Location
            new("usage.location", "📍 Location & Maps",
                "Do you use apps that need your location (Maps, Weather, Find My Device, etc.)?\n\n" +
                "• Yes = Keep geolocation and Maps services running\n" +
                "• No = Disable location tracking (better privacy, less battery use)",
                yesNoOptions),

            // Touch/Tablet
            new("device.touch", "✋ Touchscreen & Stylus",
                "Is this a touchscreen device, tablet, or do you use a stylus/pen for input?\n\n" +
                "• Yes = Keep touch keyboard, handwriting, and tablet services\n" +
                "• No = Stop touch/ink services (not needed for mouse/keyboard only)",
                yesNoOptions),

            // Developer tools
            new("usage.developer", "🔧 Developer & Diagnostic Tools",
                "Are you a developer who uses Remote Registry, Windows Subsystem for Linux (WSL), or diagnostic tools?\n\n" +
                "• Yes = Keep developer helper services available\n" +
                "• No = Stop developer-focused services",
                yesNoOptions),

            // Core telemetry
            new("usage.telemetrycore", "📊 Microsoft Diagnostics & Telemetry",
                "Do you want to send diagnostic data to Microsoft for product improvement and troubleshooting?\n\n" +
                "• Yes = Keep telemetry, Connected User Experiences, and Error Reporting active\n" +
                "• No = Stop telemetry services (improves privacy, may use less bandwidth)",
                yesNoOptions),

            // BITS/IP Helper
            new("usage.telemetryadvanced", "🌐 Background Downloads (BITS) & IPv6",
                "Do you rely on BITS for downloads (used by Windows Update, some installers) or need IPv6/Teredo?\n\n" +
                "• Yes = Keep Background Intelligent Transfer Service and IP Helper running\n" +
                "• No = These services can be disabled if you only use basic networking",
                yesNoOptions),

            // Performance helpers
            new("usage.performance", "⚡ Performance Caching (SysMain/Superfetch)",
                "Do you want Windows to prefetch frequently-used apps into memory for faster launches?\n\n" +
                "• Yes = Keep SysMain (Superfetch) active for app launch optimization\n" +
                "• No = Disable SysMain (recommended if you have an SSD or limited RAM)",
                yesNoOptions),

            // Edge updates
            new("usage.edgeupdates", "🌍 Microsoft Edge Background Updates",
                "Should Microsoft Edge automatically update itself in the background?\n\n" +
                "• Yes = Keep Edge update services active for security patches\n" +
                "• No = Stop Edge updaters (you'll need to update Edge manually)",
                yesNoOptions),

            // Cellular
            new("usage.cellular", "📶 Cellular & Mobile Data",
                "Does this PC have a SIM card or cellular data connection (4G/5G modem)?\n\n" +
                "• Yes = Keep Phone Service and cellular connectivity active\n" +
                "• No = Stop cellular services (not needed for Wi-Fi/Ethernet only)",
                yesNoOptions),

            // App Readiness
            new("usage.appreadiness", "🏪 Microsoft Store App Preparation",
                "Do you frequently install or update apps from the Microsoft Store?\n\n" +
                "• Yes = Keep App Readiness service to speed up Store app installations\n" +
                "• No = Stop App Readiness (Store apps will still work, just prepare slower)",
                yesNoOptions),

            // Remote Desktop
            new("usage.remotedesktop", "🖥️ Remote Desktop & VPN Hosting",
                "Do you allow others to connect TO this computer via Remote Desktop, or host VPN connections?\n\n" +
                "• Yes = Keep Remote Desktop Services and Remote Access running\n" +
                "• No = Stop remote connection services (you can still connect TO other PCs)",
                yesNoOptions),

            // Cloud sync
            new("usage.cloudsync", "☁️ OneDrive & Cloud Sync",
                "Do you use OneDrive, Work Folders, or sync Mail/Calendar/Contacts with a Microsoft account?\n\n" +
                "• Yes = Keep OneDrive, sync services, and account integration active\n" +
                "• No = Stop cloud sync services (files stay local only)",
                yesNoOptions),

            // Bluetooth
            new("usage.bluetooth", "🔵 Bluetooth Devices",
                "Do you use Bluetooth headphones, speakers, mice, keyboards, game controllers, or other Bluetooth devices?\n\n" +
                "• Yes = Keep Bluetooth stack running for device connections\n" +
                "• No = Stop Bluetooth services (no devices can pair or connect)",
                yesNoOptions),

            // Mobile hotspot
            new("usage.hotspot", "📡 Mobile Hotspot & Internet Sharing",
                "Do you share this PC's internet connection with other devices (Mobile Hotspot feature)?\n\n" +
                "• Yes = Keep Internet Connection Sharing services active\n" +
                "• No = Stop hotspot services",
                yesNoOptions),

            // Store apps
            new("usage.storeapps", "📦 Microsoft Store Platform",
                "Do you use UWP/Microsoft Store apps that need background services (e.g., Photos, Mail, Calendar)?\n\n" +
                "• Yes = Keep Windows Store platform services running\n" +
                "• No = Stop Store infrastructure (Store apps may not update or run properly)",
                yesNoOptions),

            // Shared experiences
            new("usage.sharedexperience", "🔗 Nearby Sharing & Cross-Device",
                "Do you use Nearby Sharing, clipboard sync between devices, or cross-device app experiences?\n\n" +
                "• Yes = Keep shared experience services active\n" +
                "• No = Stop cross-device features (better privacy)",
                yesNoOptions),

            // Search indexing
            new("usage.searchindexing", "🔍 Windows Search Indexing",
                "Do you use Windows Search to quickly find files, emails, or apps by typing in the taskbar?\n\n" +
                "• Yes = Keep Windows Search indexer running (faster searches)\n" +
                "• No = Stop indexing (searches will be slower but saves disk/CPU)",
                yesNoOptions),

            // Delivery Optimization
            new("usage.deliveryoptimization", "📥 Delivery Optimization (P2P Updates)",
                "Do you want Windows Update to download updates faster using peer-to-peer sharing?\n\n" +
                "• Yes = Keep Delivery Optimization active (uses some upload bandwidth)\n" +
                "• No = Stop P2P sharing (updates download only from Microsoft servers)",
                yesNoOptions),

            // Windows Hello Face
            new("usage.helloface", "👤 Windows Hello Face Recognition",
                "Do you sign in to this PC using Windows Hello facial recognition (camera login)?\n\n" +
                "• Yes = Keep Windows Hello Face service running\n" +
                "• No = Stop face recognition service",
                yesNoOptions),

            // AI/Copilot
            new("usage.ai", "🤖 Windows Copilot & AI Features",
                "Do you use Windows Copilot, Recall, or other AI-powered features?\n\n" +
                "• Yes = Keep AI and Copilot services running\n" +
                "• No = Stop AI/Copilot services (better privacy, saves resources)",
                yesNoOptions),

            // Cortana/Voice
            new("usage.cortana", "🎙️ Cortana & Voice Commands",
                "Do you use Cortana or 'Hey Cortana' voice commands on this PC?\n\n" +
                "• Yes = Keep Cortana and voice activation running\n" +
                "• No = Stop Cortana and voice services (Cortana is deprecated in Windows 11)",
                yesNoOptions),

            // Widgets
            new("usage.widgets", "📰 Windows Widgets Panel",
                "Do you use the Widgets panel (weather, news, stocks, etc.) on your taskbar?\n\n" +
                "• Yes = Keep Widget service running\n" +
                "• No = Stop Widgets background service",
                yesNoOptions),

            // Accessibility
            new("usage.accessibility", "♿ Accessibility Features",
                "Do you use accessibility features like Narrator, Magnifier, or speech recognition?\n\n" +
                "• Yes = Keep accessibility services ready\n" +
                "• No = Stop accessibility monitoring (you can still launch these manually)",
                yesNoOptions),

            // Media streaming
            new("usage.mediastreaming", "🎵 Media Streaming & DLNA",
                "Do you stream music/videos to other devices (TVs, speakers) or use Windows Media Player sharing?\n\n" +
                "• Yes = Keep media streaming and UPnP services active\n" +
                "• No = Stop media sharing services",
                yesNoOptions),

            // Scheduled tasks
            new("usage.scheduledtasks", "⏰ Telemetry & Maintenance Tasks",
                "Should Windows run background telemetry, Edge update, and diagnostic scheduled tasks?\n\n" +
                "• Yes = Keep all scheduled maintenance tasks enabled\n" +
                "• No = Disable telemetry, CEIP, and unnecessary scheduled tasks",
                yesNoOptions),
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
            // Gaming - Category A
            new QuestionnaireRule("usage.gaming", "no", new[] { "A" }, new[] { "bcastdvruserservice_*", "captureservice" }),

            // VR - Category B
            new QuestionnaireRule("usage.vr", "no", new[] { "B" }, Array.Empty<string>()),

            // Printing - Category C
            new QuestionnaireRule("usage.printer", "no", new[] { "C" }, new[] { "printworkflowusersvc_*" }),

            // Phone Link - Category E
            new QuestionnaireRule("usage.phone", "no", new[] { "E" }, Array.Empty<string>()),

            // Location - Category F
            new QuestionnaireRule("usage.location", "no", new[] { "F" }, Array.Empty<string>()),

            // Touch/Tablet - Category G
            new QuestionnaireRule("device.touch", "no", new[] { "G" }, new[] { "textinputhost", "penservice" }),

            // Developer tools - Category H
            new QuestionnaireRule("usage.developer", "no", new[] { "H" }, new[] { "p9rdrservice_*" }),

            // Core telemetry - Categories D, K
            new QuestionnaireRule("usage.telemetrycore", "no", new[] { "D", "K" }, new[] { "compattelrunner", "devicecensus", "webagent" }),

            // BITS/IP Helper - Specific services
            new QuestionnaireRule("usage.telemetryadvanced", "no", Array.Empty<string>(), new[] { "bits", "iphlpsvc" }),

            // Performance helpers - Category I
            new QuestionnaireRule("usage.performance", "no", new[] { "I" }, new[] { "sysmain" }),

            // Edge updates
            new QuestionnaireRule("usage.edgeupdates", "no", Array.Empty<string>(), new[] { "edgeupdate", "edgeupdateservice", @"\microsoft\edgeupdate\microsoftedgeupdatetaskmachinecore", @"\microsoft\edgeupdate\microsoftedgeupdatetaskmachineua" }),

            // Cellular
            new QuestionnaireRule("usage.cellular", "no", Array.Empty<string>(), new[] { "phonesvc" }),

            // App Readiness
            new QuestionnaireRule("usage.appreadiness", "no", Array.Empty<string>(), new[] { "appreadiness" }),

            // Remote Desktop
            new QuestionnaireRule("usage.remotedesktop", "no", Array.Empty<string>(), new[] { "remoteaccess", "termservice", "umrdpservice" }),

            // Cloud sync
            new QuestionnaireRule("usage.cloudsync", "no", Array.Empty<string>(), new[] { "onesyncsvc", @"\microsoft\office\onedrive standalone update task", "workfolderssvc", "onedrive" }),

            // Bluetooth - Category L related
            new QuestionnaireRule("usage.bluetooth", "no", Array.Empty<string>(), new[] { "bthserv", "btagservice", "bluetoothuserservice_*" }),

            // Mobile hotspot
            new QuestionnaireRule("usage.hotspot", "no", Array.Empty<string>(), new[] { "icssvc", "sharedaccess" }),

            // Store apps
            new QuestionnaireRule("usage.storeapps", "no", Array.Empty<string>(), new[] { "wsservice", "appxsvc", "staterepository" }),

            // Shared experiences
            new QuestionnaireRule("usage.sharedexperience", "no", Array.Empty<string>(), new[] { "pimindexmaintenancesvc", "userdatasvc_*", "walletservice" }),

            // Search indexing
            new QuestionnaireRule("usage.searchindexing", "no", Array.Empty<string>(), new[] { "wsearch" }),

            // Delivery Optimization
            new QuestionnaireRule("usage.deliveryoptimization", "no", Array.Empty<string>(), new[] { "dosvc" }),

            // Windows Hello Face
            new QuestionnaireRule("usage.helloface", "no", Array.Empty<string>(), new[] { "facesvc" }),

            // AI/Copilot - Category N
            new QuestionnaireRule("usage.ai", "no", new[] { "N" }, new[] { "aihost", "recallservice", "windowscopilotruntime", "semanticindex", "widgetservice" }),

            // Cortana/Voice - Category O
            new QuestionnaireRule("usage.cortana", "no", new[] { "O" }, new[] { "cortana", "cortanaui", "voiceactivationmanager", "speechruntime" }),

            // Widgets
            new QuestionnaireRule("usage.widgets", "no", Array.Empty<string>(), new[] { "widgetservice" }),

            // Accessibility - Category Q
            new QuestionnaireRule("usage.accessibility", "no", new[] { "Q" }, new[] { "narrator", "magnify", "atbroker", "assistivetechnologymonitor" }),

            // Media streaming - Category P
            new QuestionnaireRule("usage.mediastreaming", "no", new[] { "P" }, new[] { "wmpnscfg", "wmpnetwk", "upnphost", "ssdpsrv", "wmpnetworksvc" }),

            // Scheduled tasks - Category M
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

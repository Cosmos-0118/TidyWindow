using System;
using System.Collections.Generic;
using System.IO;
using TidyWindow.Core.Processes;
using Xunit;

namespace TidyWindow.Core.Tests.Processes;

public sealed class ProcessQuestionnaireEngineTests
{
    [Fact]
    public void EvaluateAndApply_PersistsRecommendations()
    {
        var catalogPath = CreateTempCatalog();
        var statePath = CreateTempState();

        try
        {
            var parser = new ProcessCatalogParser(catalogPath);
            var store = new ProcessStateStore(statePath);
            var engine = new ProcessQuestionnaireEngine(parser, store);

            var answers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["usage.gaming"] = "no",
                ["usage.vr"] = "no",
                ["usage.printer"] = "no",
                ["usage.phone"] = "no",
                ["usage.location"] = "no",
                ["device.touch"] = "no",
                ["usage.telemetry"] = "aggressive",
                ["usage.performance"] = "aggressive"
            };

            var result = engine.EvaluateAndApply(answers);

            Assert.Contains("spooler", result.RecommendedProcessIds);
            Assert.Contains("sysmain", result.RecommendedProcessIds);
            Assert.Contains("diagtrack", result.RecommendedProcessIds);
            Assert.Contains("bits", result.RecommendedProcessIds);

            var preferences = store.GetPreferences();
            Assert.Contains(preferences, pref => pref.ProcessIdentifier == "spooler" && pref.Source == ProcessPreferenceSource.Questionnaire);
            Assert.Contains(preferences, pref => pref.ProcessIdentifier == "sysmain" && pref.Source == ProcessPreferenceSource.Questionnaire);

            var questionnaire = store.GetQuestionnaireSnapshot();
            Assert.Equal("no", questionnaire.Answers["usage.printer"]);
            Assert.Contains("bits", questionnaire.AutoStopProcessIds);
        }
        finally
        {
            File.Delete(catalogPath);
            File.Delete(statePath);
        }
    }

    private static string CreateTempCatalog()
    {
        var body = """
✅ FULL EXPANDED — Safe to disable (home PCs, no feature use)

A. Xbox / Gaming (safe if you do not use Xbox/GamePass/Game Bar)
GameBar
GameInput

B. Mixed Reality / VR (safe if no VR headset)
MixedRealityPortal

C. Printing / Fax (safe if no local printer / fax)
Spooler        # Print Spooler
Fax            # Fax service

D. Telemetry / Microsoft “UX” / Diagnostics (safe; you lose crash/telemetry)
DiagTrack
WerSvc / WerFault

E. Phone Link / Device Sync / Push (safe if you don’t use Phone Link / notifications)
WpnService
WpnUserService_*

F. Location / Maps (safe if you don’t use geolocation/Maps)
MapsBroker
lfsvc

G. Ink / Touch / Tablet (safe if non-touch laptop)
TabletInputService
TouchKeyboardAndHandwritingPanelService

I. Performance helpers (optional; safe but may change perf behavior)
SysMain   # Superfetch

⚠️ Items to treat with CAUTION (don’t disable unless you know you don’t need them)
BITS
iphlpsvc
""";

        var path = Path.Combine(Path.GetTempPath(), $"TidyWindow_Questionnaire_{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, body);
        return path;
    }

    private static string CreateTempState()
    {
        return Path.Combine(Path.GetTempPath(), $"TidyWindow_State_{Guid.NewGuid():N}.json");
    }
}

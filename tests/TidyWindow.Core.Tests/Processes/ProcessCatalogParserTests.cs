using System;
using System.IO;
using System.Linq;
using TidyWindow.Core.Processes;
using Xunit;

namespace TidyWindow.Core.Tests.Processes;

public sealed class ProcessCatalogParserTests
{
    [Fact]
    public void LoadSnapshot_ParsesSafeAndCautionSections()
    {
        var catalogPath = CreateTempCatalog();
        try
        {
            var parser = new ProcessCatalogParser(catalogPath);
            var snapshot = parser.LoadSnapshot();

            Assert.Equal(7, snapshot.Entries.Count);

            var gameBar = snapshot.Entries.Single(entry => entry.DisplayName == "GameBar");
            Assert.Equal(ProcessRiskLevel.Safe, gameBar.RiskLevel);
            Assert.Equal(ProcessActionPreference.AutoStop, gameBar.RecommendedAction);
            Assert.Equal("A", gameBar.CategoryKey);

            var werFault = snapshot.Entries.Single(entry => entry.DisplayName == "WerFault");
            Assert.Equal(ProcessRiskLevel.Safe, werFault.RiskLevel);

            var ipHelper = snapshot.Entries.Single(entry => entry.DisplayName == "iphlpsvc");
            Assert.Equal(ProcessRiskLevel.Caution, ipHelper.RiskLevel);
            Assert.Equal(ProcessActionPreference.Keep, ipHelper.RecommendedAction);
            Assert.Equal("caution", ipHelper.CategoryKey);

            Assert.Contains(snapshot.Categories, category => category.Key == "caution" && category.IsCaution);
        }
        finally
        {
            File.Delete(catalogPath);
        }
    }

    [Fact]
    public void LoadSnapshot_IgnoresSectionsAfterWorkflow()
    {
        var catalogPath = CreateTempCatalog(includeWorkflowTail: true);
        try
        {
            var parser = new ProcessCatalogParser(catalogPath);
            var snapshot = parser.LoadSnapshot();

            Assert.DoesNotContain(snapshot.Entries, entry => entry.DisplayName == "ShouldNotLoad");
        }
        finally
        {
            File.Delete(catalogPath);
        }
    }


    [Fact]
    public void LoadSnapshot_FromJsonHandlesCommentsAndBackslashIdentifiers()
    {
        var json = """
{
    "categories": [
        {
            "key": "M",
            "name": "Doc Section D — Scheduled tasks",
            "description": "telemetry / maintenance"
        }
    ],
    "entries": [
        {
            "identifier": "\\microsoft\\windows\\edgeupdate\\microsoftedgeupdatetaskmachinecore",
            "displayName": "\\Microsoft\\Windows\\EdgeUpdate\\MicrosoftEdgeUpdateTaskMachineCore",
            "categoryKey": "M",
            "categoryName": "Doc Section D — Scheduled tasks",
            "categoryDescription": "telemetry / maintenance",
            "risk": "Safe",
            "recommendedAction": "AutoStop",
            "isPattern": false,
            "order": 1
        },
        // Parser should ignore this comment via JsonCommentHandling.Skip
        {
            "identifier": "\\microsoft\\windows\\rds\\*",
            "displayName": "\\Microsoft\\Windows\\RDS\\*",
            "categoryKey": "M",
            "risk": "Safe",
            "recommendedAction": "AutoStop",
            "isPattern": true,
            "order": 2
        }
    ]
}
""";

        var path = Path.Combine(Path.GetTempPath(), $"TidyWindow_Catalog_{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);

        try
        {
            var parser = new ProcessCatalogParser(path);
            var snapshot = parser.LoadSnapshot();

            Assert.Equal(2, snapshot.Entries.Count);

            var edgeTask = snapshot.Entries.Single(entry => entry.DisplayName.Contains("EdgeUpdate"));
            Assert.Equal("\\microsoft\\windows\\edgeupdate\\microsoftedgeupdatetaskmachinecore", edgeTask.Identifier);
            Assert.Equal(ProcessActionPreference.AutoStop, edgeTask.RecommendedAction);
            Assert.False(edgeTask.IsPattern);

            var rdsTask = snapshot.Entries.Single(entry => entry.Identifier == "\\microsoft\\windows\\rds\\*");
            Assert.True(rdsTask.IsPattern);
            Assert.Equal("M", rdsTask.CategoryKey);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LoadSnapshot_FromJsonAssignsServiceIdentifiers()
    {
        var json = """
{
    "entries": [
        {
            "identifier": "diagtrack",
            "displayName": "DiagTrack",
            "serviceName": "DiagTrack",
            "categoryKey": "D",
            "risk": "Safe",
            "recommendedAction": "AutoStop"
        },
        {
            "identifier": "pattern_*",
            "displayName": "pattern_*",
            "serviceName": "ShouldIgnore",
            "isPattern": true,
            "categoryKey": "X",
            "recommendedAction": "AutoStop"
        }
    ]
}
""";

        var path = Path.Combine(Path.GetTempPath(), $"TidyWindow_CatalogSvc_{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);

        try
        {
            var parser = new ProcessCatalogParser(path);
            var snapshot = parser.LoadSnapshot();

            var diagtrack = Assert.Single(snapshot.Entries, entry => entry.Identifier == "diagtrack");
            Assert.Equal("DiagTrack", diagtrack.ServiceIdentifier);
            Assert.True(diagtrack.SupportsServiceControl);

            var pattern = Assert.Single(snapshot.Entries, entry => entry.Identifier == "pattern_*");
            Assert.Null(pattern.ServiceIdentifier);
            Assert.False(pattern.SupportsServiceControl);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LoadSnapshot_FromJsonRejectsInvalidServiceNames()
    {
        var json = """
{
    "entries": [
        {
            "identifier": "diagtrack",
            "displayName": "DiagTrack",
            "serviceName": "Diag Track", // space should be rejected
            "categoryKey": "D",
            "risk": "Safe",
            "recommendedAction": "AutoStop"
        }
    ]
}
""";

        var path = Path.Combine(Path.GetTempPath(), $"TidyWindow_CatalogBadSvc_{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);

        try
        {
            var parser = new ProcessCatalogParser(path);
            Assert.Throws<InvalidDataException>(() => parser.LoadSnapshot());
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string CreateTempCatalog(bool includeWorkflowTail = false)
    {
        var body = """
✅ FULL EXPANDED — Safe to disable (home PCs, no feature use)

A. Xbox / Gaming (safe if you do not use Xbox/GamePass/Game Bar)
GameBar
WerSvc / WerFault     # Windows Error Reporting service

B. Ink / Touch / Tablet (safe if non-touch laptop)
TabletInputService    # Text input host
TouchKeyboardAndHandwritingPanelService

⚠️ Items to treat with CAUTION (don’t disable unless you know you don’t need them)
iphlpsvc (IP Helper) — safe if you don’t use IPv6
BITS (Background Intelligent Transfer Service) — prefer Manual if testing
""";

        if (includeWorkflowTail)
        {
            body += """

🔁 Quick safe workflow (PowerShell): backup, disable, revert easily
ShouldNotLoad
""";
        }

        var path = Path.Combine(Path.GetTempPath(), $"TidyWindow_Catalog_{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, body);
        return path;
    }
}

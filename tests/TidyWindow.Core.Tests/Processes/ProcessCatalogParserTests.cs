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

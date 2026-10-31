using TidyWindow.Next.Core;
using Xunit;

namespace TidyWindow.Next.Tests;

/// <summary>A PC described in memory: what is installed, what Windows stages for new accounts, and the state of the registry marks.</summary>
internal sealed class FakeDebloatPc : IDebloatSystem, IAppxStore, IAppxOps
{
    public int Build => 26300;
    public List<AppxFact> Installed = [];
    public List<ProvisionedFact> Staged = [];
    public PlaybookFacts Facts = new(false, [], false, true, false);
    public List<string> Sids = ["S-1-5-21-1-2-3-1001"];

    public List<string> Log = [];
    public HashSet<string> EndOfLife = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Deprovisioned = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Inbox = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Packages that refuse to go, as a locked or damaged app does.</summary>
    public HashSet<string> Stubborn = new(StringComparer.OrdinalIgnoreCase);
    public bool ThrowDuringRemoval;
    /// <summary>When set, a removed package stays listed with no account and this folder, as when Windows refuses the last step and keeps the files.</summary>
    public string? KeptFolder;

    public IReadOnlyList<AppxFact> Packages() => Installed.ToList();
    public IReadOnlyList<ProvisionedFact> Provisioned() => Staged.ToList();
    public PlaybookFacts Playbooks() => Facts;

    public IReadOnlyList<string> UserSids() => Sids;
    public bool DeleteInbox(string fullName) { Log.Add("inbox-" + fullName); return Inbox.Remove(fullName); }
    public bool CreateEndOfLife(string sid, string fullName) { Log.Add($"eol+ {sid} {fullName}"); return EndOfLife.Add(sid + "|" + fullName); }
    public void DeleteEndOfLife(string sid, string fullName) { Log.Add($"eol- {sid} {fullName}"); EndOfLife.Remove(sid + "|" + fullName); }
    public bool HasDeprovisioned(string family) => Deprovisioned.Contains(family);
    public void CreateDeprovisioned(string family) { Log.Add("deprov+ " + family); Deprovisioned.Add(family); }
    public void DeleteDeprovisioned(string family) { Log.Add("deprov- " + family); Deprovisioned.Remove(family); }

    public string? ClearNonRemovable(string family) { Log.Add("unlock " + family); return null; }

    public string? RemovePackage(string fullName, IReadOnlyList<string> sids)
    {
        Log.Add("remove " + fullName);
        if (ThrowDuringRemoval) throw new InvalidOperationException("boom");
        if (Stubborn.Contains(fullName)) return "0x80073CFA";
        if (KeptFolder is not null) Installed = Installed.Select(x => x.FullName == fullName ? x with { Users = [], InstallLocation = KeptFolder } : x).ToList();
        else Installed.RemoveAll(x => x.FullName == fullName);
        return null;
    }

    public string? RemoveProvisioned(string packageName)
    {
        Log.Add("provisioned- " + packageName);
        Staged.RemoveAll(x => x.PackageName == packageName);
        return null;
    }

    public static AppxFact Package(string name, string publisher = "8wekyb3d8bbwe", bool locked = false, bool framework = false, params string[] users) =>
        new(name, $"{name}_1.0.0.0_x64__{publisher}", $"{name}_{publisher}", locked, framework, users.Length == 0 ? ["S-1-5-21-1-2-3-1001"] : users);
}

public sealed class DebloatGuardTests
{
    [Theory]
    [InlineData("Microsoft.VCLibs.140.00")]
    [InlineData("Microsoft.UI.Xaml.2.8")]
    [InlineData("Microsoft.WindowsAppRuntime.1.8")]
    [InlineData("Microsoft.WindowsStore")]
    [InlineData("Microsoft.DesktopAppInstaller")]
    [InlineData("Microsoft.Windows.ShellExperienceHost")]
    [InlineData("MicrosoftWindows.Client.CBS")]
    [InlineData("MicrosoftWindows.Client.Core")]
    [InlineData("Microsoft.XboxIdentityProvider")]
    [InlineData("Microsoft.SecHealthUI")]
    [InlineData("Windows.PrintDialog")]
    [InlineData("1527c705-839a-4832-9118-54d4Bd6a0c89")]
    [InlineData("Microsoft.Xbox.TCUI")]
    [InlineData("Microsoft.GamingServices")]
    [InlineData("MicrosoftWindows.Client.WebExperience")]
    [InlineData("Microsoft.WindowsCalculator")]
    [InlineData("Microsoft.WindowsNotepad")]
    [InlineData("Microsoft.Paint")]
    [InlineData("Microsoft.ScreenSketch")]
    [InlineData("Microsoft.Windows.Photos")]
    [InlineData("Microsoft.WindowsTerminal")]
    [InlineData("Microsoft.WindowsCamera")]
    public void WhatWindowsNeedsIsNeverRemoved(string name) => Assert.True(DebloatGuard.IsProtected(name));

    [Theory]
    [InlineData("Microsoft.BingNews")]
    [InlineData("Clipchamp.Clipchamp")]
    [InlineData("MicrosoftWindows.CrossDevice")]
    [InlineData("Microsoft.GamingApp")]
    [InlineData("Microsoft.XboxApp")]
    [InlineData("Microsoft.PCManager")]
    [InlineData("Microsoft.StartExperiencesApp")]
    [InlineData("Microsoft.WidgetsPlatformRuntime")]
    [InlineData("Microsoft.Windows.AIHub")]
    [InlineData("Microsoft.Edge.GameAssist")]
    [InlineData("Microsoft.MSPaint")]
    public void OrdinaryAppsAreNotProtected(string name) => Assert.False(DebloatGuard.IsProtected(name));

    [Theory]
    [InlineData("MicrosoftWindows.Client.WebExperience")]
    [InlineData("Microsoft.XboxIdentityProvider")]
    [InlineData("Microsoft.Xbox.TCUI")]
    [InlineData("Microsoft.GamingServices")]
    [InlineData("Microsoft.WindowsCalculator")]
    [InlineData("Microsoft.WindowsNotepad")]
    [InlineData("Microsoft.Paint")]
    [InlineData("Microsoft.ScreenSketch")]
    [InlineData("Microsoft.Windows.Photos")]
    [InlineData("Microsoft.WindowsTerminal")]
    [InlineData("Microsoft.WindowsCamera")]
    [InlineData("Microsoft.WindowsStore")]
    public void NoItemInTheCatalogEverCoversThem(string name) =>
        Assert.DoesNotContain(DebloatCatalog.Load(), item => DebloatGuard.Covers(item, name, false) || item.Packages.Any(p => DebloatGuard.Matches(p, name)));

    [Theory]
    [InlineData("pc-manager", "Microsoft.PCManager")]
    [InlineData("pc-manager", "Microsoft.MicrosoftPCManager")]
    [InlineData("widgets-experiences", "Microsoft.StartExperiencesApp")]
    [InlineData("widgets-experiences", "Microsoft.WidgetsPlatformRuntime")]
    [InlineData("m365-companions", "Microsoft.M365Companions")]
    [InlineData("whiteboard-journal", "Microsoft.Whiteboard")]
    [InlineData("whiteboard-journal", "Microsoft.Office.Sway")]
    [InlineData("whiteboard-journal", "Microsoft.MicrosoftJournal")]
    [InlineData("whiteboard-journal", "Microsoft.MicrosoftPowerBIForWindows")]
    [InlineData("mobile-plans", "Microsoft.OneConnect")]
    [InlineData("mobile-plans", "Microsoft.Messaging")]
    [InlineData("remote-desktop-apps", "MicrosoftCorporationII.Windows365")]
    [InlineData("remote-desktop-apps", "Microsoft.RemoteDesktop")]
    [InlineData("bing-news-weather", "Microsoft.News")]
    [InlineData("mixed-reality-3d", "Microsoft.3DBuilder")]
    [InlineData("mixed-reality-3d", "Microsoft.3DScan")]
    [InlineData("xbox-app", "Microsoft.XboxApp")]
    [InlineData("ai-hub", "Microsoft.Windows.AIHub")]
    [InlineData("edge-game-assist", "Microsoft.Edge.GameAssist")]
    [InlineData("copilot-app", "Microsoft.Office.ActionsServer")]
    [InlineData("copilot-app", "Microsoft.WritingAssistant")]
    [InlineData("preinstalled-trials", "5A894077.McAfeeSecurity")]
    [InlineData("preinstalled-trials", "McAfeeWPSSparsePackage")]
    [InlineData("preinstalled-trials", "C27EB4BA.DropboxOEM")]
    [InlineData("preinstalled-trials", "4AE8B7C2.Booking.comPartnerEdition")]
    [InlineData("promoted-media-tools", "CyberLinkMediaSuiteEssentials")]
    [InlineData("promoted-media-tools", "DB6EA5DB.PowerDirector")]
    [InlineData("promoted-media-tools", "AdobeSystemsIncorporated.AdobePhotoshopExpress")]
    [InlineData("promoted-media-tools", "2FE3CB00.PicsArt-PhotoStudio")]
    [InlineData("promoted-media-tools", "WinZipUniversal")]
    [InlineData("candy-crush", "DisneyMagicKingdoms")]
    [InlineData("candy-crush", "NYTCrossword")]
    [InlineData("streaming-social", "HULULLC.HULUPLUS")]
    [InlineData("streaming-social", "SlingTV")]
    [InlineData("streaming-social", "Facebook.Instagram")]
    [InlineData("streaming-social", "LinkedInforWindows")]
    [InlineData("streaming-social", "CAF9E577.Plex")]
    [InlineData("oem-network-throttling", "RivetNetworks.SmartByte")]
    [InlineData("oem-network-throttling", "RivetNetworks.KillerControlCenter")]
    [InlineData("oem-dell", "ScreenovateTechnologies.DellMobileConnect")]
    [InlineData("oem-dell", "STMicroelectronicsMEMS.DellFreeFallDataProtection")]
    [InlineData("oem-lenovo", "MirametrixInc.GlancebyMirametrix")]
    [InlineData("oem-surface", "Microsoft.SurfaceHub")]
    [InlineData("oem-audio", "DolbyLaboratories.DolbyAccess")]
    [InlineData("oem-audio", "WavesAudio.MaxxAudioPro")]
    [InlineData("oem-audio", "A-Volute.NahimicCompanion")]
    public void TheNewCatalogItemsCoverTheirPackages(string id, string package)
    {
        var item = DebloatCatalog.Load().Single(x => x.Id == id);
        Assert.True(DebloatGuard.Covers(item, package, false), $"{id} should cover {package}");
    }

    [Theory]
    [InlineData("preinstalled-trials")]
    [InlineData("widgets-experiences")]
    [InlineData("remote-desktop-apps")]
    [InlineData("oem-network-throttling")]
    [InlineData("oem-surface")]
    [InlineData("oem-audio")]
    public void ItemsWithATradeOffAreMarkedCarefulAndNeverSuggested(string id)
    {
        var item = DebloatCatalog.Load().Single(x => x.Id == id);
        Assert.Equal(DebloatRisk.Careful, item.Risk);
        Assert.False(item.Recommended);
    }

    [Theory]
    [InlineData("pc-manager", DebloatGroup.Microsoft)]
    [InlineData("ai-hub", DebloatGroup.Ai)]
    [InlineData("edge-game-assist", DebloatGroup.Ai)]
    [InlineData("preinstalled-trials", DebloatGroup.Promoted)]
    [InlineData("promoted-media-tools", DebloatGroup.Promoted)]
    [InlineData("oem-network-throttling", DebloatGroup.Manufacturer)]
    public void NewItemsSitInTheTabsPeopleLookIn(string id, DebloatGroup group) => Assert.Equal(group, DebloatCatalog.Load().Single(x => x.Id == id).Group);

    [Fact]
    public void EveryStoreIdInTheCatalogCanBeUsedToInstallItAgain()
    {
        foreach (var item in DebloatCatalog.Load().Where(x => x.StoreId.Length > 0))
            Assert.True(DebloatGuard.IsToken(item.StoreId), item.Id);
    }

    [Fact]
    public void TheShippedCatalogIsValidAndNothingInItOverlapsTheGuard()
    {
        var catalog = DebloatCatalog.Load();
        Assert.NotEmpty(catalog);
        Assert.Empty(DebloatGuard.Validate(catalog));
        foreach (var pattern in catalog.SelectMany(x => x.Packages)) Assert.False(DebloatGuard.IsProtected(pattern.TrimEnd('*')), pattern);
    }

    [Fact]
    public void EveryTierIsRepresentedInTheCatalog()
    {
        var kinds = DebloatCatalog.Load().Select(x => x.Kind).Distinct().ToList();
        Assert.Contains(DebloatKind.Appx, kinds); Assert.Contains(DebloatKind.OneDrive, kinds); Assert.Contains(DebloatKind.Edge, kinds); Assert.Contains(DebloatKind.Recall, kinds);
        Assert.Contains(DebloatCatalog.Load(), x => x.Group == DebloatGroup.Manufacturer);
    }

    private static DebloatItem Item(string id = "x-item", DebloatKind kind = DebloatKind.Appx, params string[] packages) =>
        new() { Id = id, Name = "X", Group = DebloatGroup.Microsoft, Kind = kind, Summary = "s", Effect = "e", Packages = packages };

    [Theory]
    [InlineData("*")]
    [InlineData("Micro*")]
    [InlineData("Microsoft.*")]
    [InlineData("Microsoft.Windows.Search")]
    [InlineData("MicrosoftWindows.Client.*")]
    [InlineData("Windows.*")]
    [InlineData("..\\evil")]
    [InlineData("a b")]
    public void AUnsafePatternMakesTheCatalogInvalid(string pattern) => Assert.NotEmpty(DebloatGuard.Validate([Item(packages: pattern)]));

    [Fact]
    public void ABrokenItemIsReported()
    {
        Assert.NotEmpty(DebloatGuard.Validate([Item("Bad Id", packages: "Some.Package")]));
        Assert.NotEmpty(DebloatGuard.Validate([Item(packages: [])]));
        Assert.NotEmpty(DebloatGuard.Validate([Item("a-b", DebloatKind.Edge, "Some.Package")]));
        Assert.NotEmpty(DebloatGuard.Validate([Item("a-b", packages: "Some.Package"), Item("a-b", packages: "Other.Package")]));
    }

    [Theory]
    [InlineData("king.com.*", "king.com.CandyCrushSaga", true)]
    [InlineData("king.com.*", "KING.COM.Other", true)]
    [InlineData("king.com.*", "kingdom.Game", false)]
    [InlineData("Microsoft.BingNews", "microsoft.bingnews", true)]
    [InlineData("Microsoft.BingNews", "Microsoft.BingNewsPlus", false)]
    public void PatternsMatchWholeNamesOrPrefixes(string pattern, string name, bool expected) => Assert.Equal(expected, DebloatGuard.Matches(pattern, name));

    [Theory]
    [InlineData("Microsoft.BingNews_8wekyb3d8bbwe", true)]
    [InlineData("Microsoft.BingNews_1.0_x64__8wekyb3d8bbwe", true)]
    [InlineData("..\\Run", false)]
    [InlineData("a/b", false)]
    [InlineData("a b", false)]
    [InlineData("", false)]
    public void OnlyPlainNamesReachTheRegistryAndTheShell(string text, bool expected) => Assert.Equal(expected, DebloatGuard.IsToken(text));

    [Theory]
    [InlineData("S-1-5-18", true)]
    [InlineData("S-1-5-21-2263392280-518463016-1748006429-1001", true)]
    [InlineData("S-1-5-21-1'; calc; '", false)]
    [InlineData("1001", false)]
    public void AccountIdsAreChecked(string text, bool expected) => Assert.Equal(expected, DebloatGuard.IsSid(text));
}

public sealed class DebloatScanTests
{
    private static readonly IReadOnlyList<DebloatItem> Catalog = DebloatCatalog.Load();

    [Fact]
    public void OnlyWhatIsOnThisPcIsListedAndFrameworksAreNeverMatched()
    {
        var pc = new FakeDebloatPc { Installed = [FakeDebloatPc.Package("Microsoft.BingNews"), FakeDebloatPc.Package("Microsoft.BingWeather"), FakeDebloatPc.Package("Microsoft.VCLibs.140.00", framework: true), FakeDebloatPc.Package("Microsoft.WindowsStore")] };

        var scan = DebloatScan.Run(pc, Catalog);

        var news = Assert.Single(scan.Entries);
        Assert.Equal("bing-news-weather", news.Id);
        Assert.Equal(2, news.Packages.Count);
        Assert.Equal(Catalog.Count - 1, scan.NotOnThisPc);
    }

    [Fact]
    public void AWildcardCoversEveryAppFromThatMaker()
    {
        var pc = new FakeDebloatPc { Installed = [FakeDebloatPc.Package("king.com.CandyCrushSaga", "kgqvnymyfvs32"), FakeDebloatPc.Package("king.com.BubbleWitch3Saga", "kgqvnymyfvs32"), FakeDebloatPc.Package("AD2F1837.HPSupportAssistant", "v10z8vjag6ke6")] };

        var scan = DebloatScan.Run(pc, Catalog);

        Assert.Equal(2, scan.Entries.Single(x => x.Id == "candy-crush").AppCount);
        Assert.Single(scan.Entries.Single(x => x.Id == "oem-hp").Packages);
    }

    [Fact]
    public void AnAppOnlyStagedForNewAccountsIsStillFound()
    {
        var pc = new FakeDebloatPc { Staged = [new("Clipchamp.Clipchamp", "Clipchamp.Clipchamp_4.0_neutral_~_yxz26nhyzhsrt")] };

        Assert.Equal("clipchamp", Assert.Single(DebloatScan.Run(pc, Catalog).Entries).Id);
    }

    [Fact]
    public void OneDriveIsLockedWhileFoldersAreBackedUpToIt()
    {
        var pc = new FakeDebloatPc { Facts = new(true, ["Desktop", "Documents"], false, true, false) };

        var entry = DebloatScan.Find(pc, Catalog, "onedrive")!;

        Assert.False(entry.CanRemove);
        Assert.Contains("Desktop, Documents", entry.Lock);
        pc.Facts = pc.Facts with { OneDriveFolders = [] };
        Assert.True(DebloatScan.Find(pc, Catalog, "onedrive")!.CanRemove);
    }

    [Fact]
    public void EdgeIsLockedWhenItIsTheOnlyBrowser()
    {
        var pc = new FakeDebloatPc { Facts = new(false, [], true, false, false) };
        Assert.False(DebloatScan.Find(pc, Catalog, "edge")!.CanRemove);
        pc.Facts = pc.Facts with { OtherBrowser = true };
        Assert.True(DebloatScan.Find(pc, Catalog, "edge")!.CanRemove);
    }

    [Fact]
    public void ComponentsThatAreNotThereAreNotListed()
    {
        var pc = new FakeDebloatPc();
        Assert.Null(DebloatScan.Find(pc, Catalog, "edge"));
        Assert.Null(DebloatScan.Find(pc, Catalog, "onedrive"));
        Assert.Null(DebloatScan.Find(pc, Catalog, "recall"));
        Assert.Null(DebloatScan.Find(pc, Catalog, "not-in-the-catalog"));
    }
}

public sealed class AppxRemoverTests
{
    private static readonly IReadOnlyList<DebloatItem> Catalog = DebloatCatalog.Load();

    private static (AppxRemover Remover, DebloatEntry Entry) Setup(FakeDebloatPc pc, string id) =>
        (new AppxRemover(pc, pc, pc), DebloatScan.Find(pc, Catalog, id)!);

    [Fact]
    public void RemovesAnAppAndStopsWindowsBringingItBack()
    {
        var pc = new FakeDebloatPc { Installed = [FakeDebloatPc.Package("Clipchamp.Clipchamp", "yxz26nhyzhsrt")] };
        var (remover, entry) = Setup(pc, "clipchamp");

        var outcome = remover.Remove(entry);

        Assert.True(outcome.Ok, outcome.Error);
        Assert.Empty(pc.Installed);
        Assert.Contains("Clipchamp.Clipchamp_yxz26nhyzhsrt", pc.Deprovisioned);
        Assert.Equal(["Clipchamp.Clipchamp_yxz26nhyzhsrt"], outcome.Families);
    }

    [Fact]
    public void ALockedAppIsUnlockedFirstAndTheTemporaryMarksAreAlwaysTakenAway()
    {
        var full = "Microsoft.BingNews_1.0.0.0_x64__8wekyb3d8bbwe";
        var pc = new FakeDebloatPc { Installed = [FakeDebloatPc.Package("Microsoft.BingNews", locked: true)], Inbox = [full] };
        var (remover, entry) = Setup(pc, "bing-news-weather");

        var outcome = remover.Remove(entry);

        Assert.True(outcome.Ok, outcome.Error);
        Assert.Empty(pc.EndOfLife);
        Assert.Empty(pc.Inbox);
        var unlock = pc.Log.IndexOf("unlock Microsoft.BingNews_8wekyb3d8bbwe");
        Assert.True(unlock >= 0 && unlock < pc.Log.IndexOf("remove " + full));
        // The mark is made for the user and for Windows' own account before the removal, and cleared afterwards.
        Assert.Contains($"eol+ {AppxRemover.SystemSid} {full}", pc.Log);
        Assert.Contains($"eol+ S-1-5-21-1-2-3-1001 {full}", pc.Log);
        Assert.True(pc.Log.IndexOf($"eol- {AppxRemover.SystemSid} {full}") > pc.Log.IndexOf("remove " + full));
    }

    [Fact]
    public void TheMarksAreClearedEvenWhenTheRemovalThrows()
    {
        var pc = new FakeDebloatPc { Installed = [FakeDebloatPc.Package("Microsoft.BingNews", locked: true)], ThrowDuringRemoval = true };
        var (remover, entry) = Setup(pc, "bing-news-weather");

        Assert.Throws<InvalidOperationException>(() => remover.Remove(entry));

        Assert.Empty(pc.EndOfLife);
    }

    [Fact]
    public void AnAppThatWillNotGoIsReportedAndItsBlockIsTakenBack()
    {
        var pc = new FakeDebloatPc { Installed = [FakeDebloatPc.Package("Microsoft.BingNews")], Stubborn = ["Microsoft.BingNews_1.0.0.0_x64__8wekyb3d8bbwe"] };
        var (remover, entry) = Setup(pc, "bing-news-weather");

        var outcome = remover.Remove(entry);

        Assert.False(outcome.Ok);
        Assert.Contains("could not be removed", outcome.Error);
        Assert.Contains("0x80073CFA", outcome.Error);
        Assert.Empty(pc.Deprovisioned);
        Assert.Empty(pc.EndOfLife);
        Assert.Single(pc.Installed);
    }

    [Fact]
    public void AnAppStagedForNewAccountsIsDeprovisionedToo()
    {
        var staged = new ProvisionedFact("Clipchamp.Clipchamp", "Clipchamp.Clipchamp_4.0_neutral_~_yxz26nhyzhsrt");
        var pc = new FakeDebloatPc { Installed = [FakeDebloatPc.Package("Clipchamp.Clipchamp", "yxz26nhyzhsrt")], Staged = [staged] };
        var (remover, entry) = Setup(pc, "clipchamp");

        var outcome = remover.Remove(entry);

        Assert.True(outcome.Ok, outcome.Error);
        Assert.Empty(pc.Staged);
        Assert.Contains("provisioned- " + staged.PackageName, pc.Log);
        Assert.Empty(pc.EndOfLife);
    }

    [Fact]
    public void TheBlockIsMadeOnlyAfterTheRemovalBecauseWindowsRefusesToRemoveAnAppThatIsAlreadyBlocked()
    {
        var full = "Clipchamp.Clipchamp_1.0.0.0_x64__yxz26nhyzhsrt";
        var pc = new FakeDebloatPc { Installed = [FakeDebloatPc.Package("Clipchamp.Clipchamp", "yxz26nhyzhsrt")] };
        var (remover, entry) = Setup(pc, "clipchamp");

        Assert.True(remover.Remove(entry).Ok);

        var block = pc.Log.IndexOf("deprov+ Clipchamp.Clipchamp_yxz26nhyzhsrt");
        Assert.True(block > pc.Log.IndexOf("remove " + full));
    }

    [Fact]
    public void AnAppWindowsAlreadyBlockedIsNotBlockedAgain()
    {
        var pc = new FakeDebloatPc { Installed = [FakeDebloatPc.Package("Clipchamp.Clipchamp", "yxz26nhyzhsrt")], Deprovisioned = ["Clipchamp.Clipchamp_yxz26nhyzhsrt"] };
        var (remover, entry) = Setup(pc, "clipchamp");

        Assert.True(remover.Remove(entry).Ok);

        Assert.DoesNotContain("deprov+ Clipchamp.Clipchamp_yxz26nhyzhsrt", pc.Log);
    }

    [Fact]
    public void FilesThatWindowsKeptForAnAppNoAccountHasAreReportedAndTheAppCountsAsRemoved()
    {
        var folder = Path.Combine(Path.GetTempPath(), "tidywindow-kept-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "app.dll"), new byte[3_000_000]);
        try
        {
            var pc = new FakeDebloatPc { Installed = [FakeDebloatPc.Package("Clipchamp.Clipchamp", "yxz26nhyzhsrt")], KeptFolder = folder };
            var (remover, entry) = Setup(pc, "clipchamp");

            var outcome = remover.Remove(entry);

            Assert.True(outcome.Ok, outcome.Error);
            Assert.Contains("Windows kept the app's files (3 MB) on disk", outcome.Note);
            Assert.Contains("Clipchamp.Clipchamp_yxz26nhyzhsrt", pc.Deprovisioned);
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void ASecondRemovalIsHarmless()
    {
        var pc = new FakeDebloatPc { Installed = [FakeDebloatPc.Package("Clipchamp.Clipchamp", "yxz26nhyzhsrt")] };
        var changer = new LocalDebloatChanger(pc, new AppxRemover(pc, pc, pc), new NoPlaybooks(), Catalog);

        Assert.True(changer.Change(new("clipchamp")).Ok);
        var again = changer.Change(new("clipchamp"));

        Assert.True(again.Ok);
        Assert.Single(pc.Deprovisioned);
    }

    [Fact]
    public void RefusesAnythingThatIsNotCoveredByTheItem()
    {
        var pc = new FakeDebloatPc();
        var remover = new AppxRemover(pc, pc, pc);
        var item = Catalog.Single(x => x.Id == "clipchamp");

        foreach (var name in new[] { "Microsoft.WindowsStore", "Microsoft.DesktopAppInstaller", "Microsoft.BingNews" })
        {
            var entry = new DebloatEntry(item, [FakeDebloatPc.Package(name)], [], null);
            Assert.False(remover.Remove(entry).Ok, name);
        }
        Assert.Empty(pc.Log);
    }

    [Fact]
    public void RefusesAFrameworkEvenIfTheNameMatches()
    {
        var pc = new FakeDebloatPc();
        var item = Catalog.Single(x => x.Id == "clipchamp");

        var outcome = new AppxRemover(pc, pc, pc).Remove(new DebloatEntry(item, [FakeDebloatPc.Package("Clipchamp.Clipchamp", framework: true)], [], null));

        Assert.False(outcome.Ok);
        Assert.Empty(pc.Log);
    }

    [Fact]
    public void RefusesNamesThatCouldEscapeIntoAnotherRegistryKeyOrCommand()
    {
        var pc = new FakeDebloatPc();
        var item = Catalog.Single(x => x.Id == "clipchamp");
        var evil = new AppxFact("Clipchamp.Clipchamp", @"Clipchamp.Clipchamp_1\..\..\Run", "Clipchamp.Clipchamp_x", false, false, []);

        Assert.False(new AppxRemover(pc, pc, pc).Remove(new DebloatEntry(item, [evil], [], null)).Ok);
        Assert.False(new AppxRemover(pc, pc, pc).Remove(new DebloatEntry(item, [FakeDebloatPc.Package("Clipchamp.Clipchamp", users: "S-1-5-21-1'; calc; '")], [], null)).Ok);
        Assert.Empty(pc.Log);
    }

    [Fact]
    public void PuttingAnAppBackOnlyAllowsFamiliesThatBelongToTheItem()
    {
        var pc = new FakeDebloatPc { Deprovisioned = ["Clipchamp.Clipchamp_yxz26nhyzhsrt", "Microsoft.WindowsStore_8wekyb3d8bbwe"] };
        var changer = new LocalDebloatChanger(pc, new AppxRemover(pc, pc, pc), new NoPlaybooks(), Catalog);

        Assert.False(changer.Change(new("clipchamp", true, ["Microsoft.WindowsStore_8wekyb3d8bbwe"])).Ok);
        Assert.Contains("Microsoft.WindowsStore_8wekyb3d8bbwe", pc.Deprovisioned);
        Assert.True(changer.Change(new("clipchamp", true, ["Clipchamp.Clipchamp_yxz26nhyzhsrt"])).Ok);
        Assert.DoesNotContain("Clipchamp.Clipchamp_yxz26nhyzhsrt", pc.Deprovisioned);
    }

    [Fact]
    public void ProvisionedFamilyIsWorkedOutFromTheFullName() =>
        Assert.Equal("Clipchamp.Clipchamp_yxz26nhyzhsrt", AppxRemover.FamilyOf("Clipchamp.Clipchamp_4.0_neutral_~_yxz26nhyzhsrt"));

    internal sealed class NoPlaybooks : IDebloatPlaybooks
    {
        public string? Remove(DebloatKind kind) => null;
        public string? Restore(DebloatKind kind) => null;
    }
}

public sealed class DebloatPlaybookTests
{
    private static readonly IReadOnlyList<DebloatItem> Catalog = DebloatCatalog.Load();

    private sealed class Recording(Action<DebloatKind>? onRemove = null, string? error = null) : IDebloatPlaybooks
    {
        public List<string> Calls = [];
        public string? Remove(DebloatKind kind) { Calls.Add("remove " + kind); onRemove?.Invoke(kind); return error; }
        public string? Restore(DebloatKind kind) { Calls.Add("restore " + kind); return null; }
    }

    private static LocalDebloatChanger Changer(FakeDebloatPc pc, IDebloatPlaybooks books) => new(pc, new AppxRemover(pc, pc, pc), books, Catalog);

    [Fact]
    public void OneDriveIsNotTouchedWhileFoldersAreBackedUpToIt()
    {
        var pc = new FakeDebloatPc { Facts = new(true, ["Documents"], false, true, false) };
        var books = new Recording();

        var outcome = Changer(pc, books).Change(new("onedrive"));

        Assert.False(outcome.Ok);
        Assert.Empty(books.Calls);
    }

    [Fact]
    public void EdgeIsNotTouchedWhenItIsTheOnlyBrowser()
    {
        var pc = new FakeDebloatPc { Facts = new(false, [], true, false, false) };
        var books = new Recording();

        Assert.False(Changer(pc, books).Change(new("edge")).Ok);
        Assert.Empty(books.Calls);
    }

    [Fact]
    public void AComponentIsOnlyReportedRemovedWhenTheScanAgrees()
    {
        var pc = new FakeDebloatPc { Facts = new(false, [], true, true, false) };

        var stays = Changer(pc, new Recording()).Change(new("edge"));
        Assert.False(stays.Ok);

        var goes = Changer(pc, new Recording(_ => pc.Facts = pc.Facts with { Edge = false })).Change(new("edge"));
        Assert.True(goes.Ok);
    }

    [Fact]
    public void AFailedPlaybookIsReportedWithItsReason()
    {
        var pc = new FakeDebloatPc { Facts = new(true, [], false, true, false) };

        var outcome = Changer(pc, new Recording(error: "setup failed")).Change(new("onedrive"));

        Assert.False(outcome.Ok);
        Assert.Equal("setup failed", outcome.Error);
    }

    [Fact]
    public void RecallIsSwitchedOffAndCanBeSwitchedBackOn()
    {
        var pc = new FakeDebloatPc { Facts = new(false, [], false, true, true) };
        var books = new Recording();
        var changer = Changer(pc, books);

        Assert.True(changer.Change(new("recall")).Ok);
        Assert.True(changer.Change(new("recall", true)).Ok);

        Assert.Equal(["remove Recall", "restore Recall"], books.Calls);
    }
}

public sealed class OneDriveDetectionTests
{
    private static string Folder() => Path.Combine(Path.GetTempPath(), "tidywindow-onedrive-" + Guid.NewGuid().ToString("N"));
    private static bool Installed(string root, bool uninstallEntry = false) => OneDriveDetection.IsInstalled([root], File.Exists, Directory.Exists, () => uninstallEntry);

    [Fact]
    public void WhatOneDrivesUninstallerLeavesBehindIsNotOneDrive()
    {
        // This is what was left on a PC right after OneDrive was uninstalled: its logs, a locked shell extension, and the launcher stub.
        var root = Folder();
        Directory.CreateDirectory(Path.Combine(root, "26.189.0928.0009")); Directory.CreateDirectory(Path.Combine(root, "setup", "logs"));
        File.WriteAllText(Path.Combine(root, "26.189.0928.0009", "FileSyncShell64.dll"), "x");
        File.WriteAllText(Path.Combine(root, "OneDrive.App.exe"), "x");
        File.WriteAllText(Path.Combine(root, "setup", "logs", "Uninstall.log"), "x");

        Assert.False(Installed(root));
        Directory.Delete(root, true);
    }

    [Fact]
    public void TheInstallerThatWindowsAlwaysShipsIsNotEvidence()
    {
        Assert.False(Installed(Folder()));
        // OneDriveSetup.exe is not what the check looks for, so a root that holds only it is not an install.
        var root = Folder(); Directory.CreateDirectory(root); File.WriteAllText(Path.Combine(root, "OneDriveSetup.exe"), "x");
        Assert.False(Installed(root));
        Directory.Delete(root, true);
    }

    [Fact]
    public void TheProgramBesideTheRootOrInAVersionFolderCounts()
    {
        var beside = Folder(); Directory.CreateDirectory(beside); File.WriteAllText(Path.Combine(beside, "OneDrive.exe"), "x");
        var versioned = Folder(); Directory.CreateDirectory(Path.Combine(versioned, "26.189.0928.0009")); File.WriteAllText(Path.Combine(versioned, "26.189.0928.0009", "OneDrive.exe"), "x");

        Assert.True(Installed(beside));
        Assert.True(Installed(versioned));
        Directory.Delete(beside, true); Directory.Delete(versioned, true);
    }

    [Fact]
    public void TheUninstallEntryCounts() => Assert.True(Installed(Folder(), uninstallEntry: true));
}

public sealed class DebloatManagerTests
{
    private static readonly IReadOnlyList<DebloatItem> Catalog = DebloatCatalog.Load();

    private sealed class Changer(Func<DebloatRequest, DebloatOutcome> answer) : IDebloatChanger
    {
        public List<DebloatRequest> Seen = [];
        public DebloatOutcome Change(DebloatRequest request) { Seen.Add(request); return answer(request); }
    }

    private static string Folder() => Path.Combine(Path.GetTempPath(), "tidywindow-debloat-" + Guid.NewGuid().ToString("N"));

    private static DebloatEntry Entry(string id, params AppxFact[] packages) => new(Catalog.Single(x => x.Id == id), packages, [], null);

    [Fact]
    public async Task ARemovalAsksForPermissionFirstAndIsRememberedForPuttingBack()
    {
        var unlocked = 0;
        var changer = new Changer(_ => new(true, null, null, ["Clipchamp.Clipchamp_yxz26nhyzhsrt"]));
        var folder = Folder();
        var manager = new DebloatManager(changer, () => { unlocked++; return Task.CompletedTask; }, new DebloatHistory(folder));

        var outcome = await manager.RemoveAsync(Entry("clipchamp", FakeDebloatPc.Package("Clipchamp.Clipchamp", "yxz26nhyzhsrt")));

        Assert.True(outcome.Ok);
        Assert.Equal(1, unlocked);
        Assert.Equal("clipchamp", changer.Seen.Single().Id);
        var kept = new DebloatHistory(folder).Get("clipchamp")!;
        Assert.Equal(["Clipchamp.Clipchamp_yxz26nhyzhsrt"], kept.Families);
        Directory.Delete(folder, true);
    }

    [Fact]
    public async Task AFailedRemovalIsNotRemembered()
    {
        var folder = Folder();
        var manager = new DebloatManager(new Changer(_ => new(false, "no")), () => Task.CompletedTask, new DebloatHistory(folder));

        Assert.False((await manager.RemoveAsync(Entry("clipchamp", FakeDebloatPc.Package("Clipchamp.Clipchamp")))).Ok);

        Assert.Empty(manager.History.All());
    }

    [Fact]
    public async Task ALockedItemNeverReachesTheHelper()
    {
        var changer = new Changer(_ => new(true));
        var manager = new DebloatManager(changer, () => Task.CompletedTask, new DebloatHistory(Folder()));
        var locked = new DebloatEntry(Catalog.Single(x => x.Id == "edge"), [], [], DebloatScan.OnlyBrowserLock);

        Assert.False((await manager.RemoveAsync(locked)).Ok);

        Assert.Empty(changer.Seen);
    }

    [Fact]
    public async Task AHelperThatHasGoneAwayIsRestartedOnce()
    {
        var unlocked = 0; var calls = 0;
        var changer = new Changer(_ => ++calls == 1 ? throw new IOException("gone") : new(true, null, null, []));
        var manager = new DebloatManager(changer, () => { unlocked++; return Task.CompletedTask; }, new DebloatHistory(Folder()));

        Assert.True((await manager.RemoveAsync(Entry("clipchamp", FakeDebloatPc.Package("Clipchamp.Clipchamp")))).Ok);

        Assert.Equal(2, unlocked);
    }

    [Fact]
    public async Task PuttingBackAllowsTheAppAndInstallsItAgainThenForgetsIt()
    {
        var folder = Folder();
        var history = new DebloatHistory(folder);
        history.Add(new("clipchamp", "Clipchamp", DateTimeOffset.Now, ["Clipchamp.Clipchamp_yxz26nhyzhsrt"]));
        var changer = new Changer(_ => new(true));
        var commands = new List<CommandSpec>();
        var reinstaller = new DebloatReinstaller(new RecordingRunner(commands), new FixedLocator("winget.exe"));
        var manager = new DebloatManager(changer, () => Task.CompletedTask, history, reinstaller);

        var outcome = await manager.RestoreAsync(Catalog.Single(x => x.Id == "clipchamp"));

        Assert.True(outcome.Ok, outcome.Error);
        var request = changer.Seen.Single();
        Assert.True(request.Restore);
        Assert.Equal(["Clipchamp.Clipchamp_yxz26nhyzhsrt"], request.Families);
        var install = commands.Single();
        Assert.Contains("msstore", install.Arguments); Assert.Contains("9P1J8S7CCWWT", install.Arguments);
        Assert.Empty(history.All());
        Directory.Delete(folder, true);
    }

    [Fact]
    public async Task AFailedReinstallKeepsTheRecordSoItCanBeTriedAgain()
    {
        var history = new DebloatHistory(Folder());
        history.Add(new("clipchamp", "Clipchamp", DateTimeOffset.Now, ["Clipchamp.Clipchamp_yxz26nhyzhsrt"]));
        var reinstaller = new DebloatReinstaller(new RecordingRunner([], exit: 1), new FixedLocator("winget.exe"));
        var manager = new DebloatManager(new Changer(_ => new(true)), () => Task.CompletedTask, history, reinstaller);

        Assert.False((await manager.RestoreAsync(Catalog.Single(x => x.Id == "clipchamp"))).Ok);

        Assert.NotNull(history.Get("clipchamp"));
    }

    [Fact]
    public async Task WinGetIdsUseTheWinGetSourceAndStoreIdsUseTheStore()
    {
        var commands = new List<CommandSpec>();
        var reinstaller = new DebloatReinstaller(new RecordingRunner(commands), new FixedLocator("winget.exe"));

        await reinstaller.InstallAsync(Catalog.Single(x => x.Id == "onedrive"));

        Assert.Contains("winget", commands.Single().Arguments); Assert.Contains("Microsoft.OneDrive", commands.Single().Arguments);
        Assert.True(DebloatReinstaller.IsStoreProduct("9P1J8S7CCWWT")); Assert.False(DebloatReinstaller.IsStoreProduct("Microsoft.Edge"));
    }

    [Fact]
    public void TheHistorySurvivesTheAppAndMergesFamilies()
    {
        var folder = Folder();
        var first = new DebloatHistory(folder);
        first.Add(new("candy-crush", "Candy", DateTimeOffset.Now, ["king.com.A_x"]));
        first.Add(new("candy-crush", "Candy", DateTimeOffset.Now, ["king.com.B_x", "king.com.A_x"]));

        var second = new DebloatHistory(folder);

        Assert.Equal(["king.com.A_x", "king.com.B_x"], second.Get("candy-crush")!.Families.Order().ToList());
        second.Remove("candy-crush");
        Assert.Empty(new DebloatHistory(folder).All());
        Directory.Delete(folder, true);
    }

    private sealed class RecordingRunner(List<CommandSpec> commands, int exit = 0) : ICommandRunner
    {
        public Task<CommandResult> RunAsync(CommandSpec command, CancellationToken cancellationToken = default) { commands.Add(command); return Task.FromResult(new CommandResult(exit, "", "")); }
    }

    private sealed class FixedLocator(string winget) : IExecutableLocator
    {
        public string? Find(ManagerKind manager) => manager == ManagerKind.Winget ? winget : null;
        public string PowerShell => "powershell.exe";
    }
}

public sealed class DebloatHostTests
{
    private sealed class Recorder(List<DebloatRequest> seen) : IDebloatChanger
    {
        public DebloatOutcome Change(DebloatRequest request) { seen.Add(request); return new(true, null, "done", ["f_x"]); }
    }

    [Fact]
    public async Task TheHelperPassesARequestToTheChangerAndReturnsTheResult()
    {
        var seen = new List<DebloatRequest>();
        using var session = new DebloatSession(pipe => DebloatHost.RunAsync([DebloatHost.Command, pipe], new Recorder(seen), () => true, TimeSpan.FromSeconds(20)));
        await session.OpenAsync();

        var outcome = session.Change(new("clipchamp"));

        Assert.True(outcome.Ok);
        Assert.Equal("done", outcome.Note);
        Assert.Equal(["f_x"], outcome.Families);
        Assert.Equal("clipchamp", seen.Single().Id);
    }

    [Fact]
    public async Task WithoutAdministratorRightsNothingIsChanged()
    {
        var seen = new List<DebloatRequest>();
        using var session = new DebloatSession(pipe => DebloatHost.RunAsync([DebloatHost.Command, pipe], new Recorder(seen), () => false, TimeSpan.FromSeconds(20)));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => session.OpenAsync());

        Assert.Empty(seen);
    }

    [Fact]
    public async Task DecliningThePermissionPromptStopsCleanly()
    {
        using var session = new DebloatSession(_ => throw new OperationCanceledException("declined"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.OpenAsync());
        Assert.False(session.IsOpen);
    }
}

public sealed class AppxStoreRecordsTests
{
    private static readonly IReadOnlyList<DebloatItem> Catalog = DebloatCatalog.Load();

    [Fact]
    public void AnAppIsReadFromItsFullNameAndManifestPath()
    {
        var package = AppxStoreRecords.Package("Clipchamp.Clipchamp_4.0.0.0_neutral_~_yxz26nhyzhsrt", @"C:\Program Files\WindowsApps\Clipchamp.Clipchamp_4.0.0.0_neutral_~_yxz26nhyzhsrt\AppxManifest.xml")!;

        Assert.Equal("Clipchamp.Clipchamp", package.Name);
        Assert.Equal("Clipchamp.Clipchamp_yxz26nhyzhsrt", package.FamilyName);
        Assert.Equal(@"C:\Program Files\WindowsApps\Clipchamp.Clipchamp_4.0.0.0_neutral_~_yxz26nhyzhsrt", package.InstallLocation);
        Assert.False(package.NonRemovable);
        Assert.Empty(package.Users);
    }

    [Fact]
    public void ABundlesManifestIsOneFolderDeeperAndTheAppIsItsParent() =>
        Assert.Equal(@"C:\Program Files\WindowsApps\Acme.Tool_1.0_neutral_~_abc", AppxStoreRecords.FolderOf(@"C:\Program Files\WindowsApps\Acme.Tool_1.0_neutral_~_abc\AppxMetadata\AppxBundleManifest.xml"));

    [Theory]
    [InlineData("Acme.Tool_1.0.0.0_x64__abc", true)]
    [InlineData("Acme.Tool_1.0.0.0_neutral_~_abc", true)]
    [InlineData("Acme.Tool_1.0.0.0_neutral_split.language-de_abc", false)]
    [InlineData("Acme.Tool_1.0.0.0_neutral_split.scale-100_abc", false)]
    [InlineData("not-a-package", false)]
    [InlineData(@"Acme.Tool_1\..\Run_x64__abc", false)]
    public void OnlyTheAppItselfIsReadNotItsLanguageAndScalePacks(string fullName, bool expected)
    {
        Assert.Equal(expected, AppxStoreRecords.Package(fullName, "") is not null);
        Assert.Equal(expected, AppxStoreRecords.Staged(fullName, "") is not null);
    }

    [Fact]
    public void PackagesFromTheStoreAreAddedOnceToWhatWindowsListed()
    {
        var listed = new[] { FakeDebloatPc.Package("Microsoft.BingNews") };
        var store = new[]
        {
            // The store names the bundle where Windows lists the package inside it: the same app, so it must not be counted twice.
            AppxStoreRecords.Package("Microsoft.BingNews_1.0.0.0_neutral_~_8wekyb3d8bbwe", "")!,
            AppxStoreRecords.Package("Clipchamp.Clipchamp_4.0.0.0_neutral_~_yxz26nhyzhsrt", "")!
        };

        var merged = AppxStoreRecords.Merge(listed, store);

        Assert.Equal(["Microsoft.BingNews", "Clipchamp.Clipchamp"], merged.Select(x => x.Name));
    }

    [Fact]
    public void AnAppSeenOnlyInTheStoreIsFoundByTheScanWithoutAdministratorRights()
    {
        // What was left after someone right-clicked Clipchamp and chose Uninstall: gone for them, still staged for every new account.
        var pc = new FakeDebloatPc { Staged = [AppxStoreRecords.Staged("Clipchamp.Clipchamp_4.0.0.0_neutral_~_yxz26nhyzhsrt", @"C:\Program Files\WindowsApps\Clipchamp.Clipchamp_4.0.0.0_neutral_~_yxz26nhyzhsrt\AppxManifest.xml")!] };

        var entry = Assert.Single(DebloatScan.Run(pc, Catalog).Entries);

        Assert.Equal("clipchamp", entry.Id);
        Assert.Empty(entry.Packages);
        Assert.Equal(@"C:\Program Files\WindowsApps\Clipchamp.Clipchamp_4.0.0.0_neutral_~_yxz26nhyzhsrt", Assert.Single(entry.Provisioned).InstallLocation);
    }

    [Fact]
    public void ARemovedAppThatWasOnlyStagedIsDeprovisionedAndGone()
    {
        var staged = AppxStoreRecords.Staged("Clipchamp.Clipchamp_4.0.0.0_neutral_~_yxz26nhyzhsrt", "")!;
        var pc = new FakeDebloatPc { Staged = [staged] };

        var outcome = new AppxRemover(pc, pc, pc).Remove(DebloatScan.Find(pc, Catalog, "clipchamp")!);

        Assert.True(outcome.Ok, outcome.Error);
        Assert.Empty(pc.Staged);
        Assert.Contains("Clipchamp.Clipchamp_yxz26nhyzhsrt", pc.Deprovisioned);
    }
}

public sealed class DebloatCleanupTests
{
    [Theory]
    [InlineData("OneDrive", true)]
    [InlineData("onedrive", true)]
    [InlineData("OneDriveSetup", true)]
    [InlineData("OneDriveBackupTool", false)]
    [InlineData("Dropbox", false)]
    public void OnlyOneDrivesOwnStartupEntriesAreRemoved(string name, bool expected) => Assert.Equal(expected, DebloatCleanup.IsOneDriveRunValue(name));

    [Theory]
    [InlineData("MicrosoftEdgeAutoLaunch_3F1A9C0D4B6E", true)]
    [InlineData("MicrosoftEdgeAutoLaunch_", true)]
    [InlineData("Microsoft Edge Update", true)]
    [InlineData("Microsoft Teams", false)]
    [InlineData("GoogleChromeAutoLaunch_1", false)]
    public void OnlyEdgesOwnStartupEntriesAreRemoved(string name, bool expected) => Assert.Equal(expected, DebloatCleanup.IsEdgeRunValue(name));

    [Theory]
    [InlineData("OneDrive Standalone Update Task-S-1-5-21-2263392280-518463016-1748006429-1001", true)]
    [InlineData("OneDrive Reporting Task-S-1-5-21-2263392280-518463016-1748006429-1001", true)]
    [InlineData("OneDrive Per-Machine Standalone Update Task", true)]
    [InlineData("OneDrive Standalone Update Task'; calc; '", false)]
    [InlineData("OneDrive Backup Helper", false)]
    [InlineData("MicrosoftEdgeUpdateTaskMachineCore", false)]
    public void OnlyOneDrivesUpdateAndReportingTasksAreRemoved(string name, bool expected) => Assert.Equal(expected, DebloatCleanup.IsOneDriveTask(name));

    [Theory]
    [InlineData("S-1-5-21-2263392280-518463016-1748006429-1001", true)]
    [InlineData("S-1-12-1-1234-5678-9012-3456", true)]
    [InlineData("S-1-5-18", false)]
    [InlineData("S-1-5-19", false)]
    [InlineData("S-1-5-21-1-2-3-1001_Classes", false)]
    [InlineData("S-1-5-21-1'; calc; '", false)]
    [InlineData(null, false)]
    public void OnlyPeoplesAccountsAreVisited(string? sid, bool expected) => Assert.Equal(expected, DebloatCleanup.IsAccountSid(sid));

    [Fact]
    public void EdgesShortcutsAreFoundInsideEachProfileAndNowhereElse()
    {
        var links = DebloatCleanup.EdgeShortcuts(@"C:\Users\sam").ToList();

        Assert.Equal(3, links.Count);
        Assert.All(links, x => { Assert.StartsWith(@"C:\Users\sam\", x); Assert.EndsWith("Microsoft Edge.lnk", x); });
        Assert.Contains(links, x => x.Contains("User Pinned", StringComparison.Ordinal) && x.Contains("TaskBar", StringComparison.Ordinal));
    }

    [Fact]
    public void TheSideBarEntryIsOneDrivesWellKnownId() => Assert.Equal("{018D5C66-4533-4307-9B53-224DE2ED1FE6}", DebloatCleanup.OneDriveNavigationPane);
}

public sealed class DebloatRestoreAllTests
{
    private static readonly IReadOnlyList<DebloatItem> Catalog = DebloatCatalog.Load();

    private sealed class Changer(Func<DebloatRequest, DebloatOutcome> answer) : IDebloatChanger
    {
        public List<DebloatRequest> Seen = [];
        public DebloatOutcome Change(DebloatRequest request) { Seen.Add(request); return answer(request); }
    }

    private static string Folder() => Path.Combine(Path.GetTempPath(), "tidywindow-debloat-" + Guid.NewGuid().ToString("N"));

    private static DebloatManager Manager(Changer changer, DebloatHistory history, Func<Task>? unlock = null) => new(changer, unlock ?? (() => Task.CompletedTask), history);

    private static DebloatHistory Removed(params string[] ids)
    {
        var history = new DebloatHistory(Folder());
        foreach (var id in ids) history.Add(new(id, id, DateTimeOffset.Now, [id + "_fam"]));
        return history;
    }

    [Fact]
    public async Task EveryItemIsPutBackAndTheHelperIsAskedForOnlyOnce()
    {
        var unlocked = 0;
        var history = Removed("clipchamp", "maps", "todo");
        var changer = new Changer(_ => new(true));
        var manager = Manager(changer, history, () => { unlocked++; return Task.CompletedTask; });
        var shown = new List<string>();

        var batch = await manager.RestoreAllAsync(Catalog.Where(x => x.Id is "clipchamp" or "maps" or "todo").ToList(), (item, outcome) => shown.Add($"{item.Id}:{(outcome is null ? "start" : outcome.Ok ? "ok" : "failed")}"));

        Assert.False(batch.Declined);
        Assert.Equal(3, batch.Results.Count(x => x.Outcome.Ok));
        Assert.Equal(3, changer.Seen.Count(x => x.Restore));
        Assert.Empty(history.All());
        Assert.Equal(["clipchamp:start", "clipchamp:ok", "maps:start", "maps:ok", "todo:start", "todo:ok"], shown);
    }

    [Fact]
    public async Task OneItemThatFailsDoesNotStopTheOthersAndStaysListed()
    {
        var history = Removed("clipchamp", "maps");
        var manager = Manager(new Changer(r => r.Id == "clipchamp" ? new(false, "no") : new(true)), history);

        var batch = await manager.RestoreAllAsync(Catalog.Where(x => x.Id is "clipchamp" or "maps").ToList());

        Assert.Equal(["maps"], batch.Results.Where(x => x.Outcome.Ok).Select(x => x.Item.Id));
        Assert.Equal(["clipchamp"], history.All().Select(x => x.Id));
    }

    [Fact]
    public async Task DecliningThePermissionPromptStopsTheWholeBatch()
    {
        var history = Removed("clipchamp", "maps");
        var asked = 0;
        var manager = Manager(new Changer(_ => new(true)), history, () => { asked++; throw new OperationCanceledException("declined"); });

        var batch = await manager.RestoreAllAsync(Catalog.Where(x => x.Id is "clipchamp" or "maps").ToList());

        Assert.True(batch.Declined);
        Assert.Empty(batch.Results);
        Assert.Equal(1, asked);
        Assert.Equal(2, history.All().Count);
    }

    [Fact]
    public async Task AHelperThatKeepsFailingEndsTheBatchInsteadOfAskingForEveryItem()
    {
        var history = Removed("clipchamp", "maps", "todo");
        var changer = new Changer(_ => throw new IOException("gone"));
        var manager = Manager(changer, history);

        var batch = await manager.RestoreAllAsync(Catalog.Where(x => x.Id is "clipchamp" or "maps" or "todo").ToList());

        Assert.False(batch.Declined);
        var failed = Assert.Single(batch.Results);
        Assert.False(failed.Outcome.Ok);
        Assert.Equal(3, history.All().Count);
    }

    [Fact]
    public async Task ClosingThePageStopsTheBatchWithoutBlamingThePerson()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        var manager = Manager(new Changer(_ => new(true)), Removed("clipchamp"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.RestoreAllAsync(Catalog.Where(x => x.Id == "clipchamp").ToList(), cancellationToken: cancel.Token));
    }
}

public sealed class DebloatLiveTests
{
    // Read-only: scans the real PC with the shipped catalog. Nothing is removed. Opt in like the other live checks.
    [Fact]
    public void TheShippedCatalogCanBeScannedOnThisPc()
    {
        if (Environment.GetEnvironmentVariable("TIDYWINDOW_LIVE_TESTS") != "1" || !OperatingSystem.IsWindows()) return;
        var catalog = DebloatCatalog.Load();
        var result = DebloatScan.Run(new WindowsDebloatSystem(elevated: false), catalog);
        Assert.Equal(catalog.Count, result.Entries.Count + result.NotOnThisPc);
        foreach (var entry in result.Entries) Console.WriteLine($"{entry.Id,-20} apps={entry.AppCount} packages={entry.Packages.Count} lock={entry.Lock}");
    }
}

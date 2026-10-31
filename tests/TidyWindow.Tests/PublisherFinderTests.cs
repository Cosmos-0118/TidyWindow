using TidyWindow.Next.Core;
using Xunit;

namespace TidyWindow.Next.Tests;

public sealed class PublisherNamesTests
{
    [Theory]
    [InlineData("Google LLC", "Google LLC")]
    [InlineData("  Micro-Star   INT'L CO., LTD.  ", "Micro-Star INT'L CO., LTD.")]
    [InlineData("\"Acme, Inc.\"", "Acme, Inc.")]
    [InlineData("", "")]
    [InlineData("Unknown", "")]
    [InlineData("<Unknown>", "")]
    [InlineData("N/A", "")]
    [InlineData("Company Name", "")]
    [InlineData("-", "")]
    [InlineData("@%SystemRoot%\\system32\\tzres.dll,-100", "")]
    [InlineData("$(@%SystemRoot%\\system32\\foo.dll,-1)", "")]
    [InlineData("ms-resource:PublisherName", "")]
    [InlineData("DESKTOP-ABC123\\me", "")]
    [InlineData("S-1-5-21-1-2-3", "")]
    [InlineData("someone@example.com", "")]
    public void TextIsKeptOnlyWhenItReadsLikeACompany(string text, string expected) => Assert.Equal(expected, PublisherNames.Tidy(text));

    [Theory]
    [InlineData("CN=\"Anthropic, PBC\", O=\"Anthropic, PBC\", L=San Francisco, S=California, C=US, SERIALNUMBER=4860621, OID.2.5.4.15=Private Organization", "Anthropic, PBC")]
    [InlineData("CN=Tailscale Inc., O=Tailscale Inc., L=Toronto, S=Ontario, C=CA", "Tailscale Inc.")]
    [InlineData("CN=voidtools PTY LTD, O=voidtools PTY LTD, C=AU", "voidtools PTY LTD")]
    [InlineData("CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US", "Microsoft Corporation")]
    [InlineData("O=Acme Ltd, CN=Acme Release Signing", "Acme Ltd")]
    [InlineData("CN=Solo Developer, C=GB", "Solo Developer")]
    [InlineData("CN=Acme\\, Inc., C=US", "Acme, Inc.")]
    [InlineData("C=US, L=Nowhere", "")]
    [InlineData("", "")]
    public void TheCompanyIsReadFromACertificateSubject(string subject, string expected) => Assert.Equal(expected, PublisherNames.FromSubject(subject));

    [Theory]
    [InlineData("CN=Microsoft Windows Hardware Compatibility Publisher, O=Microsoft Corporation, L=Redmond, C=US")]
    [InlineData("CN=Microsoft Windows Third Party Component CA 2014, O=Microsoft Corporation")]
    [InlineData("CN=Microsoft Windows Early Launch Anti-malware Publisher, O=Microsoft Corporation")]
    public void MicrosoftSigningADriverForSomeoneElseDoesNotMakeItMicrosofts(string subject) => Assert.Equal("", PublisherNames.FromSubject(subject));
}

public sealed class PublisherFinderTests
{
    private static UninstallEntry App(string name, string publisher, string location = "", string icon = "", string uninstall = "", bool update = false) =>
        new($@"HKLM\x\{name}", name, "1.0", publisher, uninstall, "", location, icon, false, false, false, update);

    private static FileFacts File(string company = "", StartupTrust trust = StartupTrust.Verified, string signer = "") => new(true, company, "", "", trust, signer);

    private static FoundPublisher Find(FakeStartupPc pc, string name, string exe, FileFacts file, string hint = "", bool store = false) =>
        new PublisherFinder(pc).Find(name, exe, file, hint, store);

    [Fact]
    public void TheFilesOwnCompanyComesFirstWhenItHasOne()
    {
        var pc = new FakeStartupPc();
        var found = Find(pc, "Tool", @"C:\Program Files\Acme\tool.exe", File("Acme Corp", signer: "Acme Signing Ltd"));
        Assert.Equal(new FoundPublisher("Acme Corp", PublisherSource.FileProperties), found);
    }

    [Fact]
    public void AGoBinaryWithNoVersionInfoIsNamedByItsSignature()
    {
        var pc = new FakeStartupPc();
        var found = Find(pc, "Claude", @"C:\Program Files\Claude\svc.exe", File(signer: "Anthropic, PBC"));
        Assert.Equal(new FoundPublisher("Anthropic, PBC", PublisherSource.Signature), found);
    }

    [Theory]
    [InlineData(StartupTrust.Unsigned)]
    [InlineData(StartupTrust.Invalid)]
    [InlineData(StartupTrust.Unknown)]
    public void ASignatureThatDoesNotCheckOutNamesNobody(StartupTrust trust)
    {
        var found = Find(new FakeStartupPc(), "Tool", @"C:\Program Files\Acme\tool.exe", File(trust: trust, signer: "Totally Legit Inc."));
        Assert.Equal(FoundPublisher.None, found);
    }

    [Fact]
    public void AStorePackagesManifestBeatsAJunkCompanyInTheFile()
    {
        var pc = new FakeStartupPc();
        pc.Packages["WhatsAppDesktop_"] = "WhatsApp Inc.";
        var found = Find(pc, "WhatsApp", @"C:\Program Files\WindowsApps\WhatsAppDesktop_2.2_x64__abc\WhatsApp.Root.exe", File("WhatsApp.Root"));
        Assert.Equal(new FoundPublisher("WhatsApp Inc.", PublisherSource.Package), found);
    }

    [Fact]
    public void TheManifestPublisherOnAStartupTaskIsUsedEvenWhenTheProgramCannotBeRead()
    {
        var found = Find(new FakeStartupPc(), "Todos", "", File(trust: StartupTrust.Unknown), "Microsoft Corporation", store: true);
        Assert.Equal(new FoundPublisher("Microsoft Corporation", PublisherSource.Package), found);
    }

    [Fact]
    public void ACompanyThatIsJustTheFilesOwnNameIsNotACompany()
    {
        var found = Find(new FakeStartupPc(), "Chat", @"C:\Tools\Chat\chat.exe", File("Chat", signer: "Chat Labs Ltd"));
        Assert.Equal(new FoundPublisher("Chat Labs Ltd", PublisherSource.Signature), found);
    }

    [Fact]
    public void AnInstalledAppsFolderNamesTheCompanyBehindAnyFileInsideIt()
    {
        var pc = new FakeStartupPc();
        pc.Installed.Add(App("Discord", "Discord Inc.", icon: @"C:\Users\me\AppData\Local\Discord\app.ico"));
        var found = Find(pc, "Discord", @"C:\Users\me\AppData\Local\Discord\app-1.0.9\Discord.exe", File(trust: StartupTrust.Unsigned));
        Assert.Equal(new FoundPublisher("Discord Inc.", PublisherSource.InstalledApp), found);
    }

    [Fact]
    public void TheInstallLocationUninstallStringAndIconCountAndTheDeepestFolderWins()
    {
        var pc = new FakeStartupPc();
        pc.Installed.Add(App("Suite", "Big Vendor", location: @"C:\Program Files\Vendor"));
        pc.Installed.Add(App("Helper", "Small Vendor", uninstall: "\"C:\\Program Files\\Vendor\\Helper\\unins000.exe\" /SILENT"));
        Assert.Equal("Small Vendor", Find(pc, "x", @"C:\Program Files\Vendor\Helper\run.exe", File()).Name);
        Assert.Equal("Big Vendor", Find(pc, "x", @"C:\Program Files\Vendor\Other\run.exe", File()).Name);
        Assert.Equal("Big Vendor", Find(pc, "x", @"C:\program files\vendor\run.exe", File()).Name);
    }

    [Theory]
    [InlineData(@"C:\Program Files")]
    [InlineData(@"C:\Program Files\Common Files")]
    [InlineData(@"C:\Users\me")]
    [InlineData(@"C:\Users\me\AppData\Local")]
    [InlineData(@"C:\Windows")]
    [InlineData(@"C:\Windows\Installer\{ABC}")]
    [InlineData(@"C:\Users\me\Downloads")]
    [InlineData(@"C:\Users\me\AppData\Local\Temp\setup")]
    public void ASharedFolderIsNeverEvidenceOfWhoMadeAProgram(string location)
    {
        var pc = new FakeStartupPc();
        pc.Installed.Add(App("Installer thing", "Some Vendor", location: location));
        Assert.Equal(FoundPublisher.None, Find(pc, "Other", location + @"\other.exe", File()));
        Assert.Equal(FoundPublisher.None, Find(pc, "Other", @"C:\Program Files\Else\other.exe", File()));
    }

    [Fact]
    public void ProgramsSharingAFolderWithDifferentPublishersAreNotGuessedBetween()
    {
        var pc = new FakeStartupPc();
        pc.Installed.Add(App("Alpha", "Alpha Co", location: @"C:\Apps\Shared"));
        pc.Installed.Add(App("Beta", "Beta Co", location: @"C:\Apps\Shared"));
        Assert.Equal(FoundPublisher.None, Find(pc, "Gamma", @"C:\Apps\Shared\g.exe", File()));
        Assert.Equal("Beta Co", Find(pc, "Beta", @"C:\Apps\Shared\g.exe", File()).Name);
    }

    [Fact]
    public void UpdatesAndEntriesWithNoPublisherAreIgnored()
    {
        var pc = new FakeStartupPc();
        pc.Installed.Add(App("Patch", "Patch Co", location: @"C:\Apps\Patched", update: true));
        pc.Installed.Add(App("Blank", "", location: @"C:\Apps\Blank"));
        Assert.Equal(FoundPublisher.None, Find(pc, "Patch", @"C:\Apps\Patched\p.exe", File()));
        Assert.Equal(FoundPublisher.None, Find(pc, "Blank", @"C:\Apps\Blank\b.exe", File()));
    }

    [Fact]
    public void AnItemWhoseFileIsGoneIsStillNamedByWhereItUsedToLive()
    {
        var pc = new FakeStartupPc();
        pc.Installed.Add(App("Steam", "Valve Corporation", location: @"C:\Program Files (x86)\Steam"));
        var found = Find(pc, "Steam", @"C:\Program Files (x86)\Steam\steam.exe", new FileFacts(false, "", "", "", StartupTrust.Unknown));
        Assert.Equal(new FoundPublisher("Valve Corporation", PublisherSource.InstalledApp), found);
    }

    [Theory]
    [InlineData("Discord", "Discord 1.0.9 (64-bit)", true)]
    [InlineData("Discord", "Discord Canary", false)]
    [InlineData("Up", "Up", false)]
    public void WhenNoFolderMatchesTheNameOfTheItemIsMatchedExactly(string itemName, string installedName, bool matches)
    {
        var pc = new FakeStartupPc();
        pc.Installed.Add(App(installedName, "Named Vendor"));
        var found = Find(pc, itemName, @"C:\Somewhere\Else\run.exe", File());
        Assert.Equal(matches ? "Named Vendor" : "", found.Name);
    }

    [Fact]
    public void AnUnverifiedFileCannotBorrowMicrosoftsNameFromAWritableList()
    {
        var pc = new FakeStartupPc();
        pc.Installed.Add(App("Fake", "Microsoft Corporation", location: @"C:\Users\me\AppData\Local\Evil"));
        Assert.Equal(FoundPublisher.None, Find(pc, "Fake", @"C:\Users\me\AppData\Local\Evil\x.exe", File(trust: StartupTrust.Unsigned)));
        Assert.Equal(FoundPublisher.None, Find(pc, "Fake", @"C:\x\y.exe", File(trust: StartupTrust.Unsigned), "Microsoft Corporation"));
    }

    [Fact]
    public void ATaskAuthorCountsOnlyWhenItIsACompanyNotAnAccount()
    {
        var pc = new FakeStartupPc();
        Assert.Equal(new FoundPublisher("Advanced Micro Devices, Inc.", PublisherSource.TaskAuthor), Find(pc, "StartCN", @"C:\x\a.exe", File(trust: StartupTrust.Unsigned), "Advanced Micro Devices, Inc."));
        Assert.Equal(FoundPublisher.None, Find(pc, "Job", @"C:\x\a.exe", File(trust: StartupTrust.Unsigned), @"DESKTOP-1\me"));
        Assert.Equal(FoundPublisher.None, Find(pc, "Job", @"C:\x\a.exe", File(trust: StartupTrust.Unsigned), "$(@%SystemRoot%\\system32\\x.dll,-3)"));
    }

    [Fact]
    public void WhenEverySourceIsSilentThePublisherIsLeftBlankRatherThanGuessed()
    {
        Assert.Equal(FoundPublisher.None, Find(new FakeStartupPc(), "Mystery", @"C:\Program Files\Mystery\m.exe", File(trust: StartupTrust.Unsigned)));
    }
}

public sealed class PublisherInTheStartupScanTests
{
    [Fact]
    public void ABackgroundServiceWithNoVersionInfoGetsItsSignersNameAndSaysWhere()
    {
        var pc = new FakeStartupPc();
        pc.Files[@"C:\Program Files\Claude\cowork-svc.exe"] = new(true, "", "", "", StartupTrust.Verified, "Anthropic, PBC");
        pc.ServiceList.Add(new("CoworkVMService", "Claude", "", @"C:\Program Files\Claude\cowork-svc.exe", 0x10, 2, false, "LocalSystem", []));
        var entry = Assert.Single(StartupScan.Run(pc).Entries);
        Assert.Equal("Anthropic, PBC", entry.Publisher);
        Assert.Equal(PublisherSource.Signature, entry.PublisherFrom);
    }

    [Fact]
    public void AStoreAppIsNamedByItsPackageEvenWhenItsProgramCannotBeOpened()
    {
        var pc = new FakeStartupPc();
        pc.Store.Add(new("Contoso.App_abc", "Startup", 2, "Contoso.App_1.0_x64__abc", "Contoso", "Contoso Ltd.", ""));
        var entry = Assert.Single(StartupScan.Run(pc).Entries);
        Assert.Equal(new("Contoso Ltd.", PublisherSource.Package), (entry.Publisher, entry.PublisherFrom));
    }

    [Fact]
    public void ANewlyFoundPublisherFeedsTheSafetyRules()
    {
        var pc = new FakeStartupPc();
        pc.Files[@"C:\Program Files\Lights\ledsvc.exe"] = new(true, "", "", "", StartupTrust.Verified, "MICRO-STAR INTERNATIONAL CO., LTD.");
        pc.Runs.Add(new(StartupScope.Machine, false, "Lights", @"C:\Program Files\Lights\ledsvc.exe"));
        Assert.Equal(StartupRisk.Careful, Assert.Single(StartupScan.Run(pc).Entries).Risk);
    }

    [Fact]
    public void AnItemNothingCanNameStaysUnnamed()
    {
        var pc = new FakeStartupPc();
        pc.Add(@"C:\Program Files\Mystery\m.exe", "", StartupTrust.Unsigned);
        pc.Runs.Add(new(StartupScope.User, false, "Mystery", @"C:\Program Files\Mystery\m.exe"));
        var entry = Assert.Single(StartupScan.Run(pc).Entries);
        Assert.Equal("", entry.Publisher);
        Assert.Equal(PublisherSource.None, entry.PublisherFrom);
    }
}

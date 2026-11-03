using System.Runtime.Versioning;
using TidyWindow.Next.Core;
using Xunit;

namespace TidyWindow.Next.Tests;

/// <summary>
/// What a damaged Windows Installer cache needs: an update's own file put back before its registration is given up on, a saved copy that exists but is garbage
/// counted as lost, an Inno uninstaller that lost its data file, a quiet failure tried once with a window, and installers that arrive in a .zip.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RepairCacheRestoreTests : IDisposable
{
    private readonly RepairSandbox sandbox = new();
    private readonly string folder = Path.Combine(Path.GetTempPath(), "tw-cache-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly RepairHistory history;
    public RepairCacheRestoreTests() => history = new RepairHistory(folder);
    public void Dispose() { sandbox.Dispose(); try { Directory.Delete(folder, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }

    private static readonly string PatchCode = InstallRepair.Unpack(BrokenPython.PatchPacked);
    private const string Saved = @"C:\Windows\Installer\1f2e3d.msp";
    private const string Update = @"C:\Downloads\kb5012345.msp";

    private RepairService Service(FakePc pc, FakeOps ops, FakeOps? elevated = null, FakeFetcher? fetcher = null) =>
        new(pc, sandbox.User(), sandbox.Elevated(), _ => Task.CompletedTask, () => { }, history, ops, elevated ?? ops, fetcher);

    /// <summary>Python's machine record with an update it wears whose saved file is gone; the registration is real keys, so removing it can be seen and undone.</summary>
    private (FakePc Pc, string Program, string Record) WithDeadUpdate(bool damaged = false)
    {
        var data = BrokenPython.UserData("S-1-5-18");
        var program = $@"{data}\Products\{BrokenPython.Packed}\Patches\{BrokenPython.PatchPacked}";
        var record = $@"{data}\Patches\{BrokenPython.PatchPacked}";
        sandbox.Put(program, ("State", 1));
        sandbox.Put(record, ("LocalPackage", Saved));
        sandbox.Put($@"{data}\Products\{BrokenPython.Packed}\InstallProperties", ("DisplayName", "Python"));
        var pc = new FakePc { Patches = [new(BrokenPython.PatchPacked, Saved, ["Python"], [program, record], damaged)] };
        if (damaged) pc.Files.Add(Saved);
        return (pc, program, record);
    }

    private static FakeOps OpsWithUpdateIn(string chosenFolder, string code)
    {
        var ops = new FakeOps();
        ops.PatchFolders[chosenFolder] = [Update];
        ops.PatchCodes[Update] = code;
        return ops;
    }

    // ---- An update whose saved file is gone ----

    [Fact]
    public async Task AnUpdateFileFromAFolderThePersonChoseIsPutBackAndItsRegistrationStays()
    {
        var (pc, program, record) = WithDeadUpdate();
        var ops = OpsWithUpdateIn(@"C:\Downloads", PatchCode);
        ops.OnRestorePatch = () => pc.Files.Add(Saved);

        var outcome = await Service(pc, ops).FixAsync(InstallRepair.Scan(pc).Findings.ToList(), "Repaired", options: new(Folders: [@"C:\Downloads"]));

        var result = Assert.Single(outcome.Results!);
        Assert.Equal(RepairStatus.Fixed, result.Status);
        Assert.Contains("put the update's saved file back", result.Summary, StringComparison.OrdinalIgnoreCase);
        // A folder the person chose is their word for it: no signature is asked of the file.
        Assert.Equal([$"{Update} -> {Saved}"], ops.PatchRestores);
        Assert.True(sandbox.Has(program));
        Assert.True(sandbox.Has(record));
        Assert.Equal([Saved], outcome.Batch!.PutBack);
    }

    [Fact]
    public async Task AnUpdateFileAlreadyInTheInstallerFolderNeedsNoSignatureButOneInAPackageCacheDoes()
    {
        var (pc, _, _) = WithDeadUpdate();
        var left = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Installer", "left-behind.msp");
        var cached = @"C:\Users\x\AppData\Local\Package Cache\Y\kb.msp";
        var ops = new FakeOps { PatchCandidates = [cached, left] };
        ops.PatchCodes[cached] = PatchCode;
        ops.PatchCodes[left] = PatchCode;
        ops.PatchExit = 87;

        await Service(pc, ops).FixAsync(InstallRepair.Scan(pc).Findings.ToList(), "Repaired");

        Assert.Equal([$"{cached} -> {Saved} (signed)", $"{left} -> {Saved}"], ops.PatchRestores);
    }

    [Theory]
    [InlineData(@"C:\Windows\Installer\1f2e3d.msp", true)]
    [InlineData(@"C:\Windows\Installer\1F2E3D.MSP", true)]
    [InlineData(@"C:\Windows\Installer\sub\1f2e3d.msp", false)]
    [InlineData(@"C:\Windows\Installer\..\1f2e3d.msp", false)]
    [InlineData(@"C:\Windows\Installer\1f2e3d.msi", false)]
    [InlineData(@"C:\Windows\System32\1f2e3d.msp", false)]
    [InlineData(@"C:\Windows\Installer", false)]
    public void OnlyAnMspDirectlyInTheInstallerFolderCanBeWrittenOrTakenBack(string path, bool allowed)
    {
        // The test machine's Windows folder is not always C:\Windows.
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var real = path.Replace(@"C:\Windows", windows, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(allowed, WindowsInstallerOps.InstallerCachePatch(real) is not null);
    }

    [Fact]
    public void ARestoreThatIsNotAnUpdateTheRegisteredOneOrInTheRightPlaceWritesNothing()
    {
        Directory.CreateDirectory(folder);
        var target = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Installer", "tw-never-written.msp");
        var notAnUpdate = Path.Combine(folder, "garbage.msp");
        File.WriteAllText(notAnUpdate, "this is not a Windows Installer file");
        var text = Path.Combine(folder, "notes.txt");
        File.WriteAllText(text, "x");
        var ops = new WindowsInstallerOps();

        Assert.Equal(87, ops.RestorePatchCopy(text, PatchCode, target, requireSignature: false).ExitCode);
        Assert.Equal(87, ops.RestorePatchCopy(notAnUpdate, PatchCode, target, requireSignature: false).ExitCode);
        Assert.Equal(87, ops.RestorePatchCopy(notAnUpdate, PatchCode, Path.Combine(folder, "elsewhere.msp"), requireSignature: false).ExitCode);
        Assert.False(File.Exists(target));
        Assert.False(File.Exists(target + ".tidywindow-new"));
    }

    [Fact]
    public async Task UndoTakesBackTheUpdateFileTheRepairPutInPlace()
    {
        var (pc, _, _) = WithDeadUpdate();
        var ops = OpsWithUpdateIn(@"C:\Downloads", PatchCode);
        ops.OnRestorePatch = () => pc.Files.Add(Saved);
        var service = Service(pc, ops);
        var outcome = await service.FixAsync(InstallRepair.Scan(pc).Findings.ToList(), "Repaired", options: new(Folders: [@"C:\Downloads"]));

        var undo = await service.UndoAsync(outcome.Batch!);

        Assert.True(undo.Complete, string.Join(" ", undo.Problems));
        Assert.Contains(Saved, ops.Removed);
    }

    [Fact]
    public async Task AnUpdateFileFoundByItselfIsOnlyUsedWithAValidSignature()
    {
        var (pc, _, _) = WithDeadUpdate();
        var ops = new FakeOps { PatchCandidates = [@"C:\ProgramData\Package Cache\X\kb.msp"] };
        ops.PatchCodes[@"C:\ProgramData\Package Cache\X\kb.msp"] = PatchCode;
        ops.OnRestorePatch = () => pc.Files.Add(Saved);

        var outcome = await Service(pc, ops).FixAsync(InstallRepair.Scan(pc).Findings.ToList(), "Repaired");

        Assert.Equal(RepairStatus.Fixed, Assert.Single(outcome.Results!).Status);
        Assert.Equal([$@"C:\ProgramData\Package Cache\X\kb.msp -> {Saved} (signed)"], ops.PatchRestores);
    }

    [Fact]
    public async Task AFileThatCarriesAnotherUpdatesCodeIsNeverPutInPlace()
    {
        var (pc, program, record) = WithDeadUpdate();
        var ops = OpsWithUpdateIn(@"C:\Downloads", "{11111111-2222-3333-4444-555555555555}");

        var outcome = await Service(pc, ops).FixAsync(InstallRepair.Scan(pc).Findings.ToList(), "Repaired", options: new(Folders: [@"C:\Downloads"]));

        Assert.Empty(ops.PatchRestores);
        var result = Assert.Single(outcome.Results!);
        Assert.Contains("none carries this update's code", string.Join(" ", result.Steps));
        // With no file to put back, the registration is removed so Windows Installer can carry on.
        Assert.False(sandbox.Has(program));
        Assert.False(sandbox.Has(record));
    }

    [Fact]
    public async Task WhenTheHelperRefusesTheFileTheRegistrationIsRemovedAsItAlwaysWas()
    {
        var (pc, program, _) = WithDeadUpdate();
        var ops = OpsWithUpdateIn(@"C:\Downloads", PatchCode);
        ops.PatchExit = 87;

        var outcome = await Service(pc, ops).FixAsync(InstallRepair.Scan(pc).Findings.ToList(), "Repaired", options: new(Folders: [@"C:\Downloads"]));

        var result = Assert.Single(outcome.Results!);
        Assert.Equal(RepairStatus.Fixed, result.Status);
        Assert.Contains("was not put back: refused", string.Join(" ", result.Steps));
        Assert.False(sandbox.Has(program));
    }

    [Fact]
    public async Task ARepairOfAProgramPutsItsDeadUpdateFilesBackBeforeItRunsInstaller()
    {
        var product = new MsiProduct(BrokenPython.Code, "Machine", "", "Python", @"C:\Windows\Installer\a.msi");
        var (pc, _, _) = WithDeadUpdate();
        pc.Files.Add(@"C:\Windows\Installer\a.msi");
        pc.Msi = [product];
        var order = new List<string>();
        var ops = OpsWithUpdateIn(@"C:\Downloads", PatchCode);
        ops.OnRestorePatch = () => { pc.Files.Add(Saved); order.Add("update"); };
        ops.OnReinstall = () => order.Add("repair");

        var result = await Service(pc, ops).RepairProgramAsync(product, "Python", options: new(Folders: [@"C:\Downloads"]));

        Assert.Equal(RepairStatus.Fixed, result.Status);
        Assert.Equal(["update", "repair"], order);
    }

    // ---- A saved copy that is there but garbage ----

    private static readonly MsiProduct Machine = new(BrokenPython.Code, "Machine", "", "Python", @"C:\Windows\Installer\a.msi");

    [Fact]
    public void ASavedCopyThatIsEmptyOrAnotherProgramsCountsAsLost()
    {
        var pc = new FakePc { Msi = [Machine], State = _ => 5, Files = { Machine.LocalPackage! } };

        Assert.Empty(InstallRepair.Scan(pc).Findings);

        pc.DamagedCopies.Add(BrokenPython.Code);
        var finding = Assert.Single(InstallRepair.Scan(pc).Findings);
        Assert.Equal(RepairKind.InstallerRecord, finding.Kind);
        Assert.Contains("is damaged", finding.Detail);
    }

    [Fact]
    public async Task ADamagedSavedCopyIsRebuiltFromTheOriginalInstaller()
    {
        var pc = new FakePc { Msi = [Machine], State = _ => 5, Files = { Machine.LocalPackage!, @"C:\Downloads\core.msi" }, ProductsWithFiles = { BrokenPython.Code } };
        pc.DamagedCopies.Add(BrokenPython.Code);
        var ops = new FakeOps { Registered = "{Y}", Candidates = [@"C:\Downloads\core.msi"] };
        ops.Packages[@"C:\Downloads\core.msi"] = new(BrokenPython.Code, "{Y}", "1", "core");
        ops.OnReinstall = () => pc.DamagedCopies.Clear();

        var outcome = await Service(pc, ops).FixAsync(InstallRepair.Scan(pc).Findings.ToList(), "Repaired");

        Assert.Equal(RepairStatus.Fixed, Assert.Single(outcome.Results!).Status);
        Assert.Equal([@"C:\Downloads\core.msi"], ops.Reinstalled);
    }

    [Fact]
    public async Task AReinstallThatLeavesTheSavedCopyDamagedIsNotCalledFixed()
    {
        var pc = new FakePc { Msi = [Machine], State = _ => 5, Files = { Machine.LocalPackage!, @"C:\Downloads\core.msi" }, ProductsWithFiles = { BrokenPython.Code } };
        pc.DamagedCopies.Add(BrokenPython.Code);
        var ops = new FakeOps { Registered = "{Y}", Candidates = [@"C:\Downloads\core.msi"] };
        ops.Packages[@"C:\Downloads\core.msi"] = new(BrokenPython.Code, "{Y}", "1", "core");

        var outcome = await Service(pc, ops).FixAsync(InstallRepair.Scan(pc).Findings.ToList(), "Repaired", options: new(RestoreOnly: true));

        Assert.Equal(RepairStatus.NotFixed, Assert.Single(outcome.Results!).Status);
        Assert.Contains("saved copy is still missing", string.Join(" ", outcome.Results![0].Steps));
    }

    [Fact]
    public async Task RepairingFromADamagedSavedCopyGoesToTheOriginalInstead()
    {
        var pc = new FakePc { Msi = [Machine], Files = { Machine.LocalPackage!, @"C:\Downloads\core.msi" } };
        pc.DamagedCopies.Add(BrokenPython.Code);
        var ops = new FakeOps { Registered = "{Y}", Candidates = [@"C:\Downloads\core.msi"] };
        ops.Packages[@"C:\Downloads\core.msi"] = new(BrokenPython.Code, "{Y}", "1", "core");
        ops.OnReinstall = () => pc.DamagedCopies.Clear();

        var result = await Service(pc, ops).RepairProgramAsync(Machine, "Python");

        Assert.Equal(RepairStatus.Fixed, result.Status);
        // The garbage file is never handed to msiexec.
        Assert.Equal([@"C:\Downloads\core.msi"], ops.Reinstalled);
        Assert.Contains("damaged", string.Join(" ", result.Steps));
    }

    [Fact]
    public void AComponentWhoseKeyPathIsARegistryValueCountsAsTheProgramBeingThereWhileTheValueIs()
    {
        var key = @"Software\TidyWindowTest-" + Guid.NewGuid().ToString("N")[..8];
        using (var created = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(key)) created.SetValue("Installed", 1);
        try
        {
            Assert.True(WindowsInstallSystem.RegistryValuePresent($@"01:\{key}\Installed"));
            // 0x20 asks for the 64-bit view, which is the same key for an x64 test process.
            Assert.True(WindowsInstallSystem.RegistryValuePresent($@"21:\{key}\Installed") || !Environment.Is64BitOperatingSystem);
            Assert.False(WindowsInstallSystem.RegistryValuePresent($@"01:\{key}\Gone"));
            Assert.False(WindowsInstallSystem.RegistryValuePresent($@"01:\{key}-other\Installed"));
            Assert.False(WindowsInstallSystem.RegistryValuePresent($@"07:\{key}\Installed"));
            Assert.False(WindowsInstallSystem.RegistryValuePresent(@"C:\Windows"));
            Assert.False(WindowsInstallSystem.RegistryValuePresent(""));
        }
        finally { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(key, false); }
    }

    // ---- Inno Setup's uninstaller needs its data file ----

    private const string InnoKey = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Fine_is1";

    private static FakePc InnoWithoutData() => new()
    {
        Programs = [new(InnoKey, "Fine App", "2.1", "Maker", @"""C:\Program Files\Fine\unins000.exe"" /SILENT", "", @"C:\Program Files\Fine", "", false, false, false, false)],
        Files = { @"C:\Program Files\Fine\unins000.exe", @"C:\Program Files\Fine\fine.exe" },
        Folders = { @"C:\Program Files\Fine" }
    };

    [Fact]
    public void AnInnoUninstallerWithoutItsDataFileIsReportedForReviewAndIsNotCalledUsable()
    {
        var pc = InnoWithoutData();

        var finding = Assert.Single(InstallRepair.Scan(pc).Findings);

        Assert.Equal(RepairKind.BrokenEntry, finding.Kind);
        Assert.Equal(RepairLevel.Review, finding.Level);
        Assert.True(finding.Alive);
        Assert.Contains("unins000.dat", finding.Detail);
        Assert.False(InstallRepair.HasUninstaller(pc, pc.Programs[0]));

        pc.Files.Add(@"C:\Program Files\Fine\unins000.dat");

        Assert.Empty(InstallRepair.Scan(pc).Findings);
        Assert.True(InstallRepair.HasUninstaller(pc, pc.Programs[0]));
    }

    [Fact]
    public async Task TheSameVersionInstalledOverTheProgramPutsTheInnoDataFileBack()
    {
        var pc = InnoWithoutData();
        var fetcher = new FakeFetcher { Found = new("Maker.Fine", "Fine App", "2.1"), OnReinstall = () => pc.Files.Add(@"C:\Program Files\Fine\unins000.dat") };
        var before = sandbox.Dump();

        var outcome = await Service(pc, new FakeOps(), fetcher: fetcher).FixAsync(InstallRepair.Scan(pc).Findings.ToList(), "Repaired", options: new(Download: true));

        var result = Assert.Single(outcome.Results!);
        Assert.Equal(RepairStatus.Fixed, result.Status);
        Assert.Contains("reinstall Maker.Fine", fetcher.Calls);
        Assert.Contains("cannot be rebuilt", string.Join(" ", result.Steps));
        // The entry's own uninstaller was never repointed at the same file it already named.
        RepairSandbox.AssertSame(before, sandbox.Dump());
    }

    // ---- Quiet failure, Burn cache, zip ----

    [Theory]
    [InlineData("MSI (s) (A4:B0) [10:01:02:003]: Note: 1: 1722 2: CustomAction3 3: C:\\x.exe\r\nError 1722. There is a problem with this Windows Installer package.", true)]
    [InlineData("CustomAction FixUp returned actual error code 1603 (note this may not be 100% accurate if translation happened inside sandbox)", true)]
    [InlineData("MSI (s) (A4:B0) [10:01:02:003]: Note: 1: 1706 2: {X} 3: core.msi", false)]
    [InlineData("Action ended 10:01:02: LaunchConditions. Return value 3.", false)]
    public void OnlyAFailedCustomActionIsWorthRunningAgainWithAWindow(string log, bool expected)
    {
        var path = Path.Combine(folder, "retry.log");
        Directory.CreateDirectory(folder);
        File.WriteAllText(path, log, System.Text.Encoding.Unicode);

        Assert.Equal(expected, WindowsInstallerOps.CustomActionFailed(path));
        Assert.False(WindowsInstallerOps.CustomActionFailed(Path.Combine(folder, "missing.log")));
    }

    [Fact]
    public void ABurnCachePolicyAddsItsFolderToTheTwoStandardOnesAndNothingRelativeIsTrusted()
    {
        var standard = WindowsInstallerOps.BurnCacheRoots((string?)null);
        var moved = WindowsInstallerOps.BurnCacheRoots(@"D:\Cache\");

        Assert.Equal(2, standard.Count);
        Assert.Equal(standard.Concat([@"D:\Cache", @"D:\Cache\Package Cache"]), moved);
        Assert.Equal(standard, WindowsInstallerOps.BurnCacheRoots(@"relative\cache"));
        Assert.Equal(standard, WindowsInstallerOps.BurnCacheRoots("   "));
    }

    [Fact]
    public async Task AnInstallerThatWinGetDownloadedInsideAZipIsUnpackedAndUsed()
    {
        BrokenPython.PerUser(sandbox);
        var product = new MsiProduct(BrokenPython.Code, "User", RepairSandbox.Sid, "Python 3.13.9 Core Interpreter (64-bit)", @"C:\Windows\Installer\1a2b3c.msi");
        var pc = new FakePc { Msi = [product] };
        pc.State = code => code == BrokenPython.Code && sandbox.Has($@"HKCU\SOFTWARE\Microsoft\Installer\Products\{BrokenPython.Packed}") ? 5 : -1;
        pc.Programs = [new(BrokenPython.Arp(BrokenPython.Code), product.Name, "3.13.9150.0", "PSF", $"MsiExec.exe /X{BrokenPython.Code}", "", @"C:\Python313", "", true, false, false, false)];
        pc.Folders.Add(@"C:\Python313");
        var ops = new FakeOps { Registered = "{Y}" };
        ops.Archives[@"C:\dl\core.zip"] = [@"C:\unzipped\readme.txt", @"C:\unzipped\core.msi"];
        ops.Packages[@"C:\unzipped\core.msi"] = new(BrokenPython.Code, "{Y}", "3.13.9", "core");
        ops.OnReinstall = () => { pc.Msi = [product with { LocalPackage = @"C:\Windows\Installer\new.msi" }]; pc.Files.Add(@"C:\Windows\Installer\new.msi"); };
        var fetcher = new FakeFetcher { Found = new("Python.Python.3.13", "Python 3.13.9 (64-bit)", "3.13.9"), Files = [@"C:\dl\core.zip"] };

        var outcome = await Service(pc, ops, fetcher: fetcher).FixAsync(InstallRepair.Scan(pc).Findings.Where(x => x.Footprint is { Kind: "product" }).ToList(), "x", options: new(Download: true));

        Assert.Equal(RepairStatus.Fixed, Assert.Single(outcome.Results!).Status);
        Assert.Equal([@"C:\unzipped\core.msi"], ops.Reinstalled);
    }

    // ---- Programs Visual Studio installed for itself ----

    private const string DotnetSdkCode = "{F88188C9-CDDF-4229-B5A0-71E054594115}";
    private const string VisualStudioPackages = @"C:\ProgramData\Microsoft\VisualStudio\Packages\Microsoft.NetCore.SdkPlaceholder.10.0.403-servicing.26523.112,version=18.11.12324.64,machinearch=x64\";

    private static UninstallEntry Entry(string name, string source) =>
        new($@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{DotnetSdkCode}", name, "10.0.403", "Microsoft", $"MsiExec.exe /X{DotnetSdkCode}", "", "", "", true, false, false, false, InstallSource: source);

    [Theory]
    [InlineData(VisualStudioPackages, true)]
    [InlineData(@"c:\programdata\microsoft\visualstudio\packages\Some.Runtime,version=1\", true)]
    [InlineData("C:/ProgramData/Microsoft/VisualStudio/Packages/Some.Runtime/", true)]
    [InlineData(@"C:\Users\me\Downloads\", false)]
    [InlineData(@"C:\ProgramData\Microsoft\VisualStudio\Setup\", false)]
    [InlineData("", false)]
    public void ProgramsFromTheVisualStudioPackageCacheAreRecognisedByTheirSource(string source, bool expected) =>
        Assert.Equal(expected, Entry("Anything", source).FromVisualStudioCache);

    [Theory]
    [InlineData("Microsoft .NET SDK 10.0.403 (x64) from Visual Studio", true)]
    [InlineData("Visual Studio Code", false)]
    [InlineData("Microsoft Visual Studio Installer", false)]
    public void TheNameVisualStudioGivesItsRefusingPackagesIsTheFallback(string name, bool expected) =>
        Assert.Equal(expected, Entry(name, "").NamedFromVisualStudio);

    [Fact]
    public async Task RepairingAProgramWhoseInstallerOnlyRunsFromVisualStudioStopsBeforeAskingForAdministratorRights()
    {
        var product = new MsiProduct(DotnetSdkCode, "Machine", "", "Microsoft .NET SDK 10.0.403 (x64)", @"C:\Windows\Installer\sdk.msi");
        var pc = new FakePc { Msi = [product], Files = { product.LocalPackage! }, Programs = [Entry(product.Name, VisualStudioPackages)] };
        pc.NeedVisualStudio.Add(DotnetSdkCode);
        var ops = new FakeOps();

        var result = await Service(pc, ops).RepairProgramAsync(product, product.Name);

        Assert.Equal(RepairStatus.NotFixed, result.Status);
        Assert.Contains("Visual Studio Installer", result.Summary);
        Assert.Empty(ops.Reinstalled);
    }

    [Fact]
    public async Task ARepairWhoseSavedInstallerCannotBeReadStillStopsForTheNameVisualStudioGivesIt()
    {
        var product = new MsiProduct(DotnetSdkCode, "Machine", "", "Microsoft .NET SDK 10.0.403 (x64) from Visual Studio", null);
        var pc = new FakePc { Msi = [product], Programs = [Entry(product.Name, "")] };

        var result = await Service(pc, new FakeOps()).RepairProgramAsync(product, product.Name);

        Assert.Contains("Visual Studio Installer", result.Summary);
    }

    [Fact]
    public async Task AProgramVisualStudioCachedThatRepairsNormallyIsRepairedAsBefore()
    {
        // IIS Express, Web Deploy and LocalDB come from the same cache but nothing in their installers refuses a repair.
        var product = new MsiProduct(DotnetSdkCode, "Machine", "", "Microsoft Web Deploy 4.0", @"C:\Windows\Installer\sdk.msi");
        var pc = new FakePc { Msi = [product], Files = { product.LocalPackage! }, Programs = [Entry(product.Name, VisualStudioPackages)] };
        var ops = new FakeOps();

        var result = await Service(pc, ops).RepairProgramAsync(product, product.Name);

        Assert.Equal(RepairStatus.Fixed, result.Status);
    }
}

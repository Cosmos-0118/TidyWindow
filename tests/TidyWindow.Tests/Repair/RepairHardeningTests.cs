using System.Runtime.Versioning;
using TidyWindow.Next.Core;
using Xunit;

namespace TidyWindow.Next.Tests;

/// <summary>What the Repair audit found missing: records with no saved copy named, hidden packages, wrapped uninstallers, per-machine bundles, update registrations and the checks on a setup program.</summary>
[SupportedOSPlatform("windows")]
public sealed class RepairHardeningTests : IDisposable
{
    private readonly RepairSandbox sandbox = new();
    private readonly string folder = Path.Combine(Path.GetTempPath(), "tw-hardening-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly RepairHistory history;
    public RepairHardeningTests() => history = new RepairHistory(folder);
    public void Dispose() { sandbox.Dispose(); try { Directory.Delete(folder, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }

    private const string Code = "{90140000-0011-0000-0000-0000000FF1CE}";
    private const string Arp = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\";

    private static UninstallEntry Entry(string name, string key, string uninstall = "", bool msi = false, bool system = false, string location = "", string bundle = "") =>
        new(Arp + key, name, "1.0", "Maker", uninstall, "", location, "", msi, system, false, false, bundle);

    private RepairService Service(FakePc pc, FakeOps ops, FakeOps? elevated = null, FakeFetcher? fetcher = null, int[]? unlocks = null) =>
        new(pc, sandbox.User(), sandbox.Elevated(), _ => { if (unlocks is not null) unlocks[0]++; return Task.CompletedTask; }, () => { }, history, ops, elevated ?? ops, fetcher);

    // ---- Windows Installer records ----

    [Fact]
    public void AnInstalledProgramWhoseSavedCopyIsNotNamedAtAllIsReportedButAnAdvertisedOneIsNot()
    {
        var product = new MsiProduct(Code, "Machine", "", "Office", null);
        var installed = new FakePc { Msi = [product], State = _ => 5 };
        var advertised = new FakePc { Msi = [product], State = _ => 1 };

        var finding = Assert.Single(InstallRepair.Scan(installed).Findings);

        Assert.Equal(RepairKind.InstallerRecord, finding.Kind);
        Assert.Contains("no saved copy recorded", finding.Detail);
        Assert.Empty(InstallRepair.Scan(advertised).Findings);
    }

    [Fact]
    public void AHiddenWindowsInstallerPackageThatWindowsInstallerForgotIsReportedButOtherHiddenEntriesAreNot()
    {
        var pc = new FakePc
        {
            Programs =
            [
                Entry("Python 3.13.9 Core Interpreter", Code, $"MsiExec.exe /X{Code}", msi: true, system: true),
                Entry("Some hidden helper", "Helper", @"C:\gone\helper.exe", system: true)
            ]
        };

        var finding = Assert.Single(InstallRepair.Scan(pc).Findings);

        Assert.Equal("Python 3.13.9 Core Interpreter", finding.Title);
        Assert.Equal(RepairKind.BrokenEntry, finding.Kind);
    }

    [Theory]
    [InlineData("msiexec.exe /x {90140000-0011-0000-0000-0000000FF1CE}")]
    [InlineData("msiexec.exe -x {90140000-0011-0000-0000-0000000FF1CE}")]
    [InlineData("MsiExec.exe /uninstall {90140000-0011-0000-0000-0000000FF1CE} /qn")]
    [InlineData("msiexec /fa{90140000-0011-0000-0000-0000000FF1CE}")]
    [InlineData("\"C:\\Windows\\System32\\msiexec.exe\" /qn /I{90140000-0011-0000-0000-0000000FF1CE}")]
    public void EveryWayMsiexecIsToldWhichProductToActOnNamesTheProduct(string command)
    {
        var entry = Entry("App", "Custom", command);

        Assert.Equal(Code, InstallRepair.ProductCode(entry));
    }

    [Fact]
    public void ACommandThatIsNotMsiexecNamesNoProduct() =>
        Assert.Null(InstallRepair.ProductCode(Entry("App", "Custom", "setup.exe /x {90140000-0011-0000-0000-0000000FF1CE}")));

    // ---- Uninstall commands ----

    [Fact]
    public void AScriptRunByASystemHostCountsNotTheHostItself()
    {
        var pc = new FakePc { OnPath = { "cmd.exe", "powershell.exe", "rundll32.exe" } };
        var cmd = Entry("Bat", "Bat", @"cmd.exe /c ""C:\Gone\uninstall.bat""");
        var shell = Entry("Script", "Script", @"powershell.exe -NoProfile -File ""C:\Gone\remove.ps1""");
        var dll = Entry("Library", "Library", @"rundll32.exe ""C:\Gone\setup.dll"",Remove");
        var builtin = Entry("Builtin", "Builtin", @"cmd.exe /c rd /s /q ""C:\Gone""");

        Assert.False(InstallRepair.HasUninstaller(pc, cmd));
        Assert.False(InstallRepair.HasUninstaller(pc, shell));
        Assert.False(InstallRepair.HasUninstaller(pc, dll));
        // A command the host runs by itself, with no script or library to find, is as healthy as the host.
        Assert.True(InstallRepair.HasUninstaller(pc, builtin));

        pc.Files.UnionWith([@"C:\Gone\uninstall.bat", @"C:\Gone\remove.ps1", @"C:\Gone\setup.dll"]);
        Assert.True(InstallRepair.HasUninstaller(pc, cmd));
        Assert.True(InstallRepair.HasUninstaller(pc, shell));
        Assert.True(InstallRepair.HasUninstaller(pc, dll));
    }

    [Fact]
    public void ADeletedScriptBehindASystemHostMakesTheEntryBroken()
    {
        var pc = new FakePc { OnPath = { "cmd.exe" }, Programs = [Entry("Bat", "Bat", @"cmd.exe /c ""C:\Gone\uninstall.bat""", location: @"C:\Gone")] };

        var finding = Assert.Single(InstallRepair.Scan(pc).Findings);

        Assert.Equal("Bat", finding.Title);
    }

    [Fact]
    public void AnArgumentThatEndsInExeIsNotTakenForTheProgram()
    {
        var found = InstallRepair.Candidates(@"C:\Program Files\Vendor\uninst /app=C:\Program Files\Vendor\app.exe");

        Assert.Contains(@"C:\Program Files\Vendor\uninst", found);
    }

    // ---- Update (patch) registrations and the install marker ----

    [Fact]
    public void AnUpdateWhoseSavedFileIsGoneIsOfferedForReviewWithItsRegistrations()
    {
        var keys = new[] { @"HKLM\X\Products\A\Patches\P", @"HKLM\X\Patches\P" };
        var pc = new FakePc { Patches = [new("P", @"C:\Windows\Installer\1.msp", ["Office"], keys)] };

        var finding = Assert.Single(InstallRepair.Scan(pc).Findings);

        Assert.Equal(RepairKind.MissingPatch, finding.Kind);
        Assert.Equal(RepairLevel.Review, finding.Level);
        Assert.Equal(keys, finding.Keys);
        Assert.True(finding.NeedsAdmin);
        Assert.Contains("1612", finding.Detail);
    }

    [Fact]
    public void TheSavedRollbackInformationGoesWithTheInstallMarkerOnlyWhenThereIsOne()
    {
        var with = Assert.Single(InstallRepair.Scan(new FakePc { Lock = true, Rollback = true }).Findings);
        var without = Assert.Single(InstallRepair.Scan(new FakePc { Lock = true }).Findings);
        var alone = InstallRepair.Scan(new FakePc { Rollback = true }).Findings;

        Assert.Equal([InstallRepair.InstallLockKey, InstallRepair.InstallRollbackKey], with.Keys);
        Assert.Equal([InstallRepair.InstallLockKey], without.Keys);
        Assert.Empty(alone);
    }

    [Fact]
    public async Task ADeadUpdateRegistrationIsRemovedAndPutBackByTheUndo()
    {
        var sid = "S-1-5-18";
        var program = $@"{BrokenPython.UserData(sid)}\Products\{BrokenPython.Packed}\Patches\{BrokenPython.PatchPacked}";
        var record = $@"{BrokenPython.UserData(sid)}\Patches\{BrokenPython.PatchPacked}";
        sandbox.Put(program, ("State", 1));
        sandbox.Put(record, ("LocalPackage", @"C:\Windows\Installer\gone.msp"));
        sandbox.Put($@"{BrokenPython.UserData(sid)}\Products\{BrokenPython.Packed}\InstallProperties", ("DisplayName", "Python"));
        var before = sandbox.Dump();
        var pc = new FakePc { Patches = [new(BrokenPython.PatchPacked, @"C:\Windows\Installer\gone.msp", ["Python"], [program, record])] };
        var service = Service(pc, new FakeOps());

        var outcome = await service.FixAsync(InstallRepair.Scan(pc).Findings.ToList(), "Repaired");

        Assert.Equal(RepairStatus.Fixed, Assert.Single(outcome.Results!).Status);
        Assert.False(sandbox.Has(program));
        Assert.False(sandbox.Has(record));
        // The program's own record stays; only the update's registration went.
        Assert.True(sandbox.Has($@"{BrokenPython.UserData(sid)}\Products\{BrokenPython.Packed}\InstallProperties"));

        var undo = await service.UndoAsync(outcome.Batch!);

        Assert.True(undo.Complete, string.Join(" ", undo.Problems));
        RepairSandbox.AssertSame(before, sandbox.Dump());
    }

    [Theory]
    [InlineData(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\Rollback", true)]
    [InlineData(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData\S-1-5-18\Products\00004109110000000000000000F01FEC\Patches\00004109110000000000000000F01FEC", true)]
    [InlineData(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\Managed\S-1-5-21-1\Installer\Patches\00004109110000000000000000F01FEC", true)]
    [InlineData(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\Rollback\Scripts", false)]
    [InlineData(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData\S-1-5-18\Products\00004109110000000000000000F01FEC\Patches", false)]
    [InlineData(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData\S-1-5-18\Products\00004109110000000000000000F01FEC\Patches\Other", false)]
    public void TheWindowsWideRulesAllowOnlyTheUpdateAndRollbackKeysThemselves(string path, bool allowed) => Assert.Equal(allowed, RegistryRepair.Machine.IsAllowed(path));

    // ---- A Windows-wide bundle's setup program ----

    private const string MachineBundle = "{7A1B3C5D-1111-4222-8333-444455556666}";
    private static readonly string MachineCache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Package Cache", MachineBundle, "python-3.13.9-amd64.exe");

    private static FakePc MachineBundlePc()
    {
        var pc = new FakePc { Folders = { @"C:\Python313" } };
        pc.Live = () => [new(Arp + MachineBundle, "Python 3.13.9 (64-bit)", "3.13.9", "PSF", $"\"{MachineCache}\" /uninstall", "", @"C:\Python313", "", false, false, false, false, MachineBundle)];
        return pc;
    }

    [Fact]
    public async Task AWindowsWideBundlesSetupIsPutBackByTheAdministratorHelperNotByTheApp()
    {
        var pc = MachineBundlePc();
        var app = new FakeOps();
        var helper = new FakeOps { OnRepairBundle = () => pc.Files.Add(MachineCache) };
        var fetcher = new FakeFetcher { Found = new("Python.Python.3.13", "Python 3.13.9 (64-bit)", "3.13.9"), Files = [@"C:\dl\python-3.13.9-amd64.exe"] };
        var finding = Assert.Single(InstallRepair.Scan(pc).Findings) with { Alive = true };
        var unlocks = new[] { 0 };

        var outcome = await Service(pc, app, helper, fetcher, unlocks).FixAsync([finding], "x", options: new(Download: true));

        Assert.Equal(RepairStatus.Fixed, Assert.Single(outcome.Results!).Status);
        Assert.Empty(app.Repaired);
        Assert.Equal([@"C:\dl\python-3.13.9-amd64.exe"], helper.Repaired);
        Assert.Equal([MachineCache], helper.RestoredTo);
        Assert.Equal(1, unlocks[0]);
        Assert.Contains("download Python.Python.3.13 3.13.9 x64", fetcher.Calls);
    }

    [Fact]
    public async Task UndoTakesAPutBackMachineSetupAwayThroughTheHelperToo()
    {
        var batch = history.Save("x", [], files: [MachineCache]);
        var app = new FakeOps();
        var helper = new FakeOps();
        var unlocks = new[] { 0 };

        var undo = await Service(new FakePc(), app, helper, unlocks: unlocks).UndoAsync(batch);

        Assert.True(undo.Complete);
        Assert.Equal([MachineCache], helper.Removed);
        Assert.Empty(app.Removed);
        Assert.Equal(1, unlocks[0]);
    }

    [Fact]
    public async Task AFolderThePersonChoseSuppliesTheBundlesSetupBeforeAnythingIsDownloaded()
    {
        var key = $@"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{MachineBundle}";
        const string cache = @"C:\Users\X\AppData\Local\Package Cache\{7A1B3C5D-1111-4222-8333-444455556666}\python-3.13.9-amd64.exe";
        var pc = new FakePc { Folders = { @"C:\Python313" } };
        pc.Live = () => [new(key, "Python 3.13.9 (64-bit)", "3.13.9", "PSF", $"\"{cache}\" /uninstall", "", @"C:\Python313", "", false, false, false, false, MachineBundle)];
        var ops = new FakeOps { BundleExitFor = file => file.EndsWith("python-3.13.9-amd64.exe", StringComparison.OrdinalIgnoreCase) ? 0 : 87 };
        ops.OnRepairBundle = () => pc.Files.Add(cache);
        ops.Folders[@"C:\Downloads"] = [@"C:\Downloads\notepad-setup.exe", @"C:\Downloads\python-3.13.9-amd64.exe"];
        var fetcher = new FakeFetcher { Found = new("Python.Python.3.13", "Python 3.13.9 (64-bit)", "3.13.9") };
        var finding = Assert.Single(InstallRepair.Scan(pc).Findings) with { Alive = true };

        var outcome = await Service(pc, ops, fetcher: fetcher).FixAsync([finding], "x", options: new(Download: true, Folders: [@"C:\Downloads"]));

        var result = Assert.Single(outcome.Results!);
        Assert.Equal(RepairStatus.Fixed, result.Status);
        Assert.Empty(fetcher.Calls);
        Assert.Equal([@"C:\Downloads\notepad-setup.exe", @"C:\Downloads\python-3.13.9-amd64.exe"], ops.Repaired);
        // A setup program that is simply another program's is not worth a line.
        Assert.DoesNotContain("notepad", string.Join(" ", result.Steps));
    }

    [Theory]
    [InlineData("Python 3.13.9 (64-bit)", @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{A}", "x64")]
    [InlineData("Microsoft Visual C++ 2015-2022 Redistributable (x86) - 14.38", @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{A}", "x86")]
    [InlineData("Some Tool", @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\{A}", "x86")]
    [InlineData("Some Tool ARM64", @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{A}", "arm64")]
    [InlineData("Some Tool", @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{A}", null)]
    public void TheArchitectureComesFromWhatTheEntryAndItsSetupSay(string name, string key, string? expected) =>
        Assert.Equal(expected, RepairService.ArchitectureOf(new(key, name, "1", "", "", "", "", "", false, false, false, false)));

    [Fact]
    public void TheArchitectureAlsoComesFromTheCachedSetupsName()
    {
        var entry = new UninstallEntry(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{A}", "Some Redistributable", "1", "", @"""C:\ProgramData\Package Cache\{A}\VC_redist.x64.exe"" /uninstall", "", "", "", false, false, false, false);

        Assert.Equal("x64", RepairService.ArchitectureOf(entry));
    }

    // ---- Repairing a working program ----

    [Fact]
    public async Task AWorkingPerUserProgramIsRepairedFromItsSavedCopyWithoutTheHelper()
    {
        var product = new MsiProduct(Code, "User", RepairSandbox.Sid, "App", @"C:\Windows\Installer\a.msi");
        var pc = new FakePc { Files = { @"C:\Windows\Installer\a.msi" } };
        var ops = new FakeOps();
        var unlocks = new[] { 0 };

        var result = await Service(pc, ops, unlocks: unlocks).RepairProgramAsync(product, "App");

        Assert.Equal(RepairStatus.Fixed, result.Status);
        Assert.Equal([@"C:\Windows\Installer\a.msi"], ops.Reinstalled);
        Assert.Equal(0, unlocks[0]);
    }

    [Fact]
    public async Task AWorkingWindowsWideProgramIsRepairedThroughTheHelperAndARestartIsSaidWhenItIsNeeded()
    {
        var product = new MsiProduct(Code, "Machine", "", "App", @"C:\Windows\Installer\a.msi");
        var pc = new FakePc { Files = { @"C:\Windows\Installer\a.msi" } };
        var app = new FakeOps();
        var helper = new FakeOps { ExitCode = 3010 };
        var unlocks = new[] { 0 };

        var result = await Service(pc, app, helper, unlocks: unlocks).RepairProgramAsync(product, "App");

        Assert.Equal(RepairStatus.FixedAfterRestart, result.Status);
        Assert.Empty(app.Reinstalled);
        Assert.Single(helper.Reinstalled);
        Assert.Equal(1, unlocks[0]);
    }

    [Fact]
    public async Task AProgramWhoseSavedCopyIsGoneCannotBeRepairedFromHereAndNothingRuns()
    {
        var product = new MsiProduct(Code, "User", RepairSandbox.Sid, "App", @"C:\Windows\Installer\gone.msi");
        var ops = new FakeOps();

        var result = await Service(new FakePc(), ops).RepairProgramAsync(product, "App");

        Assert.Equal(RepairStatus.NotFixed, result.Status);
        Assert.Empty(ops.Reinstalled);
    }

    [Fact]
    public async Task AFailedRepairSaysWhyInPlainWords()
    {
        var product = new MsiProduct(Code, "User", RepairSandbox.Sid, "App", @"C:\Windows\Installer\a.msi");
        var pc = new FakePc { Files = { @"C:\Windows\Installer\a.msi" } };

        var result = await Service(pc, new FakeOps { ExitCode = 1612 }).RepairProgramAsync(product, "App");

        Assert.Equal(RepairStatus.NotFixed, result.Status);
        Assert.Contains("source files are not all there", result.Summary);
    }

    private static string LogWith(string line)
    {
        var path = Path.Combine(Path.GetTempPath(), "tw-msi-" + Guid.NewGuid().ToString("N")[..8] + ".log");
        File.WriteAllText(path, $"MSI (s) (DC:28) [22:55:14:432]: Product: App -- {line}\r\nAction ended 22:55:14: INSTALL. Return value 3.\r\n", System.Text.Encoding.Unicode);
        return path;
    }

    private const string Missing = "Error 1706. An installation package for the product App cannot be found. Try the installation again using a valid copy of the installation package 'App.msi'.";
    private const string Saved = @"C:\Windows\Installer\a.msi", Original = @"C:\Downloads\App.msi";

    [Fact]
    public async Task ARepairThatNeedsTheOriginalInstallerLooksForItAndRepairsWithIt()
    {
        var log = LogWith(Missing);
        try
        {
            var product = new MsiProduct(Code, "User", RepairSandbox.Sid, "App", Saved);
            var pc = new FakePc { Files = { Saved }, Msi = [product] };
            var ops = new FakeOps { Output = "Log: " + log, Candidates = [Original] };
            ops.Exits.Enqueue(1603);
            ops.Packages[Original] = new(Code, "", "1", "App");

            var result = await Service(pc, ops).RepairProgramAsync(product, "App");

            Assert.Equal(RepairStatus.Fixed, result.Status);
            Assert.Equal([Saved, Original], ops.Reinstalled);
            Assert.Contains(result.Steps, x => x.Contains("Error 1706"));
        }
        finally { File.Delete(log); }
    }

    [Fact]
    public async Task ARepairThatNeedsTheOriginalInstallerSaysSoInPlainWordsWhenItCannotBeFound()
    {
        var log = LogWith(Missing);
        try
        {
            var product = new MsiProduct(Code, "User", RepairSandbox.Sid, "App", Saved);
            var pc = new FakePc { Files = { Saved }, Msi = [product] };
            var ops = new FakeOps { Output = "Log: " + log, ExitCode = 1603 };

            var result = await Service(pc, ops).RepairProgramAsync(product, "App");

            Assert.Equal(RepairStatus.NotFixed, result.Status);
            Assert.Contains("Error 1706", result.Summary);
            Assert.Contains("original installer", result.Summary);
            Assert.Single(ops.Reinstalled);
        }
        finally { File.Delete(log); }
    }

    [Fact]
    public async Task ASourceManagementFailureWithoutAModalErrorLineStillTriggersOriginalInstallerRecovery()
    {
        var log = Path.Combine(Path.GetTempPath(), "tw-msi-" + Guid.NewGuid().ToString("N")[..8] + ".log");
        File.WriteAllText(log,
            "MSI (s) (DC:68) [23:04:35:486]: Note: 1: 1706 2:  3: iisexpress_amd64_en-us.msi \r\n" +
            "MSI (s) (DC:68) [23:04:35:486]: SOURCEMGMT: Failed to resolve source\r\n" +
            "MSI (s) (DC:68) [23:04:35:486]: Repair: Could not resolve source\r\n",
            System.Text.Encoding.Unicode);
        try
        {
            var product = new MsiProduct(Code, "User", RepairSandbox.Sid, "IIS 10.0 Express", Saved);
            var pc = new FakePc { Files = { Saved }, Msi = [product] };
            var ops = new FakeOps { Output = "Log: " + log, Candidates = [Original] };
            ops.Exits.Enqueue(1603);
            ops.Packages[Original] = new(Code, "", "10.0", "IIS 10.0 Express");

            var result = await Service(pc, ops).RepairProgramAsync(product, "IIS 10.0 Express");

            Assert.Equal(RepairStatus.Fixed, result.Status);
            Assert.Equal([Saved, Original], ops.Reinstalled);
            Assert.Contains(result.Steps, x => x.Contains("iisexpress_amd64_en-us.msi"));
        }
        finally { File.Delete(log); }
    }

    [Fact]
    public async Task WhenRecoveryFindsTheOriginalInstallerAndThatRunAlsoFailsTheSecondReasonIsReported()
    {
        var firstLog = LogWith(Missing);
        var secondLog = Path.Combine(Path.GetTempPath(), "tw-msi-" + Guid.NewGuid().ToString("N")[..8] + ".log");
        File.WriteAllText(secondLog,
            "MSI (s) (DC:84) [23:04:10:851]: Note: 1: 1316 2: C:\\Temp\\download1\\EpicInstaller-20.3.3.msi \r\n" +
            "MSI (s) (DC:84) [23:04:10:851]: SECUREREPAIR: SecureRepair Failed. Error code: 524BDD0F468\r\n",
            System.Text.Encoding.Unicode);
        try
        {
            var product = new MsiProduct(Code, "User", RepairSandbox.Sid, "Epic Games Launcher", Saved);
            var pc = new FakePc { Files = { Saved }, Msi = [product] };
            var ops = new FakeOps { Candidates = [Original] };
            ops.Exits.Enqueue(1603);
            ops.Exits.Enqueue(1603);
            ops.Outputs.Enqueue("Log: " + firstLog);
            ops.Outputs.Enqueue("Log: " + secondLog);
            ops.Packages[Original] = new(Code, "", "1.3.210.0", "Epic Games Launcher");

            var result = await Service(pc, ops).RepairProgramAsync(product, "Epic Games Launcher");

            Assert.Equal(RepairStatus.NotFixed, result.Status);
            Assert.Equal([Saved, Original], ops.Reinstalled);
            Assert.Contains("Error 1316", result.Summary);
            Assert.Contains("EpicInstaller-20.3.3.msi", result.Summary);
            Assert.DoesNotContain("Locate installer folder", result.Summary);
        }
        finally
        {
            File.Delete(firstLog);
            File.Delete(secondLog);
        }
    }

    [Fact]
    public void LockedRollbackFolderInLogIsDetectedAndExplained()
    {
        var log = Path.Combine(Path.GetTempPath(), "tw-msi-" + Guid.NewGuid().ToString("N")[..8] + ".log");
        File.WriteAllText(log,
            "MSI (s) (DC:68) [23:04:36:161]: Note: 1: 2265 2:  3: -2147287035 \r\n" +
            "MSI (s) (DC:68) [23:04:36:163]: Backup folder is created by user\r\n" +
            "MSI (s) (DC:70) [23:04:37:665]: Error in rollback skipped.\tReturn: 5\r\n",
            System.Text.Encoding.Unicode);
        try
        {
            Assert.True(WindowsInstallerOps.FailedOnRollbackFolder(log));
            var reason = RepairService.WhyItFailed("Log: " + log);
            Assert.NotNull(reason);
            Assert.Contains("Config.Msi", reason);
        }
        finally { File.Delete(log); }
    }

    [Theory]
    [InlineData(3010, 3010, true)]
    [InlineData(0, 0, true)]
    [InlineData(0, 0, false)]
    [InlineData(1603, 1603, true)]
    public void ServerFinishedCleanlyOnlyWhenTheServerSucceededWithoutARollback(int server, int expected, bool reconfigured)
    {
        var log = Path.Combine(Path.GetTempPath(), "tw-msi-" + Guid.NewGuid().ToString("N")[..8] + ".log");
        File.WriteAllText(log,
            "MSI (s) (04:70) [23:52:21:923]: Executing op: RollbackInfo(,RollbackAction=Rollback,RollbackDescription=Rolling back action:)\r\n" +
            (reconfigured ? "MSI (s) (04:70) [23:52:27:334]: Windows Installer reconfigured the product. Product Name: Epic Games Launcher. Reconfiguration success or error status: 0.\r\n" : "") +
            $"MSI (s) (04:70) [23:52:29:379]: MainEngineThread is returning {server}\r\n" +
            "MSI (c) (04:E0) [23:52:34:835]: MainEngineThread is returning 1603\r\n",
            System.Text.Encoding.Unicode);
        try
        {
            var found = WindowsInstallerOps.ServerFinishedCleanly(log);
            if (reconfigured && server is 0 or 3010) Assert.Equal(expected, found);
            else Assert.Null(found);
        }
        finally { File.Delete(log); }
    }

    [Fact]
    public void ALaunchConditionThatRefusesTheRunIsReportedInThePackagesOwnWords()
    {
        var log = Path.Combine(Path.GetTempPath(), "tw-msi-" + Guid.NewGuid().ToString("N")[..8] + ".log");
        File.WriteAllText(log,
            "MSI (s) (FC:28) [00:10:52:613]: Doing action: LaunchConditions\r\n" +
            "Action start 0:10:52: LaunchConditions.\r\n" +
            "MSI (s) (FC:28) [00:10:52:614]: Product: Microsoft .NET SDK 10.0.403 (x64) from Visual Studio -- Microsoft .NET SDK 10.0.403 (x64) was installed with Visual Studio. If you wish to uninstall it, use the Visual Studio Installer and select 'Modify'\r\n\r\n" +
            "Action ended 0:10:52: LaunchConditions. Return value 3.\r\n" +
            "MSI (c) (2C:3C) [00:10:52:625]: MainEngineThread is returning 1603\r\n",
            System.Text.Encoding.Unicode);
        try
        {
            var reason = RepairService.WhyItFailed("Log: " + log);
            Assert.NotNull(reason);
            Assert.Contains("Visual Studio Installer", reason);
            Assert.StartsWith("the program's own installer refused to run", reason);
        }
        finally { File.Delete(log); }
    }

    [Fact]
    public void ServerFinishedCleanlyIsRefusedWhenTheRollbackFailed()
    {
        var log = Path.Combine(Path.GetTempPath(), "tw-msi-" + Guid.NewGuid().ToString("N")[..8] + ".log");
        File.WriteAllText(log,
            "MSI (s) (DC:68) [23:04:37:244]: Windows Installer reconfigured the product. Reconfiguration success or error status: 0.\r\n" +
            "MSI (s) (DC:68) [23:04:37:246]: MainEngineThread is returning 0\r\n" +
            "MSI (s) (DC:70) [23:04:37:665]: Error in rollback skipped.\tReturn: 5\r\n" +
            "MSI (c) (A0:D8) [23:04:37:834]: MainEngineThread is returning 1603\r\n",
            System.Text.Encoding.Unicode);
        try { Assert.Null(WindowsInstallerOps.ServerFinishedCleanly(log)); }
        finally { File.Delete(log); }
    }

    [Fact]
    public void EmbeddedCabinetsAndEqualReinstallModeAreDetectedFromMsiTables()
    {
        Directory.CreateDirectory(folder);
        var embedded = Path.Combine(folder, "embedded.msi");
        var external = Path.Combine(folder, "external.msi");
        var cabPayload = Path.Combine(folder, "payload.bin");
        File.WriteAllBytes(cabPayload, [0x4D, 0x53, 0x43, 0x46]);

        BuildTestMsi(embedded, "#app.cab", cabPayload, "amus");
        BuildTestMsi(external, "ext.cab", null, null);

        Assert.True(WindowsInstallerOps.HasEmbeddedCabinets(embedded));
        Assert.True(WindowsInstallerOps.NeedsEqualReinstall(embedded));
        Assert.False(WindowsInstallerOps.HasEmbeddedCabinets(external));
        Assert.False(WindowsInstallerOps.NeedsEqualReinstall(external));
    }

    private static void BuildTestMsi(string msiPath, string cabinet, string? streamFile, string? reinstallMode)
    {
        var installerType = Type.GetTypeFromProgID("WindowsInstaller.Installer")!;
        var installer = Activator.CreateInstance(installerType)!;
        var db = installerType.InvokeMember("OpenDatabase", System.Reflection.BindingFlags.InvokeMethod, null, installer, [msiPath, 4])!;
        var dbType = db.GetType();
        void Exec(string sql, object? record = null)
        {
            var view = dbType.InvokeMember("OpenView", System.Reflection.BindingFlags.InvokeMethod, null, db, [sql])!;
            try
            {
                view.GetType().InvokeMember("Execute", System.Reflection.BindingFlags.InvokeMethod, null, view, record is null ? null : [record]);
                view.GetType().InvokeMember("Close", System.Reflection.BindingFlags.InvokeMethod, null, view, null);
            }
            finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(view); }
        }
        Exec("CREATE TABLE `Property` (`Property` CHAR(72) NOT NULL, `Value` CHAR(0) NOT NULL PRIMARY KEY `Property`)");
        Exec("CREATE TABLE `Media` (`DiskId` SHORT NOT NULL, `LastSequence` SHORT NOT NULL, `Cabinet` CHAR(255) PRIMARY KEY `DiskId`)");
        Exec($"INSERT INTO `Media` (`DiskId`, `LastSequence`, `Cabinet`) VALUES (1, 1, '{cabinet}')");
        if (reinstallMode is not null)
            Exec($"INSERT INTO `Property` (`Property`, `Value`) VALUES ('REINSTALLMODE', '{reinstallMode}')");
        if (streamFile is not null)
        {
            var rec = installerType.InvokeMember("CreateRecord", System.Reflection.BindingFlags.InvokeMethod, null, installer, [2])!;
            try
            {
                rec.GetType().InvokeMember("StringData", System.Reflection.BindingFlags.SetProperty, null, rec, [1, cabinet.TrimStart('#')]);
                rec.GetType().InvokeMember("SetStream", System.Reflection.BindingFlags.InvokeMethod, null, rec, [2, streamFile]);
                Exec("INSERT INTO `_Streams` (`Name`, `Data`) VALUES (?, ?)", rec);
            }
            finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(rec); }
        }
        dbType.InvokeMember("Commit", System.Reflection.BindingFlags.InvokeMethod, null, db, null);
        System.Runtime.InteropServices.Marshal.FinalReleaseComObject(db);
        System.Runtime.InteropServices.Marshal.FinalReleaseComObject(installer);
    }

    // ---- Checking a setup program ----

    [Theory]
    [InlineData("R for Windows 4.4.2", "R for Windows R-4.4.2-win.exe The R Foundation", true)]
    [InlineData("R for Windows 4.4.2", "Rider Setup JetBrains rider-setup.exe", false)]
    [InlineData("HP Support Assistant", "HP Support Assistant HPSupportAssistant.exe HP Inc.", true)]
    [InlineData("HP Support Assistant", "ShopLogic helper shoplogic.exe", false)]
    [InlineData("7-Zip 24.08 (x64)", "7-Zip 7z2408-x64.exe Igor Pavlov", true)]
    [InlineData("Python 3.13.9 (64-bit)", "Python 3.13.9 (64-bit) python-3.13.9-amd64.exe Python Software Foundation", true)]
    [InlineData("Visual Studio Code", "Python 3.13.9 (64-bit) python-3.13.9-amd64.exe Python Software Foundation", false)]
    [InlineData("Microsoft Visual C++ 2015-2022 Redistributable (x64)", "Microsoft Visual C++ 2022 Redistributable VC_redist.x64.exe Microsoft Corporation", true)]
    // Every program from a maker shares its name, so the maker alone is not enough.
    [InlineData("Microsoft Visual C++ 2015-2022 Redistributable (x64)", "Microsoft Edge Installer MicrosoftEdgeSetup.exe Microsoft Corporation", false)]
    [InlineData("Windows Software Development Kit", "Microsoft Edge Installer MicrosoftEdgeSetup.exe Microsoft Corporation", false)]
    // Every word of the name before its version has to be there, not only the first: a signed setup of another program by the same maker does not pass.
    [InlineData("HP Support Assistant", "HP Smart hpsmart.exe HP Inc.", false)]
    [InlineData("Visual Studio Code", "Microsoft Visual C++ 2022 Redistributable VC_redist.x64.exe Microsoft Corporation", false)]
    [InlineData("Mozilla Firefox (x64 en-US)", "Firefox Setup 131.0.exe Mozilla Corporation Firefox", true)]
    [InlineData("", "anything", false)]
    [InlineData("(64-bit) 1.0", "anything x64", false)]
    public void AProgramsWholeNameHasToBeInTheSetupProgramsOwnDetails(string program, string details, bool expected) =>
        Assert.Equal(expected, InstallerTrust.Names(details, program, wholeName: true));

    [Fact]
    public void ASetupProgramThatInstallsSeveralPartsOnlyHasToCarryTheProgramsFirstWord()
    {
        const string bundle = "Python 3.13.9 (64-bit) python-3.13.9-amd64.exe Python Software Foundation";
        Assert.True(InstallerTrust.Names(bundle, "Python Launcher", wholeName: false));
        Assert.False(InstallerTrust.Names(bundle, "Python Launcher", wholeName: true));
        Assert.False(InstallerTrust.Names(bundle, "Visual Studio Code", wholeName: false));
    }

    [Theory]
    [InlineData("Python Software Foundation", "Python Software Foundation", true)]
    [InlineData("Microsoft Corporation", "Microsoft Corporation", true)]
    [InlineData("Oracle America, Inc.", "Oracle Corporation", true)]
    [InlineData("JetBrains s.r.o.", "JetBrains", true)]
    [InlineData("OpenJS Foundation", "Node.js Foundation", false)]
    [InlineData("Contoso Ltd", "Python Software Foundation", false)]
    [InlineData("", "Python Software Foundation", false)]
    public void TheSignerHasToBeTheProgramsPublisherGiveOrTakeTheLegalSuffix(string signer, string publisher, bool same) =>
        Assert.Equal(same, InstallerTrust.SamePublisher(signer, publisher));

    [Fact]
    public void AnUnsignedFileIsNeverTrustedEvenWhenItsNameMatches()
    {
        var file = Path.Combine(folder, "python-setup.exe");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(file, [0x4D, 0x5A, 0, 0]);

        Assert.Equal(InstallerSignature.Unsigned, InstallerTrust.Verify(file).Signature);
        Assert.Contains("not signed", InstallerTrust.Problem(file, "Python 3.13.9 (64-bit)", "Python Software Foundation", wholeName: true));
        Assert.NotNull(InstallerTrust.Problem(Path.Combine(folder, "missing.exe"), "Python", "", wholeName: false));
        Assert.Equal(87, new WindowsInstallerOps().RestoreBundleCopy(file, "Python 3.13.9 (64-bit)", "Python Software Foundation", Path.Combine(WindowsInstallerOps.DefaultCacheRoots()[1], "{A}", "python-setup.exe")).ExitCode);
    }

    [Theory]
    [InlineData(@"C:\Users\X\AppData\Local\Package Cache\..\..\..\Windows\evil.exe")]
    [InlineData(@"C:\Users\X\AppData\Local\Package Cache\..\evil.exe")]
    [InlineData(@"D:\Somewhere\Package Cache\{A}\evil.exe")]
    public void APathThatWalksOutOfThePackageCacheOrIsNotInOneOfTheTwoRealOnesIsRefused(string target)
    {
        var ops = new WindowsInstallerOps();

        Assert.Equal(87, ops.RestoreBundleCopy(@"C:\dl\whatever.exe", "Python 3.13.9 (64-bit)", "", target).ExitCode);
        Assert.False(ops.RemoveCopy(target));
        Assert.Null(ops.CachePath(target));
    }

    [Theory]
    [InlineData("6.7.0.0", "6.7", true)]
    [InlineData("3.13.9", "3.13.9.0", true)]
    [InlineData("6.7.1", "6.7", false)]
    [InlineData("10.0", "1.0", false)]
    [InlineData("1.0", "1", true)]
    public void TrailingZeroPartsDoNotMakeAnotherVersion(string left, string right, bool same) => Assert.Equal(same, WingetFetcher.SameVersion(left, right));
}

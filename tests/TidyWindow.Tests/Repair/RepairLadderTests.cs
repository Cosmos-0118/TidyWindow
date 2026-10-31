using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Text.Json;
using Microsoft.Win32;
using TidyWindow.Next.Core;
using Xunit;

namespace TidyWindow.Next.Tests;

/// <summary>A broken PC in a throwaway registry: the Python 3.13 case, laid out the way Windows Installer and Burn lay it out.</summary>
[SupportedOSPlatform("windows")]
public sealed class BrokenPython
{
    public const string Code = "{34E4C3B0-353E-4E04-88B1-EF41B1219C86}";
    public const string Other = "{11111111-2222-3333-4444-555555555555}";
    public const string Upgrade = "{8E3A8A3D-2C5B-4E49-8D4C-0A1A1E0C7B11}";
    public const string Patch = "{AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE}";
    public const string Bundle = "{7A1B3C5D-1111-4222-8333-444455556666}";
    public const string Runtime = "{9F0E1D2C-1111-4222-8333-444455556666}";
    public static readonly string Packed = InstallRepair.Pack(Code), OtherPacked = InstallRepair.Pack(Other), UpgradePacked = InstallRepair.Pack(Upgrade), PatchPacked = InstallRepair.Pack(Patch);
    public const string Cv = @"SOFTWARE\Microsoft\Windows\CurrentVersion";
    public static string UserData(string sid) => $@"HKLM\{Cv}\Installer\UserData\{sid}";
    public static string Arp(string code) => $@"HKCU\{Cv}\Uninstall\{code}";

    public static void PerUser(RepairSandbox sb)
    {
        var sid = RepairSandbox.Sid;
        foreach (var (code, packed, name) in new[] { (Code, Packed, "Python 3.13.9 Core Interpreter (64-bit)"), (Other, OtherPacked, "Python 3.13.16 Core Interpreter (64-bit)") })
        {
            sb.Put($@"HKCU\SOFTWARE\Microsoft\Installer\Products\{packed}", ("ProductName", name), ("PackageCode", InstallRepair.Pack("{0F01F96E-3695-419B-8F5D-583AAF38417E}")));
            sb.Put($@"HKCU\SOFTWARE\Microsoft\Installer\Products\{packed}\SourceList", ("LastUsedSource", $@"n;1;C:\Users\X\AppData\Local\Package Cache\{code}v3.13.9150.0\"), ("PackageName", "core.msi"));
            sb.Put($@"HKCU\SOFTWARE\Microsoft\Installer\Products\{packed}\SourceList\Net", ("1", $@"C:\Users\X\AppData\Local\Package Cache\{code}v3.13.9150.0\"));
            sb.Put($@"HKCU\SOFTWARE\Microsoft\Installer\Features\{packed}", ("Core", "Q#1!Q"), ("", "x"));
            sb.Put($@"{UserData(sid)}\Products\{packed}\InstallProperties", ("DisplayName", name), ("LocalPackage", @"C:\Windows\Installer\1a2b3c.msi"));
            sb.Put(Arp(code), ("DisplayName", name), ("UninstallString", $"MsiExec.exe /X{code}"), ("WindowsInstaller", 1));
        }
        sb.Put($@"{UserData(sid)}\Products\{Packed}\Patches\{PatchPacked}", ("State", 1));
        sb.Put($@"{UserData(sid)}\Patches\{PatchPacked}", ("LocalPackage", @"C:\Windows\Installer\patch.msp"));
        sb.Put($@"HKCU\SOFTWARE\Microsoft\Installer\Patches\{PatchPacked}", ("Patches", "x"));
        sb.Put($@"HKCU\SOFTWARE\Microsoft\Installer\UpgradeCodes\{UpgradePacked}", (Packed, "x"), (OtherPacked, "x"));
        sb.Put($@"{UserData(sid)}\Components\C0000000000000000000000000000001", (Packed, @"C:\Python\python.exe"));
        sb.Put($@"{UserData(sid)}\Components\C0000000000000000000000000000002", (Packed, @"C:\Python\python313.dll"), (OtherPacked, @"C:\Python\python313.dll"));
        sb.Put($@"{UserData(sid)}\Components\C0000000000000000000000000000003", (OtherPacked, @"C:\Python\other.dll"));
        // Python's Burn bundle: its list entry, its dependency record, and the record that names it as a dependent of the Visual C++ runtime.
        sb.Put($@"HKCU\{Cv}\Uninstall\{Bundle}", ("DisplayName", "Python 3.13.9 (64-bit)"), ("BundleProviderKey", Bundle), ("BundleCachePath", $@"C:\Users\X\AppData\Local\Package Cache\{Bundle}\python-3.13.9-amd64.exe"), ("UninstallString", $@"""C:\Users\X\AppData\Local\Package Cache\{Bundle}\python-3.13.9-amd64.exe"" /uninstall"));
        sb.Put($@"HKCU\SOFTWARE\Classes\Installer\Dependencies\{Bundle}", ("", Bundle));
        sb.Put($@"HKCU\SOFTWARE\Classes\Installer\Dependencies\{Runtime}\Dependents\{Bundle}", ("", Bundle));
        sb.Put($@"HKCU\SOFTWARE\Classes\Installer\Dependencies\{Runtime}\Dependents\{{OTHER-BUNDLE}}", ("", "other"));
        // The permissions the screenshot's failing key must have had: this account can read it but not write to it or remove it.
        sb.Lock($@"HKCU\SOFTWARE\Microsoft\Installer\Features\{Packed}");
    }

    public static void Machine(RepairSandbox sb)
    {
        foreach (var (code, packed) in new[] { (Code, Packed), (Other, OtherPacked) })
        {
            sb.Put($@"HKLM\SOFTWARE\Classes\Installer\Products\{packed}", ("ProductName", "Machine product"), ("PackageCode", InstallRepair.Pack("{0F01F96E-3695-419B-8F5D-583AAF38417E}")));
            sb.Put($@"HKLM\SOFTWARE\Classes\Installer\Products\{packed}\SourceList\Media", ("1", "DiskPrompt;"));
            sb.Put($@"HKLM\SOFTWARE\Classes\Installer\Features\{packed}", ("Core", "x"));
            sb.Put($@"{UserData("S-1-5-18")}\Products\{packed}\InstallProperties", ("DisplayName", "Machine product"), ("LocalPackage", @"C:\Windows\Installer\gone.msi"));
            sb.Put($@"HKLM\{Cv}\Uninstall\{code}", ("DisplayName", "Machine product"));
            sb.Put($@"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\{code}", ("DisplayName", "Machine product"));
        }
        sb.Put($@"HKLM\SOFTWARE\Classes\Installer\UpgradeCodes\{UpgradePacked}", (Packed, "x"), (OtherPacked, "x"));
        sb.Put($@"{UserData("S-1-5-18")}\Components\C0000000000000000000000000000002", (Packed, @"C:\M\a.dll"), (OtherPacked, @"C:\M\a.dll"));
        sb.Put($@"{UserData("S-1-5-18")}\Components\C0000000000000000000000000000004", (Packed, @"C:\M\b.dll"));
    }
}

public sealed class FakePc : IInstallSystem
{
    public List<UninstallEntry> Programs = [];
    public List<MsiProduct> Msi = [], GhostList = [];
    public List<ValueRef> Links = [];
    public HashSet<string> Files = new(StringComparer.OrdinalIgnoreCase), Folders = new(StringComparer.OrdinalIgnoreCase);
    public Func<string, int> State = _ => InstallRepair.Unknown;
    public bool ServiceStuck;
    public List<int> Ended = [];

    public Func<IReadOnlyList<UninstallEntry>>? Live;
    public IReadOnlyList<UninstallEntry> Entries() => Live?.Invoke() ?? Programs;
    public IReadOnlyList<MsiProduct> Products() => Msi;
    public int ProductState(string code) => State(code);
    public bool FileExists(string path) => Files.Contains(path);
    public bool FolderExists(string path) => Folders.Contains(path);
    public bool DriveAvailable(string path) => true;
    public string? FindOnPath(string fileName) => null;
    public string Expand(string text) => text;
    public bool InstallLockExists() => false;
    public bool InstallerBusy() => false;
    public IReadOnlyList<RunningInstaller> Installers() => [];
    public IReadOnlyList<string> PendingRestart() => [];
    public bool EndProcess(int id) { Ended.Add(id); return true; }
    public IReadOnlyList<MsiProduct> Ghosts() => GhostList;
    public IReadOnlyList<ValueRef> DanglingLinks() => Links;
    public bool InstallerServiceStuck() => ServiceStuck;
    public HashSet<string> ProductsWithFiles = new(StringComparer.OrdinalIgnoreCase);
    public bool ProductFilesPresent(MsiProduct product) => ProductsWithFiles.Contains(product.Code);
    public HashSet<string> BundlesWithChildren = new(StringComparer.OrdinalIgnoreCase);
    public bool BundleHasChildren(string bundleId) => BundlesWithChildren.Contains(bundleId);
    public Dictionary<string, string[]> FolderFiles = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<string> FilesIn(string folder) => FolderFiles.GetValueOrDefault(folder) ?? [];
}

public sealed class FakeFetcher : IInstallerFetcher
{
    public WingetMatch? Found;
    public string[] Files = [];
    public int ReinstallExit;
    public Action? OnReinstall;
    public List<string> Calls = [];
    public WingetMatch? Match(string name, string version) { Calls.Add($"match {name} {version}"); return Found; }
    public IReadOnlyList<string> Download(WingetMatch match, string folder, string? architecture, CancellationToken cancellationToken) { Calls.Add($"download {match.Id} {match.Version} {architecture}"); return Files; }
    public RunResult Reinstall(WingetMatch match, CancellationToken cancellationToken) { Calls.Add($"reinstall {match.Id}"); OnReinstall?.Invoke(); return new(ReinstallExit, ""); }
}

public sealed class FakeOps : IInstallerOps
{
    public List<string> Candidates = [];
    public Dictionary<string, MsiPackageInfo> Packages = new(StringComparer.OrdinalIgnoreCase);
    public string? Registered;
    public int ExitCode;
    public Action? OnReinstall;
    public List<string> Reinstalled = [];
    public bool ServiceStopped, StopWorks = true;
    public Dictionary<string, string[]> Folders = new(StringComparer.OrdinalIgnoreCase), Bundles = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Expanded = [], Repaired = [];
    public int BundleExit;
    public Action? OnRepairBundle;
    public IReadOnlyList<string> FilesIn(string folder) => Folders.GetValueOrDefault(folder) ?? [];
    public IReadOnlyList<string> ExpandBundle(string exePath, string folder, string programName) { Expanded.Add(exePath); return Bundles.GetValueOrDefault(exePath) ?? []; }
    public List<string> RestoredTo = [], Removed = [];
    public bool RemoveWorks = true;
    public bool RemoveCopy(string path) { Removed.Add(path); return RemoveWorks; }
    public RunResult RestoreBundleCopy(string exePath, string programName, string targetPath) { Repaired.Add(exePath); RestoredTo.Add(targetPath); OnRepairBundle?.Invoke(); return new(BundleExit, ""); }
    public IReadOnlyList<string> CandidatePackages(MsiProduct product) => Candidates;
    public MsiPackageInfo? ReadPackage(string path) => Packages.GetValueOrDefault(path);
    public string? RegisteredPackageCode(MsiProduct product) => Registered;
    public RunResult Reinstall(string msiPath, MsiProduct product) { Reinstalled.Add(msiPath); OnReinstall?.Invoke(); return new(ExitCode, "Log: fake.log"); }
    public bool StopInstallerService() { ServiceStopped = true; return StopWorks; }
}

/// <summary>Wraps an editor so a test can make one step fail, as a locked key or a vanished helper would.</summary>
public sealed class FlakyEditor(IKeyEditor inner) : IKeyEditor
{
    public Func<string, bool> FailDelete = _ => false, FailRestore = _ => false, FailFootprint = _ => false;
    public Exception Failure = new IOException("Windows said no.");
    public int Deletes;
    public bool IsAllowed(string path) => inner.IsAllowed(path);
    public bool Exists(string path) => inner.Exists(path);
    public KeyBackup? Snapshot(string path) => inner.Snapshot(path);
    public KeyBackup? Delete(string path) { Deletes++; if (FailDelete(path)) throw Failure; return inner.Delete(path); }
    public KeyBackup? DeleteValue(string path, string name) { if (FailDelete(path)) throw Failure; return inner.DeleteValue(path, name); }
    public KeyBackup? SetText(string path, string name, string value) => inner.SetText(path, name, value);
    public void Restore(KeyBackup backup) { if (FailRestore(backup.Path)) throw Failure; inner.Restore(backup); }
    public FootprintPlan Footprint(FootprintRequest request) => inner.Footprint(request);
}

[SupportedOSPlatform("windows")]
public sealed class RepairLadderTests : IDisposable
{
    private readonly RepairSandbox sandbox = new();
    private readonly string folder = Path.Combine(Path.GetTempPath(), "tw-ladder-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly RepairHistory history;
    public RepairLadderTests() => history = new RepairHistory(folder);
    public void Dispose() { sandbox.Dispose(); try { Directory.Delete(folder, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }

    private static MsiProduct PerUserProduct => new(BrokenPython.Code, "User", RepairSandbox.Sid, "Python 3.13.9 Core Interpreter (64-bit)", @"C:\Windows\Installer\1a2b3c.msi");
    private static MsiProduct MachineProduct => new(BrokenPython.Code, "Machine", "", "Machine product", @"C:\Windows\Installer\gone.msi");

    private FakePc PcWith(MsiProduct product, string arpKey, bool alive = false)
    {
        var pc = new FakePc { Msi = [product] };
        // Windows Installer answers "known" for as long as the product's record is in the registry.
        var packed = InstallRepair.Pack(product.Code);
        pc.State = code => string.Equals(code, product.Code, StringComparison.OrdinalIgnoreCase)
            && (sandbox.Has($@"HKCU\SOFTWARE\Microsoft\Installer\Products\{packed}") || sandbox.Has($@"HKLM\SOFTWARE\Classes\Installer\Products\{packed}")) ? 5 : InstallRepair.Unknown;
        pc.Programs = [new(arpKey, product.Name, "3.13.9", "PSF", $"MsiExec.exe /X{product.Code}", "", alive ? @"C:\Python313" : "", "", true, false, false, false)];
        if (alive) pc.Folders.Add(@"C:\Python313");
        return pc;
    }

    private RepairService Service(FakePc pc, IKeyEditor? machine = null, IKeyEditor? user = null, IInstallerOps? ops = null, int[]? unlocks = null) =>
        new(pc, user ?? sandbox.User(), machine ?? sandbox.Elevated(), _ => { if (unlocks is not null) unlocks[0]++; return Task.CompletedTask; }, () => { }, history, ops, ops);

    private static RepairFinding Record(FakePc pc) => Assert.Single(InstallRepair.Scan(pc).Findings, x => x.Footprint is { Kind: "product" });

    [Fact]
    public async Task APerUserProgramWithALockedFeaturesKeyIsRemovedCompletelyAndSharedPartsSurvive()
    {
        BrokenPython.PerUser(sandbox);
        var before = sandbox.Dump();
        var pc = PcWith(PerUserProduct, BrokenPython.Arp(BrokenPython.Code));
        var finding = Record(pc);
        Assert.True(finding.NeedsAdmin);

        var outcome = await Service(pc).FixAsync([finding], "Repaired Python");

        var result = Assert.Single(outcome.Results!);
        Assert.Equal(RepairStatus.Fixed, result.Status);
        Assert.Empty(outcome.Problems);
        // Nothing anywhere names the removed product, in any key, value name or value.
        Assert.Empty(sandbox.Mentions(BrokenPython.Packed, BrokenPython.Code));
        Assert.Equal(InstallRepair.Unknown, pc.ProductState(BrokenPython.Code));
        // The newer product that shares the upgrade code and a component is untouched.
        Assert.True(sandbox.Has($@"HKCU\SOFTWARE\Microsoft\Installer\Products\{BrokenPython.OtherPacked}"));
        Assert.True(sandbox.Has(BrokenPython.Arp(BrokenPython.Other)));
        Assert.True(sandbox.HasValue($@"HKCU\SOFTWARE\Microsoft\Installer\UpgradeCodes\{BrokenPython.UpgradePacked}", BrokenPython.OtherPacked));
        Assert.True(sandbox.HasValue($@"{BrokenPython.UserData(RepairSandbox.Sid)}\Components\C0000000000000000000000000000002", BrokenPython.OtherPacked));
        Assert.False(sandbox.Has($@"{BrokenPython.UserData(RepairSandbox.Sid)}\Components\C0000000000000000000000000000001"));
        Assert.False(sandbox.Has($@"{BrokenPython.UserData(RepairSandbox.Sid)}\Patches\{BrokenPython.PatchPacked}"));
        // The change is kept whole, so it can be undone.
        Assert.NotNull(outcome.Batch);

        var undo = await Service(pc).UndoAsync(outcome.Batch!);

        Assert.True(undo.Complete, string.Join(" ", undo.Problems));
        RepairSandbox.AssertSame(before, sandbox.Dump());
        Assert.Null(history.Latest());
    }

    [Fact]
    public async Task AMachineWideProgramIsRemovedFromEveryViewAndHiveAndUndoRestoresItExactly()
    {
        BrokenPython.Machine(sandbox);
        var before = sandbox.Dump();
        var pc = PcWith(MachineProduct, $@"HKLM\{BrokenPython.Cv}\Uninstall\{BrokenPython.Code}");
        var outcome = await Service(pc).FixAsync([Record(pc)], "Repaired");

        Assert.Equal(RepairStatus.Fixed, Assert.Single(outcome.Results!).Status);
        Assert.Empty(sandbox.Mentions(BrokenPython.Packed, BrokenPython.Code));
        Assert.True(sandbox.Has($@"HKLM\SOFTWARE\Classes\Installer\Products\{BrokenPython.OtherPacked}"));
        Assert.True(sandbox.HasValue($@"HKLM\SOFTWARE\Classes\Installer\UpgradeCodes\{BrokenPython.UpgradePacked}", BrokenPython.OtherPacked));
        Assert.False(sandbox.Has($@"{BrokenPython.UserData("S-1-5-18")}\Components\C0000000000000000000000000000004"));

        Assert.True((await Service(pc).UndoAsync(outcome.Batch!)).Complete);
        RepairSandbox.AssertSame(before, sandbox.Dump());
    }

    [Fact]
    public async Task UndoWorksOnAKeyThatAnInstallerRecreatedWithPermissionsThatKeepThisAccountOut()
    {
        // The reported failure: after the repair, the Python installer ran and left the Features key behind with its own permissions; the undo then failed on it.
        BrokenPython.PerUser(sandbox);
        var before = sandbox.Dump();
        var pc = PcWith(PerUserProduct, BrokenPython.Arp(BrokenPython.Code));
        var outcome = await Service(pc).FixAsync([Record(pc)], "Repaired Python");
        var features = $@"HKCU\SOFTWARE\Microsoft\Installer\Features\{BrokenPython.Packed}";
        sandbox.Put(features, ("Core", "recreated by an installer"));
        sandbox.Lock(features);
        using (var key = sandbox.Open(features, write: false)) Assert.Equal("recreated by an installer", key.GetValue("Core"));
        // A plain write is refused, which is what the old undo did.
        Assert.ThrowsAny<Exception>(() => { using var key = sandbox.Root.OpenSubKey(@"HKU\" + RepairSandbox.Sid + features[4..], true)!; key.SetValue("Core", "x"); });

        var undo = await Service(pc).UndoAsync(outcome.Batch!);

        Assert.True(undo.Complete, string.Join(" ", undo.Problems));
        RepairSandbox.AssertSame(before, sandbox.Dump());
    }

    [Fact]
    public async Task AnUndoThatCannotFinishPutsNothingBackAndSaysExactlyWhy()
    {
        BrokenPython.PerUser(sandbox);
        var pc = PcWith(PerUserProduct, BrokenPython.Arp(BrokenPython.Code));
        var outcome = await Service(pc).FixAsync([Record(pc)], "Repaired Python");
        var afterFix = sandbox.Dump();
        var flaky = new FlakyEditor(sandbox.Elevated()) { FailRestore = path => path.Contains(@"\Features\") };

        var undo = await Service(pc, machine: flaky, user: flaky).UndoAsync(outcome.Batch!);

        Assert.False(undo.Complete);
        Assert.Contains("could not be put back, so nothing was restored", undo.Problems[0]);
        Assert.Contains(undo.Problems, x => x.StartsWith(BrokenPython.Packed + ":") && x.Contains("Windows said no."));
        RepairSandbox.AssertSame(afterFix, sandbox.Dump());
        Assert.NotNull(history.Latest());
    }

    [Fact]
    public async Task ARemovalThatFailsPartWayPutsTheWholeRecordBackAndIsReportedNotClaimed()
    {
        BrokenPython.PerUser(sandbox);
        var before = sandbox.Dump();
        var pc = PcWith(PerUserProduct, BrokenPython.Arp(BrokenPython.Code));
        var flaky = new FlakyEditor(sandbox.Elevated()) { FailDelete = path => path.Contains(@"\Features\") };

        var outcome = await Service(pc, machine: flaky).FixAsync([Record(pc)], "Repaired Python");

        var result = Assert.Single(outcome.Results!);
        Assert.Equal(RepairStatus.NotFixed, result.Status);
        Assert.Contains("Windows said no.", result.Summary);
        Assert.Contains("put back", result.Summary);
        RepairSandbox.AssertSame(before, sandbox.Dump());
        Assert.Null(outcome.Batch);
        Assert.Equal(0, outcome.Fixed);
    }

    [Fact]
    public async Task AFixIsOnlyReportedWhenWindowsInstallerNoLongerKnowsTheProgram()
    {
        BrokenPython.PerUser(sandbox);
        var before = sandbox.Dump();
        var pc = PcWith(PerUserProduct, BrokenPython.Arp(BrokenPython.Code));
        pc.State = _ => 5;

        var outcome = await Service(pc).FixAsync([Record(pc)], "Repaired Python");

        var result = Assert.Single(outcome.Results!);
        Assert.Equal(RepairStatus.NotFixed, result.Status);
        Assert.Contains("still reports the program", result.Summary);
        RepairSandbox.AssertSame(before, sandbox.Dump());
    }

    [Fact]
    public async Task ARecordThatCannotBeReadInFullIsNeverPartlyRemoved()
    {
        BrokenPython.PerUser(sandbox);
        sandbox.Lock($@"{BrokenPython.UserData(RepairSandbox.Sid)}\Components\C0000000000000000000000000000002", RegistryRights.QueryValues | RegistryRights.EnumerateSubKeys | RegistryRights.ReadPermissions);
        var before = sandbox.Dump();
        var pc = PcWith(PerUserProduct, BrokenPython.Arp(BrokenPython.Code));

        var outcome = await Service(pc).FixAsync([Record(pc)], "Repaired Python");

        var result = Assert.Single(outcome.Results!);
        Assert.Equal(RepairStatus.NotFixed, result.Status);
        Assert.Contains("removed nothing", result.Summary);
        RepairSandbox.AssertSame(before, sandbox.Dump());
    }

    [Fact]
    public void AProductAlsoRegisteredAnotherWayKeepsItsListEntryAndSharedLinks()
    {
        BrokenPython.PerUser(sandbox);
        sandbox.Put($@"HKLM\SOFTWARE\Classes\Installer\Products\{BrokenPython.Packed}", ("ProductName", "also per machine"));

        var plan = sandbox.Elevated().Footprint(new("product", BrokenPython.Code, "User", RepairSandbox.Sid));

        Assert.True(plan.Elsewhere);
        Assert.DoesNotContain(BrokenPython.Arp(BrokenPython.Code), plan.Keys);
        Assert.DoesNotContain(plan.Values, x => x.Path.Contains("UpgradeCodes"));
        Assert.Contains(plan.Keys, x => x.EndsWith(@"\Features\" + BrokenPython.Packed));
    }

    [Fact]
    public async Task AProgramThatIsStillInstalledIsRepairedFromItsOwnInstallerWithoutRemovingAnything()
    {
        BrokenPython.PerUser(sandbox);
        var before = sandbox.Dump();
        var pc = PcWith(PerUserProduct, BrokenPython.Arp(BrokenPython.Code), alive: true);
        var msi = @"C:\Users\X\AppData\Local\Package Cache\core.msi";
        var ops = new FakeOps { Candidates = [@"C:\other.msi", msi], Registered = BrokenPython.Code };
        ops.Packages[@"C:\other.msi"] = new(BrokenPython.Other, "{X}", "1", "other");
        ops.Packages[msi] = new(BrokenPython.Code, "{Y}", "3.13.9", "core");
        ops.Registered = "{Y}";
        ops.OnReinstall = () => pc.Msi = [PerUserProduct with { LocalPackage = @"C:\Windows\Installer\new.msi" }];
        pc.Files.Add(@"C:\Windows\Installer\new.msi");

        var outcome = await Service(pc, ops: ops).FixAsync([Record(pc)], "Repaired Python");

        var result = Assert.Single(outcome.Results!);
        Assert.Equal(RepairStatus.Fixed, result.Status);
        Assert.Equal([msi], ops.Reinstalled);
        RepairSandbox.AssertSame(before, sandbox.Dump());
        Assert.Null(outcome.Batch);
    }

    [Fact]
    public async Task AProductWhoseListEntryNamesNoFolderButWhoseFilesAreOnThePcIsRepairedNotRemoved()
    {
        // Python's "Core Interpreter" package: no InstallLocation and no icon in its list entry, but its files are all there.
        BrokenPython.PerUser(sandbox);
        var before = sandbox.Dump();
        var pc = PcWith(PerUserProduct, BrokenPython.Arp(BrokenPython.Code));
        pc.ProductsWithFiles.Add(BrokenPython.Code);
        var msi = @"C:\Users\X\AppData\Local\Package Cache\core.msi";
        var ops = new FakeOps { Candidates = [msi], Registered = "{Y}" };
        ops.Packages[msi] = new(BrokenPython.Code, "{Y}", "3.13.9", "core");
        ops.OnReinstall = () => pc.Msi = [PerUserProduct with { LocalPackage = @"C:\Windows\Installer\new.msi" }];
        pc.Files.Add(@"C:\Windows\Installer\new.msi");

        var finding = Record(pc);
        Assert.True(finding.Alive);
        var outcome = await Service(pc, ops: ops).FixAsync([finding], "Repaired Python");

        Assert.Equal(RepairStatus.Fixed, Assert.Single(outcome.Results!).Status);
        Assert.Equal([msi], ops.Reinstalled);
        RepairSandbox.AssertSame(before, sandbox.Dump());
    }

    [Theory]
    [InlineData(3010, RepairStatus.FixedAfterRestart)]
    [InlineData(1603, RepairStatus.Fixed)]
    public async Task ARepairNeedingARestartIsSaidSoAndAFailedOneFallsBackToRemovingTheRecord(int exit, RepairStatus expected)
    {
        BrokenPython.PerUser(sandbox);
        var pc = PcWith(PerUserProduct, BrokenPython.Arp(BrokenPython.Code), alive: true);
        var msi = @"C:\cache\core.msi";
        var ops = new FakeOps { Candidates = [msi], ExitCode = exit, Registered = "{Y}" };
        ops.Packages[msi] = new(BrokenPython.Code, "{Y}", "3.13.9", "core");
        if (exit == 3010) { ops.OnReinstall = () => pc.Msi = [PerUserProduct with { LocalPackage = @"C:\Windows\Installer\new.msi" }]; pc.Files.Add(@"C:\Windows\Installer\new.msi"); }

        var outcome = await Service(pc, ops: ops).FixAsync([Record(pc)], "Repaired Python");

        var result = Assert.Single(outcome.Results!);
        Assert.Equal(expected, result.Status);
        Assert.Equal(exit == 3010, outcome.RestartNeeded);
        if (exit == 1603)
        {
            Assert.Contains(result.Steps, x => x.Contains("could not repair it"));
            Assert.Empty(sandbox.Mentions(BrokenPython.Packed, BrokenPython.Code));
        }
    }

    [Fact]
    public async Task AnInstallerFileOfAnotherBuildIsNeverUsedToRepairAProgram()
    {
        BrokenPython.PerUser(sandbox);
        var pc = PcWith(PerUserProduct, BrokenPython.Arp(BrokenPython.Code), alive: true);
        var ops = new FakeOps { Candidates = [@"C:\cache\core.msi"], Registered = "{REGISTERED}" };
        ops.Packages[@"C:\cache\core.msi"] = new(BrokenPython.Code, "{ANOTHER BUILD}", "3.13.9", "core");

        await Service(pc, ops: ops).FixAsync([Record(pc)], "Repaired Python");

        Assert.Empty(ops.Reinstalled);
    }

    [Fact]
    public async Task APythonBundleWhoseCachedInstallerIsGoneLosesItsEntryAndItsDependencyRecordsButNotWhatOthersNeed()
    {
        BrokenPython.PerUser(sandbox);
        var arp = $@"HKCU\{BrokenPython.Cv}\Uninstall\{BrokenPython.Bundle}";
        var pc = new FakePc { Programs = [new(arp, "Python 3.13.9 (64-bit)", "3.13.9", "PSF", $@"""C:\Users\X\AppData\Local\Package Cache\{BrokenPython.Bundle}\python-3.13.9-amd64.exe"" /uninstall", "", "", "", false, false, false, false, BrokenPython.Bundle)] };

        var finding = Assert.Single(InstallRepair.Scan(pc).Findings);
        Assert.Equal(RepairKind.BundleRecord, finding.Kind);
        var outcome = await Service(pc).FixAsync([finding], "Repaired Python");

        Assert.Equal(RepairStatus.Fixed, Assert.Single(outcome.Results!).Status);
        Assert.False(sandbox.Has(arp));
        Assert.False(sandbox.Has($@"HKCU\SOFTWARE\Classes\Installer\Dependencies\{BrokenPython.Bundle}"));
        Assert.False(sandbox.Has($@"HKCU\SOFTWARE\Classes\Installer\Dependencies\{BrokenPython.Runtime}\Dependents\{BrokenPython.Bundle}"));
        Assert.True(sandbox.Has($@"HKCU\SOFTWARE\Classes\Installer\Dependencies\{BrokenPython.Runtime}\Dependents\{{OTHER-BUNDLE}}"));
    }

    private sealed class Sink : IProgress<RepairProgress>
    {
        public readonly List<RepairProgress> Seen = [];
        public void Report(RepairProgress value) => Seen.Add(value);
    }

    [Fact]
    public async Task AFixSaysWhichItemItIsOnWhatItIsDoingAndWhatAlreadyFinished()
    {
        BrokenPython.PerUser(sandbox);
        var pc = PcWith(PerUserProduct, BrokenPython.Arp(BrokenPython.Code));
        var sink = new Sink();
        var unlocks = new int[1];

        await Service(pc, unlocks: unlocks).FixAsync([Record(pc)], "Repaired Python", progress: sink);

        // The first thing said is the permission request, before any item has started.
        Assert.StartsWith("Asking Windows for administrator permission", sink.Seen[0].Doing);
        Assert.Equal(0, sink.Seen[0].Index);
        var last = sink.Seen[^1];
        Assert.Equal((1, 1), (last.Index, last.Total));
        Assert.Equal(PerUserProduct.Name, last.Title);
        Assert.Contains(sink.Seen, x => x.Doing.StartsWith("Finding every place Windows Installer keeps"));
        Assert.Contains(sink.Seen, x => x.Doing.StartsWith("Removing ") && x.Doing.Contains("registry key"));
        // What finished is listed in order, and only the last few are kept.
        Assert.Contains(last.Done, x => x.StartsWith("Checked afterwards"));
        Assert.True(last.Done.Count <= 4);
    }

    [Fact]
    public async Task ABundleNamedByItsOwnIdInEveryDependentsListLosesAllThoseLinksAndItsProviderKey()
    {
        // Real Burn layout (Python 3.12.8 on this PC): the provider is "CPython-3.12", but every Dependents list names the bundle by the id of its list entry.
        const string Provider = "CPython-3.12";
        sandbox.Put($@"HKCU\{BrokenPython.Cv}\Uninstall\{BrokenPython.Bundle}", ("DisplayName", "Python 3.12.8 (64-bit)"), ("BundleProviderKey", Provider), ("UninstallString", @"""C:\gone\python-3.12.8-amd64.exe"" /uninstall"));
        sandbox.Put($@"HKCU\SOFTWARE\Classes\Installer\Dependencies\{Provider}\Dependents\{BrokenPython.Bundle}", ("", ""));
        sandbox.Put($@"HKCU\SOFTWARE\Classes\Installer\Dependencies\{BrokenPython.Runtime}\Dependents\{BrokenPython.Bundle}", ("", ""));
        sandbox.Put($@"HKCU\SOFTWARE\Classes\Installer\Dependencies\{BrokenPython.Runtime}\Dependents\{{OTHER-BUNDLE}}", ("", "other"));
        var arp = $@"HKCU\{BrokenPython.Cv}\Uninstall\{BrokenPython.Bundle}";
        var pc = new FakePc { Programs = [new(arp, "Python 3.12.8 (64-bit)", "3.12.8", "PSF", @"""C:\gone\python-3.12.8-amd64.exe"" /uninstall", "", "", "", false, false, false, false, Provider)] };

        var outcome = await Service(pc).FixAsync([Assert.Single(InstallRepair.Scan(pc).Findings)], "Repaired Python");

        Assert.Equal(RepairStatus.Fixed, Assert.Single(outcome.Results!).Status);
        Assert.False(sandbox.Has(arp));
        Assert.False(sandbox.Has($@"HKCU\SOFTWARE\Classes\Installer\Dependencies\{Provider}"));
        Assert.False(sandbox.Has($@"HKCU\SOFTWARE\Classes\Installer\Dependencies\{BrokenPython.Runtime}\Dependents\{BrokenPython.Bundle}"));
        Assert.True(sandbox.Has($@"HKCU\SOFTWARE\Classes\Installer\Dependencies\{BrokenPython.Runtime}\Dependents\{{OTHER-BUNDLE}}"));
    }

    [Fact]
    public void ABundleWhoseInstalledProductsStillListItIsCountedAsStillInstalled()
    {
        var arp = $@"HKCU\{BrokenPython.Cv}\Uninstall\{BrokenPython.Bundle}";
        var pc = new FakePc { Programs = [new(arp, "Python 3.12.8 (64-bit)", "3.12.8", "PSF", @"""C:\gone\python-3.12.8-amd64.exe"" /uninstall", "", "", "", false, false, false, false, "CPython-3.12")] };
        Assert.False(Assert.Single(InstallRepair.Scan(pc).Findings).Alive);

        pc.BundlesWithChildren.Add(BrokenPython.Bundle);
        var finding = Assert.Single(InstallRepair.Scan(pc).Findings);
        Assert.True(finding.Alive);
        Assert.Equal(RepairLevel.Review, finding.Level);
    }

    [Fact]
    public async Task AHalfRegisteredProgramLeftByACutShortRepairIsFoundAndCleared()
    {
        BrokenPython.PerUser(sandbox);
        // Only the user's half is left: the machine bookkeeping was removed or never put back.
        using (var user = sandbox.Open($@"{BrokenPython.UserData(RepairSandbox.Sid)}\Products", write: true)) user.DeleteSubKeyTree(BrokenPython.Packed);
        var pc = new FakePc { GhostList = [PerUserProduct with { LocalPackage = null }], State = c => c == BrokenPython.Code ? 1 : -1 };

        var finding = Assert.Single(InstallRepair.Scan(pc).Findings);
        Assert.Equal(RepairLevel.Review, finding.Level);
        Assert.StartsWith("ghost:", finding.Id);
        pc.State = c => sandbox.Has($@"HKCU\SOFTWARE\Microsoft\Installer\Products\{BrokenPython.Packed}") ? 1 : -1;
        var outcome = await Service(pc).FixAsync([finding], "Repaired Python");

        Assert.Equal(RepairStatus.Fixed, Assert.Single(outcome.Results!).Status);
        Assert.Empty(sandbox.Mentions(BrokenPython.Packed, BrokenPython.Code));
    }

    [Fact]
    public async Task LeftoverUpgradeLinksAreOneFindingThatRemovesOnlyThoseLinksAndUndoes()
    {
        BrokenPython.PerUser(sandbox);
        var before = sandbox.Dump();
        var link = new ValueRef($@"HKCU\SOFTWARE\Microsoft\Installer\UpgradeCodes\{BrokenPython.UpgradePacked}", BrokenPython.Packed);
        var pc = new FakePc { Links = [link] };

        var finding = Assert.Single(InstallRepair.Scan(pc).Findings);
        Assert.Equal(RepairKind.DanglingLinks, finding.Kind);
        var outcome = await Service(pc).FixAsync([finding], "Repaired");

        Assert.Equal(RepairStatus.Fixed, Assert.Single(outcome.Results!).Status);
        Assert.False(sandbox.HasValue(link.Path, BrokenPython.Packed));
        Assert.True(sandbox.HasValue(link.Path, BrokenPython.OtherPacked));
        Assert.True((await Service(pc).UndoAsync(outcome.Batch!)).Complete);
        RepairSandbox.AssertSame(before, sandbox.Dump());
    }

    [Fact]
    public async Task AUserKeyThatNeedsMoreRightsIsHandedToTheHelperAndTheHelperIsOnlyAskedWhenNeeded()
    {
        const string key = @"HKCU\SOFTWARE\Microsoft\Installer\Products\00004109110000000000000000F01FEC";
        sandbox.Put(key, ("ProductName", "x"));
        var user = new FlakyEditor(sandbox.User()) { FailDelete = _ => true, Failure = new UnauthorizedAccessException("denied") };
        var unlocks = new[] { 0 };
        var pc = new FakePc();

        var outcome = await Service(pc, user: user, unlocks: unlocks).FixAsync([new("k", RepairKind.BrokenEntry, "Ghost", "", "", RepairLevel.Recommended, [key])], "x");

        Assert.Equal(RepairStatus.Fixed, Assert.Single(outcome.Results!).Status);
        Assert.False(sandbox.Has(key));
        Assert.Equal(1, unlocks[0]);
        Assert.Contains("helper took over", string.Join(" ", outcome.Results![0].Steps));
    }

    [Fact]
    public async Task AStuckInstallerServiceIsStoppedThroughTheHelperAndOnlyThatService()
    {
        var pc = new FakePc { ServiceStuck = true };
        var ops = new FakeOps();

        var finding = Assert.Single(InstallRepair.Scan(pc).Findings);
        var outcome = await Service(pc, ops: ops).FixAsync([finding], "x");

        Assert.Equal(RepairKind.InstallerService, finding.Kind);
        Assert.True(ops.ServiceStopped);
        Assert.Equal(RepairStatus.Fixed, Assert.Single(outcome.Results!).Status);
        ops.StopWorks = false;
        Assert.Equal(RepairStatus.NotFixed, Assert.Single((await Service(pc, ops: ops).FixAsync([finding], "x")).Results!).Status);
    }

    [Fact]
    public void ProductCodesUnpackToTheirOriginal()
    {
        Assert.Equal(BrokenPython.Code, InstallRepair.Unpack(BrokenPython.Packed));
        Assert.Equal("{90140000-0011-0000-0000-0000000FF1CE}", InstallRepair.Unpack("00004109110000000000000000F01FEC"));
        Assert.Throws<ArgumentException>(() => InstallRepair.Unpack("short"));
    }
}

[SupportedOSPlatform("windows")]
public sealed class RegistryFidelityTests : IDisposable
{
    private readonly RepairSandbox sandbox = new();
    public void Dispose() => sandbox.Dispose();
    private const string Key = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData\S-1-5-18\Products\00004109110000000000000000F01FEC";

    [Fact]
    public void EveryKindOfValueAndTheOwnerPermissionsComeBackByteForByte()
    {
        sandbox.Put(Key + @"\InstallProperties", ("DisplayName", "x"));
        using (var key = sandbox.Root.CreateSubKey(Key + @"\InstallProperties", true))
        {
            key.SetValue("", "default value");
            key.SetValue("Expand", @"%ProgramFiles%\x", RegistryValueKind.ExpandString);
            key.SetValue("Many", new[] { "a", "b" }, RegistryValueKind.MultiString);
            key.SetValue("Big", 1L << 40, RegistryValueKind.QWord);
            key.SetValue("Raw", new byte[] { 1, 2, 3, 0, 255 }, RegistryValueKind.Binary);
            key.SetValue("None", new byte[] { 9, 8 }, RegistryValueKind.None);
        }
        sandbox.Lock(Key, RegistryRights.SetValue | RegistryRights.CreateSubKey | RegistryRights.Delete);
        var before = sandbox.Dump();
        var repair = sandbox.Elevated();

        var copy = repair.Delete(Key);
        Assert.False(sandbox.Has(Key));
        repair.Restore(copy!);

        RepairSandbox.AssertSame(before, sandbox.Dump());
    }

    [Fact]
    public void ThePermissionsWindowsInstallerGivesPerUserProductKeysAreRemovedAndRestoredAndAPlainDeleteSilentlyLeavesThem()
    {
        // Read from a real PC: per-user product keys are owned by Administrators with a protected list that gives this account read access only.
        // The first version of the repair deleted them with DeleteSubKeyTree, which reports no error and removes nothing, so it said "repaired" and left the record in place.
        const string path = @"HKCU\SOFTWARE\Microsoft\Installer\Features\00004109110000000000000000F01FEC";
        sandbox.Put(path, ("Core", "x"));
        using (var key = sandbox.Root.OpenSubKey(@"HKU\" + RepairSandbox.Sid + path[4..], RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryRights.ChangePermissions)!)
        {
            var security = new RegistrySecurity();
            // Administrators are left out of the list: on a PC where the tests run elevated (a build server) that group would give full access,
            // and the plain delete below would succeed. The real keys are owned by Administrators, but only an elevated token ever gets to use that.
            security.SetSecurityDescriptorSddlForm("D:P(A;OICI;CCSWRPWPRC;;;WD)(A;OICI;KA;;;SY)", AccessControlSections.Access);
            key.SetAccessControl(security);
        }
        var before = sandbox.Dump();
        using (var parent = sandbox.Root.OpenSubKey(@"HKU\" + RepairSandbox.Sid + @"\SOFTWARE\Microsoft\Installer\Features", true)!)
        {
            parent.DeleteSubKeyTree("00004109110000000000000000F01FEC", throwOnMissingSubKey: false);
        }
        Assert.True(sandbox.Has(path));

        var copy = sandbox.Elevated().Delete(path);

        Assert.False(sandbox.Has(path));
        sandbox.Elevated().Restore(copy!);
        RepairSandbox.AssertSame(before, sandbox.Dump());
    }

    [Fact]
    public void ARecordIsNeverRemovedWhenItsPermissionsCannotBeRead()
    {
        sandbox.Put(Key, ("a", "b"));
        sandbox.Lock(Key, RegistryRights.ReadPermissions | RegistryRights.QueryValues);

        Assert.ThrowsAny<UnauthorizedAccessException>(() => sandbox.Elevated().Delete(Key));
        Assert.True(sandbox.Has(Key));
    }

    [Fact]
    public void ARemovedValueTakesItsKeyWithItWhenNothingElseIsLeftAndComesBackWithTheKeysPermissions()
    {
        const string link = @"HKLM\SOFTWARE\Classes\Installer\UpgradeCodes\00004109110000000000000000F01FEC";
        const string name = "11111111111111111111111111111111";
        sandbox.Put(link, (name, "x"));
        var before = sandbox.Dump();
        var repair = sandbox.Elevated();

        var copy = repair.DeleteValue(link, name);

        Assert.False(sandbox.Has(link));
        Assert.True(copy!.ValuesOnly);
        repair.Restore(copy);
        RepairSandbox.AssertSame(before, sandbox.Dump());
    }

    [Theory]
    [InlineData(@"HKLM\SOFTWARE\Classes\Installer\UpgradeCodes\00004109110000000000000000F01FEC", "Evil")]
    [InlineData(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run", "11111111111111111111111111111111")]
    [InlineData(@"HKLM\SOFTWARE\Classes\Installer\Products\00004109110000000000000000F01FEC", "11111111111111111111111111111111")]
    public void OnlyAProductLinkCanBeTakenOutOfASharedKey(string path, string name)
    {
        var repair = sandbox.Elevated();
        Assert.False(repair.IsValueAllowed(path, name));
        Assert.Throws<InvalidOperationException>(() => repair.DeleteValue(path, name));
    }

    [Fact]
    public void TheHelperMayReachTheUsersInstallerKeysAndTheWindowsWideOnesButNothingElse()
    {
        var repair = sandbox.Elevated();
        Assert.True(repair.IsAllowed(@"HKCU\SOFTWARE\Microsoft\Installer\Features\00004109110000000000000000F01FEC"));
        Assert.True(repair.IsAllowed(@"HKCU\SOFTWARE\Classes\Installer\Dependencies\{abc}\Dependents\{def}"));
        Assert.True(repair.IsAllowed(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\Managed\S-1-5-21-1\Installer\Products\00004109110000000000000000F01FEC"));
        Assert.False(repair.IsAllowed(@"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Run\Evil"));
        Assert.False(repair.IsAllowed(@"HKCU\SOFTWARE\Microsoft\Installer"));
        Assert.False(repair.IsAllowed(@"HKLM\SOFTWARE\Classes\Installer\Products\..\..\..\Run"));
    }

    [Fact]
    public void CopiesSavedByEarlierVersionsStillRestore()
    {
        const string old = """{"Path":"HKCU\\SOFTWARE\\Microsoft\\Installer\\Features\\00004109110000000000000000F01FEC","Values":[{"Name":"Core","Kind":"String","Text":"x","Number":null,"Bytes":null,"Lines":null},{"Name":"N","Kind":"DWord","Number":7}],"Children":[]}""";
        var backup = JsonSerializer.Deserialize<KeyBackup>(old)!;

        sandbox.User().Restore(backup);

        using var key = sandbox.Open(backup.Path);
        Assert.Equal("x", key.GetValue("Core"));
        Assert.Equal(7, key.GetValue("N"));
    }
}

public sealed class RepairHostTests
{
    private const string Key = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Ghost";

    private sealed class Recorder : IKeyEditor
    {
        public List<string> Deleted = [], Values = [];
        public List<KeyBackup> Restored = [];
        public bool IsAllowed(string path) => path == Key;
        public bool Exists(string path) => true;
        public KeyBackup? Snapshot(string path) => new(path, [new("DisplayName", "String", Text: "Ghost")], []);
        public KeyBackup? Delete(string path) { Deleted.Add(path); return Snapshot(path); }
        public KeyBackup? DeleteValue(string path, string name) { Values.Add(path + "|" + name); return new(path, [], [], ValuesOnly: true); }
        public KeyBackup? SetText(string path, string name, string value) { Values.Add(path + "|" + name + "=" + value); return new(path, [], [], ValuesOnly: true); }
        public void Restore(KeyBackup backup) => Restored.Add(backup);
        public FootprintPlan Footprint(FootprintRequest request) => new([Key], [new(Key, "v")], [], ["note"], true);
    }

    private sealed class Ops : IElevatedOps
    {
        public MsiProduct? Reinstalled;
        public RunResult Reinstall(string msiPath, MsiProduct product) { Reinstalled = product; return new(3010, "ok"); }
        public bool StopInstallerService() => true;
    }

    private static RepairSession Session(Recorder editor, Ops ops, bool admin = true) =>
        new(pipe => RepairHost.RunAsync([RepairHost.Command, pipe], _ => editor, ops, () => admin, TimeSpan.FromSeconds(20)));

    [Fact]
    public async Task TheHelperRemovesRestoresAndPlansAndReturnsCopies()
    {
        var editor = new Recorder(); var ops = new Ops();
        using var session = Session(editor, ops);
        await session.OpenAsync();

        var copy = session.Delete(Key);
        session.Restore(copy!);
        session.DeleteValue(Key, "v");
        var plan = session.Footprint(new("product", "{X}"));

        Assert.Equal([Key], editor.Deleted);
        Assert.Equal("Ghost", copy!.Values[0].Text);
        Assert.Equal(Key, Assert.Single(editor.Restored).Path);
        Assert.Equal([Key + "|v"], editor.Values);
        Assert.True(plan.Elsewhere);
        Assert.Equal([Key], plan.Keys);
        Assert.True(session.Exists(Key));
        Assert.Equal(Key, session.Snapshot(Key)!.Path);
    }

    [Fact]
    public async Task TheHelperRepairsAProgramAndStopsTheServiceOnRequest()
    {
        var ops = new Ops();
        using var session = Session(new Recorder(), ops);
        await session.OpenAsync();

        var result = session.Reinstall(@"C:\x\core.msi", new("{X}", "Machine", "", "Core", null));

        Assert.Equal(3010, result.ExitCode);
        Assert.Equal("{X}", ops.Reinstalled!.Code);
        Assert.True(session.StopInstallerService());
    }

    [Fact]
    public async Task TheHelperRefusesAKeyOutsideItsAllowListWhoeverAsks()
    {
        var editor = new Recorder();
        using var session = Session(editor, new Ops());
        await session.OpenAsync();

        Assert.Throws<IOException>(() => session.Delete(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run"));
        Assert.Throws<IOException>(() => session.Restore(new(@"HKLM\SYSTEM\CurrentControlSet\Services\Evil", [], [])));
        Assert.Throws<IOException>(() => session.Snapshot(@"HKLM\SAM\SAM"));
        Assert.Throws<IOException>(() => session.DeleteValue(@"HKLM\SYSTEM\Setup", "x"));

        Assert.Empty(editor.Deleted); Assert.Empty(editor.Restored); Assert.Empty(editor.Values);
    }

    [Fact]
    public async Task AHelperWithoutAdministratorRightsIsNotAccepted()
    {
        using var session = Session(new Recorder(), new Ops(), admin: false);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => session.OpenAsync());
        Assert.False(session.IsOpen);
    }

    [Fact]
    public async Task ADeclinedPromptStopsEverything()
    {
        using var session = new RepairSession(_ => throw new OperationCanceledException("declined"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.OpenAsync());
        Assert.False(session.IsOpen);
    }

    [Fact]
    public async Task ClosingTheSessionStopsTheHelper()
    {
        var finished = new TaskCompletionSource();
        using var session = new RepairSession(async pipe =>
        {
            await RepairHost.RunAsync([RepairHost.Command, pipe], _ => new Recorder(), new Ops(), () => true, TimeSpan.FromSeconds(20));
            finished.SetResult();
        });
        await session.OpenAsync();

        session.Close();

        await finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(session.IsOpen);
    }
}

[SupportedOSPlatform("windows")]
public sealed class RepairServiceBasicsTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "tw-repair-" + Guid.NewGuid().ToString("N")[..8]);
    public RepairServiceBasicsTests() => Directory.CreateDirectory(root);
    public void Dispose() { try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    private sealed class Editor : IKeyEditor
    {
        public Dictionary<string, KeyBackup> Keys = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Refuse = new(StringComparer.OrdinalIgnoreCase);
        public bool Exists(string path) => Keys.ContainsKey(path);
        public KeyBackup? Snapshot(string path) => Keys.GetValueOrDefault(path);
        public KeyBackup? Delete(string path)
        {
            if (Refuse.Contains(path)) throw new UnauthorizedAccessException("denied");
            return Keys.Remove(path, out var key) ? key : null;
        }
        public KeyBackup? DeleteValue(string path, string name) => null;
        public KeyBackup? SetText(string path, string name, string value) => null;
        public void Restore(KeyBackup backup) => Keys[backup.Path] = backup;
        public FootprintPlan Footprint(FootprintRequest request) => FootprintPlan.Empty;
    }

    private static KeyBackup Key(string path) => new(path, [new("DisplayName", "String", Text: path)], []);
    private static RepairFinding Finding(string title, params string[] keys) => new(title, RepairKind.BrokenEntry, title, "", "", RepairLevel.Recommended, keys);

    [Fact]
    public async Task ARepairRemovesTheKeysKeepsACopyAndCanBeUndone()
    {
        const string user = @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Ghost";
        const string machine = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Phantom";
        var users = new Editor { Keys = { [user] = Key(user) } };
        var machines = new Editor { Keys = { [machine] = Key(machine) } };
        var unlocks = 0; var locks = 0;
        var history = new RepairHistory(root);
        var service = new RepairService(new FakePc(), users, machines, _ => { unlocks++; return Task.CompletedTask; }, () => locks++, history);

        var outcome = await service.FixAsync([Finding("Ghost", user), Finding("Phantom", machine)], "Removed 2 leftovers");

        Assert.Equal(2, outcome.Fixed);
        Assert.Empty(outcome.Problems);
        Assert.Empty(users.Keys); Assert.Empty(machines.Keys);
        Assert.Equal((1, 1), (unlocks, locks));
        Assert.NotNull(history.Latest());

        var undo = await service.UndoAsync(history.Latest()!);

        Assert.True(undo.Complete);
        Assert.Equal(2, undo.Restored);
        Assert.Contains(user, users.Keys.Keys);
        Assert.Contains(machine, machines.Keys.Keys);
        Assert.Null(history.Latest());
    }

    [Fact]
    public async Task OnlyRepairsThatReachWindowsWideKeysAskForPermission()
    {
        const string user = @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Ghost";
        var unlocks = 0;
        var service = new RepairService(new FakePc(), new Editor { Keys = { [user] = Key(user) } }, new Editor(), _ => { unlocks++; return Task.CompletedTask; }, () => { }, new RepairHistory(root));

        await service.FixAsync([Finding("Ghost", user)], "Removed 1 leftover");

        Assert.Equal(0, unlocks);
    }

    [Fact]
    public async Task DecliningThePermissionPromptChangesNothing()
    {
        const string machine = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Phantom";
        var machines = new Editor { Keys = { [machine] = Key(machine) } };
        var history = new RepairHistory(root);
        var service = new RepairService(new FakePc(), new Editor(), machines, _ => throw new OperationCanceledException(), () => { }, history);

        await Assert.ThrowsAsync<OperationCanceledException>(() => service.FixAsync([Finding("Phantom", machine)], "x"));

        Assert.Single(machines.Keys);
        Assert.Null(history.Latest());
    }

    [Fact]
    public async Task OneKeyThatCannotBeRemovedDoesNotStopTheRestAndIsReportedWithItsReason()
    {
        const string good = @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Good";
        const string bad = @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Bad";
        var users = new Editor { Keys = { [good] = Key(good), [bad] = Key(bad) }, Refuse = { bad } };
        var machines = new Editor { Keys = { [bad] = Key(bad) }, Refuse = { bad } };
        var service = new RepairService(new FakePc(), users, machines, _ => Task.CompletedTask, () => { }, new RepairHistory(root));

        var outcome = await service.FixAsync([Finding("Bad", bad), Finding("Good", good)], "x");

        Assert.Equal(1, outcome.Fixed);
        Assert.Contains("Bad: denied", outcome.Problems);
        Assert.Equal([bad], users.Keys.Keys);
        Assert.NotNull(outcome.Batch);
    }

    [Fact]
    public async Task AFindingWithSeveralKeysIsRemovedWholeOrNotAtAll()
    {
        const string first = @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\One";
        const string second = @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Two";
        var users = new Editor { Keys = { [first] = Key(first), [second] = Key(second) }, Refuse = { second } };
        var machines = new Editor { Refuse = { second } };
        machines.Keys[second] = Key(second);
        var service = new RepairService(new FakePc(), users, machines, _ => Task.CompletedTask, () => { }, new RepairHistory(root));

        var outcome = await service.FixAsync([Finding("Both", first, second)], "x");

        Assert.Equal(0, outcome.Fixed);
        Assert.Equal([first, second], users.Keys.Keys.OrderBy(x => x));
        Assert.Null(outcome.Batch);
    }

    [Fact]
    public async Task AStuckInstallerIsEndedThroughTheSystem()
    {
        var pc = new FakePc();
        var service = new RepairService(pc, new Editor(), new Editor(), _ => Task.CompletedTask, () => { }, new RepairHistory(root));

        var outcome = await service.FixAsync([new("process:9", RepairKind.RunningInstaller, "msiexec", "", "", RepairLevel.Review, [], 9)], "x");

        Assert.Equal([9], pc.Ended);
        Assert.Equal(1, outcome.Fixed);
        Assert.Null(outcome.Batch);
    }

    [Fact]
    public void OnlyTheLastFewRepairsAreKept()
    {
        var history = new RepairHistory(root, keep: 2);
        var start = DateTimeOffset.Now;
        for (var i = 0; i < 4; i++) history.Save("repair " + i, [Key("k" + i)], start.AddMinutes(i));

        Assert.Equal(["repair 3", "repair 2"], history.All().Select(x => x.Title));
    }
}

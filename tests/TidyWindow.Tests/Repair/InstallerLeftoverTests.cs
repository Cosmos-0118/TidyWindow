using System.Runtime.Versioning;
using TidyWindow.Next.Core;
using Xunit;

namespace TidyWindow.Next.Tests;

/// <summary>Stands in for the real folder editor: folders are entries in a set, and what is set aside is remembered so Undo and the quarantine can be checked.</summary>
public sealed class MemoryFolders(HashSet<string> present) : IFolderEditor
{
    public Func<string, bool> FailAside = _ => false, FailPutBack = _ => false;
    public List<string> Aside = [], Discarded = [];
    public FolderBackup? SetAside(string path, string vault)
    {
        if (FailAside(path)) throw new IOException("Windows said no.");
        if (!present.Remove(path)) return null;
        Aside.Add(path);
        return new(path, vault + @"\x\" + Path.GetFileName(path));
    }
    public void PutBack(FolderBackup backup)
    {
        if (FailPutBack(backup.Original)) throw new IOException("Windows said no.");
        if (!present.Add(backup.Original)) throw new IOException("A folder is there again.");
    }
    public void TakeAgain(FolderBackup backup) => present.Remove(backup.Original);
    public void Discard(FolderBackup backup) => Discarded.Add(backup.Original);
}

[SupportedOSPlatform("windows")]
public sealed class InstallerLeftoverTests : IDisposable
{
    private readonly RepairSandbox sandbox = new();
    private readonly string folder = Path.Combine(Path.GetTempPath(), "tw-leftover-" + Guid.NewGuid().ToString("N")[..8]);
    public void Dispose() { sandbox.Dispose(); try { Directory.Delete(folder, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }

    private static string ProgramFiles => Environment.GetEnvironmentVariable("ProgramFiles")!;
    private static string Windows => Environment.GetEnvironmentVariable("SystemRoot")!;
    private static string ProgramData => Environment.GetEnvironmentVariable("ProgramData")!;
    private const string Code = BrokenPython.Code;
    private static string Icons => Path.Combine(Windows, "Installer", Code);
    private static string Cache => Path.Combine(ProgramData, "Package Cache", Code + "v3.13.9");

    private RepairService Service(FakePc pc, MemoryFolders user, MemoryFolders machine, int[]? unlocks = null) =>
        new(pc, sandbox.User(), sandbox.Elevated(), _ => { if (unlocks is not null) unlocks[0]++; return Task.CompletedTask; }, () => { }, new RepairHistory(folder), userFolders: user, machineFolders: machine);

    private FakePc MachineProductPc()
    {
        BrokenPython.Machine(sandbox);
        var product = new MsiProduct(Code, "Machine", "", "Machine product", @"C:\Windows\Installer\gone.msi");
        var packed = InstallRepair.Pack(Code);
        var pc = new FakePc { Msi = [product] };
        pc.State = code => string.Equals(code, Code, StringComparison.OrdinalIgnoreCase) && sandbox.Has($@"HKLM\SOFTWARE\Classes\Installer\Products\{packed}") ? 5 : InstallRepair.Unknown;
        pc.Programs = [new($@"HKLM\{BrokenPython.Cv}\Uninstall\{Code}", product.Name, "3.13.9", "PSF", $"MsiExec.exe /X{Code}", "", "", "", true, false, false, false)];
        return pc;
    }

    // ---- What may be set aside ----

    [Theory]
    [InlineData(@"C:\ProgramData\Package Cache\{34E4C3B0-353E-4E04-88B1-EF41B1219C86}v3.13.9", FolderKind.InstallerCache)]
    [InlineData(@"C:\ProgramData\Package Cache\{34E4C3B0-353E-4E04-88B1-EF41B1219C86}", FolderKind.InstallerCache)]
    [InlineData(@"C:\Windows\Installer\{34E4C3B0-353E-4E04-88B1-EF41B1219C86}", FolderKind.InstallerCache)]
    [InlineData(@"C:\Users\me\AppData\Roaming\Microsoft\Installer\{34E4C3B0-353E-4E04-88B1-EF41B1219C86}", FolderKind.InstallerCache)]
    [InlineData(@"C:\Users\me\AppData\Local\Package Cache\{34E4C3B0-353E-4E04-88B1-EF41B1219C86}v1", FolderKind.InstallerCache)]
    [InlineData(@"C:\Config.Msi", FolderKind.RollbackFolder)]
    [InlineData(@"C:\Program Files\Ghost App", FolderKind.ProgramFolder)]
    [InlineData(@"C:\Program Files (x86)\Geeks3D\FurMark2_x64", FolderKind.ProgramFolder)]
    [InlineData(@"C:\Users\me\AppData\Local\Programs\Python\Python312", FolderKind.ProgramFolder)]
    // What is shared, or is not a program's own, is refused whatever it is asked for.
    [InlineData(@"C:\Program Files", FolderKind.None)]
    [InlineData(@"C:\Program Files\Common Files", FolderKind.None)]
    [InlineData(@"C:\Program Files\WindowsApps", FolderKind.None)]
    [InlineData(@"C:\Program Files\Windows Defender", FolderKind.None)]
    [InlineData(@"C:\Program Files\Common Files\Vendor", FolderKind.None)]
    [InlineData(@"C:\Program Files\Microsoft SDKs\Windows\Deep\Down", FolderKind.None)]
    [InlineData(@"C:\Windows", FolderKind.None)]
    [InlineData(@"C:\Windows\System32", FolderKind.None)]
    [InlineData(@"C:\Windows\Installer", FolderKind.None)]
    [InlineData(@"C:\Windows\Installer\$PatchCache$", FolderKind.None)]
    [InlineData(@"C:\ProgramData", FolderKind.None)]
    [InlineData(@"C:\ProgramData\Package Cache", FolderKind.None)]
    [InlineData(@"C:\ProgramData\Package Cache\NotAGuid", FolderKind.None)]
    [InlineData(@"C:\Users\me", FolderKind.None)]
    [InlineData(@"C:\Users\me\Documents", FolderKind.None)]
    [InlineData(@"C:\Users\me\AppData\Local\Programs", FolderKind.None)]
    [InlineData(@"C:\Program Files\..\Windows", FolderKind.None)]
    [InlineData(@"D:\Program Files\Ghost", FolderKind.None)]
    [InlineData(@"C:\Config.Msi\Sub", FolderKind.None)]
    public void OnlyInstallerCachesRollbackFilesAndAProgramsOwnFolderAreEverSetAside(string path, FolderKind expected)
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SystemRoot"] = @"C:\Windows", ["ProgramData"] = @"C:\ProgramData", ["LOCALAPPDATA"] = @"C:\Users\me\AppData\Local", ["APPDATA"] = @"C:\Users\me\AppData\Roaming", ["SystemDrive"] = "C:",
            ["ProgramFiles"] = @"C:\Program Files", ["ProgramFiles(x86)"] = @"C:\Program Files (x86)", ["ProgramW6432"] = @"C:\Program Files", ["USERPROFILE"] = @"C:\Users\me"
        };
        Assert.Equal(expected, RepairFolders.Classify(path, name => env.GetValueOrDefault(name)));
    }

    [Fact]
    public void FoldersUnderTheUsersProfileAreTheUsersOwnAndTheRestNeedTheHelper()
    {
        Func<string, string?> env = name => name == "USERPROFILE" ? @"C:\Users\me" : null;
        Assert.False(RepairFolders.IsMachine(@"C:\Users\me\AppData\Local\Programs\Python", env));
        Assert.True(RepairFolders.IsMachine(@"C:\Program Files\Ghost", env));
        Assert.True(RepairFolders.IsMachine(@"C:\Users\mean\AppData\Local\Programs\X", env));
    }

    [Fact]
    public void WindowsInstallersIconFoldersInTheProfileNeedTheHelperBecauseTheInstallerLocksThem()
    {
        Func<string, string?> env = name => name switch { "USERPROFILE" => @"C:\Users\me", "APPDATA" => @"C:\Users\me\AppData\Roaming", _ => null };
        Assert.True(RepairFolders.IsMachine(@"C:\Users\me\AppData\Roaming\Microsoft\Installer\{57AC2A86-EC99-4E9C-9FF9-15DAA88D1FAE}", env));
        Assert.True(RepairFolders.IsMachine(@"C:\Users\me\AppData\Roaming\microsoft\installer\{57ac2a86-ec99-4e9c-9ff9-15daa88d1fae}\", env));
        Assert.False(RepairFolders.IsMachine(@"C:\Users\me\AppData\Roaming\Microsoft\InstallerX\{57AC2A86-EC99-4E9C-9FF9-15DAA88D1FAE}", env));
        Assert.False(RepairFolders.IsMachine(@"C:\Users\me\AppData\Local\Package Cache\{57AC2A86-EC99-4E9C-9FF9-15DAA88D1FAE}v1", env));
    }

    // ---- Sweeping on a purge ----

    [Fact]
    public async Task RemovingAGoneProgramsRecordAlsoSetsItsIconFolderAsideAndUndoMovesItBack()
    {
        var pc = MachineProductPc();
        pc.Icons[Code] = [Icons];
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Icons };
        var machine = new MemoryFolders(present);
        var finding = Assert.Single(InstallRepair.Scan(pc).Findings, x => x.Footprint is { Kind: "product" });
        var service = Service(pc, new MemoryFolders([]), machine);

        var outcome = await service.FixAsync([finding], "Repaired");

        Assert.Equal(RepairStatus.Fixed, Assert.Single(outcome.Results!).Status);
        Assert.DoesNotContain(Icons, present);
        var kept = Assert.Single(outcome.Batch!.SetAside);
        Assert.Equal(Icons, kept.Original);

        var undo = await service.UndoAsync(outcome.Batch);

        Assert.True(undo.Complete, string.Join(" ", undo.Problems));
        Assert.Equal(1, undo.FoldersRestored);
        Assert.Contains(Icons, present);
    }

    [Fact]
    public async Task ThePackageCacheOfAProductIsLeftAloneWhenOnlyItsRecordIsKnownBecauseASetupProgramMayStillUseIt()
    {
        var pc = MachineProductPc();
        pc.Icons[Code] = [Icons];
        pc.Caches[Code] = [Cache];
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Icons, Cache };
        var finding = Assert.Single(InstallRepair.Scan(pc).Findings, x => x.Footprint is { Kind: "product" });

        await Service(pc, new MemoryFolders([]), new MemoryFolders(present)).FixAsync([finding], "Repaired");

        Assert.DoesNotContain(Icons, present);
        Assert.Contains(Cache, present);
    }

    [Fact]
    public async Task AProgramThatIsStillThereKeepsItsInstallerFilesAndItsFolderUnlessThePersonSaidOtherwise()
    {
        var folderPath = Path.Combine(ProgramFiles, "Ghost App");
        var key = @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Ghost";
        sandbox.Put(key, ("DisplayName", "Ghost App"));
        var entry = new UninstallEntry(key, "Ghost App", "1.0", "Maker", @"""" + Path.Combine(folderPath, "unins000.exe") + @"""", "", folderPath, "", false, false, false, false);
        var pc = new FakePc { Programs = [entry] };
        pc.Folders.Add(folderPath);
        var finding = new RepairFinding("entry:x", RepairKind.BrokenEntry, "Ghost App", "", key, RepairLevel.Review, [key], Alive: true, Entry: entry);
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { folderPath };
        var machine = new MemoryFolders(present);

        var kept = await Service(pc, new MemoryFolders([]), machine).FixAsync([finding], "Removed", options: new(RestoreOnly: false));

        Assert.Equal(RepairStatus.Fixed, Assert.Single(kept.Results!).Status);
        Assert.Contains(folderPath, present);
        Assert.Empty(machine.Aside);
    }

    [Fact]
    public async Task WhenThePersonAsksTheProgramsOwnFolderIsSetAsideAndAskingForTheHelperHappensOnce()
    {
        var folderPath = Path.Combine(ProgramFiles, "Ghost App");
        var key = @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Ghost";
        sandbox.Put(key, ("DisplayName", "Ghost App"));
        var entry = new UninstallEntry(key, "Ghost App", "1.0", "Maker", @"""" + Path.Combine(folderPath, "unins000.exe") + @"""", "", folderPath, "", false, false, false, false);
        var pc = new FakePc { Programs = [entry] };
        pc.Folders.Add(folderPath);
        var finding = new RepairFinding("entry:x", RepairKind.BrokenEntry, "Ghost App", "", key, RepairLevel.Review, [key], Alive: true, Entry: entry);
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { folderPath };
        var unlocks = new[] { 0 };

        var outcome = await Service(pc, new MemoryFolders([]), new MemoryFolders(present), unlocks).FixAsync([finding], "Removed", options: new(RemoveProgramFolders: true));

        Assert.Equal(RepairStatus.Fixed, Assert.Single(outcome.Results!).Status);
        Assert.DoesNotContain(folderPath, present);
        Assert.Equal(1, unlocks[0]);
        Assert.Equal(folderPath, Assert.Single(outcome.Batch!.SetAside).Original);
    }

    [Theory]
    [InlineData("Common Files")]
    [InlineData("WindowsApps")]
    public async Task ASharedFolderNamedAsAProgramsLocationIsNeverSetAsideEvenWhenAsked(string shared)
    {
        var folderPath = Path.Combine(ProgramFiles, shared);
        var key = @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Odd";
        sandbox.Put(key, ("DisplayName", "Odd"));
        var entry = new UninstallEntry(key, "Odd", "1.0", "Maker", "", "", folderPath, "", false, false, false, false);
        var pc = new FakePc { Programs = [entry] };
        pc.Folders.Add(folderPath);
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { folderPath };
        var machine = new MemoryFolders(present);

        await Service(pc, new MemoryFolders([]), machine).FixAsync([new RepairFinding("entry:x", RepairKind.BrokenEntry, "Odd", "", key, RepairLevel.Review, [key], Entry: entry)], "Removed", options: new(RemoveProgramFolders: true));

        Assert.Contains(folderPath, present);
        Assert.Empty(machine.Aside);
    }

    [Fact]
    public async Task AFolderThatAnotherListedProgramAlsoUsesIsNotSetAside()
    {
        var folderPath = Path.Combine(ProgramFiles, "Suite");
        var key = @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\One";
        sandbox.Put(key, ("DisplayName", "One"));
        var one = new UninstallEntry(key, "One", "1.0", "Maker", "", "", folderPath, "", false, false, false, false);
        var two = new UninstallEntry(@"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Two", "Two", "1.0", "Maker", "", "", folderPath, "", false, false, false, false);
        var pc = new FakePc { Programs = [one, two] };
        pc.Folders.Add(folderPath);
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { folderPath };

        await Service(pc, new MemoryFolders([]), new MemoryFolders(present)).FixAsync([new RepairFinding("entry:x", RepairKind.BrokenEntry, "One", "", key, RepairLevel.Review, [key], Entry: one)], "Removed", options: new(RemoveProgramFolders: true));

        Assert.Contains(folderPath, present);
    }

    [Fact]
    public async Task ClearingAnInterruptedInstallsMarkerAlsoSetsItsRollbackFolderAside()
    {
        sandbox.Put(InstallRepair.InstallLockKey, ("x", 1));
        var rollback = @"C:\Config.Msi";
        var pc = new FakePc { Lock = true, RollbackPath = rollback };
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { rollback };
        var finding = Assert.Single(InstallRepair.Scan(pc).Findings, x => x.Kind == RepairKind.InstallLock);

        var outcome = await Service(pc, new MemoryFolders([]), new MemoryFolders(present)).FixAsync([finding], "Repaired");

        Assert.DoesNotContain(rollback, present);
        Assert.Equal(rollback, Assert.Single(outcome.Batch!.SetAside).Original);
    }

    [Fact]
    public async Task AnOrphanedRollbackFolderWithoutAnInProgressMarkerIsFoundAndSetAside()
    {
        var rollback = @"C:\Config.Msi";
        var pc = new FakePc { Lock = false, RollbackPath = rollback };
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { rollback };
        var finding = Assert.Single(InstallRepair.Scan(pc).Findings, x => x.Id == "rollback-folder");

        var outcome = await Service(pc, new MemoryFolders([]), new MemoryFolders(present)).FixAsync([finding], "Repaired");

        Assert.DoesNotContain(rollback, present);
        Assert.Equal(rollback, Assert.Single(outcome.Batch!.SetAside).Original);
    }

    [Fact]
    public async Task AFailedSetAsidePutsBackWhatItAlreadyMovedAndKeepsTheRecordRepairIntact()
    {
        var pc = MachineProductPc();
        var second = Path.Combine(Windows, "Installer", "{11111111-2222-3333-4444-555555555555}");
        pc.Icons[Code] = [Icons, second];
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Icons, second };
        var machine = new MemoryFolders(present) { FailAside = path => path == second };
        var finding = Assert.Single(InstallRepair.Scan(pc).Findings, x => x.Footprint is { Kind: "product" });

        var outcome = await Service(pc, new MemoryFolders([]), machine).FixAsync([finding], "Repaired");

        var result = Assert.Single(outcome.Results!);
        Assert.Equal(RepairStatus.Fixed, result.Status);
        Assert.Contains(result.Steps, x => x.Contains("Could not set the program's leftover folders aside"));
        Assert.Equal(new[] { Icons, second }.Order(), present.Order());
        Assert.Empty(outcome.Batch!.SetAside);
    }

    // ---- Finding the ones already left behind ----

    [Fact]
    public async Task IconFoldersOfProgramsWindowsInstallerHasNeverHeardOfAreOfferedAndSetAsideTogether()
    {
        var a = Path.Combine(Windows, "Installer", "{66FE8944-1720-5E4E-A546-2A5609FEF6BB}");
        var b = Path.Combine(Windows, "Installer", "{58A3FC32-1111-2222-3333-444444444444}");
        var pc = new FakePc { Orphans = [a, b] };
        var finding = Assert.Single(InstallRepair.Scan(pc).Findings, x => x.Kind == RepairKind.InstallerFiles);
        Assert.Equal(RepairLevel.Recommended, finding.Level);
        Assert.True(finding.NeedsAdmin);
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { a, b };
        var service = Service(pc, new MemoryFolders([]), new MemoryFolders(present));

        var outcome = await service.FixAsync([finding], "Removed leftovers");

        Assert.Equal(RepairStatus.Fixed, Assert.Single(outcome.Results!).Status);
        Assert.Empty(present);
        Assert.True((await service.UndoAsync(outcome.Batch!)).Complete);
        Assert.Equal(new[] { a, b }.Order(), present.Order());
    }

    [Fact]
    public async Task IconFoldersInTheProfileAreSetAsideByTheHelperBecauseTheAccountItselfMayOnlyReadThem()
    {
        // The account's own editor would be denied, as it is on a real PC: only SYSTEM and Administrators may change %APPDATA%\Microsoft\Installer.
        var a = Path.Combine(Windows, "Installer", "{66FE8944-1720-5E4E-A546-2A5609FEF6BB}");
        var b = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Installer", "{57AC2A86-EC99-4E9C-9FF9-15DAA88D1FAE}");
        var pc = new FakePc { Orphans = [a, b] };
        var finding = Assert.Single(InstallRepair.Scan(pc).Findings, x => x.Kind == RepairKind.InstallerFiles);
        Assert.True(finding.NeedsAdmin);
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { a, b };
        var user = new MemoryFolders(new(present, StringComparer.OrdinalIgnoreCase)) { FailAside = _ => true };
        var machine = new MemoryFolders(present);
        var unlocks = new int[1];
        var service = Service(pc, user, machine, unlocks);

        var outcome = await service.FixAsync([finding], "Removed leftovers");

        Assert.Equal(RepairStatus.Fixed, Assert.Single(outcome.Results!).Status);
        Assert.Empty(present);
        Assert.Empty(user.Aside);
        Assert.Equal(new[] { a, b }.Order(), machine.Aside.Order());
        Assert.Equal(1, unlocks[0]);

        var undo = await service.UndoAsync(outcome.Batch!);

        Assert.True(undo.Complete, string.Join(" ", undo.Problems));
        Assert.Equal(new[] { a, b }.Order(), present.Order());
    }

    // ---- The quarantine ----

    [Fact]
    public async Task EverythingARepairSetAsideIsListedAndKeepingOneMovesItBackAndLeavesTheRestOfTheRepair()
    {
        var pc = new FakePc { Orphans = [Path.Combine(Windows, "Installer", "{66FE8944-1720-5E4E-A546-2A5609FEF6BB}"), Path.Combine(Windows, "Installer", "{58A3FC32-1111-2222-3333-444444444444}")] };
        var present = new HashSet<string>(pc.Orphans, StringComparer.OrdinalIgnoreCase);
        var service = Service(pc, new MemoryFolders([]), new MemoryFolders(present));
        await service.FixAsync([Assert.Single(InstallRepair.Scan(pc).Findings, x => x.Kind == RepairKind.InstallerFiles)], "Removed leftovers");

        var items = service.Quarantine();
        Assert.Equal(2, items.Count);
        Assert.All(items, x => Assert.Equal(QuarantineKind.Folder, x.Kind));

        var outcome = await service.ResolveAsync([items[0]], keep: true);

        Assert.Empty(outcome.Problems);
        Assert.Equal(1, outcome.Done);
        Assert.Single(present);
        Assert.Single(service.Quarantine());
    }

    [Fact]
    public async Task RemovingForGoodDeletesWhatWasKeptAndOnceNothingIsLeftTheRepairLeavesTheUndoTab()
    {
        var pc = new FakePc { Orphans = [Path.Combine(Windows, "Installer", "{66FE8944-1720-5E4E-A546-2A5609FEF6BB}")] };
        var present = new HashSet<string>(pc.Orphans, StringComparer.OrdinalIgnoreCase);
        var machine = new MemoryFolders(present);
        var service = Service(pc, new MemoryFolders([]), machine);
        await service.FixAsync([Assert.Single(InstallRepair.Scan(pc).Findings, x => x.Kind == RepairKind.InstallerFiles)], "Removed leftovers");
        var history = new RepairHistory(folder);
        Assert.Single(history.All());

        var outcome = await service.ResolveAsync(service.Quarantine(), keep: false);

        Assert.Empty(outcome.Problems);
        Assert.Equal(pc.Orphans, machine.Discarded);
        Assert.Empty(service.Quarantine());
        Assert.Empty(history.All());
    }

    [Fact]
    public async Task OneItemThatCannotBePutBackIsReportedAndTheOthersCarryOn()
    {
        var a = Path.Combine(Windows, "Installer", "{66FE8944-1720-5E4E-A546-2A5609FEF6BB}");
        var b = Path.Combine(Windows, "Installer", "{58A3FC32-1111-2222-3333-444444444444}");
        var pc = new FakePc { Orphans = [a, b] };
        var present = new HashSet<string>(pc.Orphans, StringComparer.OrdinalIgnoreCase);
        var machine = new MemoryFolders(present);
        var service = Service(pc, new MemoryFolders([]), machine);
        await service.FixAsync([Assert.Single(InstallRepair.Scan(pc).Findings, x => x.Kind == RepairKind.InstallerFiles)], "Removed leftovers");
        machine.FailPutBack = path => path == a;

        var outcome = await service.ResolveAsync(service.Quarantine(), keep: true);

        Assert.Equal(1, outcome.Done);
        Assert.Single(outcome.Problems);
        Assert.Equal([b], present.ToArray());
        Assert.Equal(a, Assert.Single(service.Quarantine()).Original);
    }

    [Fact]
    public async Task ShortcutFilesAreInTheQuarantineToAndKeepingOneRestoresIt()
    {
        var pc = new LinkPc();
        var lnk = pc.Link($@"{LinkPc.User}\Gone\Gone.lnk", @"C:\Gone\g.exe").Path;
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { lnk };
        var links = new MemoryLinks(present);
        var service = new RepairService(pc, sandbox.User(), sandbox.Elevated(), _ => Task.CompletedTask, () => { }, new RepairHistory(folder), userLinks: links, machineLinks: new MemoryLinks([]));
        await service.FixAsync([Assert.Single(InstallRepair.Scan(pc).Findings)], "Removed");

        var item = Assert.Single(service.Quarantine());
        Assert.Equal(QuarantineKind.Shortcut, item.Kind);
        Assert.Equal("Gone", item.Name);

        await service.ResolveAsync([item], keep: true);

        Assert.Contains(lnk, present);
        Assert.Empty(service.Quarantine());
    }

    private FolderBackup Kept(string name)
    {
        var stored = Path.Combine(folder, "SetAside", name, "App");
        Directory.CreateDirectory(stored);
        File.WriteAllText(Path.Combine(stored, "app.exe"), "x");
        return new(Path.Combine(Path.GetPathRoot(folder)!, "Program Files", "App"), stored);
    }

    [Fact]
    public void ARepairCountLimitNeverPushesOutOneThatStillHoldsFolders()
    {
        var history = new RepairHistory(folder, keep: 1);
        var old = DateTimeOffset.Now.AddDays(-5);
        history.Save("held", [], old, folders: [Kept("a")]);
        history.Save("plain", [], old.AddMinutes(1), files: ["x"]);
        history.Save("newest", [], DateTimeOffset.Now, files: ["y"]);

        var left = history.All().Select(x => x.Title).ToList();

        Assert.Contains("held", left);
        Assert.DoesNotContain("plain", left);
        Assert.Contains("newest", left);
    }

    [Fact]
    public void FoldersSetAsideForMoreThanAMonthAreDeletedByTheClockWhileTheRepairKeepsItsUndo()
    {
        var history = new RepairHistory(folder);
        var kept = Kept("b");
        history.Save("old", [], DateTimeOffset.Now.AddDays(-40), files: ["x"], folders: [kept]);

        Assert.Equal(0, history.Expire(DateTimeOffset.Now.AddDays(-20)));
        Assert.True(Directory.Exists(kept.Stored));
        Assert.Equal(1, history.Expire(DateTimeOffset.Now));

        Assert.False(Directory.Exists(kept.Stored));
        var batch = Assert.Single(history.All());
        Assert.Empty(batch.SetAside);
        Assert.Equal(["x"], batch.PutBack);
    }

    [Fact]
    public void FoldersSetAsideWithinAMonthAreLeftAloneAndARepairWithNothingLeftLeavesTheList()
    {
        var history = new RepairHistory(folder);
        var recent = Kept("c");
        var stale = Kept("d");
        history.Save("recent", [], DateTimeOffset.Now.AddDays(-29), folders: [recent]);
        history.Save("stale", [], DateTimeOffset.Now.AddDays(-31), folders: [stale]);

        Assert.Equal(1, history.Expire());

        Assert.True(Directory.Exists(recent.Stored));
        Assert.False(Directory.Exists(stale.Stored));
        Assert.Equal(["recent"], history.All().Select(x => x.Title));
    }

    [Fact]
    public void AnItemKnowsWhenItWillBeDeleted()
    {
        var created = new DateTimeOffset(2026, 11, 2, 10, 0, 0, TimeSpan.Zero);
        Assert.Equal(created.AddDays(30), RepairHistory.ExpiresAt(created));
    }

    [Fact]
    public void ReadingAThousandRepairsTwiceReadsEachFileOnce()
    {
        var history = new RepairHistory(folder);
        for (var i = 0; i < 300; i++) history.Save("repair " + i, [], DateTimeOffset.Now.AddMinutes(-i), files: ["x" + i]);

        Assert.Equal(300, history.All().Count);
        var path = Directory.EnumerateFiles(folder, "*.json").First();
        var stamp = File.GetLastWriteTimeUtc(path);
        // A copy that changed on disk is read again; the rest come from memory.
        File.WriteAllText(path, File.ReadAllText(path).Replace("repair", "edited"));
        File.SetLastWriteTimeUtc(path, stamp.AddSeconds(5));

        Assert.Contains(history.All(), x => x.Title.StartsWith("edited"));
        Assert.Equal(300, history.All().Count);
    }

    // ---- The real folder editor ----

    private string Vault(string root) => Path.Combine(root, "TidyWindow.Next", "RepairBackups", "SetAside");

    [Fact]
    public void TheFolderEditorMovesAFolderIntoTheVaultAndBackWithEverythingInIt()
    {
        var root = Path.Combine(folder, "fs");
        var allowedRoot = Path.Combine(root, "Programs");
        var program = Path.Combine(allowedRoot, "Ghost");
        Directory.CreateDirectory(Path.Combine(program, "bin"));
        File.WriteAllText(Path.Combine(program, "bin", "ghost.exe"), "exe");
        File.WriteAllText(Path.Combine(program, "readme.txt"), "hello");
        var editor = new FileFolderEditor(path => ShortcutPlaces.Under(path, allowedRoot));

        var kept = editor.SetAside(program, Vault(root))!;

        Assert.False(Directory.Exists(program));
        Assert.Equal("exe", File.ReadAllText(Path.Combine(kept.Stored, "bin", "ghost.exe")));
        editor.PutBack(kept);
        Assert.Equal("hello", File.ReadAllText(Path.Combine(program, "readme.txt")));
        Assert.False(Directory.Exists(kept.Stored));
    }

    [Fact]
    public void TheFolderEditorTakesAFolderAgainAndDiscardsForGood()
    {
        var root = Path.Combine(folder, "fs2");
        var allowedRoot = Path.Combine(root, "Programs");
        var program = Path.Combine(allowedRoot, "Ghost");
        Directory.CreateDirectory(program);
        var locked = Path.Combine(program, "locked.dll");
        File.WriteAllText(locked, "x");
        File.SetAttributes(locked, FileAttributes.ReadOnly);
        var editor = new FileFolderEditor(path => ShortcutPlaces.Under(path, allowedRoot));
        var kept = editor.SetAside(program, Vault(root))!;
        editor.PutBack(kept);

        editor.TakeAgain(kept);
        Assert.False(Directory.Exists(program));
        Assert.True(Directory.Exists(kept.Stored));

        editor.Discard(kept);
        Assert.False(Directory.Exists(kept.Stored));
        Assert.False(Directory.Exists(Path.GetDirectoryName(kept.Stored)));
        Assert.False(Directory.Exists(program));
    }

    [Fact]
    public void TheFolderEditorWillNotOverwriteAFolderThatIsThereAgainAndRefusesWhatItWasNotGiven()
    {
        var root = Path.Combine(folder, "fs3");
        var allowedRoot = Path.Combine(root, "Programs");
        var program = Path.Combine(allowedRoot, "Ghost");
        Directory.CreateDirectory(program);
        var editor = new FileFolderEditor(path => ShortcutPlaces.Under(path, allowedRoot));
        var kept = editor.SetAside(program, Vault(root))!;
        Directory.CreateDirectory(program);
        File.WriteAllText(Path.Combine(program, "new.txt"), "x");

        Assert.Throws<IOException>(() => editor.PutBack(kept));
        Assert.True(File.Exists(Path.Combine(program, "new.txt")));
        Assert.True(Directory.Exists(kept.Stored));

        var outside = Path.Combine(root, "Elsewhere");
        Directory.CreateDirectory(outside);
        Assert.Throws<IOException>(() => editor.SetAside(outside, Vault(root)));
        Assert.True(Directory.Exists(outside));
        // The vault is the only place anything is moved to, or deleted from.
        Assert.Throws<IOException>(() => editor.SetAside(program, Path.Combine(root, "Anywhere")));
        Assert.Throws<IOException>(() => editor.Discard(new(program, outside)));
        Assert.True(Directory.Exists(outside));
        Assert.Null(editor.SetAside(Path.Combine(allowedRoot, "Missing"), Vault(root)));
    }
}

using System.Runtime.Versioning;
using Microsoft.Win32;
using TidyWindow.Next.Core;
using Xunit;

namespace TidyWindow.Next.Tests;

/// <summary>The two ways a program stays broken after its record is fine: its installer file is gone, or it has no uninstaller to run.</summary>
[SupportedOSPlatform("windows")]
public sealed class RepairSourcesTests : IDisposable
{
    private readonly RepairSandbox sandbox = new();
    private readonly string folder = Path.Combine(Path.GetTempPath(), "tw-sources-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly RepairHistory history;
    public RepairSourcesTests() => history = new RepairHistory(folder);
    public void Dispose() { sandbox.Dispose(); try { Directory.Delete(folder, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }

    private const string Msi = @"C:\Downloads\core.msi";
    private static MsiProduct Product => new(BrokenPython.Code, "User", RepairSandbox.Sid, "Python 3.13.9 Core Interpreter (64-bit)", @"C:\Windows\Installer\1a2b3c.msi");

    private FakePc Python()
    {
        BrokenPython.PerUser(sandbox);
        var packed = BrokenPython.Packed;
        var pc = new FakePc { Msi = [Product] };
        pc.State = code => code == BrokenPython.Code && sandbox.Has($@"HKCU\SOFTWARE\Microsoft\Installer\Products\{packed}") ? 5 : -1;
        pc.Programs =
        [
            new(BrokenPython.Arp(BrokenPython.Code), Product.Name, "3.13.9150.0", "PSF", $"MsiExec.exe /X{BrokenPython.Code}", "", @"C:\Python313", "", true, false, false, false),
            new($@"HKCU\{BrokenPython.Cv}\Uninstall\{BrokenPython.Bundle}", "Python 3.13.9 (64-bit)", "3.13.9", "PSF", @"""C:\gone\python-3.13.9-amd64.exe"" /uninstall", "", "", "", false, false, false, false, BrokenPython.Bundle)
        ];
        pc.Folders.Add(@"C:\Python313");
        return pc;
    }

    private static FakeOps Ops(FakePc pc, params string[] msis)
    {
        var ops = new FakeOps { Registered = "{Y}" };
        foreach (var msi in msis) ops.Packages[msi] = new(BrokenPython.Code, "{Y}", "3.13.9", "core");
        ops.OnReinstall = () => { pc.Msi = [Product with { LocalPackage = @"C:\Windows\Installer\new.msi" }]; pc.Files.Add(@"C:\Windows\Installer\new.msi"); };
        return ops;
    }

    private RepairService Service(FakePc pc, FakeOps ops, FakeFetcher? fetcher = null) =>
        new(pc, sandbox.User(), sandbox.Elevated(), _ => Task.CompletedTask, () => { }, history, ops, ops, fetcher);

    private static RepairFinding Record(FakePc pc) => Assert.Single(InstallRepair.Scan(pc).Findings, x => x.Footprint is { Kind: "product" });

    [Fact]
    public async Task AFolderThePersonChoseSuppliesTheInstallerAndTheRecordIsLeftAlone()
    {
        var pc = Python();
        var before = sandbox.Dump();
        var ops = Ops(pc, Msi);
        ops.Folders[@"C:\Downloads"] = [@"C:\Downloads\notes.exe", Msi];

        var outcome = await Service(pc, ops).FixAsync([Record(pc)], "Repaired", options: new(Folders: [@"C:\Downloads"]));

        var result = Assert.Single(outcome.Results!);
        Assert.Equal(RepairStatus.Fixed, result.Status);
        Assert.Contains("a folder the person chose", string.Join(" ", result.Steps));
        Assert.Equal([Msi], ops.Reinstalled);
        RepairSandbox.AssertSame(before, sandbox.Dump());
    }

    [Fact]
    public async Task ASetupProgramIsUnpackedAndOnlyThePackageOfTheExactBuildIsUsed()
    {
        var pc = Python();
        var ops = Ops(pc, @"C:\layout\core.msi");
        ops.Packages[@"C:\layout\dev.msi"] = new(BrokenPython.Other, "{Z}", "3.13.9", "dev");
        ops.Folders[@"C:\Downloads"] = [@"C:\Downloads\python-3.13.9-amd64.exe"];
        ops.Bundles[@"C:\Downloads\python-3.13.9-amd64.exe"] = [@"C:\layout\dev.msi", @"C:\layout\core.msi"];

        var outcome = await Service(pc, ops).FixAsync([Record(pc)], "Repaired", options: new(Folders: [@"C:\Downloads"]));

        Assert.Equal(RepairStatus.Fixed, Assert.Single(outcome.Results!).Status);
        Assert.Equal([@"C:\layout\core.msi"], ops.Reinstalled);
        Assert.Equal([@"C:\Downloads\python-3.13.9-amd64.exe"], ops.Expanded);
    }

    [Fact]
    public async Task NothingIsDownloadedUnlessThePersonAllowedIt()
    {
        var pc = Python();
        var ops = Ops(pc, @"C:\layout\core.msi");
        var fetcher = new FakeFetcher { Found = new("Python.Python.3.13", "Python 3.13.9 (64-bit)", "3.13.9"), Files = [@"C:\dl\python-3.13.9-amd64.exe"] };

        var outcome = await Service(pc, ops, fetcher).FixAsync([Record(pc)], "x");

        Assert.Empty(fetcher.Calls);
        Assert.Contains("was not allowed", string.Join(" ", outcome.Results![0].Steps));
    }

    [Fact]
    public async Task TheOriginalInstallerIsFetchedWhenAllowedAndNothingOnThePcMatches()
    {
        var pc = Python();
        var before = sandbox.Dump();
        var ops = Ops(pc, @"C:\layout\core.msi");
        ops.Bundles[@"C:\dl\python-3.13.9-amd64.exe"] = [@"C:\layout\core.msi"];
        var fetcher = new FakeFetcher { Found = new("Python.Python.3.13", "Python 3.13.9 (64-bit)", "3.13.9"), Files = [@"C:\dl\python-3.13.9-amd64.exe"] };

        var outcome = await Service(pc, ops, fetcher).FixAsync([Record(pc)], "x", options: new(Download: true));

        Assert.Equal(RepairStatus.Fixed, Assert.Single(outcome.Results!).Status);
        Assert.Contains(fetcher.Calls, x => x.StartsWith("match Python"));
        Assert.Contains("download Python.Python.3.13 3.13.9 x64", fetcher.Calls);
        Assert.Equal([@"C:\layout\core.msi"], ops.Reinstalled);
        RepairSandbox.AssertSame(before, sandbox.Dump());
    }

    [Fact]
    public async Task ADownloadThatIsNotThisBuildIsNeverUsed()
    {
        var pc = Python();
        var ops = Ops(pc);
        ops.Bundles[@"C:\dl\other.exe"] = [@"C:\layout\other.msi"];
        ops.Packages[@"C:\layout\other.msi"] = new(BrokenPython.Other, "{Z}", "1", "other");
        var fetcher = new FakeFetcher { Found = new("Python.Python.3.13", "Python 3.13.9 (64-bit)", "3.13.9"), Files = [@"C:\dl\other.exe"] };

        var outcome = await Service(pc, ops, fetcher).FixAsync([Record(pc)], "x", options: new(Download: true));

        Assert.Empty(ops.Reinstalled);
        Assert.Contains("not this exact build", string.Join(" ", outcome.Results![0].Steps));
        // With nothing to repair it from, the record is removed so the program can be installed again.
        Assert.Empty(sandbox.Mentions(BrokenPython.Packed, BrokenPython.Code));
    }

    // ---- A program with no installer record (Inno Setup, NSIS) whose uninstaller is gone ----

    private const string Ghost = @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Ghost_is1";
    private const string Folder = @"C:\Games\Ghost";

    private FakePc InnoGame(bool uninstallerElsewhere)
    {
        sandbox.Put(Ghost, ("DisplayName", "Ghost Game"), ("DisplayVersion", "1.2"), ("Publisher", "Ghost Co"), ("UninstallString", @"""C:\Games\Ghost\unins000.exe"" /SILENT"), ("InstallLocation", Folder));
        var pc = new FakePc();
        pc.Live = () =>
        {
            using var key = sandbox.Open(Ghost);
            return [new(Ghost, "Ghost Game", "1.2", "Ghost Co", key.GetValue("UninstallString") as string ?? "", "", Folder, "", false, false, false, false)];
        };
        pc.Folders.Add(Folder);
        if (uninstallerElsewhere) { pc.FolderFiles[Folder] = [@"C:\Games\Ghost\ghost.exe", @"C:\Games\Ghost\unins001.exe"]; pc.Files.Add(@"C:\Games\Ghost\unins001.exe"); }
        return pc;
    }

    private RepairFinding Entry(FakePc pc) => Assert.Single(InstallRepair.Scan(pc).Findings, x => x.Entry is not null);

    [Fact]
    public async Task AnEntryWhoseUninstallerWasRenamedIsPointedAtTheOneThatIsStillThereKeepingItsSwitchesAndUndoes()
    {
        var pc = InnoGame(uninstallerElsewhere: true);
        var before = sandbox.Dump();
        var finding = Entry(pc);
        Assert.True(finding.Alive);
        var service = Service(pc, new FakeOps());

        var outcome = await service.FixAsync([finding], "Repaired Ghost Game");

        Assert.Equal(RepairStatus.Fixed, Assert.Single(outcome.Results!).Status);
        using (var key = sandbox.Open(Ghost)) Assert.Equal(@"""C:\Games\Ghost\unins001.exe"" /SILENT", key.GetValue("UninstallString"));
        Assert.True(sandbox.Has(Ghost));

        var undo = await service.UndoAsync(outcome.Batch!);

        Assert.True(undo.Complete);
        RepairSandbox.AssertSame(before, sandbox.Dump());
    }

    [Fact]
    public async Task AnEntryWithNoUninstallerAnywhereGetsItBackFromTheOriginalInstallerWhenThatIsAllowed()
    {
        var pc = InnoGame(uninstallerElsewhere: false);
        var fetcher = new FakeFetcher { Found = new("Ghost.Game", "Ghost Game", "1.2"), OnReinstall = () => pc.Files.Add(@"C:\Games\Ghost\unins000.exe") };

        var outcome = await Service(pc, new FakeOps(), fetcher).FixAsync([Entry(pc)], "x", options: new(Download: true));

        var result = Assert.Single(outcome.Results!);
        Assert.Equal(RepairStatus.Fixed, result.Status);
        Assert.Contains("reinstall Ghost.Game", fetcher.Calls);
        Assert.True(sandbox.Has(Ghost));
        Assert.Null(outcome.Batch);
    }

    [Fact]
    public async Task WithoutPermissionToDownloadOrWhenTheReinstallFailsTheEntryIsRemovedAndTheResultSaysTheFilesStay()
    {
        var pc = InnoGame(uninstallerElsewhere: false);
        var fetcher = new FakeFetcher { Found = new("Ghost.Game", "Ghost Game", "1.2"), ReinstallExit = 1603 };

        var plain = await Service(pc, new FakeOps(), fetcher).FixAsync([Entry(pc)], "x");
        Assert.Empty(fetcher.Calls);
        Assert.Equal(RepairStatus.Fixed, plain.Results![0].Status);
        Assert.Contains(@"files were left in C:\Games\Ghost", plain.Results[0].Summary);
        Assert.False(sandbox.Has(Ghost));

        pc = InnoGame(uninstallerElsewhere: false);
        var failed = await Service(pc, new FakeOps(), fetcher).FixAsync([Entry(pc)], "x", options: new(Download: true));
        Assert.Contains("reinstall Ghost.Game", fetcher.Calls);
        Assert.Contains("still names an uninstaller that is not there", string.Join(" ", failed.Results![0].Steps));
        Assert.False(sandbox.Has(Ghost));
    }

    [Fact]
    public async Task APythonBundleWithItsCachedSetupGoneIsRepairedFromTheDownloadedSetupWhenAllowed()
    {
        BrokenPython.PerUser(sandbox);
        var arp = $@"HKCU\{BrokenPython.Cv}\Uninstall\{BrokenPython.Bundle}";
        var cached = false;
        var pc = new FakePc();
        pc.Live = () =>
        {
            using var key = sandbox.Open(arp);
            return [new(arp, "Python 3.13.9 (64-bit)", "3.13.9", "PSF", key.GetValue("UninstallString") as string ?? "", "", "", "", false, false, false, false, BrokenPython.Bundle)];
        };
        var ops = new FakeOps { OnRepairBundle = () => { cached = true; pc.Files.Add(@"C:\Users\X\AppData\Local\Package Cache\{7A1B3C5D-1111-4222-8333-444455556666}\python-3.13.9-amd64.exe"); } };
        var fetcher = new FakeFetcher { Found = new("Python.Python.3.13", "Python 3.13.9 (64-bit)", "3.13.9"), Files = [@"C:\dl\python-3.13.9-amd64.exe"] };
        // The cached setup program is named by the entry; make the entry say the program is there so the repair is offered.
        pc.Folders.Add(@"C:\Python313");
        var finding = Assert.Single(InstallRepair.Scan(pc).Findings);
        finding = finding with { Alive = true };

        var outcome = await Service(pc, ops, fetcher).FixAsync([finding], "x", options: new(Download: true));

        Assert.True(cached);
        Assert.Equal([@"C:\dl\python-3.13.9-amd64.exe"], ops.Repaired);
        Assert.Equal([@"C:\Users\X\AppData\Local\Package Cache\{7A1B3C5D-1111-4222-8333-444455556666}\python-3.13.9-amd64.exe"], ops.RestoredTo);
        Assert.Equal(RepairStatus.Fixed, Assert.Single(outcome.Results!).Status);
        Assert.True(sandbox.Has(arp));
    }

    [Fact]
    public async Task ASetupProgramPutBackInThePackageCacheIsKeptInTheUndoTabAndTakenAwayAgainByTheUndo()
    {
        BrokenPython.PerUser(sandbox);
        var arp = $@"HKCU\{BrokenPython.Cv}\Uninstall\{BrokenPython.Bundle}";
        var cachedFile = @"C:\Users\X\AppData\Local\Package Cache\{7A1B3C5D-1111-4222-8333-444455556666}\python-3.13.9-amd64.exe";
        var pc = new FakePc();
        pc.Live = () =>
        {
            using var key = sandbox.Open(arp);
            return [new(arp, "Python 3.13.9 (64-bit)", "3.13.9", "PSF", key.GetValue("UninstallString") as string ?? "", "", "", "", false, false, false, false, BrokenPython.Bundle)];
        };
        var ops = new FakeOps { OnRepairBundle = () => pc.Files.Add(cachedFile) };
        var fetcher = new FakeFetcher { Found = new("Python.Python.3.13", "Python 3.13.9 (64-bit)", "3.13.9"), Files = [@"C:\dl\python-3.13.9-amd64.exe"] };
        pc.Folders.Add(@"C:\Python313");
        var service = Service(pc, ops, fetcher);

        var outcome = await service.FixAsync([InstallRepair.Scan(pc).Findings.Single() with { Alive = true }], "x", options: new(Download: true));

        // The repair only copied a file, and that alone is something to undo.
        var batch = Assert.IsType<RepairBatch>(outcome.Batch);
        Assert.Empty(batch.Keys);
        Assert.Equal([cachedFile], batch.PutBack);

        var undo = await service.UndoAsync(batch);

        Assert.True(undo.Complete);
        Assert.Equal(1, undo.FilesRemoved);
        Assert.Equal([cachedFile], ops.Removed);
    }

    [Fact]
    public async Task AnUndoThatCannotRemoveThePutBackSetupSaysSoAndStaysInTheUndoTab()
    {
        var history = new RepairHistory(Path.Combine(Path.GetTempPath(), "tw-undo-" + Guid.NewGuid().ToString("N")[..8]));
        var batch = history.Save("x", [], files: [@"C:\Users\X\AppData\Local\Package Cache\{A}\setup.exe"]);
        var ops = new FakeOps { RemoveWorks = false };
        var service = new RepairService(new FakePc(), sandbox.User(), sandbox.Elevated(), _ => Task.CompletedTask, () => { }, history, ops);

        var undo = await service.UndoAsync(batch);

        Assert.False(undo.Complete);
        Assert.Contains("setup.exe", undo.Problems[0]);
        Assert.Equal(batch.Id, Assert.Single(history.All()).Id);
    }

    [Fact]
    public void AHistoryFileFromBeforeRepairsCouldPutFilesBackStillLoads()
    {
        var folder = Path.Combine(Path.GetTempPath(), "tw-old-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "20260101-000000-000.json"), """{"Id":"20260101-000000-000","Created":"2026-01-01T00:00:00+00:00","Title":"old","Keys":[]}""");
        var batch = Assert.Single(new RepairHistory(folder).All());
        Assert.Empty(batch.PutBack);
    }
}

[SupportedOSPlatform("windows")]
public sealed class RegistrySetTextTests : IDisposable
{
    private readonly RepairSandbox sandbox = new();
    public void Dispose() => sandbox.Dispose();
    private const string Entry = @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\App_is1";

    [Theory]
    [InlineData(@"C:\Windows\System32\evil.exe")]
    [InlineData(@"C:\Users\X\AppData\Local\Package Cache\{A}\notes.txt")]
    public void ASetupProgramIsOnlyEverCopiedIntoWindowsPackageCacheAsAnExecutable(string target)
    {
        var result = new WindowsInstallerOps().RestoreBundleCopy(@"C:\dl\whatever.exe", "Python 3.13.9 (64-bit)", target);
        Assert.Equal(87, result.ExitCode);
    }

    [Fact]
    public void ATextValueIsChangedKeepingItsTypeAndAValueThatDidNotExistIsRemovedAgainByTheUndo()
    {
        sandbox.Put(Entry, ("DisplayName", "App"));
        using (var key = sandbox.Root.CreateSubKey(@"HKU\" + RepairSandbox.Sid + Entry[4..], true)) key.SetValue("UninstallString", @"%ProgramFiles%\App\unins000.exe", RegistryValueKind.ExpandString);
        var before = sandbox.Dump();
        var repair = sandbox.User();

        var changed = repair.SetText(Entry, "UninstallString", @"""C:\App\unins001.exe""");
        var added = repair.SetText(Entry, "QuietUninstallString", @"""C:\App\unins001.exe"" /S");

        using (var key = sandbox.Open(Entry))
        {
            Assert.Equal(RegistryValueKind.ExpandString, key.GetValueKind("UninstallString"));
            Assert.Equal(@"""C:\App\unins001.exe"" /S", key.GetValue("QuietUninstallString"));
        }
        Assert.Equal(["QuietUninstallString"], added!.Absent);
        repair.Restore(added);
        repair.Restore(changed!);
        RepairSandbox.AssertSame(before, sandbox.Dump());
    }

    [Theory]
    [InlineData("DisplayName")]
    [InlineData("InstallLocation")]
    [InlineData("DisplayIcon")]
    public void OnlyTheUninstallCommandsOfAListEntryCanBeChanged(string name)
    {
        sandbox.Put(Entry, ("DisplayName", "App"));
        Assert.Throws<InvalidOperationException>(() => sandbox.Elevated().SetText(Entry, name, "x"));
    }

    [Fact]
    public void TheCommandOfAnythingThatIsNotAListEntryCannotBeChanged()
    {
        const string run = @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        sandbox.Put(run, ("UninstallString", "x"));
        Assert.Throws<InvalidOperationException>(() => sandbox.Elevated().SetText(run, "UninstallString", "evil.exe"));
    }
}

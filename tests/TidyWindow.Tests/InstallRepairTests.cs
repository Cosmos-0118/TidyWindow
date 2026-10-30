using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using TidyWindow.Next.Core;
using Xunit;

namespace TidyWindow.Next.Tests;

[SupportedOSPlatform("windows")]
public sealed class InstallRepairTests
{
    private const string Arp = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\";
    private const string Code = "{90140000-0011-0000-0000-0000000FF1CE}";

    private sealed class FakePc : IInstallSystem
    {
        public List<UninstallEntry> Programs = [];
        public List<MsiProduct> Msi = [];
        public HashSet<string> Files = new(StringComparer.OrdinalIgnoreCase), Folders = new(StringComparer.OrdinalIgnoreCase), OnPath = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> KnownProducts = new(StringComparer.OrdinalIgnoreCase), MissingDrives = new(StringComparer.OrdinalIgnoreCase);
        public bool Lock, Busy;
        public List<RunningInstaller> Running = [];
        public List<string> Restart = [];
        public List<int> Ended = [];

        public IReadOnlyList<UninstallEntry> Entries() => Programs;
        public IReadOnlyList<MsiProduct> Products() => Msi;
        public int ProductState(string code) => KnownProducts.Contains(code) ? 5 : InstallRepair.Unknown;
        public bool FileExists(string path) => Files.Contains(path);
        public bool FolderExists(string path) => Folders.Contains(path);
        public bool DriveAvailable(string path) => !MissingDrives.Contains(Path.GetPathRoot(path) ?? "");
        public string? FindOnPath(string fileName) => OnPath.Contains(fileName) ? @"C:\Windows\System32\" + fileName : null;
        public string Expand(string text) => text.Replace("%ProgramFiles%", @"C:\Program Files");
        public bool InstallLockExists() => Lock;
        public bool InstallerBusy() => Busy;
        public IReadOnlyList<RunningInstaller> Installers() => Running;
        public IReadOnlyList<string> PendingRestart() => Restart;
        public bool EndProcess(int id) { Ended.Add(id); return true; }
    }

    private static UninstallEntry Entry(string name, string key, string uninstall = "", string location = "", string icon = "", bool msi = false, bool noRemove = false, bool system = false, bool update = false) =>
        new(Arp + key, name, "1.0", "Maker", uninstall, "", location, icon, msi, system, noRemove, update);

    [Fact]
    public void ProductCodesArePackedTheWayWindowsInstallerNamesItsKeys()
    {
        Assert.Equal("00004109110000000000000000F01FEC", InstallRepair.Pack(Code));
        Assert.Equal("00004109110000000000000000F01FEC", InstallRepair.Pack(Code.ToLowerInvariant()));
        Assert.Throws<ArgumentException>(() => InstallRepair.Pack("{nope}"));
    }

    [Fact]
    public void AProgramWhoseUninstallerAndFilesAreGoneIsSafeToRemove()
    {
        var pc = new FakePc { Programs = [Entry("Ghost App", "Ghost", @"""C:\Program Files\Ghost\unins000.exe"" /SILENT", @"C:\Program Files\Ghost")] };

        var finding = Assert.Single(InstallRepair.Scan(pc).Findings);

        Assert.Equal(RepairLevel.Recommended, finding.Level);
        Assert.Equal(RepairKind.BrokenEntry, finding.Kind);
        Assert.Equal([Arp + "Ghost"], finding.Keys);
        Assert.True(finding.NeedsAdmin);
    }

    [Fact]
    public void AMissingUninstallerWithTheProgramStillThereIsOnlyOfferedForReview()
    {
        var pc = new FakePc { Programs = [Entry("Half App", "Half", @"C:\Program Files\Half\unins000.exe", @"C:\Program Files\Half")] };
        pc.Folders.Add(@"C:\Program Files\Half");

        Assert.Equal(RepairLevel.Review, Assert.Single(InstallRepair.Scan(pc).Findings).Level);
    }

    [Fact]
    public void WorkingEntriesAreLeftAlone()
    {
        var pc = new FakePc
        {
            Programs =
            [
                Entry("Fine", "Fine", @"""C:\Program Files\Fine\unins000.exe"" /S"),
                Entry("Unquoted", "Unquoted", @"C:\Program Files\Unquoted App\uninstall.exe /quiet"),
                Entry("Variable", "Variable", @"%ProgramFiles%\Variable\remove.exe"),
                Entry("Windows tool", "Tool", "rundll32.exe shell32.dll,Control_RunDLL x"),
                Entry("Settings link", "Link", "ms-settings:appsfeatures"),
                Entry("Built in", "Locked", noRemove: true),
                Entry("Hidden", "Hidden", @"C:\gone.exe", system: true),
                Entry("Security patch", "KB1", @"C:\gone.exe", update: true),
                Entry("", "NoName", @"C:\gone.exe")
            ]
        };
        pc.Files.UnionWith([@"C:\Program Files\Fine\unins000.exe", @"C:\Program Files\Unquoted App\uninstall.exe", @"C:\Program Files\Variable\remove.exe"]);
        pc.OnPath.Add("rundll32.exe");

        Assert.Empty(InstallRepair.Scan(pc).Findings);
    }

    [Fact]
    public void AProgramOnAnUnpluggedDriveIsNotCalledBroken()
    {
        var pc = new FakePc { Programs = [Entry("Portable", "Portable", @"""E:\Tools\unins000.exe""")], MissingDrives = { @"E:\" } };

        Assert.Empty(InstallRepair.Scan(pc).Findings);
    }

    [Fact]
    public void AnEntryThatDoesNotSayWhereItLivesIsNeverCalledSafeToRemove()
    {
        var pc = new FakePc { Programs = [Entry("Vague", "Vague", @"C:\Program Files\Vague\unins000.exe")] };

        Assert.Equal(RepairLevel.Review, Assert.Single(InstallRepair.Scan(pc).Findings).Level);
    }

    [Fact]
    public void ABundleWhoseCachedInstallerWasClearedIsOnlyReviewedBecauseTheProgramMayStillWork()
    {
        const string cached = @"C:\ProgramData\Package Cache\{0e3bb569}\VC_redist.x64.exe";
        var pc = new FakePc { Programs = [Entry("Visual C++ Redistributable", "{0e3bb569-69d6-4c34-bff9-c2f81db5e5f0}", "\"" + cached + "\" /uninstall", icon: cached + ",0")] };

        var finding = Assert.Single(InstallRepair.Scan(pc).Findings);

        Assert.Equal(RepairLevel.Review, finding.Level);
        Assert.Contains("installer again", finding.Detail);
    }

    [Fact]
    public void AnEntryWithNoUninstallerAtAllIsReviewedUnlessItIsLockedOnPurpose()
    {
        var pc = new FakePc { Programs = [Entry("Orphan", "Orphan"), Entry("Locked", "Locked", noRemove: true)] };

        var finding = Assert.Single(InstallRepair.Scan(pc).Findings);

        Assert.Equal("Orphan", finding.Title);
    }

    [Fact]
    public void AWindowsInstallerEntryIsBrokenOnlyWhenWindowsInstallerHasNeverHeardOfIt()
    {
        var pc = new FakePc { Programs = [Entry("Old Office", Code, "MsiExec.exe /X" + Code, msi: true)] };
        pc.OnPath.Add("MsiExec.exe");

        Assert.Equal(RepairLevel.Recommended, Assert.Single(InstallRepair.Scan(pc).Findings).Level);

        pc.KnownProducts.Add(Code);
        Assert.Empty(InstallRepair.Scan(pc).Findings);
    }

    [Fact]
    public void AnMsiProductCodeIsFoundInTheCommandWhenTheKeyNameIsNotTheCode()
    {
        var pc = new FakePc { Programs = [Entry("Renamed", "RenamedKey", "msiexec /i" + Code)] };

        Assert.Equal(Code, InstallRepair.ProductCode(pc.Programs[0]));
        Assert.Single(InstallRepair.Scan(pc).Findings);
    }

    [Fact]
    public void AWindowsInstallerRecordWithoutItsSavedPackageListsEveryKeyItLeavesBehind()
    {
        var pc = new FakePc
        {
            Msi = [new(Code, "Machine", "", "Office", @"C:\Windows\Installer\1234.msi"), new("{11111111-2222-3333-4444-555555555555}", "Machine", "", "Healthy", @"C:\Windows\Installer\ok.msi")],
            Programs = [Entry("Office", Code, msi: true)]
        };
        pc.Files.Add(@"C:\Windows\Installer\ok.msi");
        pc.KnownProducts.Add(Code);

        var finding = Assert.Single(InstallRepair.Scan(pc).Findings);

        Assert.Equal(RepairKind.InstallerRecord, finding.Kind);
        Assert.True(finding.NeedsAdmin);
        Assert.Contains(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData\S-1-5-18\Products\00004109110000000000000000F01FEC", finding.Keys);
        Assert.Contains(@"HKLM\SOFTWARE\Classes\Installer\Products\00004109110000000000000000F01FEC", finding.Keys);
        Assert.Contains(@"HKLM\SOFTWARE\Classes\Installer\Features\00004109110000000000000000F01FEC", finding.Keys);
        Assert.Contains(Arp + Code, finding.Keys);
        Assert.All(finding.Keys, key => Assert.True(RegistryRepair.Machine.IsAllowed(key), key));
    }

    [Fact]
    public void APerUserInstallerRecordUsesTheUsersOwnKeys()
    {
        var product = new MsiProduct(Code, "User", "S-1-5-21-1-2-3-1001", "Tool", @"C:\gone.msi");

        var keys = InstallRepair.RecordKeys(product);

        Assert.Contains(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData\S-1-5-21-1-2-3-1001\Products\00004109110000000000000000F01FEC", keys);
        Assert.Contains(@"HKCU\SOFTWARE\Microsoft\Installer\Products\00004109110000000000000000F01FEC", keys);
        Assert.All(keys, key => Assert.True((InstallRepair.IsMachine(key) ? RegistryRepair.Machine : RegistryRepair.User).IsAllowed(key), key));
    }

    [Fact]
    public void AnInstallMarkerWithNothingInstallingIsStaleButARunningInstallIsNeverCalledStale()
    {
        var pc = new FakePc { Lock = true };

        var stale = Assert.Single(InstallRepair.Scan(pc).Findings);
        Assert.Equal(RepairKind.InstallLock, stale.Kind);
        Assert.Equal(RepairLevel.Recommended, stale.Level);
        Assert.True(RegistryRepair.Machine.IsAllowed(stale.Keys[0]));

        pc.Busy = true;
        Assert.Empty(InstallRepair.Scan(pc).Findings);

        pc.Busy = false;
        pc.Running.Add(new(42, "Windows Installer (msiexec)", DateTimeOffset.Now.AddMinutes(-30)));
        var running = Assert.Single(InstallRepair.Scan(pc).Findings);
        Assert.Equal(RepairKind.RunningInstaller, running.Kind);
        Assert.Equal(RepairLevel.Review, running.Level);
        Assert.Equal(42, running.ProcessId);
    }

    [Fact]
    public void APendingRestartIsReportedButHasNothingToFix()
    {
        var pc = new FakePc { Restart = ["Windows Update"] };

        var finding = Assert.Single(InstallRepair.Scan(pc).Findings);

        Assert.Equal(RepairLevel.Info, finding.Level);
        Assert.False(finding.CanFix);
    }

    [Fact]
    public void RemovingAProgramByHandAlsoClearsItsInstallerRecordWhenItHasOne()
    {
        var product = new MsiProduct(Code, "Machine", "", "Office", null);
        var entry = Entry("Office", Code, msi: true);

        var keys = InstallRepair.RemovalKeys(entry, [product]);

        Assert.Equal(4, keys.Count);
        Assert.Equal([Arp + "plain"], InstallRepair.RemovalKeys(Entry("Plain", "plain"), [product]));
    }
}

public sealed class RepairServiceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "tw-repair-" + Guid.NewGuid().ToString("N")[..8]);
    public RepairServiceTests() => Directory.CreateDirectory(root);
    public void Dispose() { try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    private sealed class Editor : IKeyEditor
    {
        public Dictionary<string, KeyBackup> Keys = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Refuse = new(StringComparer.OrdinalIgnoreCase);
        public KeyBackup? Delete(string path)
        {
            if (Refuse.Contains(path)) throw new UnauthorizedAccessException("denied");
            return Keys.Remove(path, out var key) ? key : null;
        }
        public void Restore(KeyBackup backup) => Keys[backup.Path] = backup;
    }

    private static KeyBackup Key(string path) => new(path, [new("DisplayName", "String", Text: path)], []);

    private sealed class Pc : IInstallSystem
    {
        public List<int> Ended = [];
        public IReadOnlyList<UninstallEntry> Entries() => [];
        public IReadOnlyList<MsiProduct> Products() => [];
        public int ProductState(string code) => -1;
        public bool FileExists(string path) => false;
        public bool FolderExists(string path) => false;
        public bool DriveAvailable(string path) => true;
        public string? FindOnPath(string fileName) => null;
        public string Expand(string text) => text;
        public bool InstallLockExists() => false;
        public bool InstallerBusy() => false;
        public IReadOnlyList<RunningInstaller> Installers() => [];
        public IReadOnlyList<string> PendingRestart() => [];
        public bool EndProcess(int id) { Ended.Add(id); return true; }
    }

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
        var service = new RepairService(new Pc(), users, machines, _ => { unlocks++; return Task.CompletedTask; }, () => locks++, history);

        var outcome = await service.FixAsync([Finding("Ghost", user), Finding("Phantom", machine)], "Removed 2 leftovers");

        Assert.Equal(2, outcome.Fixed);
        Assert.Empty(outcome.Problems);
        Assert.Empty(users.Keys); Assert.Empty(machines.Keys);
        Assert.Equal((1, 1), (unlocks, locks));
        Assert.NotNull(history.Latest());

        var restored = await service.UndoAsync(history.Latest()!);

        Assert.Equal(2, restored);
        Assert.Contains(user, users.Keys.Keys);
        Assert.Contains(machine, machines.Keys.Keys);
        Assert.Null(history.Latest());
    }

    [Fact]
    public async Task OnlyRepairsThatReachWindowsWideKeysAskForPermission()
    {
        const string user = @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Ghost";
        var unlocks = 0;
        var service = new RepairService(new Pc(), new Editor { Keys = { [user] = Key(user) } }, new Editor(), _ => { unlocks++; return Task.CompletedTask; }, () => { }, new RepairHistory(root));

        await service.FixAsync([Finding("Ghost", user)], "Removed 1 leftover");

        Assert.Equal(0, unlocks);
    }

    [Fact]
    public async Task DecliningThePermissionPromptChangesNothing()
    {
        const string machine = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Phantom";
        var machines = new Editor { Keys = { [machine] = Key(machine) } };
        var history = new RepairHistory(root);
        var service = new RepairService(new Pc(), new Editor(), machines, _ => throw new OperationCanceledException(), () => { }, history);

        await Assert.ThrowsAsync<OperationCanceledException>(() => service.FixAsync([Finding("Phantom", machine)], "x"));

        Assert.Single(machines.Keys);
        Assert.Null(history.Latest());
    }

    [Fact]
    public async Task OneKeyThatCannotBeRemovedDoesNotStopTheRestAndIsReported()
    {
        const string good = @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Good";
        const string bad = @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Bad";
        var users = new Editor { Keys = { [good] = Key(good), [bad] = Key(bad) }, Refuse = { bad } };
        var service = new RepairService(new Pc(), users, new Editor(), _ => Task.CompletedTask, () => { }, new RepairHistory(root));

        var outcome = await service.FixAsync([Finding("Bad", bad), Finding("Good", good)], "x");

        Assert.Equal(1, outcome.Fixed);
        Assert.Contains("Bad: denied", outcome.Problems);
        Assert.Equal([bad], users.Keys.Keys);
        Assert.NotNull(outcome.Batch);
    }

    [Fact]
    public async Task AStuckInstallerIsEndedThroughTheSystem()
    {
        var pc = new Pc();
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

[SupportedOSPlatform("windows")]
public sealed class RegistryRepairTests : IDisposable
{
    private const string Base = @"SOFTWARE\TidyWindowTests";
    private readonly string name = "t" + Guid.NewGuid().ToString("N")[..8];
    private readonly RegistryRepair repair = new([new(@"^HKCU\\SOFTWARE\\TidyWindowTests\\t[0-9a-f]{8}$", RegexOptions.IgnoreCase)]);
    private string Path => $@"HKCU\{Base}\{name}";

    public void Dispose() { using var key = Registry.CurrentUser.OpenSubKey(Base, true); try { key?.DeleteSubKeyTree(name, false); } catch (ArgumentException) { } }

    [Fact]
    public void ARemovedKeyComesBackExactlyAsItWas()
    {
        using (var key = Registry.CurrentUser.CreateSubKey($@"{Base}\{name}"))
        {
            key.SetValue("DisplayName", "Ghost App");
            key.SetValue("Number", 7, RegistryValueKind.DWord);
            key.SetValue("Big", 1L << 40, RegistryValueKind.QWord);
            key.SetValue("Path", @"%ProgramFiles%\Ghost", RegistryValueKind.ExpandString);
            key.SetValue("Many", new[] { "a", "b" }, RegistryValueKind.MultiString);
            key.SetValue("Blob", new byte[] { 1, 2, 3 }, RegistryValueKind.Binary);
            using var child = key.CreateSubKey("Inner"); child.SetValue("Deep", "yes");
        }

        var copy = repair.Delete(Path);

        Assert.NotNull(copy);
        Assert.False(repair.Exists(Path));
        repair.Restore(copy!);
        using var restored = Registry.CurrentUser.OpenSubKey($@"{Base}\{name}")!;
        Assert.Equal("Ghost App", restored.GetValue("DisplayName"));
        Assert.Equal(7, restored.GetValue("Number"));
        Assert.Equal(1L << 40, restored.GetValue("Big"));
        Assert.Equal(@"%ProgramFiles%\Ghost", restored.GetValue("Path", null, RegistryValueOptions.DoNotExpandEnvironmentNames));
        Assert.Equal(RegistryValueKind.ExpandString, restored.GetValueKind("Path"));
        Assert.Equal(new[] { "a", "b" }, (string[])restored.GetValue("Many")!);
        Assert.Equal(new byte[] { 1, 2, 3 }, (byte[])restored.GetValue("Blob")!);
        using var inner = restored.OpenSubKey("Inner")!;
        Assert.Equal("yes", inner.GetValue("Deep"));
    }

    [Fact]
    public void AKeyThatIsAlreadyGoneIsNotAnError()
    {
        Assert.Null(repair.Delete(Path));
    }

    [Theory]
    [InlineData(@"HKCU\SOFTWARE\Microsoft")]
    [InlineData(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall")]
    [InlineData(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\A\B")]
    [InlineData(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\..\..\Run")]
    [InlineData(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run\Evil")]
    [InlineData(@"HKLM\SYSTEM\CurrentControlSet\Services\Evil")]
    [InlineData(@"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Ghost")]
    public void TheWindowsWideRulesRefuseAnythingBeyondTheInstallLeftovers(string path)
    {
        Assert.False(RegistryRepair.Machine.IsAllowed(path));
        Assert.Throws<InvalidOperationException>(() => RegistryRepair.Machine.Delete(path));
    }

    [Theory]
    [InlineData(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{GUID}")]
    [InlineData(@"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Some App")]
    [InlineData(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData\S-1-5-18\Products\00004109110000000000000000F01FEC")]
    [InlineData(@"HKLM\SOFTWARE\Classes\Installer\Features\00004109110000000000000000F01FEC")]
    [InlineData(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\InProgress")]
    public void TheWindowsWideRulesAllowTheInstallLeftovers(string path) => Assert.True(RegistryRepair.Machine.IsAllowed(path));
}

public sealed class RepairHostTests
{
    private const string Key = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Ghost";

    private static RepairSession Session(List<string> deleted, List<KeyBackup> restored, bool admin = true) =>
        new(pipe => RepairHost.RunAsync([RepairHost.Command, pipe],
            path => { deleted.Add(path); return new KeyBackup(path, [new("DisplayName", "String", Text: "Ghost")], []); },
            restored.Add, path => path == Key, () => admin, TimeSpan.FromSeconds(20)));

    [Fact]
    public async Task TheHelperRemovesAndRestoresAnAllowedKeyAndReturnsACopy()
    {
        var deleted = new List<string>(); var restored = new List<KeyBackup>();
        using var session = Session(deleted, restored);
        await session.OpenAsync();

        var copy = session.Delete(Key);
        session.Restore(copy!);

        Assert.Equal([Key], deleted);
        Assert.Equal("Ghost", copy!.Values[0].Text);
        Assert.Equal(Key, Assert.Single(restored).Path);
    }

    [Fact]
    public async Task TheHelperRefusesAKeyOutsideItsAllowListWhoeverAsks()
    {
        var deleted = new List<string>(); var restored = new List<KeyBackup>();
        using var session = Session(deleted, restored);
        await session.OpenAsync();

        Assert.Throws<IOException>(() => session.Delete(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run"));
        Assert.Throws<IOException>(() => session.Restore(new(@"HKLM\SYSTEM\CurrentControlSet\Services\Evil", [], [])));

        Assert.Empty(deleted); Assert.Empty(restored);
    }

    [Fact]
    public async Task AHelperWithoutAdministratorRightsIsNotAccepted()
    {
        using var session = Session([], [], admin: false);
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
            await RepairHost.RunAsync([RepairHost.Command, pipe], _ => null, _ => { }, _ => true, () => true, TimeSpan.FromSeconds(20));
            finished.SetResult();
        });
        await session.OpenAsync();

        session.Close();

        await finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(session.IsOpen);
    }
}

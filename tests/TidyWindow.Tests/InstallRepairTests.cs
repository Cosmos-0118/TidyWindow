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

using System.Runtime.Versioning;
using TidyWindow.Next.Core;
using Xunit;
using Xunit.Abstractions;

namespace TidyWindow.Next.Tests;

/// <summary>
/// Opt-in checks against this PC's real Windows Installer data, switched on with TIDYWINDOW_LIVE_TESTS=1. They only read: nothing here
/// removes, writes or repairs anything, so they are safe on a PC whose installs must not be touched.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class LiveRepairTests(ITestOutputHelper output)
{
    private static bool Live => Environment.GetEnvironmentVariable("TIDYWINDOW_LIVE_TESTS") == "1" && OperatingSystem.IsWindows();

    [Fact]
    public void TheScanOfThisPcAgreesWithWindowsInstallerAndEveryRemovalPlanStaysInsideTheAllowList()
    {
        if (!Live) return;
        var system = new WindowsInstallSystem();
        var scan = InstallRepair.Scan(system);
        var sid = RegistryRepair.CurrentSid();
        var read = RegistryRepair.Elevated(sid);
        output.WriteLine($"{scan.Entries.Count} list entries, {scan.Products.Count} Windows Installer products, {scan.Findings.Count} findings.");
        foreach (var group in scan.Findings.GroupBy(x => x.Kind)) output.WriteLine($"  {group.Key}: {group.Count()}");
        foreach (var finding in scan.Findings.Where(x => x.Footprint is not null).Take(25))
        {
            // Planning only reads. Keys it cannot read as this account are reported, never guessed.
            var plan = read.Footprint(finding.Footprint! with { Fresh = false });
            output.WriteLine($"  {finding.Title}: {plan.Keys.Count} keys, {plan.Values.Count} shared links, {plan.Unreadable.Count} unreadable{(plan.Elsewhere ? ", also registered elsewhere" : "")}");
            Assert.All(plan.Keys, key => Assert.True(read.IsAllowed(key), key));
            Assert.All(plan.Values, link => Assert.True(read.IsValueAllowed(link.Path, link.Name), link.Path));
        }
    }

    [Fact]
    public void EveryRealProductCodeSurvivesPackingAndUnpacking()
    {
        if (!Live) return;
        var products = new WindowsInstallSystem().Products();
        Assert.All(products, product => Assert.Equal(product.Code, InstallRepair.Unpack(InstallRepair.Pack(product.Code)), ignoreCase: true));
        output.WriteLine($"{products.Count} real product codes round-tripped.");
    }

    [Fact]
    public void ARealProductKeyIsCapturedByteForByteWithItsPermissions()
    {
        if (!Live) return;
        var repair = RegistryRepair.User;
        using var list = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Installer\Products");
        var name = list?.GetSubKeyNames().FirstOrDefault(x => x.Length == 32);
        if (name is null) { output.WriteLine("No per-user Windows Installer product on this PC to capture."); return; }
        var path = @"HKCU\SOFTWARE\Microsoft\Installer\Products\" + name;

        var first = repair.Snapshot(path)!;
        var second = repair.Snapshot(path)!;

        Assert.NotNull(first.Sddl);
        Assert.Equal(first.Sddl, second.Sddl);
        Assert.Equal(first.Values.Select(x => (x.Name, x.RawType, Convert.ToHexString(x.Bytes ?? []))), second.Values.Select(x => (x.Name, x.RawType, Convert.ToHexString(x.Bytes ?? []))));
        output.WriteLine($"{path}: {first.Values.Count} values, {first.Children.Count} subkeys, permissions {first.Sddl}");
    }

    [Fact]
    public void InstallerFilesOnThisPcAreReadByTheirOwnProductCode()
    {
        if (!Live) return;
        var ops = new WindowsInstallerOps();
        var read = 0;
        foreach (var product in new WindowsInstallSystem().Products().Where(x => !string.IsNullOrEmpty(x.LocalPackage) && File.Exists(x.LocalPackage)).Take(10))
        {
            var info = ops.ReadPackage(product.LocalPackage!);
            Assert.NotNull(info);
            Assert.Equal(product.Code, info!.ProductCode, ignoreCase: true);
            if (ops.RegisteredPackageCode(product) is { } registered) Assert.Equal(registered, info.PackageCode, ignoreCase: true);
            read++;
        }
        output.WriteLine($"{read} installer files matched their registered product and package codes.");
    }

    [Fact]
    public void WingetNamesTheInstalledProgramsItKnowsAtTheirExactInstalledVersion()
    {
        if (!Live) return;
        var fetcher = new WingetFetcher();
        var checkedCount = 0;
        // Reads only: each installed program that has an installer family WinGet knows is looked up by its exact name and version.
        foreach (var entry in new WindowsInstallSystem().Entries().Where(x => x.BundleProvider.Length > 0 && x.Name.StartsWith("Python", StringComparison.OrdinalIgnoreCase)).Take(3))
        {
            var match = fetcher.Match(entry.Name, entry.Version);
            output.WriteLine($"{entry.Name} {entry.Version}: {(match is null ? "no match" : match.Id + " " + match.Version)}");
            if (match is not null) { Assert.True(PackageOutputParser.IsSafeId(match.Id)); Assert.StartsWith(match.Version.Split('.')[0], entry.Version); }
            checkedCount++;
        }
        output.WriteLine($"{checkedCount} programs looked up.");
    }

    [Theory]
    [InlineData("Python 3.12.8 (64-bit)", "3.12.8150.0", "Python.Python.3.12", "3.12.8")]
    [InlineData("Python 3.13.9 (64-bit)", "3.13.9150.0", "Python.Python.3.13", "3.13.9")]
    public void WingetFindsThePackageOfABundleItDoesNotListByNameWhenThePackageHasThatVersion(string name, string version, string id, string expected)
    {
        if (!Live) return;
        var match = new WingetFetcher().Match(name, version);
        output.WriteLine($"{name} {version}: {(match is null ? "no match" : match.Id + " " + match.Version)}");
        Assert.NotNull(match);
        Assert.Equal(id, match!.Id);
        Assert.Equal(expected, match.Version);
    }

    [Fact]
    public void TheCachedSetupOfARealBundleOnThisPcPassesTheTrustCheckForItsOwnName()
    {
        if (!Live) return;
        var entry = new WindowsInstallSystem().Entries().First(x => x.BundleProvider.Length > 0 && x.Name.StartsWith("Python 3.13", StringComparison.OrdinalIgnoreCase));
        var setup = InstallRepair.Candidates(entry.UninstallString).First();
        output.WriteLine($"{entry.Name}: {setup}");
        Assert.True(WindowsInstallerOps.IsTrusted(setup.Trim('"'), entry.Name));
        Assert.False(WindowsInstallerOps.IsTrusted(setup.Trim('"'), "Visual Studio Code"));
    }

    [Fact]
    public void WingetSearchesFromTheFullNameDownToItsFamily()
    {
        Assert.Equal(["Python 3.12.8", "Python 3.12", "Python 3", "Python"], WingetFetcher.Queries("Python 3.12.8 (64-bit)"));
        Assert.Equal(["WinSCP 6.7", "WinSCP 6", "WinSCP"], WingetFetcher.Queries("WinSCP 6.7"));
    }

    /// <summary>Downloads a vendor setup program and runs its <c>/layout</c>, so it needs TIDYWINDOW_LIVE_DOWNLOAD=1 as well. Nothing is installed; everything goes to a temp folder that is deleted.</summary>
    [Fact]
    public void TheOriginalInstallerOfAnInstalledBundleCanBeFetchedCheckedUnpackedAndMatchedToItsProduct()
    {
        if (Environment.GetEnvironmentVariable("TIDYWINDOW_LIVE_DOWNLOAD") != "1" || !Live) return;
        var system = new WindowsInstallSystem();
        var entry = system.Entries().First(x => x.BundleProvider.Length > 0 && x.Name.StartsWith("Python", StringComparison.OrdinalIgnoreCase));
        var fetcher = new WingetFetcher();
        var ops = new WindowsInstallerOps();
        var work = Path.Combine(Path.GetTempPath(), "tw-live-download-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var match = fetcher.Match(entry.Name, entry.Version) ?? throw new InvalidOperationException("WinGet does not know " + entry.Name);
            var files = fetcher.Download(match, Path.Combine(work, "download"), "x64", CancellationToken.None);
            output.WriteLine($"downloaded: {string.Join(", ", files.Select(Path.GetFileName))}");
            var setup = Assert.Single(files, x => x.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
            Assert.True(WindowsInstallerOps.IsTrusted(setup, entry.Name), "The downloaded setup program is not validly signed or does not name the program.");
            var packages = ops.ExpandBundle(setup, Path.Combine(work, "layout"), entry.Name);
            output.WriteLine($"{packages.Count} packages unpacked");
            var products = system.Products();
            var matched = packages.Select(ops.ReadPackage).Where(x => x is not null).Where(x => products.Any(p => string.Equals(p.Code, x!.ProductCode, StringComparison.OrdinalIgnoreCase))).ToList();
            foreach (var package in matched) output.WriteLine($"matches an installed product: {package!.Name} {package.ProductCode} package {package.PackageCode}");
            Assert.NotEmpty(matched);
            foreach (var package in matched) Assert.Equal(ops.RegisteredPackageCode(products.First(p => string.Equals(p.Code, package!.ProductCode, StringComparison.OrdinalIgnoreCase))), package!.PackageCode, ignoreCase: true);
        }
        finally { try { Directory.Delete(work, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
    }

    [Fact]
    public void TheInstallerServiceStateCanBeRead()
    {
        if (!Live) return;
        var state = WindowsInstallerOps.ServiceState();
        output.WriteLine("msiserver: " + state);
        Assert.NotNull(state);
    }
}

/// <summary>
/// Needs an elevated window and TIDYWINDOW_ELEVATED_TESTS=1. Runs the privileged path (backup, restore and take-ownership privileges) on a throwaway tree under HKCU:
/// fake product codes only, so no real Windows Installer record is read as a target or changed.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ElevatedRepairTests(ITestOutputHelper output) : IDisposable
{
    private readonly RepairSandbox sandbox = new();
    private readonly string folder = Path.Combine(Path.GetTempPath(), "tw-elev-" + Guid.NewGuid().ToString("N")[..8]);

    private static bool Enabled => Environment.GetEnvironmentVariable("TIDYWINDOW_ELEVATED_TESTS") == "1" && OperatingSystem.IsWindows()
        && new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent()).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);

    public void Dispose()
    {
        if (Enabled) { try { foreach (var path in new[] { Features, Machine, MachineChild }) sandbox.Elevated().Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { } }
        sandbox.Dispose();
        try { Directory.Delete(folder, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static readonly string Features = $@"HKCU\SOFTWARE\Microsoft\Installer\Features\{BrokenPython.Packed}";
    private static readonly string Machine = $@"{BrokenPython.UserData(RepairSandbox.Sid)}\Products\{BrokenPython.Packed}";
    private static string MachineChild => Machine + @"\InstallProperties";
    private const string Real = "O:BAG:SYD:P(A;OICI;CCSWRPWPRC;;;WD)(A;OICI;KA;;;SY)(A;OICI;KA;;;BA)";
    private const string SystemOnly = "O:SYG:SYD:P(A;OICI;KA;;;SY)";

    private void Set(string path, string sddl)
    {
        var sub = path.StartsWith(@"HKCU\") ? @"HKU\" + RepairSandbox.Sid + path[4..] : path;
        using var key = RegistryNative.OpenPrivileged(sandbox.Root, sub, true) ?? throw new InvalidOperationException("Could not open " + path + " with the privileges.");
        var security = new System.Security.AccessControl.RegistrySecurity();
        security.SetSecurityDescriptorSddlForm(sddl, System.Security.AccessControl.AccessControlSections.All);
        key.SetAccessControl(security);
    }

    [Fact]
    public async Task TheElevatedHelperRemovesAndRestoresKeysOwnedBySystemAndTheOnesInstallerRealisticallyLocks()
    {
        if (!Enabled) return;
        Assert.True(RegistryNative.EnablePrivileges(), "The backup, restore and take-ownership privileges could not be turned on.");
        BrokenPython.PerUser(sandbox);
        // Undo what PerUser's own test lock did, then use the two layouts that matter: what the Installer service really leaves, and a key only SYSTEM can touch.
        Set(Features, Real);
        Set(Machine, SystemOnly);
        Set(MachineChild, SystemOnly);
        var repair = sandbox.Elevated();
        var before = new[] { Features, Machine, MachineChild }.Select(x => repair.Snapshot(x)!.Sddl).ToList();
        output.WriteLine(string.Join(Environment.NewLine, before));
        Assert.Contains("O:SY", before[1]);
        // Without the privileges a plain account cannot even open the SYSTEM-only key.
        Assert.ThrowsAny<Exception>(() => { using var key = sandbox.Root.OpenSubKey(Machine, true); });

        var folderHistory = new RepairHistory(folder);
        var pc = new FakePc { Msi = [new(BrokenPython.Code, "User", RepairSandbox.Sid, "Python", @"C:\gone.msi")] };
        pc.State = _ => sandbox.Has($@"HKCU\SOFTWARE\Microsoft\Installer\Products\{BrokenPython.Packed}") ? 5 : -1;
        pc.Programs = [new(BrokenPython.Arp(BrokenPython.Code), "Python", "3", "PSF", "", "", "", "", true, false, false, false)];
        var service = new RepairService(pc, sandbox.User(), repair, _ => Task.CompletedTask, () => { }, folderHistory);
        var finding = Assert.Single(InstallRepair.Scan(pc).Findings, x => x.Footprint is { Kind: "product" });

        var outcome = await service.FixAsync([finding], "Repaired");

        Assert.True(outcome.Results![0].Status == RepairStatus.Fixed, outcome.Results[0].Summary);
        Assert.Empty(sandbox.Mentions(BrokenPython.Packed, BrokenPython.Code));
        var undo = await service.UndoAsync(outcome.Batch!);
        Assert.True(undo.Complete, string.Join(" ", undo.Problems));
        var after = new[] { Features, Machine, MachineChild }.Select(x => repair.Snapshot(x)!.Sddl).ToList();
        Assert.Equal(before, after);
        output.WriteLine("Removed and restored with the original owner and permissions on all three keys.");
    }
}

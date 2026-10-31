using TidyWindow.Next.Core;
using Xunit;
using Xunit.Abstractions;

namespace TidyWindow.Next.Tests;

public sealed class WindowsAppCoverageTests(ITestOutputHelper output)
{
    // Verbatim `winget list` output: packages from a source, a Store-source app, registry programs and Store packages.
    private const string List = """
        Name                                                         Id                                                                                      Version                       Available     Source
        --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
        Discord                                                      ARP\User\X64\Discord                                                                    1.0.9266
        Git                                                          Git.Git                                                                                 2.56.0.4                                    winget
        Microsoft .Net Native Framework Package 2.2                  MSIX\Microsoft.NET.Native.Framework.2.2_2.2.29512.0_x64__8wekyb3d8bbwe                  2.2.29512.0
        Microsoft .Net Native Framework Package 2.2                  MSIX\Microsoft.NET.Native.Framework.2.2_2.2.29512.0_x86__8wekyb3d8bbwe                  2.2.29512.0
        Opera GX Stable 138.0.6044.45                                XPDBZ4MPRKNN30                                                                          138.0.6044.45                               msstore
        Squish                                                       ARP\Machine\X64\{C6C6E8C1-6D1B-4A02-92F8-08E2E6D70B3D}}_is1                             2.3.0
        Wallpaper Engine                                             ARP\Machine\X64\Steam App 431960                                                        Unknown
        Windows Notepad                                              MSIX\Microsoft.WindowsNotepad_11.2607.14.0_x64__8wekyb3d8bbwe                           11.2607.14.0
        """;

    private static IReadOnlyList<ManagedPackage> Read(string output) => PackageOutputParser.Parse(ManagerKind.Winget, output, int.MaxValue, windowsApps: true)
        .Select(x => new ManagedPackage(ManagerKind.Winget, x.Id, x.Name, x.Version, Source: x.Source)).ToArray();

    [Fact]
    public void RegistryProgramsAndStoreAppsAreListedWithTheirOrigin()
    {
        var apps = Read(List);
        Assert.Equal(8, apps.Count);
        Assert.Equal(AppOrigin.Package, apps.Single(x => x.Id == "Git.Git").Origin);
        Assert.Equal("msstore", apps.Single(x => x.Id == "XPDBZ4MPRKNN30").Source);
        Assert.Equal(AppOrigin.WindowsProgram, apps.Single(x => x.Name == "Discord").Origin);
        Assert.Equal(@"ARP\Machine\X64\Steam App 431960", apps.Single(x => x.Name == "Wallpaper Engine").Id);
        Assert.Equal(@"ARP\Machine\X64\{C6C6E8C1-6D1B-4A02-92F8-08E2E6D70B3D}}_is1", apps.Single(x => x.Name == "Squish").Id);
        Assert.Equal(AppOrigin.StoreApp, apps.Single(x => x.Name == "Windows Notepad").Origin);
    }

    [Fact]
    public void WithoutTheOptionOnlySourcePackagesAreKept()
    {
        var apps = PackageOutputParser.Parse(ManagerKind.Winget, List, int.MaxValue);
        Assert.All(apps, x => Assert.True(PackageOutputParser.IsSafeId(x.Id)));
        Assert.DoesNotContain(apps, x => x.Name == "Discord");
    }

    [Fact]
    public void LibrariesAndWindowsPartsAreHiddenButAppsAreNot()
    {
        var kept = Read(List).Where(x => !InstallationOutput.IsPlatformComponent(x)).Select(x => x.Name).ToArray();
        Assert.DoesNotContain("Microsoft .Net Native Framework", kept);
        Assert.Contains("Windows Notepad", kept);
        Assert.Contains("Discord", kept);
    }

    [Theory]
    [InlineData(@"MSIX\MicrosoftCorporationII.WinAppRuntime.Main.1.8_8000.994.2142.0_x64__8wekyb3d8bbwe")]
    [InlineData(@"MSIX\Microsoft.WinAppRuntime.DDLM.8000.642.119.0-x6_8000.642.119.0_x64__8wekyb3d8bbwe")]
    [InlineData(@"MSIX\Microsoft.VCLibs.140.00_14.0.33519.0_x64__8wekyb3d8bbwe")]
    [InlineData(@"MSIX\MicrosoftWindows.Speech.en-US.1_1.0.28.0_x64__cw5n1h2txyewy")]
    [InlineData(@"MSIX\MicrosoftWindows.Client.WebExperience_526.21100.40.0_x64__cw5n1h2txyewy")]
    public void PlatformStorePackagesAreRecognised(string id) =>
        Assert.True(InstallationOutput.IsPlatformComponent(new(ManagerKind.Winget, id, "x", "1")));

    [Theory]
    [InlineData(@"MSIX\Microsoft.WindowsCalculator_11.2607.0.0_x64__8wekyb3d8bbwe")]
    [InlineData(@"MSIX\5319275A.WhatsAppDesktop_2.2641.101.0_x64__cv1g1gvanyjgm")]
    [InlineData(@"MSIX\Microsoft.Paint_11.2605.81.0_x64__8wekyb3d8bbwe")]
    [InlineData(@"ARP\Machine\X64\Android Studio")]
    public void RealAppsAreNotTreatedAsPlatformParts(string id) =>
        Assert.False(InstallationOutput.IsPlatformComponent(new(ManagerKind.Winget, id, "x", "1")));

    [Theory]
    [InlineData("ARP\\Machine\\X64\\a\"b")]
    [InlineData(@"ARP\Machine\X64\a|calc")]
    [InlineData(@"ARP\Machine\X64\a&&calc>x")]
    [InlineData(@"ARP\Machine\X64\%PATH%")]
    [InlineData(@"ARP\Machine\X64\..\..\x")]
    [InlineData(@"ARP\Machine\X64\Vendor App…")]
    [InlineData(@"ARP\Machine\X99\App")]
    [InlineData(@"C:\Windows\System32\calc.exe")]
    public void UnsafeRegistryStyleIdentifiersAreRejected(string id)
    {
        var service = new InstallationService(new PackageService(new NoRunner(), new Locator()), new NoRunner(), new Locator());
        Assert.Throws<ArgumentException>(() => service.ActionCommand(Ready(), new(ManagerKind.Winget, id, "x", "1"), PackageAction.Uninstall));
    }

    [Fact]
    public void WindowsAppsUninstallByExactIdWithoutASourceAndCannotUpdate()
    {
        var service = new InstallationService(new PackageService(new NoRunner(), new Locator()), new NoRunner(), new Locator());
        var discord = new ManagedPackage(ManagerKind.Winget, @"ARP\User\X64\Discord", "Discord", "1");
        var command = service.ActionCommand(Ready(), discord, PackageAction.Uninstall);
        Assert.Equal(["uninstall", "--id", @"ARP\User\X64\Discord", "--exact", "--accept-source-agreements", "--disable-interactivity"], command.Arguments);
        Assert.Throws<InvalidOperationException>(() => service.ActionCommand(Ready(), discord with { AvailableVersion = "2" }, PackageAction.Update));
    }

    [Fact]
    public void StoreSourcePackagesUninstallFromTheStoreSource()
    {
        var service = new InstallationService(new PackageService(new NoRunner(), new Locator()), new NoRunner(), new Locator());
        var opera = new ManagedPackage(ManagerKind.Winget, "XPDBZ4MPRKNN30", "Opera GX", "1", Source: "msstore");
        Assert.Contains("msstore", service.ActionCommand(Ready(), opera, PackageAction.Uninstall).Arguments);
        var git = new ManagedPackage(ManagerKind.Winget, "Git.Git", "Git", "1", Source: "winget");
        Assert.Contains("winget", service.ActionCommand(Ready(), git, PackageAction.Uninstall).Arguments);
    }

    [Fact]
    public async Task WingetInventoryAsksForEverythingOnlyWhenWindowsAppsAreWanted()
    {
        var runner = new NoRunner { Output = List };
        var service = new PackageService(runner, new Locator());
        await service.ReadInstalledAsync(Ready());
        Assert.Contains("--source", runner.Commands[^1].Arguments);
        var all = await service.ReadInstalledAsync(Ready(), windowsApps: true);
        Assert.DoesNotContain("--source", runner.Commands[^1].Arguments);
        Assert.Contains(all.Apps, x => x.Name == "Discord" && x.Source.Length == 0);
    }

    [Fact]
    public void CoverageCountsWhatWindowsListsThatNoManagerReported()
    {
        var windows = new InstalledApp[]
        {
            new(null, null, "Discord", "1"), new(null, null, "Google Chrome", "1"), new(null, null, "Visual Studio Code (User)", "1"),
            new(null, null, "Git version 2.50", "1"), new(null, null, "Git version 2.50", "1")
        };
        var coverage = InstalledInventory.Coverage(windows, ["Discord", "Visual Studio Code", "Git version 2.50"]);
        Assert.Equal(4, coverage.Total);
        Assert.Equal(3, coverage.Listed);
        Assert.Equal(["Google Chrome"], coverage.Missing);
        Assert.Equal(75, coverage.Percent);
    }

    // Measures this PC against Windows' own installed-apps list. Opt in with TIDYWINDOW_LIVE_TESTS=1.
    [Fact]
    public async Task LiveCoverageOfWindowsInstalledAppsWhenExplicitlyEnabled()
    {
        if (Environment.GetEnvironmentVariable("TIDYWINDOW_LIVE_TESTS") != "1" || !OperatingSystem.IsWindows()) return;
        var packages = new PackageService(new ProcessRunner(), new ExecutableLocator());
        var service = new InstallationService(packages, new ProcessRunner(), new ExecutableLocator());
        var listed = new List<string>();
        foreach (var status in await packages.DetectAsync())
        {
            if (status.Health != ManagerHealth.Ready) continue;
            var inventory = await service.ReadAsync(status, checkUpdates: false);
            output.WriteLine($"{status.Definition.Name}: {inventory.Packages.Count} apps. {inventory.Error}");
            listed.AddRange(inventory.Packages.Select(x => x.Name));
        }
        var coverage = InstalledInventory.Coverage(InstalledInventory.ReadListedByWindows(), listed);
        output.WriteLine($"Coverage: {coverage.Listed}/{coverage.Total} = {coverage.Percent:0.0}%");
        foreach (var name in coverage.Missing) output.WriteLine("  missing: " + name);
        Assert.True(coverage.Percent >= 95, $"Only {coverage.Percent:0.0}% of Windows' installed apps are listed.");
    }

    private static ManagerStatus Ready() => new(ManagerDefinition.All.Single(x => x.Kind == ManagerKind.Winget), ManagerHealth.Ready, "1", "winget.exe", "Ready");
    private sealed class Locator : IExecutableLocator
    {
        public string? Find(ManagerKind manager) => "winget.exe";
        public string PowerShell => "powershell.exe";
    }
    private sealed class NoRunner : ICommandRunner
    {
        public string Output { get; init; } = "";
        public List<CommandSpec> Commands { get; } = [];
        public Task<CommandResult> RunAsync(CommandSpec command, CancellationToken cancellationToken = default)
        { Commands.Add(command); return Task.FromResult(new CommandResult(0, Output, "")); }
    }
}

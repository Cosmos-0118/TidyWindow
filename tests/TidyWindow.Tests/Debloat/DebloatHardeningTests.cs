using System.Diagnostics;
using System.Security.Principal;
using System.Text.RegularExpressions;
using TidyWindow.Next.Core;
using Xunit;

namespace TidyWindow.Next.Tests;

/// <summary>The programs the elevated helper runs: where they start, what they may load, and how their output is read.</summary>
public sealed class DebloatProcessHardeningTests
{
    [Fact]
    public void TheElevatedPowerShellOnlyLoadsModulesWindowsShipsAndStartsInSystem32()
    {
        var info = DebloatProcess.StartInfo(DebloatProcess.PowerShell, ["-NoProfile"]);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        Assert.Equal([Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsPowerShell", "Modules"), Path.Combine(windows, "System32", "WindowsPowerShell", "v1.0", "Modules")], info.Environment["PSModulePath"]!.Split(';'));
        Assert.DoesNotContain("Documents", info.Environment["PSModulePath"], StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Path.Combine(windows, "System32"), info.WorkingDirectory);
        Assert.False(info.UseShellExecute);
        Assert.Equal(["-NoProfile"], info.ArgumentList);
    }

    public static IEnumerable<object[]> Scripts() =>
    [
        ["packages for everyone", DebloatScripts.Packages(true)],
        ["packages for this account", DebloatScripts.Packages(false)],
        ["remove", DebloatScripts.RemovePackage("Acme.Tool_1.0.0.0_x64__abc", ["S-1-5-21-1-2-3-1001"])],
        ["provisioned", DebloatScripts.RemoveProvisioned("Acme.Tool_1.0.0.0_neutral_~_abc")],
        ["tasks", DebloatScripts.ListOneDriveTasks],
        ["unregister", DebloatScripts.UnregisterTasks(["OneDrive Reporting Task"])]
    ];

    [Theory]
    [MemberData(nameof(Scripts))]
    public void EveryCmdletFromAModuleIsNamedWithItsModule(string name, string script)
    {
        // Where-Object, ForEach-Object and Out-Null live in the engine itself and cannot be replaced by a module on disk; everything else must say which module it comes from.
        var bare = Regex.Matches(script, @"(?<![\\\w-])[A-Z][a-z]+-[A-Z][A-Za-z]+\b").Select(m => m.Value).Distinct().ToList();
        Assert.All(bare, cmdlet => Assert.Contains(cmdlet, new[] { "Where-Object", "ForEach-Object", "Out-Null" }));
        Assert.True(name.Length > 0);
    }

    [Fact]
    public void TheAppCmdletsComeFromTheModulesWindowsShips()
    {
        Assert.Contains(@"Appx\Get-AppxPackage", DebloatScripts.Packages(true));
        Assert.Contains(@"Appx\Remove-AppxPackage", DebloatScripts.RemovePackage("Acme.Tool_1.0.0.0_x64__abc", []));
        Assert.Contains(@"Microsoft.PowerShell.Management\Stop-Process", DebloatScripts.RemovePackage("Acme.Tool_1.0.0.0_x64__abc", []));
        Assert.Contains(@"Dism\Remove-AppxProvisionedPackage", DebloatScripts.RemoveProvisioned("Acme.Tool_1.0.0.0_neutral_~_abc"));
        Assert.Contains(@"ScheduledTasks\Get-ScheduledTask", DebloatScripts.ListOneDriveTasks);
        Assert.Contains(@"ScheduledTasks\Unregister-ScheduledTask", DebloatScripts.UnregisterTasks(["OneDrive Reporting Task"]));
    }

    [Fact]
    public void WarningsAreSilencedSoTheyCannotLandInTheJsonTheScriptsReturn() => Assert.Contains("$WarningPreference='SilentlyContinue'", DebloatProcess.Prelude);

    [Fact]
    public void WhatAProgramPrintsAndWhatItComplainsAboutAreKeptApart()
    {
        if (!OperatingSystem.IsWindows()) return;
        var cmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "cmd.exe");

        var result = DebloatProcess.Run(cmd, ["/d", "/c", "echo out& echo err 1>&2& exit 3"], TimeSpan.FromSeconds(30));

        Assert.False(result.TimedOut);
        Assert.Equal(3, result.ExitCode);
        Assert.Equal("out", result.Output);
        Assert.Equal("err", result.Error);
        Assert.Equal("err", result.Problem);
    }

    [Fact]
    public void AProgramThatLeavesSomethingHoldingItsOutputDoesNotHoldTheHelperUp()
    {
        if (!OperatingSystem.IsWindows()) return;
        var cmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "cmd.exe");
        // start /b leaves a child that inherits the output pipes and lives on for a while after cmd itself has ended.
        // The grace has to be long enough for a busy CI runner to deliver "done", yet shorter than the ~7 s the ping lives, so the test still proves it is not waited for.
        var timer = Stopwatch.StartNew();

        var result = DebloatProcess.Run(cmd, ["/d", "/c", "start /b cmd /d /c ping -n 8 127.0.0.1 & echo done"], TimeSpan.FromSeconds(60), grace: TimeSpan.FromSeconds(3));

        Assert.False(result.TimedOut);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("done", result.Output);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(6), $"Took {timer.Elapsed}.");
    }
}

/// <summary>The OneDrive and Edge steps with the machine behind delegates.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class PlaybookHardeningTests
{
    private const string Setup = @"C:\Program Files (x86)\Microsoft\Edge\Application\156.0.1.1\Installer\setup.exe";
    private const string Product = "{3193C6F3-8C79-30A0-AE89-121FE521ABFF}";
    private static DebloatProcess.Result Exit(int code, string output = "") => new(code, output, false);

    [Fact]
    public void EdgeThatStaysAfterItsUninstallerRanKeepsItsShortcutsAndIsReportedWithTheCode()
    {
        var forgotten = false;

        var outcome = WindowsPlaybooks.RemoveEdgeCore(() => true, () => new EdgeInstall(Setup, null), _ => Exit(0), _ => Exit(19, "another install is running"), () => forgotten = true);

        Assert.False(outcome.Ok);
        Assert.Contains("still on this PC", outcome.Error);
        Assert.Contains("error code 19", outcome.Error);
        Assert.Contains("another install is running", outcome.Error);
        Assert.False(forgotten);
    }

    [Fact]
    public void EdgeIsForgottenOnlyOnceItIsReallyGone()
    {
        var present = true; var forgotten = false;

        var outcome = WindowsPlaybooks.RemoveEdgeCore(() => true, () => present ? new EdgeInstall(Setup, null) : null, _ => Exit(0), _ => { present = false; return Exit(0); }, () => forgotten = true);

        Assert.True(outcome.Ok, outcome.Error);
        Assert.True(forgotten);
    }

    [Fact]
    public void AnEdgeFromTheMsiGoesThroughWindowsInstallerAndItsOwnSetupOnlyIfThatLeftItBehind()
    {
        var present = true; var calls = new List<string>();

        var outcome = WindowsPlaybooks.RemoveEdgeCore(() => true, () => present ? new EdgeInstall(Setup, Product) : null,
            product => { calls.Add("msi " + product); present = false; return Exit(0); }, _ => { calls.Add("setup"); return Exit(0); }, () => calls.Add("forget"));

        Assert.True(outcome.Ok, outcome.Error);
        Assert.Equal(["msi " + Product, "forget"], calls);
    }

    [Fact]
    public void AnUninstallerThatTimesOutIsReportedAndNothingIsForgotten()
    {
        var forgotten = false;

        var outcome = WindowsPlaybooks.RemoveEdgeCore(() => true, () => new EdgeInstall(Setup, null), _ => Exit(0), _ => new(-1, "", true), () => forgotten = true);

        Assert.False(outcome.Ok);
        Assert.Contains("too long", outcome.Error);
        Assert.False(forgotten);
    }

    [Fact]
    public void EdgeIsNotTouchedWithoutAnotherBrowserForEveryone()
    {
        var touched = false;

        var outcome = WindowsPlaybooks.RemoveEdgeCore(() => false, () => new EdgeInstall(Setup, null), _ => { touched = true; return Exit(0); }, _ => { touched = true; return Exit(0); }, () => touched = true);

        Assert.False(outcome.Ok);
        Assert.Equal(DebloatScan.OnlyBrowserLock, outcome.Error);
        Assert.False(touched);
    }

    [Fact]
    public void OneDriveIsForgottenOnlyOnceItIsGoneFromTheProfileThatAsked()
    {
        var forgotten = false;
        const string setup = @"C:\Windows\System32\OneDriveSetup.exe";

        var stays = WindowsPlaybooks.RemoveOneDriveCore(() => true, setup, _ => Exit(0), () => forgotten = true, sameAccount: false);
        Assert.False(stays.Ok);
        Assert.Contains("different account", stays.Error);
        Assert.False(forgotten);

        var failed = WindowsPlaybooks.RemoveOneDriveCore(() => true, setup, _ => Exit(5), () => forgotten = true, sameAccount: true);
        Assert.Contains("error code 5", failed.Error);
        Assert.False(forgotten);

        var goes = WindowsPlaybooks.RemoveOneDriveCore(() => false, setup, _ => Exit(0), () => forgotten = true, sameAccount: true);
        Assert.True(goes.Ok);
        Assert.True(forgotten);

        Assert.Contains("not found", WindowsPlaybooks.RemoveOneDriveCore(() => true, null, _ => Exit(0), () => forgotten = true, sameAccount: true).Error);
    }

    [Fact]
    public void OneDrivesUninstallerIsOnlyEverRunFromWindowsOrProgramFiles()
    {
        var setups = WindowsPlaybooks.OneDriveSetups(@"C:\Windows", @"C:\Program Files", root => [Path.Combine(root, "26.1.1.1", "OneDriveSetup.exe")]).ToList();

        Assert.Equal([@"C:\Windows\SysWOW64\OneDriveSetup.exe", @"C:\Windows\System32\OneDriveSetup.exe", @"C:\Program Files\Microsoft OneDrive\26.1.1.1\OneDriveSetup.exe"], setups);
        Assert.All(setups, x => Assert.DoesNotContain(@"\Users\", x, StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("Google Chrome", true)]
    [InlineData("FIREFOX.EXE", true)]
    [InlineData("Microsoft Edge", false)]
    [InlineData("Microsoft Edge Beta", false)]
    [InlineData("IEXPLORE.EXE", false)]
    public void OnlyBrowsersOtherThanEdgeAndInternetExplorerCount(string name, bool expected) => Assert.Equal(expected, BrowserDetection.IsOtherBrowser(name));

    [Theory]
    [InlineData("\"C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe\"", "C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe")]
    [InlineData("\"C:\\Program Files\\Mozilla Firefox\\firefox.exe\" -osint -url \"%1\"", "C:\\Program Files\\Mozilla Firefox\\firefox.exe")]
    [InlineData("C:\\Browsers\\Brave\\brave.exe --flag", "C:\\Browsers\\Brave\\brave.exe")]
    [InlineData("chrome.exe", null)]
    [InlineData("\"C:\\a\\..\\b\\x.exe\"", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void TheBrowsersProgramIsReadFromItsOpenCommand(string? command, string? expected) => Assert.Equal(expected, BrowserDetection.ProgramOf(command));
}

public sealed class DebloatCatalogHardeningTests
{
    private static readonly IReadOnlyList<DebloatItem> Catalog = DebloatCatalog.Load();

    private static DebloatItem Item(string id = "x-item", string storeId = "", string[]? packages = null, string[]? except = null) =>
        new() { Id = id, Name = "X", Group = DebloatGroup.Microsoft, Summary = "s", Effect = "e", StoreId = storeId, Packages = packages ?? ["Acme.Tool"], Except = except ?? [] };

    [Theory]
    [InlineData("9P1J8S7CCWWT", true)]
    [InlineData("XP8BT8DW290MPQ", true)]
    [InlineData("XPFFTQ037JWMHS", true)]
    [InlineData("Microsoft.Edge", false)]
    [InlineData("9P1J8S7CCWW", false)]
    [InlineData("AP1J8S7CCWWT", false)]
    [InlineData("", false)]
    public void StoreProductIdsComeInTwoLengths(string id, bool expected) => Assert.Equal(expected, DebloatReinstaller.IsStoreProduct(id));

    [Fact]
    public void AnIdThatWinGetWouldNotKnowMakesTheCatalogInvalid()
    {
        Assert.NotEmpty(DebloatGuard.Validate([Item(storeId: "not a store id")]));
        Assert.NotEmpty(DebloatGuard.Validate([Item(storeId: "XP8BT8DW290MP")]));
        Assert.Empty(DebloatGuard.Validate([Item(storeId: "XP8BT8DW290MPQ")]));
        Assert.Empty(DebloatGuard.Validate([Item(storeId: "Microsoft.OneDrive")]));
    }

    [Fact]
    public async Task TheNewTeamsIsItsOwnCarefulItemAndItsStoreIdReachesTheStore()
    {
        var teams = Catalog.Single(x => x.Id == "teams");
        Assert.Equal(DebloatRisk.Careful, teams.Risk);
        Assert.False(teams.Recommended);
        Assert.True(DebloatGuard.Covers(teams, "MSTeams", false));
        Assert.False(DebloatGuard.Covers(Catalog.Single(x => x.Id == "teams-consumer"), "MSTeams", false));
        var commands = new List<CommandSpec>();
        var outcome = await new DebloatReinstaller(new Runner(commands), new Locator()).InstallAsync(teams);
        Assert.True(outcome.Ok);
        Assert.Contains("msstore", commands.Single().Arguments);
        Assert.Contains("XP8BT8DW290MPQ", commands.Single().Arguments);
    }

    [Theory]
    [InlineData("oem-hp", "AD2F1837.HPSystemEventUtility", "AD2F1837.HPSupportAssistant")]
    [InlineData("oem-dell", "DellInc.DellDigitalDelivery", "DellInc.DellSupportAssistforPCs")]
    [InlineData("oem-asus", "B9ECED6F.ArmouryCrate", "B9ECED6F.MyASUS")]
    [InlineData("oem-lenovo", "E0469640.LenovoUtility", "E0469640.LenovoSmartCommunication")]
    public void AMakersHardwareControlsAreKeptWhileItsExtrasGo(string id, string kept, string removed)
    {
        var item = Catalog.Single(x => x.Id == id);
        Assert.False(DebloatGuard.Covers(item, kept, false));
        Assert.True(DebloatGuard.Covers(item, removed, false));
        var pc = new FakeDebloatPc { Installed = [FakeDebloatPc.Package(kept, "v10z8vjag6ke6")] };
        Assert.Null(DebloatScan.Find(pc, Catalog, id));
    }

    [Fact]
    public void AnExceptionThatNoPackageWouldRemoveIsAMistake()
    {
        Assert.NotEmpty(DebloatGuard.Validate([Item(packages: ["AcmeCorp.*"], except: ["Other.Thing"])]));
        Assert.NotEmpty(DebloatGuard.Validate([Item(packages: ["AcmeCorp.*"], except: ["a b"])]));
        Assert.Empty(DebloatGuard.Validate([Item(packages: ["AcmeCorp.*"], except: ["AcmeCorp.Keeper"])]));
    }

    [Fact]
    public async Task AnItemWithNothingToInstallItFromIsAllowedBackButNotCalledBack()
    {
        var history = new DebloatHistory(Path.Combine(Path.GetTempPath(), "tidywindow-debloat-" + Guid.NewGuid().ToString("N")));
        history.Add(new("dev-home", "Dev Home", DateTimeOffset.Now, ["Microsoft.Windows.DevHome_8wekyb3d8bbwe"]));
        var manager = new DebloatManager(new Changer(_ => new(true)), () => Task.CompletedTask, history, new DebloatReinstaller(new Runner([]), new Locator()));

        var outcome = await manager.RestoreAsync(Catalog.Single(x => x.Id == "dev-home"));

        Assert.True(outcome.Ok);
        Assert.True(outcome.AllowedOnly);
        Assert.Contains("not installed", outcome.Note);
        Assert.Equal(outcome.Note, history.Get("dev-home")!.Note);
    }

    [Fact]
    public async Task AWinGetThatTakesTooLongOrCannotStartIsAFailureNotACrash()
    {
        var locator = new Locator();
        Assert.Contains("too long", (await new DebloatReinstaller(new Runner([], new TimeoutException()), locator).InstallAsync(Catalog.Single(x => x.Id == "clipchamp"))).Error);
        Assert.False((await new DebloatReinstaller(new Runner([], new System.ComponentModel.Win32Exception(5)), locator).InstallAsync(Catalog.Single(x => x.Id == "clipchamp"))).Ok);
    }

    private sealed class Changer(Func<DebloatRequest, DebloatOutcome> answer) : IDebloatChanger
    {
        public DebloatOutcome Change(DebloatRequest request) => answer(request);
    }

    private sealed class Runner(List<CommandSpec> commands, Exception? throws = null) : ICommandRunner
    {
        public Task<CommandResult> RunAsync(CommandSpec command, CancellationToken cancellationToken = default)
        {
            if (throws is not null) throw throws;
            commands.Add(command);
            return Task.FromResult(new CommandResult(0, "", ""));
        }
    }

    private sealed class Locator : IExecutableLocator
    {
        public string? Find(ManagerKind manager) => manager == ManagerKind.Winget ? "winget.exe" : null;
        public string PowerShell => "powershell.exe";
    }
}

public sealed class AppxRemoverHardeningTests
{
    private static readonly IReadOnlyList<DebloatItem> Catalog = DebloatCatalog.Load();

    [Fact]
    public void AnAppThatWillNotGoIsLockedAgainAsItWas()
    {
        var pc = new FakeDebloatPc { Installed = [FakeDebloatPc.Package("Microsoft.BingNews", locked: true)], Stubborn = ["Microsoft.BingNews_1.0.0.0_x64__8wekyb3d8bbwe"] };

        var outcome = new AppxRemover(pc, pc, pc).Remove(DebloatScan.Find(pc, Catalog, "bing-news-weather")!);

        Assert.False(outcome.Ok);
        Assert.Equal(["unlock Microsoft.BingNews_8wekyb3d8bbwe", "lock Microsoft.BingNews_8wekyb3d8bbwe"], pc.Log.Where(x => x.StartsWith("unlock ") || x.StartsWith("lock ")));
    }

    [Fact]
    public void AnAppThatGoesIsNotLockedAgain()
    {
        var pc = new FakeDebloatPc { Installed = [FakeDebloatPc.Package("Microsoft.BingNews", locked: true)] };

        Assert.True(new AppxRemover(pc, pc, pc).Remove(DebloatScan.Find(pc, Catalog, "bing-news-weather")!).Ok);

        Assert.DoesNotContain(pc.Log, x => x.StartsWith("lock "));
    }

    [Fact]
    public void TheLockIsPutBackEvenWhenTheRemovalThrows()
    {
        var pc = new FakeDebloatPc { Installed = [FakeDebloatPc.Package("Microsoft.BingNews", locked: true)], ThrowDuringRemoval = true };

        Assert.Throws<InvalidOperationException>(() => new AppxRemover(pc, pc, pc).Remove(DebloatScan.Find(pc, Catalog, "bing-news-weather")!));

        Assert.Contains("lock Microsoft.BingNews_8wekyb3d8bbwe", pc.Log);
    }

    [Fact]
    public void AnAppWindowsRegisteredAgainUnderANewVersionStillCountsAsThere()
    {
        var pc = new FakeDebloatPc { Installed = [FakeDebloatPc.Package("Clipchamp.Clipchamp", "yxz26nhyzhsrt")], Reregisters = true };

        var outcome = new AppxRemover(pc, pc, pc).Remove(DebloatScan.Find(pc, Catalog, "clipchamp")!);

        Assert.False(outcome.Ok);
        Assert.Contains("could not be removed", outcome.Error);
        Assert.Empty(pc.Deprovisioned);
    }
}

public sealed class DebloatHostHardeningTests
{
    private sealed class Recorder(List<DebloatRequest> seen) : IDebloatChanger
    {
        public DebloatOutcome Change(DebloatRequest request) { seen.Add(request); return new(true); }
    }

    [Fact]
    public async Task TheHelperLearnsWhoAskedFromThePipeNotFromTheRequest()
    {
        if (!OperatingSystem.IsWindows()) return;
        var seen = new List<DebloatRequest>();
        using var session = new DebloatSession(pipe => DebloatHost.RunAsync([DebloatHost.Command, pipe], new Recorder(seen), () => true, TimeSpan.FromSeconds(20)));
        await session.OpenAsync();

        session.Change(new("clipchamp", UserSid: "S-1-5-21-9-9-9-9999"));

        using var identity = WindowsIdentity.GetCurrent();
        Assert.Equal(identity.User!.Value, seen.Single().UserSid);
    }

    [Fact]
    public async Task AHelperThatConnectsAndSaysNothingUsefulIsAnErrorNotACrash()
    {
        using var session = new DebloatSession(async name =>
        {
            using var pipe = await HelperPipe.ConnectAsync(name);
            // Reads the hello, then answers with something that is not JSON and leaves.
            await new PipeLineReader(pipe).ReadLineAsync();
            var writer = new StreamWriter(pipe) { AutoFlush = true };
            await writer.WriteLineAsync("");
        });

        await Assert.ThrowsAsync<IOException>(() => session.OpenAsync());

        Assert.False(session.IsOpen);
    }
}

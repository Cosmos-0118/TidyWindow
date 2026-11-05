using System.Text.Json;
using TidyWindow.Next.Core;
using Xunit;

namespace TidyWindow.Next.Tests;

internal static class DriverData
{
    public const string UpdateId = "702e6ed7-4f31-4f3b-9f49-de333467baf8";
    public const string HardwareId = @"HDAUDIO\FUNC_01&VEN_10EC&DEV_0897";

    public static DriverOffer Offer(string title = "Realtek - MEDIA - 6.0.9700.1", DateOnly? date = null, bool eula = true, int problem = 0, string? hardware = null, bool reboot = false, string id = UpdateId) =>
        new(id, 1, title, "Media", "Realtek", "Realtek", "Realtek Audio", hardware ?? HardwareId.ToLowerInvariant(), date ?? new DateOnly(2026, 6, 1), 1_000_000, problem, reboot, eula);

    public static InstalledDriver Driver(string version = "6.0.9600.1", string inf = "oem5.inf", DateOnly? date = null, string key = @"HDAUDIO\FUNC_01\4&1", params string[] ids) =>
        new(key, "Realtek Audio", "MEDIA", version, date ?? new DateOnly(2026, 1, 1), "Realtek", inf, ids.Length > 0 ? ids : [HardwareId, @"HDAUDIO\FUNC_01&VEN_10EC"]);
}

public sealed class DriverCompareTests
{
    [Theory]
    [InlineData("Realtek Semiconductor Corp. - MEDIA - 6.0.9700.1", "6.0.9700.1")]
    [InlineData("Microsoft Corporation AudioProcessingObject Driver Update (1.0.4.55794)", "1.0.4.55794")]
    [InlineData("Intel(R) 10 Graphics 31.0.101.5333", "31.0.101.5333")]
    [InlineData("Driver 1.2.3 and then 4.5.6.7", "4.5.6.7")]
    public void TheVersionAtTheEndOfATitleIsFound(string title, string expected) => Assert.Equal(Version.Parse(expected), DriverCompare.VersionIn(title));

    [Theory]
    [InlineData("Intel(R) 10 Graphics")]
    [InlineData("Version 1.2")]
    [InlineData("")]
    [InlineData(null)]
    public void ATitleWithoutAVersionHasNone(string? title) => Assert.Null(DriverCompare.VersionIn(title));

    [Fact]
    public void AHigherVersionIsNewerAndALowerOneIsOlderWhateverTheDates()
    {
        var installed = new[] { DriverData.Driver("6.0.9600.1", date: new DateOnly(2026, 1, 1)) };
        // The offer is dated earlier but carries the higher version: the version decides.
        var newer = DriverCompare.Assess(DriverData.Offer("Realtek - MEDIA - 6.0.9700.1", new DateOnly(2025, 1, 1)), installed);
        Assert.Equal(DriverVerdict.Newer, newer.Verdict);
        Assert.True(newer.CanInstall);
        var older = DriverCompare.Assess(DriverData.Offer("Realtek - MEDIA - 6.0.9500.1", new DateOnly(2027, 1, 1)), installed);
        Assert.Equal(DriverVerdict.Older, older.Verdict);
        Assert.False(older.CanInstall);
    }

    [Fact]
    public void WhenNoVersionCanBeReadTheDatesDecide()
    {
        var installed = new[] { DriverData.Driver("weird", date: new DateOnly(2026, 3, 1)) };
        Assert.Equal(DriverVerdict.Newer, DriverCompare.Assess(DriverData.Offer("Audio", new DateOnly(2026, 4, 1)), installed).Verdict);
        Assert.Equal(DriverVerdict.Older, DriverCompare.Assess(DriverData.Offer("Audio", new DateOnly(2026, 2, 1)), installed).Verdict);
        var same = DriverCompare.Assess(DriverData.Offer("Audio", new DateOnly(2026, 3, 1)), installed);
        Assert.Equal(DriverVerdict.Same, same.Verdict);
        Assert.True(same.CanInstall);
    }

    [Fact]
    public void TheSameVersionIsComparedByDateAndAnOfferWithNothingToCompareIsSaidSo()
    {
        var installed = new[] { DriverData.Driver("6.0.9700.1", date: new DateOnly(2026, 6, 1)) };
        Assert.Equal(DriverVerdict.Same, DriverCompare.Assess(DriverData.Offer("Realtek - MEDIA - 6.0.9700.1", new DateOnly(2026, 6, 1)), installed).Verdict);
        var undated = DriverData.Offer("Audio") with { Date = null };
        Assert.Equal(DriverVerdict.Unknown, DriverCompare.Assess(undated, [DriverData.Driver("x")]).Verdict);
        Assert.True(DriverCompare.Assess(undated, [DriverData.Driver("x")]).CanInstall);
    }

    [Fact]
    public void AnOfferForHardwareWithNoDriverIsNew()
    {
        var finding = DriverCompare.Assess(DriverData.Offer(), [DriverData.Driver(ids: @"PCI\VEN_8086&DEV_1234")]);
        Assert.Equal(DriverVerdict.New, finding.Verdict);
        Assert.Null(finding.Installed);
        Assert.True(finding.CanInstall);
    }

    [Fact]
    public void HardwareIdsMatchIgnoringCaseAndByTheirMoreSpecificForms()
    {
        // Windows Update gives a lower-case id; a device lists longer ones that begin with it.
        var specific = DriverData.Driver(ids: @"PCI\VEN_1002&DEV_7590&SUBSYS_061E1043&REV_C0");
        Assert.NotNull(DriverCompare.Match(DriverData.Offer(hardware: @"pci\ven_1002&dev_7590"), [specific]));
        Assert.NotNull(DriverCompare.Match(DriverData.Offer(hardware: @"pci\ven_1002&dev_7590&subsys_061e1043&rev_c0"), [specific]));
        // But an id that merely starts with the same letters is a different device.
        Assert.Null(DriverCompare.Match(DriverData.Offer(hardware: @"pci\ven_1002&dev_759"), [specific]));
        Assert.Null(DriverCompare.Match(DriverData.Offer(hardware: ""), [specific]));
    }

    [Fact]
    public void WithSeveralDevicesTheNewestInstalledDriverIsTheOneAnOfferIsJudgedAgainst()
    {
        var oldest = DriverData.Driver("6.0.1.0", key: "a");
        var newest = DriverData.Driver("6.5.0.0", key: "b");
        var finding = DriverCompare.Assess(DriverData.Offer("Realtek - MEDIA - 6.2.0.0"), [oldest, newest]);
        Assert.Equal("b", finding.Installed!.DeviceKey);
        Assert.Equal(DriverVerdict.Older, finding.Verdict);
    }

    [Fact]
    public void OnlyANewOrNewerOrUnknownOfferIsPutFirst()
    {
        var installed = new[] { DriverData.Driver("6.0.9600.1") };
        Assert.True(DriverCompare.IsActionable(DriverCompare.Assess(DriverData.Offer("A - 7.0.0.0"), installed)));
        Assert.False(DriverCompare.IsActionable(DriverCompare.Assess(DriverData.Offer("A - 5.0.0.0"), installed)));
    }
}

public sealed class DriverGuardTests
{
    [Theory]
    [InlineData("702e6ed7-4f31-4f3b-9f49-de333467baf8", true)]
    [InlineData("702E6ED7-4F31-4F3B-9F49-DE333467BAF8", true)]
    [InlineData("702e6ed74f314f3b9f49de333467baf8", false)]
    [InlineData("{702e6ed7-4f31-4f3b-9f49-de333467baf8}", false)]
    [InlineData("C:\\Windows\\system32\\evil.exe", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlyARealUpdateIdGetsThroughToTheHelpersSearch(string? value, bool expected) => Assert.Equal(expected, DriverGuard.IsUpdateId(value));

    [Theory]
    [InlineData("oem5.inf", true)]
    [InlineData("OEM123.INF", true)]
    [InlineData("netrasa.inf", false)]
    [InlineData("oem5.inf /force", false)]
    [InlineData(@"..\oem5.inf", false)]
    [InlineData("oem.inf", false)]
    [InlineData(null, false)]
    public void OnlyAVendorDriverInTheStoreIsEverNamedToPnputil(string? value, bool expected) => Assert.Equal(expected, DriverGuard.IsOemInf(value));

    [Fact]
    public void ASavedDriverIdIsAGuidWithoutDashesSoItCannotBeAPath()
    {
        Assert.True(DriverGuard.IsBackupId(Guid.NewGuid().ToString("N")));
        Assert.False(DriverGuard.IsBackupId(Guid.NewGuid().ToString("D")));
        Assert.False(DriverGuard.IsBackupId(@"..\..\x"));
        Assert.False(DriverGuard.IsBackupId(null));
    }

    [Fact]
    public async Task PnputilIsNeverGivenAnythingThatIsNotAVendorDriverOrAnInfFile()
    {
        var pnp = new PnpUtil(new NoRunner());
        await Assert.ThrowsAsync<ArgumentException>(() => pnp.ExportAsync("netrasa.inf", "x", default));
        await Assert.ThrowsAsync<ArgumentException>(() => pnp.DeleteAsync("oem5.inf /force", default));
        await Assert.ThrowsAsync<ArgumentException>(() => pnp.AddAsync(@"C:\x\payload.exe", default));
    }

    private sealed class NoRunner : ICommandRunner
    {
        public Task<CommandResult> RunAsync(CommandSpec command, CancellationToken cancellationToken = default) => throw new InvalidOperationException("nothing should be run");
    }

    [Fact]
    public async Task PnputilIsRunWithTheFixedArgumentsForEachJob()
    {
        var runner = new RecordingRunner();
        var pnp = new PnpUtil(runner);
        await pnp.ExportAsync("oem5.inf", @"C:\store\b", default);
        await pnp.AddAsync(@"C:\store\b\x.inf", default);
        await pnp.DeleteAsync("oem9.inf", default);
        await pnp.ScanAsync(default);
        Assert.All(runner.Specs, x => Assert.EndsWith("pnputil.exe", x.FileName, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(["/export-driver oem5.inf C:\\store\\b", "/add-driver C:\\store\\b\\x.inf", "/delete-driver oem9.inf /uninstall /force", "/scan-devices"], runner.Specs.Select(x => string.Join(' ', x.Arguments)));
    }

    private sealed class RecordingRunner : ICommandRunner
    {
        public readonly List<CommandSpec> Specs = [];
        public Task<CommandResult> RunAsync(CommandSpec command, CancellationToken cancellationToken = default) { Specs.Add(command); return Task.FromResult(new CommandResult(0, "", "")); }
    }
}

public sealed class DriverNoticeTests
{
    private static IReadOnlyList<string> Notices(Dictionary<string, int> numbers, Dictionary<string, string>? text = null) =>
        DriverPolicy.Notices((path, name) => numbers.TryGetValue(path + "|" + name, out var value) ? value : null, (path, name) => text is not null && text.TryGetValue(path + "|" + name, out var value) ? value : null);

    private const string Policy = @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate";

    [Fact]
    public void ACleanPcHasNothingToReport() => Assert.Empty(Notices([]));

    [Fact]
    public void ThePolicyThatExcludesDriversIsReportedWhetherGroupPolicyOrIntuneSetIt()
    {
        Assert.Single(Notices(new() { [Policy + "|ExcludeWUDriversInQualityUpdate"] = 1 }));
        Assert.Single(Notices(new() { [@"SOFTWARE\Microsoft\PolicyManager\current\device\Update|ExcludeWUDriversInQualityUpdate"] = 1 }));
        Assert.Empty(Notices(new() { [Policy + "|ExcludeWUDriversInQualityUpdate"] = 0 }));
    }

    [Fact]
    public void AnUpdateServerIsReportedOnlyWhenItIsBothNamedAndTurnedOn()
    {
        var named = new Dictionary<string, string> { [Policy + "|WUServer"] = "https://wsus.example" };
        Assert.Single(Notices(new() { [Policy + @"\AU|UseWUServer"] = 1 }, named));
        Assert.Empty(Notices(new() { [Policy + @"\AU|UseWUServer"] = 0 }, named));
        Assert.Empty(Notices(new() { [Policy + @"\AU|UseWUServer"] = 1 }));
    }

    [Fact]
    public void BlockedInternetAccessAndTheNeverSearchSettingAreReported()
    {
        Assert.Single(Notices(new() { [Policy + "|DoNotConnectToWindowsUpdateInternetLocations"] = 1 }));
        Assert.Single(Notices(new() { [@"SOFTWARE\Microsoft\Windows\CurrentVersion\DriverSearching|SearchOrderConfig"] = 0 }));
        Assert.Empty(Notices(new() { [@"SOFTWARE\Microsoft\Windows\CurrentVersion\DriverSearching|SearchOrderConfig"] = 1 }));
    }
}

public sealed class DriverMakerTests
{
    private static InstalledDriver Display(string provider) => new("k", "GPU", "Display", "1.0.0.0", null, provider, "oem1.inf", []);

    [Fact]
    public void TheToolsThatFitAreChosenFromTheGraphicsDriverAndThePcsMaker()
    {
        Assert.Equal("NVIDIA app", Assert.Single(DriverMakers.For([Display("NVIDIA")], "ASUS")).Name);
        Assert.Contains("AMD", Assert.Single(DriverMakers.For([Display("Advanced Micro Devices, Inc.")], "")).Name);
        Assert.Contains("Intel", Assert.Single(DriverMakers.For([Display("Intel Corporation")], "")).Name);
        Assert.Equal(["Dell Command | Update"], DriverMakers.For([], "Dell Inc.").Select(x => x.Name));
        Assert.Equal(["Lenovo System Update"], DriverMakers.For([], "LENOVO").Select(x => x.Name));
        Assert.Equal(["HP Support Assistant"], DriverMakers.For([], "HP").Select(x => x.Name));
        Assert.Empty(DriverMakers.For([Display("Microsoft")], "Micro-Star International Co., Ltd."));
    }

    [Fact]
    public void ADriverThatIsNotAGraphicsDriverDoesNotChooseAGraphicsTool()
    {
        var audio = new InstalledDriver("k", "Audio", "MEDIA", "1", null, "NVIDIA", "oem1.inf", []);
        Assert.Empty(DriverMakers.For([audio], ""));
    }

    [Fact]
    public void EveryMakerToolIsAnHttpsLinkAndEveryWingetIdIsWellFormed()
    {
        var all = DriverMakers.For([Display("NVIDIA"), Display("Advanced Micro Devices, Inc."), Display("Intel")], "Dell").Concat(DriverMakers.For([], "LENOVO")).Concat(DriverMakers.For([], "HP"));
        Assert.All(all, tool =>
        {
            Assert.True(Uri.TryCreate(tool.Url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps, tool.Name);
            Assert.True(tool.WinGetId is null || System.Text.RegularExpressions.Regex.IsMatch(tool.WinGetId, @"^[A-Za-z0-9]+(\.[A-Za-z0-9\-]+)+$"), tool.Name);
        });
    }
}

public sealed class DriverHistoryTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "tw-drivers-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(folder, true); } catch (IOException) { } }

    private static DriverChange Change(string? backup, DateTimeOffset when) => new(DriverData.UpdateId, "t", "Realtek Audio", "1", "2", when, backup);

    [Fact]
    public void WhatWasInstalledIsKeptNewestFirstAndSurvivesARestart()
    {
        var history = new DriverHistory(folder);
        history.Add(Change("a", new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)));
        history.Add(Change("b", new(2026, 2, 1, 0, 0, 0, TimeSpan.Zero)));
        Assert.Equal(["b", "a"], new DriverHistory(folder).All().Select(x => x.BackupId));
    }

    [Fact]
    public void PuttingAnEarlierDriverBackMarksOnlyThatRecord()
    {
        var history = new DriverHistory(folder);
        history.Add(Change("a", DateTimeOffset.Now.AddDays(-1)));
        history.Add(Change("b", DateTimeOffset.Now));
        history.MarkRestored("a");
        Assert.Equal([false, true], history.All().Select(x => x.Restored));
    }

    [Fact]
    public void AnUnreadableFileIsAnEmptyHistoryNotAnError()
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "installed.json"), "{ not json");
        Assert.Empty(new DriverHistory(folder).All());
    }

    [Fact]
    public void OnlyTheNewestFiftyAreKept()
    {
        var history = new DriverHistory(folder);
        for (var i = 0; i < 60; i++) history.Add(Change(null, DateTimeOffset.UnixEpoch.AddDays(i)));
        Assert.Equal(50, history.All().Count);
    }
}

internal sealed class FakeUpdates : IDriverUpdates
{
    public List<DriverOffer> Offers = [];
    public DriverInstallOutcome Outcome = new(true, false, null);
    public Exception? SearchFails;
    public readonly List<string> Installs = [];
    public Action? OnInstall;
    public int Searches;

    public IReadOnlyList<DriverOffer> Search()
    {
        Searches++;
        if (SearchFails is not null) throw SearchFails;
        return Offers.ToList();
    }

    public DriverInstallOutcome Install(string updateId, Action<string> say)
    {
        Installs.Add(updateId);
        say("installing");
        OnInstall?.Invoke();
        return Outcome;
    }
}

internal sealed class FakeInstalled : IInstalledDrivers
{
    public List<InstalledDriver> Drivers = [];
    public IReadOnlyList<InstalledDriver> Read() => Drivers.ToList();
}

internal sealed class FakePnp : IPnpUtil
{
    public readonly List<string> Calls = [];
    public int ExportExit, AddExit, DeleteExit;
    public bool ExportWritesInf = true;
    public Action? OnDelete;

    public Task<CommandResult> ExportAsync(string infName, string folder, CancellationToken cancellationToken)
    {
        Calls.Add("export " + infName);
        if (ExportExit == 0 && ExportWritesInf) { Directory.CreateDirectory(folder); File.WriteAllText(Path.Combine(folder, "saved.inf"), "[Version]"); }
        return Task.FromResult(new CommandResult(ExportExit, "", ExportExit == 0 ? "" : "export failed"));
    }

    public Task<CommandResult> AddAsync(string infPath, CancellationToken cancellationToken) { Calls.Add("add " + Path.GetFileName(infPath)); return Task.FromResult(new CommandResult(AddExit, "", AddExit == 0 ? "" : "add failed")); }

    public Task<CommandResult> DeleteAsync(string infName, CancellationToken cancellationToken)
    {
        Calls.Add("delete " + infName);
        OnDelete?.Invoke();
        return Task.FromResult(new CommandResult(DeleteExit, "", DeleteExit == 0 ? "" : "delete failed"));
    }

    public Task<CommandResult> ScanAsync(CancellationToken cancellationToken) { Calls.Add("scan"); return Task.FromResult(new CommandResult(0, "", "")); }
}

public sealed class DriverInstallerTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "tw-driver-store-" + Guid.NewGuid().ToString("N"));
    private readonly FakeUpdates updates = new();
    private readonly FakeInstalled installed = new();
    private readonly FakePnp pnp = new();
    private readonly DriverBackups backups;
    private readonly DriverInstaller installer;
    private readonly List<string> said = [];

    public DriverInstallerTests()
    {
        backups = new DriverBackups(new ProtectedStore("drivers", root, enforce: false));
        installer = new DriverInstaller(updates, installed, backups, pnp, () => new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero));
        updates.Offers.Add(DriverData.Offer());
        installed.Drivers.Add(DriverData.Driver());
    }

    public void Dispose() { try { Directory.Delete(root, true); } catch (IOException) { } }

    private Task<DriverResult> Install(string id = DriverData.UpdateId, CancellationToken token = default) => installer.InstallAsync(id, said.Add, token);

    /// <summary>What the PC looks like after the update: a new vendor package serves the device.</summary>
    private void WindowsUpdateReplacesTheDriver() => updates.OnInstall = () => installed.Drivers[0] = DriverData.Driver("6.0.9700.1", "oem9.inf", new DateOnly(2026, 6, 1));

    [Fact]
    public async Task ADriverIsSavedThenInstalledThenReadBackAndTheWayBackIsKept()
    {
        WindowsUpdateReplacesTheDriver();

        var result = await Install();

        Assert.True(result.Ok, result.Error);
        Assert.True(result.Verified);
        Assert.Equal("6.0.9600.1", result.FromVersion);
        Assert.Equal("6.0.9700.1", result.ToVersion);
        Assert.Equal(["export oem5.inf"], pnp.Calls);
        Assert.Equal([DriverData.UpdateId], updates.Installs);
        var record = backups.Load(result.BackupId!)!;
        Assert.Equal("oem5.inf", record.InfName);
        Assert.Equal("oem9.inf", record.NewInfName);
        Assert.Equal("6.0.9600.1", record.Version);
        Assert.True(Directory.EnumerateFiles(backups.FolderFor(result.BackupId!), "*.inf").Any());
    }

    [Fact]
    public async Task AnIdThatIsNotAnUpdateIdNeverReachesWindowsUpdate()
    {
        var result = await Install(@"C:\evil.exe");
        Assert.False(result.Ok);
        Assert.Equal(0, updates.Searches);
        Assert.Empty(updates.Installs);
    }

    [Fact]
    public async Task OnlyWhatWindowsUpdateJustOfferedCanBeInstalled()
    {
        var result = await Install("11111111-1111-1111-1111-111111111111");
        Assert.False(result.Ok);
        Assert.Empty(updates.Installs);
        Assert.Empty(pnp.Calls);
    }

    [Fact]
    public async Task ADriverOlderThanTheOneInUseIsRefusedBeforeAnythingIsSavedOrChanged()
    {
        updates.Offers = [DriverData.Offer("Realtek - MEDIA - 6.0.9000.1")];

        var result = await Install();

        Assert.False(result.Ok);
        Assert.Contains("Older", result.Error);
        Assert.Empty(pnp.Calls);
        Assert.Empty(updates.Installs);
    }

    [Fact]
    public async Task ADriverWithLicenceTermsIsLeftForSettingsBecauseTidyWindowWillNotAcceptThemForYou()
    {
        updates.Offers = [DriverData.Offer(eula: false)];
        var result = await Install();
        Assert.False(result.Ok);
        Assert.Contains("licence", result.Error);
        Assert.Empty(updates.Installs);
    }

    [Fact]
    public async Task IfTheCurrentDriverCannotBeSavedNothingIsInstalled()
    {
        pnp.ExportExit = 1;

        var result = await Install();

        Assert.False(result.Ok);
        Assert.Contains("nothing was changed", result.Error);
        Assert.Empty(updates.Installs);
        Assert.False(Directory.Exists(root) && Directory.EnumerateDirectories(Path.Combine(root, "drivers"), "backup-*").Any());
    }

    [Fact]
    public async Task AnExportThatWritesNoDriverFilesCountsAsNotSaved()
    {
        pnp.ExportWritesInf = false;
        var result = await Install();
        Assert.False(result.Ok);
        Assert.Empty(updates.Installs);
    }

    [Fact]
    public async Task ADriverThatComesWithWindowsIsNotExportedBecauseWindowsKeepsIt()
    {
        installed.Drivers[0] = DriverData.Driver(inf: "netrasa.inf");
        WindowsUpdateReplacesTheDriver();

        var result = await Install();

        Assert.True(result.Ok, result.Error);
        Assert.Empty(pnp.Calls);
        Assert.Null(result.BackupId);
        Assert.Contains(said, x => x.Contains("comes with Windows"));
    }

    [Fact]
    public async Task AFailedInstallLeavesNoSavedCopyBehindAndSaysWhy()
    {
        updates.Outcome = new(false, false, "Windows Update could not install the driver. (0x80240016)");

        var result = await Install();

        Assert.False(result.Ok);
        Assert.Contains("0x80240016", result.Error);
        Assert.Equal(["export oem5.inf"], pnp.Calls);
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(root, "drivers"), "backup-*"));
    }

    [Fact]
    public async Task ASuccessThatTheRecordsDoNotShowIsReportedAsUnverifiedAndKeepsNoSavedCopy()
    {
        // Windows Update says it worked, but the device still has the same driver.
        var result = await Install();

        Assert.True(result.Ok);
        Assert.False(result.Verified);
        Assert.Null(result.BackupId);
        Assert.Contains("still show the old driver", result.Note);
    }

    [Fact]
    public async Task ARestartThatWindowsNeedsIsPassedOn()
    {
        updates.Outcome = new(true, true, null);
        WindowsUpdateReplacesTheDriver();
        var result = await Install();
        Assert.True(result.RebootRequired);
        Assert.Contains("restart", result.Note, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AStopBeforeAnythingChangesChangesNothing()
    {
        using var stop = new CancellationTokenSource();
        stop.Cancel();

        var result = await Install(token: stop.Token);

        Assert.False(result.Ok);
        Assert.Contains("Stopped", result.Error);
        Assert.Empty(updates.Installs);
        Assert.Empty(pnp.Calls);
    }

    [Fact]
    public async Task AStopAfterTheSaveButBeforeTheInstallStillChangesNothingOnThePc()
    {
        using var stop = new CancellationTokenSource();
        pnp.OnDelete = null;
        var token = stop.Token;
        updates.Offers = [DriverData.Offer()];
        // The save finishes, then the stop arrives: the installer notices before it hands anything to Windows Update.
        var exporting = new StopAfterExport(pnp, stop);
        var local = new DriverInstaller(updates, installed, backups, exporting);

        var result = await local.InstallAsync(DriverData.UpdateId, said.Add, token);

        Assert.False(result.Ok);
        Assert.Empty(updates.Installs);
    }

    private sealed class StopAfterExport(IPnpUtil inner, CancellationTokenSource stop) : IPnpUtil
    {
        public async Task<CommandResult> ExportAsync(string infName, string folder, CancellationToken cancellationToken) { var r = await inner.ExportAsync(infName, folder, cancellationToken); stop.Cancel(); return r; }
        public Task<CommandResult> AddAsync(string infPath, CancellationToken cancellationToken) => inner.AddAsync(infPath, cancellationToken);
        public Task<CommandResult> DeleteAsync(string infName, CancellationToken cancellationToken) => inner.DeleteAsync(infName, cancellationToken);
        public Task<CommandResult> ScanAsync(CancellationToken cancellationToken) => inner.ScanAsync(cancellationToken);
    }

    [Fact]
    public async Task WindowsUpdateBeingUnreachableIsASentenceNotACrash()
    {
        updates.SearchFails = new DriverUpdateException("Windows Update could not be reached.");
        var result = await Install();
        Assert.False(result.Ok);
        Assert.Equal("Windows Update could not be reached.", result.Error);
    }

    // ---- Putting the earlier driver back ----

    private async Task<string> InstallThenKeepTheBackup()
    {
        WindowsUpdateReplacesTheDriver();
        var result = await Install();
        Assert.True(result.Ok, result.Error);
        pnp.Calls.Clear();
        return result.BackupId!;
    }

    [Fact]
    public async Task TheSavedDriverIsPutBackBeforeTheUpdatesDriverIsRemoved()
    {
        var id = await InstallThenKeepTheBackup();
        // Removing the update's package lets Windows serve the device from the one that was put back.
        pnp.OnDelete = () => installed.Drivers[0] = DriverData.Driver("6.0.9600.1", "oem10.inf");

        var result = await installer.RestoreAsync(id, said.Add, default);

        Assert.True(result.Ok, result.Error);
        Assert.True(result.Verified);
        Assert.Equal(["add saved.inf", "delete oem9.inf", "scan"], pnp.Calls);
        Assert.Null(backups.Load(id));
        Assert.False(Directory.Exists(backups.FolderFor(id)));
    }

    [Fact]
    public async Task IfTheSavedDriverCannotBeStagedNothingIsRemoved()
    {
        var id = await InstallThenKeepTheBackup();
        pnp.AddExit = 1;

        var result = await installer.RestoreAsync(id, said.Add, default);

        Assert.False(result.Ok);
        Assert.Equal(["add saved.inf"], pnp.Calls);
        Assert.NotNull(backups.Load(id));
    }

    [Fact]
    public async Task IfWindowsWillNotRemoveTheUpdatesDriverTheFailureIsReportedAndTheSavedCopyIsKept()
    {
        var id = await InstallThenKeepTheBackup();
        pnp.DeleteExit = 1;

        var result = await installer.RestoreAsync(id, said.Add, default);

        Assert.False(result.Ok);
        Assert.Contains("would not remove", result.Error);
        Assert.NotNull(backups.Load(id));
    }

    [Fact]
    public async Task ADeviceThatHasAnotherDriverNowIsNotTouched()
    {
        var id = await InstallThenKeepTheBackup();
        installed.Drivers[0] = DriverData.Driver("7.0.0.0", "oem11.inf");

        var result = await installer.RestoreAsync(id, said.Add, default);

        Assert.False(result.Ok);
        Assert.Contains("changed again", result.Error);
        Assert.Empty(pnp.Calls);
    }

    [Fact]
    public async Task ARestoreThatDoesNotShowTheEarlierDriverIsReportedAsUnverifiedAndKeepsTheSavedCopy()
    {
        var id = await InstallThenKeepTheBackup();

        var result = await installer.RestoreAsync(id, said.Add, default);

        Assert.True(result.Ok);
        Assert.False(result.Verified);
        Assert.True(result.RebootRequired);
        Assert.NotNull(backups.Load(id));
    }

    [Fact]
    public async Task RestoringAnIdThatIsNotASavedDriverDoesNothing()
    {
        Assert.False((await installer.RestoreAsync(@"..\..\Windows", said.Add, default)).Ok);
        Assert.False((await installer.RestoreAsync(Guid.NewGuid().ToString("N"), said.Add, default)).Ok);
        Assert.Empty(pnp.Calls);
    }

    [Fact]
    public async Task ARecordThatDoesNotSayWhichDriverTheUpdateAddedCannotBeRestored()
    {
        // The new driver could not be told apart from the old one (same package), so there is nothing safe to remove.
        updates.OnInstall = () => installed.Drivers[0] = DriverData.Driver("6.0.9700.1", "oem5.inf", new DateOnly(2026, 6, 1));
        var install = await Install();
        Assert.True(install.Ok, install.Error);
        pnp.Calls.Clear();

        var result = await installer.RestoreAsync(install.BackupId!, said.Add, default);

        Assert.False(result.Ok);
        Assert.Contains("Roll Back", result.Error);
        Assert.Empty(pnp.Calls);
    }
}

public sealed class DriverPipeTests
{
    private static DriverSession Session(DriverInstaller installer, bool admin = true) =>
        new(pipe => DriverHost.RunAsync([DriverHost.Command, pipe], installer, () => admin, TimeSpan.FromSeconds(20)));

    private sealed class Lines : IProgress<string>
    {
        public readonly List<string> Items = [];
        public void Report(string value) { lock (Items) Items.Add(value); }
    }

    private static (DriverInstaller Installer, FakeUpdates Updates, string Root) Parts()
    {
        var root = Path.Combine(Path.GetTempPath(), "tw-driver-pipe-" + Guid.NewGuid().ToString("N"));
        var updates = new FakeUpdates { Offers = [DriverData.Offer()] };
        var installed = new FakeInstalled { Drivers = [DriverData.Driver(inf: "netrasa.inf")] };
        updates.OnInstall = () => installed.Drivers[0] = DriverData.Driver("6.0.9700.1", "oem9.inf", new DateOnly(2026, 6, 1));
        return (new DriverInstaller(updates, installed, new DriverBackups(new ProtectedStore("drivers", root, enforce: false)), new FakePnp()), updates, root);
    }

    [Fact]
    public async Task TheHelperInstallsByIdAndSendsItsWordsAndTheResultBack()
    {
        var (installer, updates, root) = Parts();
        try
        {
            using var session = Session(installer);
            await session.OpenAsync();
            var lines = new Lines();

            var result = await session.InstallAsync(DriverData.UpdateId, lines, default);

            Assert.True(result.Ok, result.Error);
            Assert.Equal("6.0.9700.1", result.ToVersion);
            Assert.Equal([DriverData.UpdateId], updates.Installs);
            Assert.Contains("installing", lines.Items);
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }

    [Fact]
    public async Task WithoutAdministratorRightsTheHelperDoesNothing()
    {
        var (installer, updates, root) = Parts();
        try
        {
            using var session = Session(installer, admin: false);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => session.OpenAsync());
            Assert.Empty(updates.Installs);
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }

    [Fact]
    public async Task ARequestWithoutAnIdIsAnsweredWithAFailureNotInstalled()
    {
        var (installer, updates, root) = Parts();
        try
        {
            using var session = Session(installer);
            await session.OpenAsync();
            var result = await session.InstallAsync("", new Lines(), default);
            Assert.False(result.Ok);
            Assert.Empty(updates.Installs);
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }

    [Fact]
    public async Task DecliningThePermissionPromptStopsCleanly()
    {
        using var session = new DriverSession(_ => throw new OperationCanceledException("declined"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.OpenAsync());
        Assert.False(session.IsOpen);
    }
}

public sealed class DriverManagerTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "tw-driver-mgr-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(folder, true); } catch (IOException) { } }

    private sealed class Changer(DriverResult result) : IDriverChanger
    {
        public int Opens, Installs, Restores;
        public Task OpenAsync(CancellationToken cancellationToken = default) { Opens++; return Task.CompletedTask; }
        public Task<DriverResult> InstallAsync(string updateId, IProgress<string> say, CancellationToken cancellationToken) { Installs++; return Task.FromResult(result); }
        public Task<DriverResult> RestoreAsync(string backupId, IProgress<string> say, CancellationToken cancellationToken) { Restores++; return Task.FromResult(result); }
    }

    private sealed class Quiet : IProgress<string> { public void Report(string value) { } }

    [Fact]
    public async Task ARefusedDowngradeNeverCostsAPermissionPrompt()
    {
        var changer = new Changer(new(true));
        var manager = new DriverManager(changer, new DriverHistory(folder));
        var older = DriverCompare.Assess(DriverData.Offer("A - 5.0.0.0"), [DriverData.Driver("6.0.0.0")]);

        var result = await manager.InstallAsync(older, new Quiet(), default);

        Assert.False(result.Ok);
        Assert.Equal(0, changer.Opens);
    }

    [Fact]
    public async Task AnInstallIsRecordedWithItsWayBackAndARestoreMarksIt()
    {
        var backup = Guid.NewGuid().ToString("N");
        var changer = new Changer(new(true, BackupId: backup, FromVersion: "1", ToVersion: "2"));
        var manager = new DriverManager(changer, new DriverHistory(folder), () => new DateTimeOffset(2026, 11, 5, 0, 0, 0, TimeSpan.Zero));
        var finding = DriverCompare.Assess(DriverData.Offer("A - 7.0.0.0"), [DriverData.Driver("6.0.0.0")]);

        await manager.InstallAsync(finding, new Quiet(), default);
        var change = Assert.Single(manager.History.All());
        Assert.Equal(backup, change.BackupId);
        Assert.Equal("Realtek Audio", change.DeviceName);

        await manager.RestoreAsync(change, new Quiet(), default);
        Assert.True(Assert.Single(manager.History.All()).Restored);
    }

    [Fact]
    public async Task AFailedInstallIsNotRecorded()
    {
        var manager = new DriverManager(new Changer(new(false, "no")), new DriverHistory(folder));
        var finding = DriverCompare.Assess(DriverData.Offer("A - 7.0.0.0"), [DriverData.Driver("6.0.0.0")]);
        await manager.InstallAsync(finding, new Quiet(), default);
        Assert.Empty(manager.History.All());
    }

    [Fact]
    public async Task AChangeWithNoSavedCopyCannotBeRestored()
    {
        var changer = new Changer(new(true));
        var manager = new DriverManager(changer, new DriverHistory(folder));
        var result = await manager.RestoreAsync(new(DriverData.UpdateId, "t", "d", null, null, DateTimeOffset.Now, null), new Quiet(), default);
        Assert.False(result.Ok);
        Assert.Equal(0, changer.Opens);
    }
}

public sealed class DriverScanTests
{
    private sealed class Runner(Func<CommandResult> answer) : ICommandRunner
    {
        public CommandSpec? Seen;
        public Task<CommandResult> RunAsync(CommandSpec command, CancellationToken cancellationToken = default) { Seen = command; return Task.FromResult(answer()); }
    }

    private sealed class Drivers(params InstalledDriver[] list) : IInstalledDrivers { public IReadOnlyList<InstalledDriver> Read() => list; }

    private static string Reply(params DriverOffer[] offers) => JsonSerializer.Serialize(new DriverScanReply(offers, null)) + "\r\n";

    private static DriverScanner Scanner(ICommandRunner runner, string? path, params InstalledDriver[] drivers) => new(runner, path, new Drivers(drivers), () => ["a notice"], () => "");

    private static string ExistingFile()
    {
        var file = Path.Combine(Path.GetTempPath(), "tw-fake-host-" + Guid.NewGuid().ToString("N") + ".exe");
        File.WriteAllText(file, "");
        return file;
    }

    [Fact]
    public async Task OffersAreSetBesideTheInstalledDriverAndOrderedProblemsFirstThenWorthInstallingThenTheRest()
    {
        var file = ExistingFile();
        try
        {
            var problem = DriverData.Offer("Problem - 7.0.0.0", problem: 28, hardware: @"pci\ven_1&dev_2", id: "11111111-1111-1111-1111-111111111111");
            var better = DriverData.Offer("Audio - 7.0.0.0");
            var older = DriverData.Offer("Other - 1.0.0.0", hardware: @"pci\ven_9&dev_9", id: "22222222-2222-2222-2222-222222222222");
            var runner = new Runner(() => new(0, Reply(older, better, problem), ""));
            var scanner = Scanner(runner, file, DriverData.Driver("6.0.0.0"), DriverData.Driver("9.0.0.0", key: "o", ids: @"PCI\VEN_9&DEV_9"));

            var scan = await scanner.ScanAsync(default);

            Assert.Null(scan.Error);
            Assert.Equal(["Problem - 7.0.0.0", "Audio - 7.0.0.0", "Other - 1.0.0.0"], scan.Findings.Select(x => x.Offer.Title));
            Assert.Equal(DriverVerdict.Older, scan.Findings[2].Verdict);
            Assert.Equal(["a notice"], scan.Notices);
            Assert.Equal([DriverScanHost.Command], runner.Seen!.Arguments);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task AnOfferWithAnIdThatIsNotAnUpdateIdIsDropped()
    {
        var file = ExistingFile();
        try
        {
            var runner = new Runner(() => new(0, Reply(DriverData.Offer(id: @"..\x")), ""));
            Assert.Empty((await Scanner(runner, file).ScanAsync(default)).Findings);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task AMissingSearchProgramIsASentence()
    {
        var scan = await Scanner(new Runner(() => throw new InvalidOperationException()), @"C:\nowhere\TidyWindow.CleanHost.exe").ScanAsync(default);
        Assert.Contains("missing", scan.Error);
        Assert.Empty(scan.Findings);
    }

    [Fact]
    public async Task AnErrorFromWindowsUpdateIsPassedOnWithTheNotices()
    {
        var file = ExistingFile();
        try
        {
            var runner = new Runner(() => new(0, JsonSerializer.Serialize(new DriverScanReply([], "Windows Update could not be reached.")), ""));
            var scan = await Scanner(runner, file).ScanAsync(default);
            Assert.Equal("Windows Update could not be reached.", scan.Error);
            Assert.Equal(["a notice"], scan.Notices);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task ASearchThatDiesWithoutAnswerOrAnswersInNonsenseIsReportedNotThrown()
    {
        var file = ExistingFile();
        try
        {
            Assert.Contains("exit code 7", (await Scanner(new Runner(() => new(7, "", "")), file).ScanAsync(default)).Error);
            Assert.Contains("could not be read", (await Scanner(new Runner(() => new(0, "hello\n", "")), file).ScanAsync(default)).Error);
            Assert.Contains("too long", (await Scanner(new Runner(() => throw new TimeoutException()), file).ScanAsync(default)).Error);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task CancellingTheSearchPassesTheTokenToTheProgramRunnerSoItCanBeEnded()
    {
        var file = ExistingFile();
        try
        {
            using var cancel = new CancellationTokenSource();
            var runner = new WaitingRunner();
            var scanning = Scanner(runner, file).ScanAsync(cancel.Token);
            await runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scanning);
        }
        finally { File.Delete(file); }
    }

    private sealed class WaitingRunner : ICommandRunner
    {
        public readonly TaskCompletionSource Started = new();
        public async Task<CommandResult> RunAsync(CommandSpec command, CancellationToken cancellationToken = default)
        {
            Started.SetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new(0, "", "");
        }
    }

    [Fact]
    public async Task TheSearchProgramPrintsOneLineOfJsonThatTheScannerCanRead()
    {
        var updates = new FakeUpdates { Offers = [DriverData.Offer()] };
        var output = new StringWriter();

        var exit = await DriverScanHost.RunAsync([DriverScanHost.Command], updates, output);

        Assert.Equal(0, exit);
        var reply = DriverScanHost.Parse(output.ToString())!;
        Assert.Equal(DriverData.UpdateId, Assert.Single(reply.Offers).UpdateId);
        Assert.Equal(new DateOnly(2026, 6, 1), reply.Offers[0].Date);
        Assert.DoesNotContain("\n", output.ToString().TrimEnd());
    }

    [Fact]
    public async Task TheSearchProgramTurnsEveryFailureIntoAnError()
    {
        var expected = new FakeUpdates { SearchFails = new DriverUpdateException("down") };
        var unexpected = new FakeUpdates { SearchFails = new InvalidCastException("odd") };
        var a = new StringWriter(); var b = new StringWriter();

        await DriverScanHost.RunAsync([DriverScanHost.Command], expected, a);
        await DriverScanHost.RunAsync([DriverScanHost.Command], unexpected, b);

        Assert.Equal("down", DriverScanHost.Parse(a.ToString())!.Error);
        Assert.Contains("odd", DriverScanHost.Parse(b.ToString())!.Error);
        Assert.Equal(64, await DriverScanHost.RunAsync(["--something-else"], expected, a));
    }
}

public sealed class DriverLiveTests
{
    [LiveFact]
    public void TheRealPcsInstalledDriversAreReadWithTheirVersionsDatesAndHardwareIds()
    {
        if (!OperatingSystem.IsWindows()) return;
        var drivers = new WindowsInstalledDrivers().Read();
        Assert.NotEmpty(drivers);
        Assert.Contains(drivers, x => x.HardwareIds.Count > 0 && x.Version.Length > 0 && x.Date is not null && x.InfName.EndsWith(".inf", StringComparison.OrdinalIgnoreCase));
        Assert.All(drivers, x => Assert.DoesNotContain('@', x.DeviceName));
        // The class comes from the class key, not the device: without that, no graphics driver would ever be recognised.
        Assert.True(drivers.Count(x => x.DeviceClass.Length > 0) > drivers.Count / 2);
    }

    [LiveFact]
    public void TheRealPcsPolicyAndMakerCanBeRead()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.NotNull(WindowsDriverPolicy.Notices());
        Assert.NotNull(WindowsDriverPolicy.ComputerMaker());
    }
}

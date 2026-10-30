using System.Text;
using TidyWindow.Next.Core;
using Xunit;
using Xunit.Abstractions;

namespace TidyWindow.Next.Tests;

public sealed class InstallationTests(ITestOutputHelper output)
{
    private static ManagerStatus Ready(ManagerKind kind) => new(ManagerDefinition.All.Single(x => x.Kind == kind), ManagerHealth.Ready, "1", "manager.exe", "Ready");
    private static ManagedPackage App(ManagerKind kind = ManagerKind.Chocolatey) => new(kind, "vendor.app", "App", "1.0", "2.0");
    private static InstallationService Create(FakeRunner runner) => new(new PackageService(runner, new FakeLocator()), runner, new FakeLocator()) { VerifyDelays = [] };

    [Fact]
    public void UpdatesPreserveExactIdsVersionsAndPins()
    {
        const string table = "Name              Id               Version     Available\n-----------------------------------------------------------\nGoogle Play Games Google.PlayGames 26.10.305.0 157.0.8089.0\n1 upgrades available.\n";
        var winget = Assert.Single(InstallationOutput.ParseUpdates(ManagerKind.Winget, table));
        Assert.Equal("Google.PlayGames", winget.Id);
        Assert.Equal("26.10.305.0", winget.Version);
        Assert.Equal("157.0.8089.0", winget.AvailableVersion);
        var choco = Assert.Single(InstallationOutput.ParseUpdates(ManagerKind.Chocolatey, "vendor.app|1.0|2.0|true\n"));
        Assert.True(choco.Held);
    }

    [Fact]
    public void UpdatesAreReadWhenEveryNameIsShortAndTheHeaderColumnsSitOneSpaceApart()
    {
        // Real WinGet output with a single short package: "Name" is the widest cell in its column, so "Name Id" has one space.
        const string table = "Name Id      Version  Available Source\r\n--------------------------------------\r\nGit  Git.Git 2.56.0.3 2.56.0.4  winget\r\n1 upgrades available.\r\n1 package(s) have version numbers that cannot be determined. Use --include-unknown to see all results.\r\n";
        var git = Assert.Single(InstallationOutput.ParseUpdates(ManagerKind.Winget, table));
        Assert.Equal("Git.Git", git.Id);
        Assert.Equal("Git", git.Name);
        Assert.Equal("2.56.0.3", git.Version);
        Assert.Equal("2.56.0.4", git.AvailableVersion);
    }

    [Fact]
    public void ScoopStructuredInventoryPreservesCustomManifestsAndFailedGlobalHeldInstalls()
    {
        const string json = "Installed apps:\nTIDYWINDOW_JSON:[{\"Name\":\"python\",\"Version\":\"3.14\",\"Source\":\"C:\\\\custom\\\\python.json\",\"Info\":\"Global install, Held package\"},{\"Name\":\"mingw\",\"Version\":null,\"Source\":null,\"Info\":\"Install failed\"}]";
        var apps = InstallationOutput.ParseScoop(json, false);
        Assert.Equal(2, apps.Count);
        Assert.True(apps[0].Global); Assert.True(apps[0].Held);
        Assert.Equal("python", apps[0].Id);
        Assert.Equal("", apps[1].Version);
        Assert.Equal("Install failed", apps[1].Note);
        Assert.Throws<FormatException>(() => InstallationOutput.ParseScoop("broken config", false));
    }

    [Theory]
    [InlineData(ManagerKind.Winget, PackageAction.Update)]
    [InlineData(ManagerKind.Winget, PackageAction.Uninstall)]
    [InlineData(ManagerKind.Chocolatey, PackageAction.Update)]
    [InlineData(ManagerKind.Chocolatey, PackageAction.Uninstall)]
    [InlineData(ManagerKind.Scoop, PackageAction.Update)]
    [InlineData(ManagerKind.Scoop, PackageAction.Uninstall)]
    public void ActionsUseOneExactPackageAndNeverForceOrPurge(ManagerKind kind, PackageAction action)
    {
        var command = Create(new()).ActionCommand(Ready(kind), App(kind), action);
        var arguments = kind == ManagerKind.Scoop ? Encoding.Unicode.GetString(Convert.FromBase64String(command.Arguments[^1])) : string.Join(" ", command.Arguments);
        Assert.Contains("vendor.app", arguments);
        Assert.DoesNotContain("--all", arguments); Assert.DoesNotContain("--force", arguments); Assert.DoesNotContain("--purge", arguments);
        Assert.Equal(kind == ManagerKind.Chocolatey, command.Elevate);
        if (kind == ManagerKind.Winget) { Assert.Contains("--exact", arguments); Assert.Contains("--source winget", arguments); }
        if (kind == ManagerKind.Chocolatey && action == PackageAction.Uninstall) Assert.DoesNotContain("--remove-dependencies", arguments);
    }

    [Fact]
    public void GlobalScoopActionRetainsScopeAndRequestsElevation()
    {
        var command = Create(new()).ActionCommand(Ready(ManagerKind.Scoop), App(ManagerKind.Scoop) with { Global = true }, PackageAction.Uninstall);
        Assert.True(command.Elevate);
        Assert.Contains("'--global'", Encoding.Unicode.GetString(Convert.FromBase64String(command.Arguments[^1])));
    }

    [Theory]
    [InlineData("all")]
    [InlineData("--all")]
    [InlineData("*")]
    [InlineData("foo;calc")]
    [InlineData("Vendor.App…")]
    public void RejectsBulkAndUnsafeIdentifiers(string id)
    {
        var service = Create(new());
        Assert.Throws<ArgumentException>(() => service.ActionCommand(Ready(ManagerKind.Chocolatey), App() with { Id = id }, PackageAction.Uninstall));
    }

    [Fact]
    public void HeldPackagesAndWrongManagersCannotUpdate()
    {
        var service = Create(new());
        Assert.Throws<InvalidOperationException>(() => service.ActionCommand(Ready(ManagerKind.Chocolatey), App() with { Held = true }, PackageAction.Update));
        Assert.Throws<InvalidOperationException>(() => service.ActionCommand(Ready(ManagerKind.Winget), App(), PackageAction.Uninstall));
    }

    [Fact]
    public async Task UpdateCheckFailureKeepsInstalledAppsAndReportsError()
    {
        var runner = new FakeRunner { Handler = c => c.Arguments[0] == "list" ? new(0, "vendor.app|1.0", "") : new(1, "", "Offline") };
        var result = await Create(runner).ReadAsync(Ready(ManagerKind.Chocolatey));
        Assert.Single(result.Packages); Assert.Null(result.Error); Assert.Contains("Offline", result.UpdateError);
    }

    [Fact]
    public async Task ChocolateyEnhancedExitTwoReturnsUpdatesRatherThanFailure()
    {
        var runner = new FakeRunner { Handler = c => c.Arguments[0] == "list" ? new(0, "vendor.app|1.0", "") : new(2, "vendor.app|1.0|2.0|false", "") };
        var result = await Create(runner).ReadAsync(Ready(ManagerKind.Chocolatey));
        Assert.Null(result.UpdateError); Assert.Equal("2.0", Assert.Single(result.Packages).AvailableVersion);
    }

    [Theory]
    [InlineData(PackageAction.Update)]
    [InlineData(PackageAction.Uninstall)]
    public async Task CompletionRequiresFreshInventoryEvidence(PackageAction action)
    {
        var changed = false;
        var runner = new FakeRunner { Handler = c =>
        {
            if (c.Arguments[0] == "list") return new(0, changed ? action == PackageAction.Uninstall ? "0 packages installed." : "vendor.app|2.0" : "vendor.app|1.0", "");
            changed = true; return new(0, "Done", "");
        } };
        var result = await Create(runner).RunAsync(Ready(ManagerKind.Chocolatey), App(), action);
        Assert.Equal(ActionOutcome.Completed, result.Outcome);
        Assert.Equal(3, runner.Commands.Count);
    }

    [Theory]
    [InlineData(PackageAction.Update)]
    [InlineData(PackageAction.Uninstall)]
    public async Task ZeroExitWithUnchangedInventoryIsNotReportedAsSuccess(PackageAction action)
    {
        var runner = new FakeRunner { Handler = c => new(0, c.Arguments[0] == "list" ? "vendor.app|1.0" : "Done", "") };
        Assert.Equal(ActionOutcome.Unverified, (await Create(runner).RunAsync(Ready(ManagerKind.Chocolatey), App(), action)).Outcome);
    }

    [Fact]
    public async Task MissingPackageDoesNotStartAnUninstaller()
    {
        var runner = new FakeRunner { Handler = _ => new(0, "0 packages installed.", "") };
        Assert.Equal(ActionOutcome.Unverified, (await Create(runner).RunAsync(Ready(ManagerKind.Chocolatey), App(), PackageAction.Uninstall)).Outcome);
        Assert.Equal("list", Assert.Single(runner.Commands).Arguments[0]);
    }

    [Theory]
    [InlineData(-1, ActionOutcome.Failed)]
    [InlineData(3010, ActionOutcome.RestartRequired)]
    [InlineData(1641, ActionOutcome.RestartRequired)]
    public async Task DeclinedElevationAndRestartCodesAreHandled(int exit, ActionOutcome expected)
    {
        var runner = new FakeRunner { Handler = c => new(c.Arguments[0] == "list" ? 0 : exit, c.Arguments[0] == "list" ? "vendor.app|1.0" : "", "") };
        Assert.Equal(expected, (await Create(runner).RunAsync(Ready(ManagerKind.Chocolatey), App(), PackageAction.Uninstall)).Outcome);
    }

    // WinGet's "install technology is different" code, the real failure for Microsoft Edge.
    private const int TechnologyMismatch = -1978335090;

    private static FakeRunner EdgeLikeRunner(Func<string> available) => new()
    {
        Handler = c => c.Arguments[0] switch
        {
            "list" => new(0, "vendor.app|1.0", ""),
            "outdated" => new(2, $"vendor.app|1.0|{available()}|false", ""),
            _ => new(TechnologyMismatch, "A newer version was found, but the install technology is different from the current version installed.", "")
        }
    };

    [Fact]
    public async Task UpdateTheManagerCannotApplyStopsBeingOfferedButReturnsWithANewerVersion()
    {
        var offered = "2.0";
        var service = new InstallationService(new PackageService(EdgeLikeRunner(() => offered), new FakeLocator()), EdgeLikeRunner(() => offered), new FakeLocator(), new SkippedUpdates()) { VerifyDelays = [] };
        var manager = Ready(ManagerKind.Chocolatey);
        Assert.Equal("2.0", Assert.Single((await service.ReadAsync(manager)).Packages).AvailableVersion);

        var result = await service.RunAsync(manager, App(), PackageAction.Update);
        Assert.Equal(ActionOutcome.Failed, result.Outcome);
        Assert.Contains("different way", result.Detail);
        Assert.Contains("won’t offer version 2.0 again", result.Detail);

        var skipped = Assert.Single((await service.ReadAsync(manager)).Packages);
        Assert.Null(skipped.AvailableVersion);
        Assert.Contains("2.0", skipped.Note);

        offered = "3.0";
        Assert.Equal("3.0", Assert.Single((await service.ReadAsync(manager)).Packages).AvailableVersion);
    }

    [Fact]
    public void SkippedUpdatesSurviveARestart()
    {
        var file = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "skipped.json");
        try
        {
            new SkippedUpdates(file).Skip(App(), "2.0");
            var reloaded = new SkippedUpdates(file);
            Assert.True(reloaded.IsSkipped(App(), "2.0"));
            Assert.False(reloaded.IsSkipped(App(), "3.0"));
            Assert.False(reloaded.IsSkipped(App() with { Id = "other.app" }, "2.0"));
        }
        finally { Directory.Delete(Path.GetDirectoryName(file)!, true); }
    }

    [Fact]
    public void OtherFailuresAreNotRememberedAsUnupdatable()
    {
        Assert.True(InstallFailure.UpdateCannotBeApplied(TechnologyMismatch));
        Assert.False(InstallFailure.UpdateCannotBeApplied(-1978335226));
        Assert.False(InstallFailure.UpdateCannotBeApplied(1));
    }

    [Fact]
    public async Task ManagementSharesTheInstallationGateAndReleasesItAfterFailure()
    {
        var runner = new FakeRunner { Handler = _ => throw new IOException("offline") };
        var locator = new FakeLocator();
        var packages = new PackageService(runner, locator);
        var service = new InstallationService(packages, runner, locator);
        await service.RunAsync(Ready(ManagerKind.Chocolatey), App(), PackageAction.Uninstall);
        var pending = new TaskCompletionSource<CommandResult>();
        runner.AsyncHandler = _ => pending.Task;
        var install = packages.InstallAsync(Ready(ManagerKind.Chocolatey), new(ManagerKind.Chocolatey, "vendor.app", "App", "1", "source"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RunAsync(Ready(ManagerKind.Chocolatey), App(), PackageAction.Uninstall));
        pending.SetResult(new(0, "", "")); await install;
        runner.AsyncHandler = null;
        await service.RunAsync(Ready(ManagerKind.Chocolatey), App(), PackageAction.Uninstall);
    }

    [Fact]
    public async Task LiveInstalledPageReadsRealPackagesAndUpdatesWhenEnabled()
    {
        if (Environment.GetEnvironmentVariable("TIDYWINDOW_LIVE_TESTS") != "1") return;
        var runner = new ProcessRunner(); var locator = new ExecutableLocator();
        var packages = new PackageService(runner, locator);
        var service = new InstallationService(packages, runner, locator);
        foreach (var status in await packages.DetectAsync())
        {
            if (status.Health == ManagerHealth.Missing) continue;
            Assert.Equal(ManagerHealth.Ready, status.Health);
            var result = await service.ReadAsync(status);
            output.WriteLine($"{status.Definition.Name}: {result.Packages.Count} installed, {result.Packages.Count(x => x.AvailableVersion is not null)} updates. {result.Error} {result.UpdateError}");
            Assert.Null(result.Error); Assert.Null(result.UpdateError); Assert.NotEmpty(result.Packages);
            Assert.All(result.Packages, app =>
            {
                Assert.Contains(app.Id, service.ActionCommand(status, app, PackageAction.Uninstall).FileName + " " + string.Join(" ", app.Manager == ManagerKind.Scoop ? [Encoding.Unicode.GetString(Convert.FromBase64String(service.ActionCommand(status, app, PackageAction.Uninstall).Arguments[^1]))] : service.ActionCommand(status, app, PackageAction.Uninstall).Arguments));
            });
        }
    }

    private sealed class FakeLocator : IExecutableLocator
    {
        public string? Find(ManagerKind manager) => "manager.exe";
        public string PowerShell => "powershell.exe";
    }
    private sealed class FakeRunner : ICommandRunner
    {
        public List<CommandSpec> Commands { get; } = [];
        public Func<CommandSpec, CommandResult> Handler { get; init; } = _ => new(0, "", "");
        public Func<CommandSpec, Task<CommandResult>>? AsyncHandler { get; set; }
        public Task<CommandResult> RunAsync(CommandSpec command, CancellationToken cancellationToken = default)
        { Commands.Add(command); return AsyncHandler?.Invoke(command) ?? Task.FromResult(Handler(command)); }
    }
}

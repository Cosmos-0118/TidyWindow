using TidyWindow.Next.Core;
using Xunit;

namespace TidyWindow.Next.Tests;

public sealed class InstallRobustnessTests
{
    // The real WinGet output when MSYS2 was already installed outside WinGet.
    private const string Msys2Output = "Found MSYS2 Installer [MSYS2.MSYS2] Version 20260927\nSuccessfully verified installer hash\nStarting package install...\nInstaller failed with exit code: 1";
    private static readonly Package Msys2 = new(ManagerKind.Chocolatey, "msys2", "MSYS2 Installer", "20260927", "source");

    [Theory]
    [InlineData("MSYS2", "MSYS2 Installer")]
    [InlineData("MSYS2 Installer", "MSYS2")]
    [InlineData("Notepad++ Setup", "Notepad++")]
    [InlineData("Inkscape - Installer", "Inkscape")]
    public void GenericInstallerWordsDoNotHideAnInstalledApp(string windowsName, string packageName)
    {
        var inventory = new InstalledInventory([new(null, null, windowsName, "1.0")]);
        Assert.NotNull(inventory.Find(new(ManagerKind.Winget, "Vendor.App", packageName, "2", "source")));
    }

    [Theory]
    [InlineData("Git", "GitHub Installer")]
    [InlineData("Git", "Git Extras Setup")]
    [InlineData("Setup", "Installer")]
    public void OnlyTheTrailingGenericWordIsIgnored(string windowsName, string packageName)
    {
        var inventory = new InstalledInventory([new(null, null, windowsName, "1")]);
        Assert.Null(inventory.Find(new(ManagerKind.Winget, "Vendor.App", packageName, "2", "source")));
    }

    [Fact]
    public void ARealWinGetFailureIsExplainedInPlainLanguage()
    {
        var reason = InstallFailure.Explain(new(-1978335226, Msys2Output, ""));
        Assert.Contains("reported error 1", reason);
        Assert.Contains("unspecified error", reason);
    }

    [Theory]
    [InlineData(-1978334971, "out of space")]
    [InlineData(-1978334969, "internet connection")]
    [InlineData(-1978334961, "policy")]
    public void DocumentedWinGetCodesGetAReason(int code, string expected) =>
        Assert.Contains(expected, InstallFailure.Explain(new(code, "", "")));

    [Fact]
    public void UnknownCodesAreNotInvented() => Assert.Null(InstallFailure.Explain(new(12345, "some output", "")));

    [Fact]
    public void AlreadyInstalledAndRestartCodesAreNotFailures()
    {
        Assert.Equal(ActionOutcome.Completed, InstallFailure.NotAFailure(-1978334963));
        Assert.Equal(ActionOutcome.Completed, InstallFailure.NotAFailure(-1978334962));
        Assert.Equal(ActionOutcome.RestartRequired, InstallFailure.NotAFailure(-1978334967));
        Assert.Null(InstallFailure.NotAFailure(-1978335226));
        Assert.Null(InstallFailure.NotAFailure(1));
    }

    [Fact]
    public async Task AppAlreadyOnWindowsIsNeverInstalledOver()
    {
        var runner = new Runner(); var locator = new Locator();
        var result = await Executor(runner, locator, () => new InstalledInventory([new(null, null, "MSYS2", "20260611")])).ExecuteAsync(PackageOperation.Install(Msys2), default);
        Assert.Equal(ActionOutcome.Completed, result.Outcome);
        Assert.Contains("already installed", result.Message);
        Assert.Contains("20260611", result.Detail);
        Assert.Empty(runner.Installs);
    }

    [Fact]
    public async Task FailedInstallKeepsTheReasonAndStaysAFailureWhenNothingAppeared()
    {
        var runner = new Runner { InstallResult = new(-1978335226, Msys2Output, "") };
        var result = await Executor(runner, new Locator(), () => new InstalledInventory([])).ExecuteAsync(PackageOperation.Install(Msys2), default);
        Assert.Equal(ActionOutcome.Failed, result.Outcome);
        Assert.Contains("reported error 1", result.Detail);
        Assert.Contains("Exit code: -1978335226", result.Detail);
        Assert.Single(runner.Installs);
    }

    [Fact]
    public async Task InstallerErrorWithTheAppNowPresentIsAWarningNotAnError()
    {
        var runner = new Runner { InstallResult = new(1603, "", "") };
        var reads = 0;
        var executor = Executor(runner, new Locator(), () => new InstalledInventory(++reads == 1 ? [] : [new(null, null, "MSYS2", "1")]));
        var result = await executor.ExecuteAsync(PackageOperation.Install(Msys2), default);
        Assert.Equal(ActionOutcome.Unverified, result.Outcome);
        Assert.Contains("installer reported an error", result.Message);
    }

    [Fact]
    public async Task WinGetAlreadyInstalledCodeCompletesInsteadOfFailing()
    {
        var runner = new Runner { InstallResult = new(-1978334963, "A different version is already installed", "") };
        var result = await Executor(runner, new Locator(), () => new InstalledInventory([])).ExecuteAsync(PackageOperation.Install(Msys2), default);
        Assert.Equal(ActionOutcome.Completed, result.Outcome);
    }

    [Fact]
    public async Task UnreadableWindowsInventoryDoesNotBlockAnInstall()
    {
        var runner = new Runner();
        var result = await Executor(runner, new Locator(), () => throw new UnauthorizedAccessException()).ExecuteAsync(PackageOperation.Install(Msys2), default);
        Assert.Single(runner.Installs);
        Assert.NotEqual(ActionOutcome.Failed, result.Outcome);
    }

    [Theory]
    [InlineData("No installed package found matching input criteria.\r\n1 package(s) have version numbers that cannot be determined. Use --include-unknown to see all results.")]
    [InlineData("No available upgrade found.\nNo newer package versions are available from the configured sources.")]
    [InlineData("")]
    public void WinGetSayingNothingNeedsUpdatingIsNotAnError(string output) =>
        Assert.Empty(InstallationOutput.ParseUpdates(ManagerKind.Winget, output));

    [Fact]
    public void GenuinelyUnreadableUpdateOutputStillFailsLoudly() =>
        Assert.Throws<FormatException>(() => InstallationOutput.ParseUpdates(ManagerKind.Winget, "Something unexpected happened"));

    [Fact]
    public async Task UninstallThatFinishesRegisteringALittleLaterIsStillVerified()
    {
        // Google Play Games case: the command succeeds, but the app stays listed for a moment.
        var lists = 0;
        var runner = new Runner { ListOutput = () => ++lists <= 2 ? "game|1.0" : "other|1" };
        var packages = new PackageService(runner, new Locator());
        var service = new InstallationService(packages, runner, new Locator()) { VerifyDelays = [TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero] };
        var manager = new ManagerStatus(ManagerDefinition.All[1], ManagerHealth.Ready, "2", "choco.exe", "Ready");
        var result = await service.RunAsync(manager, new(ManagerKind.Chocolatey, "game", "Game", "1.0"), PackageAction.Uninstall);
        Assert.Equal(ActionOutcome.Completed, result.Outcome);
        Assert.True(lists >= 3);
    }

    [Fact]
    public async Task UninstallThatNeverSettlesIsReportedUnverifiedAfterABoundedWait()
    {
        var lists = 0;
        var runner = new Runner { ListOutput = () => { lists++; return "game|1.0"; } };
        var packages = new PackageService(runner, new Locator());
        var service = new InstallationService(packages, runner, new Locator()) { VerifyDelays = [TimeSpan.Zero, TimeSpan.Zero] };
        var manager = new ManagerStatus(ManagerDefinition.All[1], ManagerHealth.Ready, "2", "choco.exe", "Ready");
        var result = await service.RunAsync(manager, new(ManagerKind.Chocolatey, "game", "Game", "1.0"), PackageAction.Uninstall);
        Assert.Equal(ActionOutcome.Unverified, result.Outcome);
        Assert.Equal(1 + 1 + 2, lists); // pre-check, first verify read, then one re-read per delay
    }

    private static PackageOperationExecutor Executor(Runner runner, Locator locator, Func<InstalledInventory> windows)
    {
        var packages = new PackageService(runner, locator);
        return new(packages, new(packages, runner, locator), locator, windows);
    }

    private sealed class Locator : IExecutableLocator
    {
        public string? Find(ManagerKind manager) => "choco.exe";
        public string PowerShell => "powershell.exe";
    }

    private sealed class Runner : ICommandRunner
    {
        public CommandResult InstallResult { get; init; } = new(0, "Installed", "");
        public Func<string>? ListOutput { get; init; }
        public List<string> Installs { get; } = [];
        private bool installed;
        public Task<CommandResult> RunAsync(CommandSpec command, CancellationToken cancellationToken = default)
        {
            if (command.Arguments[0] == "list") return Task.FromResult(new CommandResult(0, ListOutput?.Invoke() ?? (installed ? "msys2|20260927" : "other|1"), ""));
            if (command.Arguments[0] == "uninstall") return Task.FromResult(new CommandResult(0, "Successfully uninstalled", ""));
            Installs.Add(command.Arguments[1]);
            installed = InstallResult.ExitCode == 0;
            return Task.FromResult(InstallResult);
        }
    }
}

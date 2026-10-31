using System.Text;
using TidyWindow.Next.Core;
using Xunit;
using Xunit.Abstractions;

namespace TidyWindow.Next.Tests;

public sealed class ManagerRecoveryTests(ITestOutputHelper output)
{
    [Fact]
    public async Task WorkingManagerIsVerifiedWithoutRunningAnyRepair()
    {
        using var fixture = new Fixture();
        var result = await fixture.Recovery.RecoverAsync(ManagerKind.Chocolatey);
        Assert.True(result.Success); Assert.False(result.Changed); Assert.Null(result.LogDirectory);
        Assert.Equal(0, fixture.Runner.Repairs);
    }

    [Fact]
    public async Task MissingManagerInstallsThenVerifiesRealState()
    {
        using var fixture = new Fixture();
        fixture.Locator.Path = null;
        var result = await fixture.Recovery.RecoverAsync(ManagerKind.Chocolatey);
        Assert.True(result.Success); Assert.True(result.Changed);
        Assert.Equal(ManagerHealth.Ready, result.Status.Health);
        Assert.Equal(1, fixture.Runner.Repairs);
    }

    [Fact]
    public async Task BrokenManagerPreservesPackageRecordsBeforeRepair()
    {
        using var fixture = new Fixture();
        fixture.Runner.Broken = true;
        var records = Path.Combine(fixture.Root, "lib", "git"); Directory.CreateDirectory(records);
        var record = Path.Combine(records, "git.nuspec"); File.WriteAllText(record, "<package>existing record</package>");
        var config = Path.Combine(fixture.Root, "config"); Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(config, "chocolatey.config"), "<chocolatey />");
        var result = await fixture.Recovery.RecoverAsync(ManagerKind.Chocolatey);
        Assert.True(result.Success);
        Assert.Equal(File.ReadAllText(record), File.ReadAllText(Path.Combine(result.LogDirectory!, "records", "git", "git.nuspec")));
        Assert.Equal("<chocolatey />", File.ReadAllText(Path.Combine(result.LogDirectory!, "config-chocolatey.config")));
    }

    [Fact]
    public async Task SuccessfulExitAloneCannotReportSuccessfulRepair()
    {
        using var fixture = new Fixture(); fixture.Runner.Broken = true; fixture.Runner.RepairWorks = false;
        var result = await fixture.Recovery.RecoverAsync(ManagerKind.Chocolatey);
        Assert.False(result.Success); Assert.Equal(ManagerHealth.Unavailable, result.Status.Health);
    }

    [Fact]
    public async Task OfflineSourceDoesNotTriggerReinstallation()
    {
        using var fixture = new Fixture(); fixture.Runner.SourceError = "Network unreachable";
        var result = await fixture.Recovery.RecoverAsync(ManagerKind.Chocolatey);
        Assert.False(result.Success); Assert.Equal(ManagerHealth.ConnectionIssue, result.Status.Health);
        Assert.Equal(0, fixture.Runner.Repairs);
    }

    [Fact]
    public async Task PolicyRestrictionDoesNotTriggerReinstallation()
    {
        using var fixture = new Fixture(); fixture.Runner.SourceError = "Operation blocked by Group Policy";
        var result = await fixture.Recovery.RecoverAsync(ManagerKind.Chocolatey);
        Assert.False(result.Success); Assert.Equal(ManagerHealth.Restricted, result.Status.Health);
        Assert.Equal(0, fixture.Runner.Repairs);
    }

    [Fact]
    public async Task CorruptInventoryCannotBeReportedHealthy()
    {
        using var fixture = new Fixture(); fixture.Runner.Inventory = "Unrecognized output from a broken configuration";
        var health = new ManagerHealthService(fixture.Packages, fixture.Runner, fixture.Locator);
        var result = await health.CheckAsync(ManagerKind.Chocolatey);
        Assert.Equal(ManagerHealth.Unavailable, result.Health);
        Assert.Equal(ManagerProblem.Configuration, result.Problem);
    }

    [Theory]
    [InlineData(ManagerKind.Winget, ManagerProblem.Executable, false)]
    [InlineData(ManagerKind.Winget, ManagerProblem.Source, true)]
    [InlineData(ManagerKind.Chocolatey, ManagerProblem.NotInstalled, true)]
    [InlineData(ManagerKind.Scoop, ManagerProblem.Executable, false)]
    public async Task RecoveryScriptsParseAndElevateOnlyRequiredOperations(ManagerKind kind, ManagerProblem problem, bool elevate)
    {
        using var fixture = new Fixture();
        var definition = ManagerDefinition.All.Single(x => x.Kind == kind);
        var command = fixture.Recovery.CreateCommand(new(definition, ManagerHealth.Unavailable, "", "manager.exe", "broken", problem), new(fixture.Root, ""), fixture.Root);
        Assert.Equal(elevate, command.Elevate);
        var script = Encoding.Unicode.GetString(Convert.FromBase64String(command.Arguments[^1]));
        Assert.DoesNotContain("Remove-Item", script);
        Assert.True(command.Arguments.Sum(x => x.Length + 3) < 30000, "Recovery command exceeds the Windows command-line limit.");
        var scriptFile = Path.Combine(fixture.Root, "parse-target.ps1"); File.WriteAllText(scriptFile, script);
        var parseScript = "$tokens=$null; $errors=$null; [Management.Automation.Language.Parser]::ParseFile('" + scriptFile.Replace("'", "''") + "',[ref]$tokens,[ref]$errors) | Out-Null; if($errors.Count) { $errors | ForEach-Object { [Console]::Error.WriteLine($_.Message) }; exit 1 }";
        var parsed = await new ProcessRunner().RunAsync(new(new ExecutableLocator().PowerShell,
            ["-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(parseScript))], TimeSpan.FromSeconds(10)));
        Assert.True(parsed.ExitCode == 0, parsed.Error);
    }

    [Fact]
    public void RootDirectoryCannotBeUsedAsAnAutomaticRepairTarget()
    {
        Assert.Throws<InvalidOperationException>(() => ManagerLocations.ValidateRoot(Path.GetPathRoot(Environment.SystemDirectory)!));
        Assert.Throws<InvalidOperationException>(() => ManagerLocations.ValidateRoot(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
        Assert.Throws<InvalidOperationException>(() => ManagerLocations.ValidateRoot("relative/path"));
    }

    [Fact]
    public async Task LiveManagersAreHealthyAndRecoveryIsIdempotentWhenExplicitlyEnabled()
    {
        if (Environment.GetEnvironmentVariable("TIDYWINDOW_LIVE_TESTS") != "1") return;
        var runner = new ProcessRunner(); var locator = new ExecutableLocator(); var packages = new PackageService(runner, locator);
        var health = new ManagerHealthService(packages, runner, locator);
        var recovery = new ManagerRecoveryService(packages, runner, locator);
        foreach (var manager in ManagerDefinition.All)
        {
            var status = await health.CheckAsync(manager.Kind, true);
            output.WriteLine($"{manager.Name}: {status.Health} {status.Version}. {status.Detail}");
            Assert.Equal(ManagerHealth.Ready, status.Health);
            var result = await recovery.RecoverAsync(manager.Kind);
            Assert.True(result.Success, result.Status.Detail);
            Assert.False(result.Changed);
        }
    }

    private sealed class Fixture : IDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "TidyWindow-Recovery-Tests", Guid.NewGuid().ToString("N"));
        public string Root => Path.Combine(DirectoryPath, "manager");
        public Locator Locator { get; } = new();
        public FakeRunner Runner { get; }
        public PackageService Packages { get; }
        public ManagerRecoveryService Recovery { get; }
        public Fixture()
        {
            Directory.CreateDirectory(Root);
            Runner = new(Locator); Packages = new(Runner, Locator);
            Recovery = new(Packages, Runner, Locator, _ => new(Root, ""), Path.Combine(DirectoryPath, "jobs"));
        }
        public void Dispose()
        {
            var expected = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "TidyWindow-Recovery-Tests")) + Path.DirectorySeparatorChar;
            if (Path.GetFullPath(DirectoryPath).StartsWith(expected, StringComparison.OrdinalIgnoreCase)) Directory.Delete(DirectoryPath, true);
        }
    }
    private sealed class Locator : IExecutableLocator
    {
        public string? Path { get; set; } = "choco.exe";
        public string? Find(ManagerKind manager) => Path;
        public string PowerShell => new ExecutableLocator().PowerShell;
    }
    private sealed class FakeRunner(Locator locator) : ICommandRunner
    {
        public bool Broken { get; set; }
        public bool RepairWorks { get; set; } = true;
        public string Inventory { get; set; } = "git|2.50";
        public string? SourceError { get; set; }
        public int Repairs { get; private set; }
        public Task<CommandResult> RunAsync(CommandSpec command, CancellationToken cancellationToken = default)
        {
            if (command.Arguments.Contains("-EncodedCommand"))
            {
                Repairs++;
                if (RepairWorks) { Broken = false; locator.Path = "choco.exe"; }
                return Task.FromResult(new CommandResult(0, "Recovery ran", ""));
            }
            var first = command.Arguments[0];
            return Task.FromResult(first switch
            {
                "--version" => Broken ? new(1, "", "Executable is damaged") : new CommandResult(0, "2.7.5", ""),
                "list" => new(0, Inventory, ""),
                "search" => SourceError is null ? new(0, "7zip|26.05", "") : new(1, "", SourceError),
                _ => throw new InvalidOperationException("Unexpected command")
            });
        }
    }
}

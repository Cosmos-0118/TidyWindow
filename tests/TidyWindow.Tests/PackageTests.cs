using System.Text;
using TidyWindow.Next.Core;
using Xunit;
using Xunit.Abstractions;

namespace TidyWindow.Next.Tests;

public sealed class PackageTests(ITestOutputHelper output)
{
    private static ManagerStatus Ready(ManagerKind kind) => new(ManagerDefinition.All.Single(x => x.Kind == kind), ManagerHealth.Ready, "1.0", kind + ".exe", "Ready");

    [Fact]
    public void WingetTablePreservesNamesAndExactIds()
    {
        const string output = "Name                         Id                         Version     Match\n--------------------------------------------------------------------------------\nMicrosoft Visual Studio Code Microsoft.VisualStudioCode 1.143.1     Moniker: vscode\n";
        var package = Assert.Single(PackageOutputParser.Parse(ManagerKind.Winget, output));
        Assert.Equal("Microsoft.VisualStudioCode", package.Id);
        Assert.Equal("Microsoft Visual Studio Code", package.Name);
        Assert.Equal("1.143.1", package.Version);
    }

    [Fact]
    public void TableUsesTerminalCellsForWideCharactersAndLocalizedHeaders()
    {
        const string output = "名称          标识符             版本\n--------------------------------------\n测试编辑器    Vendor.Editor      2.0\n";
        var package = Assert.Single(PackageOutputParser.Parse(ManagerKind.Winget, output));
        Assert.Equal("Vendor.Editor", package.Id);
        Assert.Equal("测试编辑器", package.Name);
    }

    [Theory]
    [InlineData("Vendor.App…")]
    [InlineData("--override")]
    [InlineData("a;Start-Process")]
    [InlineData("a\"b")]
    [InlineData("../app")]
    public void UnsafeOrTruncatedIdentifiersCannotBeInstalled(string id)
    {
        var service = new PackageService(new FakeRunner(), new FakeLocator());
        Assert.Throws<ArgumentException>(() => service.InstallCommand(Ready(ManagerKind.Winget), new(ManagerKind.Winget, id, "App", "1", "winget")));
    }

    [Fact]
    public void ChocolateyParsesOnlyMachineReadableRowsAndDeduplicates()
    {
        var packages = PackageOutputParser.Parse(ManagerKind.Chocolatey, "Chocolatey v2.0\n\u001b[32mgit|2.50.0\u001b[0m\ngit|2.50.0\n2 packages found.\n");
        Assert.Equal("git", Assert.Single(packages).Id);
    }

    [Fact]
    public void ScoopUsesBucketQualifiedIds()
    {
        var packages = PackageOutputParser.Parse(ManagerKind.Scoop, "Results from local buckets...\n\nName    Version  Source  Binaries\n----    -------  ------  --------\ngit     2.50.0   main\n");
        Assert.Equal("main/git", Assert.Single(packages).Id);
    }

    [Fact]
    public async Task MissingManagersNeverLaunchAnyProcess()
    {
        var runner = new FakeRunner();
        var service = new PackageService(runner, new FakeLocator { Path = null });
        var statuses = await service.DetectAsync();
        Assert.All(statuses, x => Assert.Equal(ManagerHealth.Missing, x.Health));
        Assert.Empty(runner.Commands);
    }

    [Fact]
    public async Task FailingVersionProbeIsUnavailableRatherThanMissing()
    {
        var service = new PackageService(new FakeRunner { Handler = (_, _) => Task.FromResult(new CommandResult(1, "", "broken")) }, new FakeLocator());
        var statuses = await service.DetectAsync();
        Assert.All(statuses, x => Assert.Equal(ManagerHealth.Unavailable, x.Health));
    }

    [Fact]
    public async Task SearchArgumentsDoNotBecomeShellCommands()
    {
        const string query = "git; $(whoami) & calc";
        var runner = new FakeRunner();
        var service = new PackageService(runner, new FakeLocator());
        await service.SearchAsync(Ready(ManagerKind.Winget), query);
        var command = Assert.Single(runner.Commands);
        Assert.Equal(query, command.Arguments[2]);
        Assert.DoesNotContain("cmd", command.FileName);
        Assert.False(command.Elevate);
    }

    [Fact]
    public async Task ScoopQuotesPowerShellLiteralsAndEscapesSearchRegex()
    {
        var runner = new FakeRunner();
        var service = new PackageService(runner, new FakeLocator());
        await service.SearchAsync(Ready(ManagerKind.Scoop), "a'b;$(whoami)");
        var command = Assert.Single(runner.Commands);
        var script = Encoding.Unicode.GetString(Convert.FromBase64String(command.Arguments[^1]));
        Assert.Contains("a''b;\\$\\(whoami\\)", script);
        Assert.Contains("-EncodedCommand", command.Arguments);
    }

    [Fact]
    public async Task NoMatchExitIsEmptyButNetworkFailureIsAnError()
    {
        var runner = new FakeRunner { Handler = (_, _) => Task.FromResult(new CommandResult(unchecked((int)0x8A150014), "No package found", "")) };
        var service = new PackageService(runner, new FakeLocator());
        var empty = await service.SearchAsync(Ready(ManagerKind.Winget), "nomatch");
        Assert.Null(empty.Error); Assert.Empty(empty.Packages);
        runner.Handler = (_, _) => Task.FromResult(new CommandResult(1, "", "Network unreachable"));
        var failed = await service.SearchAsync(Ready(ManagerKind.Winget), "git");
        Assert.Contains("Network unreachable", failed.Error);
    }

    [Fact]
    public async Task CancellationPropagatesInsteadOfBecomingNoResults()
    {
        var runner = new FakeRunner { Handler = async (_, token) => { await Task.Delay(Timeout.Infinite, token); return new(0, "", ""); } };
        var service = new PackageService(runner, new FakeLocator());
        using var cancellation = new CancellationTokenSource();
        var search = service.SearchAsync(Ready(ManagerKind.Winget), "git", cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => search);
    }

    [Fact]
    public void InstallUsesExactSourceAndElevatesOnlyChocolatey()
    {
        var service = new PackageService(new FakeRunner(), new FakeLocator());
        foreach (var manager in ManagerDefinition.All)
        {
            var spec = service.InstallCommand(Ready(manager.Kind), new(manager.Kind, "Vendor.App", "App", "1", "source"));
            Assert.Equal(manager.Kind == ManagerKind.Chocolatey, spec.Elevate);
            if (manager.Kind == ManagerKind.Winget) { Assert.Contains("--exact", spec.Arguments); Assert.Contains("--source", spec.Arguments); }
        }
    }

    [Fact]
    public async Task ConcurrentInstallsAreRejectedAndGateRecovers()
    {
        var completion = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new FakeRunner { Handler = (_, _) => completion.Task };
        var service = new PackageService(runner, new FakeLocator());
        var package = new Package(ManagerKind.Winget, "Vendor.App", "App", "1", "winget");
        var first = service.InstallAsync(Ready(ManagerKind.Winget), package);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.InstallAsync(Ready(ManagerKind.Winget), package));
        completion.SetResult(new(0, "done", ""));
        await first;
        Assert.Equal(0, (await service.InstallAsync(Ready(ManagerKind.Winget), package)).ExitCode);
    }

    [Fact]
    public async Task ProcessRunnerDrainsBothStreamsAndPreservesExitCode()
    {
        var script = "[Console]::Out.Write(('x' * 150000)); [Console]::Error.Write(('y' * 150000)); exit 7";
        var result = await new ProcessRunner().RunAsync(PowerShell(script, TimeSpan.FromSeconds(15)));
        Assert.Equal(7, result.ExitCode);
        Assert.Equal(131072, result.Output.Length);
        Assert.Equal(131072, result.Error.Length);
    }

    [Fact]
    public async Task ProcessRunnerStopsTimedOutProcess()
    {
        await Assert.ThrowsAsync<TimeoutException>(() => new ProcessRunner().RunAsync(PowerShell("Start-Sleep -Seconds 30", TimeSpan.FromMilliseconds(300))));
    }

    [Fact]
    public async Task LiveWingetSmokeTestWhenExplicitlyEnabled()
    {
        if (Environment.GetEnvironmentVariable("TIDYWINDOW_LIVE_TESTS") != "1") return;
        var service = new PackageService(new ProcessRunner(), new ExecutableLocator());
        var status = (await service.DetectAsync()).Single(x => x.Definition.Kind == ManagerKind.Winget);
        Assert.Equal(ManagerHealth.Ready, status.Health);
        var result = await service.SearchAsync(status, "Microsoft.VisualStudioCode");
        Assert.Null(result.Error);
        Assert.Contains(result.Packages, x => x.Id == "Microsoft.VisualStudioCode");
    }

    [Fact]
    public async Task LiveInstalledSourcesSearchWhenExplicitlyEnabled()
    {
        if (Environment.GetEnvironmentVariable("TIDYWINDOW_LIVE_TESTS") != "1") return;
        var service = new PackageService(new ProcessRunner(), new ExecutableLocator());
        var statuses = await service.DetectAsync();
        foreach (var status in statuses)
        {
            output.WriteLine($"{status.Definition.Name}: {status.Health} {status.Version} {status.Detail}");
            if (status.Health == ManagerHealth.Missing) continue;
            Assert.Equal(ManagerHealth.Ready, status.Health);
            var result = await service.SearchAsync(status, "git");
            output.WriteLine($"{status.Definition.Name}: {result.Packages.Count} results. {result.Error}");
            Assert.Null(result.Error);
            Assert.NotEmpty(result.Packages);
            var installed = await service.ReadInstalledAsync(status);
            output.WriteLine($"{status.Definition.Name}: {installed.Apps.Count} installed packages. {installed.Error}");
            Assert.Null(installed.Error);
            var inventory = new InstalledInventory(installed.Apps);
            foreach (var app in installed.Apps)
                Assert.NotNull(inventory.Find(new(status.Definition.Kind, app.Id!, app.Name, app.Version, "source")));
        }
    }

    private static CommandSpec PowerShell(string script, TimeSpan timeout) => new(new ExecutableLocator().PowerShell,
        ["-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script))], timeout);
    private sealed class FakeLocator : IExecutableLocator
    {
        public string? Path { get; init; } = "manager.exe";
        public string? Find(ManagerKind manager) => Path;
        public string PowerShell => "powershell.exe";
    }
    private sealed class FakeRunner : ICommandRunner
    {
        public List<CommandSpec> Commands { get; } = [];
        public Func<CommandSpec, CancellationToken, Task<CommandResult>> Handler { get; set; } = (_, _) => Task.FromResult(new CommandResult(0, "", ""));
        public Task<CommandResult> RunAsync(CommandSpec command, CancellationToken cancellationToken = default)
        { Commands.Add(command); return Handler(command, cancellationToken); }
    }
}

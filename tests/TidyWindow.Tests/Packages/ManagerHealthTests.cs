using System.Text;
using TidyWindow.Next.Core;
using Xunit;

namespace TidyWindow.Next.Tests;

/// <summary>What the health check concludes from what a manager says, and what it refuses to run at all.</summary>
public sealed class ManagerHealthTests : IDisposable
{
    private readonly string temp = Path.Combine(Path.GetTempPath(), "tidywindow-health-" + Guid.NewGuid().ToString("N"));

    public ManagerHealthTests() => Directory.CreateDirectory(temp);
    public void Dispose() { try { Directory.Delete(temp, true); } catch (IOException) { } }

    private sealed class Locator(string? path) : IExecutableLocator
    {
        public string? Find(ManagerKind manager) => path;
        public string PowerShell => "powershell.exe";
    }

    private sealed class Runner : ICommandRunner
    {
        public List<CommandSpec> Commands { get; } = [];
        public Func<CommandSpec, CommandResult> Handler { get; set; } = _ => new(0, "", "");
        public Task<CommandResult> RunAsync(CommandSpec command, CancellationToken cancellationToken = default) { Commands.Add(command); return Task.FromResult(Handler(command)); }
    }

    private static string First(CommandSpec command) => command.Arguments.Contains("-EncodedCommand") ? Encoding.Unicode.GetString(Convert.FromBase64String(command.Arguments[^1])) : command.Arguments[0];

    private static ManagerHealthService Service(Runner runner, string path = @"C:\managers\manager.exe", Func<ManagerKind, string, string?>? policy = null, IChocolateyFeed? feed = null)
        => new(new PackageService(runner, new Locator(path), feed), runner, new Locator(path), policy ?? ((_, _) => null));

    [Fact]
    public async Task ChocolateyFromAFolderThisAccountCanChangeIsRefusedBeforeItIsRun()
    {
        if (!OperatingSystem.IsWindows()) return;
        var planted = Path.Combine(temp, "choco.exe");
        File.WriteAllText(planted, "planted");
        var runner = new Runner();
        var status = await Service(runner, planted, ManagerHealthService.DefaultElevatedProblem).CheckAsync(ManagerKind.Chocolatey);
        Assert.Equal(ManagerHealth.Unavailable, status.Health);
        Assert.Equal(ManagerProblem.Executable, status.Problem);
        Assert.Contains("administrator rights", status.Detail);
        Assert.Empty(runner.Commands);
        // The same rule does not apply to managers that never run with administrator rights from the health check's point of view.
        runner.Handler = c => First(c).Contains("--version") ? new(0, "v1.11.0", "") : new(0, "Name Id Version\n----------------\n", "");
        Assert.Equal(ManagerHealth.Ready, (await Service(runner, planted, ManagerHealthService.DefaultElevatedProblem).CheckAsync(ManagerKind.Winget)).Health);
    }

    [Fact]
    public async Task NotAnsweringInTimeIsNotADamagedInstallationAndIsNotRepaired()
    {
        var slow = new Runner { Handler = _ => throw new TimeoutException("The package manager did not respond in time. Check your connection and try again.") };
        var status = await Service(slow).CheckAsync(ManagerKind.Chocolatey);
        Assert.Equal(ManagerHealth.Unavailable, status.Health);
        Assert.Equal(ManagerProblem.Timeout, status.Problem);
        Assert.Contains("did not respond in time", status.Detail);

        var inventory = new Runner { Handler = c => c.Arguments[0] == "--version" ? new(0, "2.7.5", "") : throw new TimeoutException("The package manager did not respond in time. Check your connection and try again.") };
        Assert.Equal(ManagerProblem.Timeout, (await Service(inventory).CheckAsync(ManagerKind.Chocolatey)).Problem);

        var packages = new PackageService(inventory, new Locator(@"C:\managers\manager.exe"));
        var recovery = new ManagerRecoveryService(packages, inventory, new Locator(@"C:\managers\manager.exe"), _ => new(Path.Combine(temp, "root"), ""), Path.Combine(temp, "jobs"), (_, _) => null, Path.Combine(temp, "protected"));
        var result = await recovery.RecoverAsync(ManagerKind.Chocolatey);
        Assert.False(result.Success); Assert.False(result.Changed); Assert.Null(result.LogDirectory);
        Assert.Contains("did not respond in time", result.Message);
        Assert.DoesNotContain(inventory.Commands, x => x.Arguments.Contains("-EncodedCommand"));
    }

    [Fact]
    public async Task APolicyOrNetworkFailureIsRecognizedByItsErrorTypeWhenTheMessageIsNotEnglish()
    {
        var restricted = new Runner { Handler = _ => new(1, "", "Die Datei kann nicht geladen werden, da die Ausführung von Skripts auf diesem System deaktiviert ist.\nUnauthorizedAccess System.Management.Automation.PSSecurityException SecurityError\n") };
        var status = await Service(restricted).CheckAsync(ManagerKind.Scoop);
        Assert.Equal(ManagerHealth.Restricted, status.Health);
        Assert.Equal(ManagerProblem.Policy, status.Problem);

        var offline = new Runner { Handler = c => First(c).Contains("--version") ? new(0, "Current Scoop version:\nv0.5.3 - Released at 2025-08-11\n", "") : new(1, "", "Der Remotename konnte nicht aufgelöst werden\nWebCmdletWebResponseException System.Net.WebException ConnectionError\n") };
        status = await Service(offline).CheckAsync(ManagerKind.Scoop);
        Assert.Equal(ManagerHealth.ConnectionIssue, status.Health);
    }

    [Theory]
    [InlineData("Current Scoop version:\nv0.5.3 - Released at 2025-08-11\n\n'main' bucket:\n1a2b3c4 7zip: Update to version 24.09\n", "v0.5.3")]
    [InlineData("Current Scoop version:\nabc1234 fix(core): Update to version 9.9 (#1234)\n\n'main' bucket:\n5e6f7a8 git: Update to version 2.50.0\n", "abc1234")]
    [InlineData("Current Scoop version:\r\n\r\nv0.4.2 - Released at 2024-04-02\r\n", "v0.4.2")]
    [InlineData("0.3.1\n", "0.3.1")]
    public void ScoopsOwnVersionIsTheLineAfterTheHeadingNotABucketsCommit(string output, string expected)
    {
        Assert.Equal(expected, ManagerHealthService.ScoopVersion(output));
    }

    [Theory]
    [InlineData("1.4.0", true)]
    [InlineData("v1.1.0", true)]
    [InlineData("2.7.5", false)]
    [InlineData("Available", false)]
    public async Task ChocolateyOneListsOnlyLocalPackagesWhenAsked(string version, bool localOnly)
    {
        var runner = new Runner { Handler = c => c.Arguments[0] == "--version" ? new(0, version, "") : new(0, "git|2.50\n", "") };
        var status = await Service(runner).CheckAsync(ManagerKind.Chocolatey);
        Assert.Equal(version == "Available" ? ManagerHealth.Unavailable : ManagerHealth.Ready, status.Health);
        if (version == "Available") return;
        var list = Assert.Single(runner.Commands, x => x.Arguments[0] == "list");
        Assert.Equal(localOnly, list.Arguments.Contains("--local-only"));
    }

    private sealed class Feed : IChocolateyFeed
    {
        public int Searches;
        public Task<IReadOnlyList<Package>> SearchAsync(string query, int limit, CancellationToken token = default) { Searches++; return Task.FromResult<IReadOnlyList<Package>>([new(ManagerKind.Chocolatey, "7zip", "7-Zip", "24.09", "chocolatey")]); }
        public Task WarmAsync(CancellationToken token = default) => Task.CompletedTask;
    }

    [Fact]
    public async Task VerifyingTheSourceAsksTheCommandLineEvenWhenTheFeedWouldAnswer()
    {
        var feed = new Feed();
        var runner = new Runner { Handler = c => c.Arguments[0] switch { "--version" => new(0, "2.7.5", ""), "list" => new(0, "git|2.50\n", ""), _ => new(1, "", "Unable to connect to the remote server") } };
        var status = await Service(runner, feed: feed).CheckAsync(ManagerKind.Chocolatey, checkSource: true);
        Assert.Equal(ManagerHealth.ConnectionIssue, status.Health);
        Assert.Equal(0, feed.Searches);
        Assert.Contains(runner.Commands, x => x.Arguments[0] == "search");
        runner.Handler = c => c.Arguments[0] switch { "--version" => new(0, "2.7.5", ""), "list" => new(0, "git|2.50\n", ""), _ => new(0, "7zip|24.09\n", "") };
        Assert.Equal(ManagerHealth.Ready, (await Service(runner, feed: feed).CheckAsync(ManagerKind.Chocolatey, checkSource: true)).Health);
        Assert.Equal(0, feed.Searches);
    }

    [Fact]
    public void SecretsInErrorTextDoNotReachTheStatusOrTheLog()
    {
        var status = new ManagerStatus(ManagerDefinition.All[1], ManagerHealth.Ready, "", @"C:\choco.exe", "");
        var detail = ManagerHealthService.Classify(status, "Unable to connect through https://alice:hunter2@proxy.example:8080/ (apikey=abc123def) token: xyz", ManagerProblem.Source).Detail;
        Assert.DoesNotContain("hunter2", detail);
        Assert.DoesNotContain("abc123def", detail);
        Assert.DoesNotContain("xyz", detail);
        Assert.Contains("proxy.example", detail);
    }
}

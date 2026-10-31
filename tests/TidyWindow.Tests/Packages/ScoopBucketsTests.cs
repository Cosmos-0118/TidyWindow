using System.Text;
using TidyWindow.Next.Core;
using Xunit;

namespace TidyWindow.Next.Tests;

public sealed class ScoopBucketsTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "tidywindow-buckets-" + Guid.NewGuid().ToString("N"));

    public ScoopBucketsTests() => Directory.CreateDirectory(Path.Combine(root, "buckets"));
    public void Dispose() { try { Directory.Delete(root, true); } catch (IOException) { } }

    private sealed class Locator : IExecutableLocator
    {
        public string? Find(ManagerKind manager) => "manager.exe";
        public string PowerShell => "powershell.exe";
    }

    private sealed class Runner(Func<CommandSpec, CommandResult> handler) : ICommandRunner
    {
        public List<CommandSpec> Commands { get; } = [];
        public Task<CommandResult> RunAsync(CommandSpec command, CancellationToken cancellationToken = default) { Commands.Add(command); return Task.FromResult(handler(command)); }
    }

    private ManagerStatus Scoop(ManagerHealth health = ManagerHealth.Ready) =>
        new(ManagerDefinition.All.Single(x => x.Kind == ManagerKind.Scoop), health, "0.6", Path.Combine(root, "shims", "scoop.ps1"), "Ready");

    private static string Script(CommandSpec command) => Encoding.Unicode.GetString(Convert.FromBase64String(command.Arguments[^1]));

    private void Bucket(string name, string remote = "", int manifests = 3)
    {
        var folder = Path.Combine(root, "buckets", name);
        Directory.CreateDirectory(Path.Combine(folder, "bucket"));
        for (var i = 0; i < manifests; i++) File.WriteAllText(Path.Combine(folder, "bucket", $"app{i}.json"), """{"version":"1"}""");
        if (remote.Length == 0) return;
        Directory.CreateDirectory(Path.Combine(folder, ".git"));
        File.WriteAllText(Path.Combine(folder, ".git", "config"), $"[core]\n\trepositoryformatversion = 0\n[remote \"origin\"]\n\turl = {remote}\n\tfetch = +refs/heads/*:refs/remotes/origin/*\n[branch \"master\"]\n\tremote = origin\n");
    }

    private ScoopBuckets Service(Runner runner, bool git = true, PackageService? packages = null) =>
        new(packages ?? new PackageService(runner, new Locator()), runner, _ => git ? @"C:\git\git.exe" : null);

    [Fact]
    public void AddedBucketsAreListedWithTheirAddressAndHowManyAppsTheyHold()
    {
        Bucket("main", "https://github.com/ScoopInstaller/Main.git", 5);
        Bucket("extras", "https://github.com/ScoopInstaller/Extras", 2);
        var overview = Service(new Runner(_ => new(0, "", ""))).Read(Scoop());
        Assert.Equal(["extras", "main"], overview.Added.Select(x => x.Name));
        Assert.Equal(5, overview.Added.Single(x => x.Name == "main").Manifests);
        Assert.Equal("https://github.com/ScoopInstaller/Main.git", overview.Added.Single(x => x.Name == "main").Source);
    }

    [Fact]
    public void OnlyBucketsNotYetAddedAreOfferedAndMainNeverIs()
    {
        Bucket("main", "https://github.com/ScoopInstaller/Main");
        Bucket("extras", "https://github.com/ScoopInstaller/Extras");
        var offered = Service(new Runner(_ => new(0, "", ""))).Read(Scoop()).Available;
        Assert.DoesNotContain(offered, x => x.Name is "main" or "extras");
        Assert.Contains(offered, x => x.Name == "versions" && x.Official);
        Assert.Contains(offered, x => x.Name == "dorado" && !x.Official);
        Assert.Equal(offered.Count, offered.Select(x => x.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void ABucketAddedByHandUnderAnotherNameIsNotOfferedAgain()
    {
        Bucket("my-games", "https://github.com/ScoopInstaller/Games.git");
        var offered = Service(new Runner(_ => new(0, "", ""))).Read(Scoop()).Available;
        Assert.DoesNotContain(offered, x => x.Name == "games");
    }

    [Fact]
    public void OfficialAddressesComeFromScoopsOwnListAndUnsafeEntriesAreIgnored()
    {
        var folder = Path.Combine(root, "apps", "scoop", "current");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "buckets.json"), """
            { "main": "https://github.com/ScoopInstaller/Main",
              "versions": "https://github.com/MovedOrg/Versions",
              "sneaky": "https://evil.example/repo",
              "quoted": "https://github.com/x/y'; calc",
              "bad name": "https://github.com/x/z",
              "fresh": "https://github.com/ScoopInstaller/Fresh" }
            """);
        var offered = Service(new Runner(_ => new(0, "", ""))).Read(Scoop()).Available;
        Assert.Equal("https://github.com/MovedOrg/Versions", offered.Single(x => x.Name == "versions").Url);
        Assert.Contains(offered, x => x.Name == "fresh" && x.Official);
        Assert.DoesNotContain(offered, x => x.Name is "sneaky" or "quoted" or "bad name");
        Assert.All(offered, x => Assert.Matches(@"^https://github\.com/[\w.-]+/[\w.-]+$", x.Url));
    }

    [Fact]
    public void MissingGitIsReportedUpFront()
    {
        Assert.True(Service(new Runner(_ => new(0, "", "")), git: false).Read(Scoop()).GitMissing);
        Assert.False(Service(new Runner(_ => new(0, "", "")), git: true).Read(Scoop()).GitMissing);
    }

    [Fact]
    public async Task AddingRunsExactlyTheListedAddressAndReportsWhatLandedOnDisk()
    {
        var runner = new Runner(command =>
        {
            Assert.Contains("'bucket' 'add' 'versions' 'https://github.com/ScoopInstaller/Versions'", Script(command));
            Bucket("versions", "https://github.com/ScoopInstaller/Versions", 630);
            return new(0, "The versions bucket was added successfully.", "");
        });
        var result = await Service(runner).AddAsync(Scoop(), "versions");
        Assert.True(result.Ok);
        Assert.Contains("630", result.Message);
        Assert.Single(runner.Commands);
        Assert.False(runner.Commands[0].Elevate);
    }

    [Theory]
    [InlineData("not-a-bucket")]
    [InlineData("https://evil.example/repo")]
    [InlineData("versions'; calc; '")]
    [InlineData("")]
    public async Task NothingOutsideTheListCanBeAdded(string name)
    {
        var runner = new Runner(_ => new(0, "", ""));
        var result = await Service(runner).AddAsync(Scoop(), name);
        Assert.False(result.Ok);
        Assert.Empty(runner.Commands);
    }

    [Fact]
    public async Task AlreadyAddedIsASuccessThatDoesNothing()
    {
        Bucket("extras", "https://github.com/ScoopInstaller/Extras");
        var runner = new Runner(_ => new(0, "", ""));
        var result = await Service(runner).AddAsync(Scoop(), "EXTRAS");
        Assert.True(result.Ok);
        Assert.Empty(runner.Commands);
    }

    [Fact]
    public async Task WithoutGitNothingIsStartedAndTheReasonIsPlain()
    {
        var runner = new Runner(_ => new(0, "", ""));
        var result = await Service(runner, git: false).AddAsync(Scoop(), "versions");
        Assert.False(result.Ok);
        Assert.Equal(ScoopBuckets.GitNeeded, result.Message);
        Assert.Empty(runner.Commands);
    }

    [Fact]
    public async Task AFailedCloneIsAFailureEvenIfScoopSaidNothingWrong()
    {
        // Exit code 0 but nothing on disk, and a non-zero exit with a half-made folder: neither counts as added.
        var quiet = new Runner(_ => new(0, "", ""));
        Assert.False((await Service(quiet).AddAsync(Scoop(), "versions")).Ok);
        var broken = new Runner(_ => { Bucket("java", "", 0); return new(1, "", "fatal: unable to access 'https://github.com/ScoopInstaller/Java/': Could not resolve host"); });
        var result = await Service(broken).AddAsync(Scoop(), "java");
        Assert.False(result.Ok);
        Assert.Contains("Could not resolve host", result.Detail);
    }

    [Fact]
    public async Task AddingWaitsOutAnInstallationInsteadOfRunningBesideIt()
    {
        var runner = new Runner(_ => new(0, "", ""));
        var packages = new PackageService(runner, new Locator());
        await packages.OperationGate.WaitAsync();
        try
        {
            var result = await Service(runner, packages: packages).AddAsync(Scoop(), "versions");
            Assert.False(result.Ok);
            Assert.Empty(runner.Commands);
        }
        finally { packages.OperationGate.Release(); }
        // And the gate is free again afterwards, so the next add is not stuck behind a failed one.
        Assert.True(await packages.OperationGate.WaitAsync(0));
        packages.OperationGate.Release();
    }

    [Fact]
    public async Task TheGateIsReleasedWhenTheCommandFails()
    {
        var runner = new Runner(_ => throw new InvalidOperationException("boom"));
        var packages = new PackageService(runner, new Locator());
        var result = await Service(runner, packages: packages).AddAsync(Scoop(), "versions");
        Assert.False(result.Ok);
        Assert.True(await packages.OperationGate.WaitAsync(0));
        packages.OperationGate.Release();
    }

    [Fact]
    public async Task ScoopThatIsNotReadyCannotAddAnything()
    {
        var runner = new Runner(_ => new(0, "", ""));
        Assert.False((await Service(runner).AddAsync(Scoop(ManagerHealth.Missing), "versions")).Ok);
        Assert.Empty(runner.Commands);
        Assert.Same(BucketOverview.Unavailable, Service(runner).Read(new ManagerStatus(ManagerDefinition.All[0], ManagerHealth.Ready, "1", "winget.exe", "")));
    }

    [Fact]
    public async Task ANewBucketIsFoundByTheNextSearchWithoutAnyRefresh()
    {
        var runner = new Runner(_ => { Bucket("versions", "https://github.com/ScoopInstaller/Versions", 1); File.WriteAllText(Path.Combine(root, "buckets", "versions", "bucket", "app0.json"), """{"version":"9.9"}"""); return new(0, "", ""); });
        var catalog = new ScoopCatalog();
        var packages = new PackageService(runner, new Locator(), scoop: catalog);
        Bucket("main", "https://github.com/ScoopInstaller/Main", 1);
        Assert.DoesNotContain((await packages.SearchAsync(Scoop(), "app0")).Packages, x => x.Source == "versions");
        Assert.True((await Service(runner, packages: packages).AddAsync(Scoop(), "versions")).Ok);
        Assert.Contains((await packages.SearchAsync(Scoop(), "app0")).Packages, x => x.Id == "versions/app0" && x.Version == "9.9");
    }
}

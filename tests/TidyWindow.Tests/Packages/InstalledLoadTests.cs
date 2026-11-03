using TidyWindow.Next.Core;
using Xunit;

namespace TidyWindow.Next.Tests;

/// <summary>How quickly the Installed page gets something to show: the list before the update check, and the saved list from the last run.</summary>
public sealed class InstalledLoadTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "tidywindow-installed-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(folder, recursive: true); } catch (IOException) { } }

    private const string Installed = """
        Name       Id           Version   Source
        ----------------------------------------
        Git        Git.Git      2.56.0    winget
        Discord    ARP\User\X64\Discord  1.0.9
        """;

    private const string Upgrades = """
        Name  Id       Version  Available  Source
        ----------------------------------------
        Git   Git.Git  2.56.0   2.57.0     winget
        """;

    private static ManagerStatus Ready() => new(ManagerDefinition.All.Single(x => x.Kind == ManagerKind.Winget), ManagerHealth.Ready, "1", "winget.exe", "Ready");

    private sealed class Locator : IExecutableLocator
    {
        public string? Find(ManagerKind manager) => "winget.exe";
        public string PowerShell => "powershell.exe";
    }

    private sealed class Runner : ICommandRunner
    {
        public List<CommandSpec> Commands { get; } = [];
        public Task<CommandResult> RunAsync(CommandSpec command, CancellationToken cancellationToken = default)
        {
            Commands.Add(command);
            return Task.FromResult(new CommandResult(0, command.Arguments.Contains("--upgrade-available") ? Upgrades : Installed, ""));
        }
    }

    private sealed class Recorder(Runner runner) : IProgress<ManagedInventory>
    {
        public ManagedInventory? Early { get; private set; }
        public int CommandsWhenReported { get; private set; }
        public void Report(ManagedInventory value) { Early = value; CommandsWhenReported = runner.Commands.Count; }
    }

    [Fact]
    public async Task TheInstalledListIsReportedBeforeTheUpdateCheckStarts()
    {
        var runner = new Runner();
        var service = new InstallationService(new PackageService(runner, new Locator()), runner, new Locator());
        var recorder = new Recorder(runner);

        var full = await service.ReadAsync(Ready(), installedReady: recorder);

        Assert.NotNull(recorder.Early);
        Assert.Equal(1, recorder.CommandsWhenReported); // only `winget list` had run
        Assert.Equal(2, runner.Commands.Count);
        Assert.Contains("--upgrade-available", runner.Commands[1].Arguments);
        Assert.All(recorder.Early!.Packages, x => Assert.Null(x.AvailableVersion));
        Assert.Equal("2.57.0", full.Packages.Single(x => x.Id == "Git.Git").AvailableVersion);
        Assert.Equal(recorder.Early.Packages.Count, full.Packages.Count);
    }

    [Fact]
    public async Task NothingIsReportedEarlyWhenUpdatesAreNotChecked()
    {
        var runner = new Runner();
        var service = new InstallationService(new PackageService(runner, new Locator()), runner, new Locator());
        var recorder = new Recorder(runner);

        await service.ReadAsync(Ready(), checkUpdates: false, installedReady: recorder);

        Assert.Null(recorder.Early);
        Assert.Single(runner.Commands);
    }

    [Fact]
    public void ASavedListComesBackAsItWasSaved()
    {
        var snapshot = new InstalledSnapshot(Path.Combine(folder, "apps.json"));
        var saved = new[]
        {
            new ManagedPackage(ManagerKind.Winget, "Git.Git", "Git", "2.56.0", "2.57.0", Source: "winget"),
            new ManagedPackage(ManagerKind.Scoop, "7zip", "7zip", "24.0", Global: true, Held: true, Note: "Global install, Held package")
        };

        snapshot.Save(saved);

        Assert.Equal(saved, snapshot.Load());
    }

    [Fact]
    public void AMissingOrDamagedSavedListIsSimplyEmpty()
    {
        var path = Path.Combine(folder, "apps.json");
        var snapshot = new InstalledSnapshot(path);
        Assert.Empty(snapshot.Load());

        Directory.CreateDirectory(folder);
        File.WriteAllText(path, "{ not json");
        Assert.Empty(snapshot.Load());

        File.WriteAllText(path, """{"Format":99,"Apps":[]}""");
        Assert.Empty(snapshot.Load());
    }

    [Fact]
    public void SavedEntriesWithoutAnIdOrNameAreLeftOut()
    {
        var path = Path.Combine(folder, "apps.json");
        Directory.CreateDirectory(folder);
        File.WriteAllText(path, """{"Format":1,"Apps":[{"Manager":"Winget","Id":"","Name":"x","Version":"1"},{"Manager":"Winget","Id":"A.B","Name":"A","Version":"1"}]}""");

        var loaded = new InstalledSnapshot(path).Load();

        Assert.Single(loaded);
        Assert.Equal("A.B", loaded[0].Id);
    }

    [Fact]
    public void AListSavedWithAManagerThisVersionDoesNotKnowIsIgnoredWhole()
    {
        var path = Path.Combine(folder, "apps.json");
        Directory.CreateDirectory(folder);
        File.WriteAllText(path, """{"Format":1,"Apps":[{"Manager":"Winget","Id":"A.B","Name":"A","Version":"1"},{"Manager":"Nope","Id":"C","Name":"C","Version":"1"}]}""");

        Assert.Empty(new InstalledSnapshot(path).Load());
    }

    [Fact]
    public void SavingOverAnExistingListReplacesItWithoutLeavingATemporaryFile()
    {
        var path = Path.Combine(folder, "apps.json");
        var snapshot = new InstalledSnapshot(path);
        snapshot.Save([new ManagedPackage(ManagerKind.Winget, "A.A", "A", "1")]);
        snapshot.Save([new ManagedPackage(ManagerKind.Winget, "B.B", "B", "2")]);

        Assert.Equal("B.B", Assert.Single(snapshot.Load()).Id);
        Assert.False(File.Exists(path + ".tmp"));
    }
}

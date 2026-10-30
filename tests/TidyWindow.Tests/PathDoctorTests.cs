using TidyWindow.Next.Core;
using Xunit;

namespace TidyWindow.Next.Tests;

public sealed class PathDoctorTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "tw-path-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly FakeStore store = new();
    private int broadcasts;

    public PathDoctorTests() => Directory.CreateDirectory(root);
    public void Dispose() { try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    private string Folder(string name, bool create = true)
    {
        var path = Path.Combine(root, name);
        if (create) Directory.CreateDirectory(path);
        return path;
    }

    private static string Expand(string text) => text.Replace("%ROOT%", Path.GetTempPath().TrimEnd('\\'));
    private PathAnalyzer Analyzer() => new(Expand, Directory.Exists);
    private PathDoctor Doctor() => new(store, new PathBackup(Path.Combine(root, "_backups")), Analyzer(), () => broadcasts++);

    private sealed class FakeStore : IPathStore
    {
        public string? Machine, User;
        public readonly Dictionary<string, string> Variables = [];
        public string? Read(PathScope scope) => scope == PathScope.Machine ? Machine : User;
        public string? Variable(PathScope scope, string name) => Variables.GetValueOrDefault(name);
        public void WriteUser(string value) => User = value;
    }

    [Fact]
    public void SpellingsOfTheSameFolderCountAsDuplicates()
    {
        var a = Folder("tools");
        var snapshot = Analyzer().Analyze(a, $"{a.ToUpperInvariant()}\\;{a.Replace('\\', '/')};{Folder("other")}");
        Assert.Equal(3, snapshot.User.Count);
        Assert.Equal(PathAnalyzer.MachineLabel, snapshot.User[0].DuplicateOf);
        Assert.Equal(PathAnalyzer.MachineLabel, snapshot.User[1].DuplicateOf);
        Assert.False(snapshot.User[2].Duplicate);
    }

    [Fact]
    public void MissingFoldersAreFlaggedButNetworkAndUnresolvedOnesAreGivenTheBenefitOfTheDoubt()
    {
        var snapshot = Analyzer().Analyze(null, $"{Folder("gone", create: false)};\\\\server\\share\\bin;%UNKNOWN%\\bin");
        Assert.True(snapshot.User[0].Missing);
        Assert.False(snapshot.User[1].Missing);
        Assert.False(snapshot.User[2].Missing);
    }

    [Fact]
    public void EmptyEntriesAndTrailingSeparatorsAreIgnored()
    {
        var a = Folder("a");
        var snapshot = Analyzer().Analyze(null, $";{a};;");
        Assert.Single(snapshot.User);
        Assert.Equal(a, snapshot.User[0].Raw);
    }

    [Fact]
    public void VariablesStayUnexpandedWhenEntriesAreEdited()
    {
        Assert.Equal("%USERPROFILE%\\bin;C:\\x", PathEdit.Remove("%USERPROFILE%\\bin;C:\\gone;C:\\x", [1]));
        Assert.Equal("C:\\b;C:\\a;C:\\c", PathEdit.Move("C:\\a;C:\\b;C:\\c", 1, 0));
        Assert.Equal("C:\\a;C:\\c;C:\\b", PathEdit.Move("C:\\a;C:\\b;C:\\c", 1, 99));
    }

    [Fact]
    public void FixingRemovesOnlyMissingAndRepeatedUserFoldersAndKeepsTheOrder()
    {
        var a = Folder("a"); var b = Folder("b"); var gone = Folder("gone", create: false);
        store.Machine = a;
        store.User = $"{b};{gone};{a};{b};{Folder("c")}";
        var doctor = Doctor();
        var change = doctor.FixProblems();
        Assert.True(change.Changed);
        Assert.Equal($"{b};{Folder("c")}", store.User);
        Assert.Contains("repeated", change.Message);
        Assert.Contains("no longer exist", change.Message);
        Assert.Equal(1, broadcasts);
        Assert.Equal(a, store.Machine);
    }

    [Fact]
    public void NothingIsWrittenWhenThereIsNothingToFix()
    {
        store.User = Folder("a");
        Assert.False(Doctor().FixProblems().Changed);
        Assert.Equal(0, broadcasts);
        Assert.Null(Doctor().LastBackup);
    }

    [Fact]
    public void EveryChangeCanBeUndone()
    {
        var a = Folder("a"); var b = Folder("b");
        store.User = $"{a};{b}";
        var doctor = Doctor();
        var entry = doctor.Load().User[1];
        Assert.True(doctor.Move(entry, 0).Changed);
        Assert.Equal($"{b};{a}", store.User);
        Assert.True(doctor.Undo().Changed);
        Assert.Equal($"{a};{b}", store.User);
        Assert.False(doctor.Undo().Changed);
    }

    [Fact]
    public void ARemovalIsRefusedIfThePathChangedUnderneath()
    {
        var a = Folder("a"); var b = Folder("b");
        store.User = $"{a};{b}";
        var doctor = Doctor();
        var entry = doctor.Load().User[0];
        store.User = $"{b};{a}";
        var change = doctor.Remove(entry);
        Assert.False(change.Changed);
        Assert.Equal($"{b};{a}", store.User);
    }

    [Fact]
    public void AddingChecksTheFolderExistsAndIsNotAlreadyThere()
    {
        var a = Folder("a");
        store.Machine = a; store.User = "";
        var doctor = Doctor();
        Assert.False(doctor.Add(Folder("nope", create: false)).Changed);
        Assert.False(doctor.Add(a + "\\").Changed);
        var fresh = Folder("fresh");
        Assert.True(doctor.Add($"\"{fresh}\\\"").Changed);
        Assert.Equal(fresh, store.User);
    }

    [Fact]
    public void TheWinnerIsTheFirstFolderInSearchOrderAndWindowsWideFoldersComeFirst()
    {
        var machine = Folder("m"); var first = Folder("u1"); var second = Folder("u2");
        File.WriteAllText(Path.Combine(second, "tool.exe"), "x");
        File.WriteAllText(Path.Combine(first, "tool.cmd"), "x");
        var snapshot = Analyzer().Analyze(machine, $"{first};{second}");
        var resolver = new CommandResolver(File.Exists, _ => false, [".EXE", ".CMD"]);
        var lookup = resolver.Find("tool", snapshot);
        Assert.Equal(2, lookup.Candidates.Count);
        Assert.EndsWith("tool.cmd", lookup.Winner!.File);
        Assert.Equal(PathScope.User, lookup.Winner.Scope);

        File.WriteAllText(Path.Combine(machine, "tool.exe"), "x");
        Assert.Equal(PathScope.Machine, resolver.Find("tool", Analyzer().Analyze(machine, $"{first};{second}")).Winner!.Scope);
    }

    [Fact]
    public void WithinOneFolderExtensionsAreTriedInPathextOrder()
    {
        var a = Folder("a");
        File.WriteAllText(Path.Combine(a, "t.cmd"), "x"); File.WriteAllText(Path.Combine(a, "t.exe"), "x");
        var lookup = new CommandResolver(File.Exists, _ => false, [".COM", ".EXE", ".BAT", ".CMD"]).Find("t", Analyzer().Analyze(null, a));
        Assert.EndsWith("t.exe", lookup.Winner!.File);
    }

    [Fact]
    public void ACommandThatIsNowhereHasNoWinner()
    {
        var lookup = new CommandResolver(File.Exists, _ => false, [".EXE"]).Find("nothing-here", Analyzer().Analyze(null, Folder("a")));
        Assert.Null(lookup.Winner);
    }

    [Fact]
    public void TheMicrosoftStorePlaceholderIsRecognisedFromWhatItSays()
    {
        Assert.True(CommandProbe.Parse("Python was not found; run without arguments to install from the Microsoft Store, or disable", 49).StoreStub);
        Assert.True(CommandProbe.Parse("", 9009).StoreStub);
        var real = CommandProbe.Parse("Python 3.12.4\n", 0);
        Assert.False(real.StoreStub);
        Assert.Equal("Python 3.12.4", real.Version);
        Assert.Equal("pip 24.0", CommandProbe.Parse("pip 24.0 from C:\\x\\site-packages\\pip (python 3.12)", 0).Version);
    }

    [Fact]
    public async Task AProgramIsAskedForItsVersionWithoutAWindow()
    {
        var script = Path.Combine(Folder("bin"), "demo.cmd");
        File.WriteAllText(script, "@echo off\r\necho demo 1.2.3\r\n");
        var result = await CommandProbe.RunAsync("demo", script);
        Assert.Equal("demo 1.2.3", result.Version);
        Assert.False(result.StoreStub);
    }

    [Fact]
    public async Task AProgramThatNeverAnswersIsAbandoned()
    {
        var script = Path.Combine(Folder("bin"), "slow.cmd");
        File.WriteAllText(script, "@echo off\r\nping -n 30 127.0.0.1 >nul\r\n");
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var result = await CommandProbe.RunAsync("slow", script);
        Assert.Equal("", result.Version);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(12), timer.Elapsed.ToString());
    }

    [Fact]
    public void JavaHomeProblemsAreExplained()
    {
        var home = Folder("jdk"); var other = Folder("other");
        Directory.CreateDirectory(Path.Combine(home, "bin"));
        var inHome = new CommandLookup("java", [new(Path.Combine(home, "bin", "java.exe"), PathScope.User, 0, false)]);
        var elsewhere = new CommandLookup("java", [new(Path.Combine(other, "java.exe"), PathScope.User, 0, false)]);
        Assert.Empty(PathAdvice.JavaHome(home, inHome, Directory.Exists));
        Assert.Empty(PathAdvice.JavaHome(null, elsewhere, Directory.Exists));
        Assert.Contains("runs", PathAdvice.JavaHome(home, elsewhere, Directory.Exists).Single());
        Assert.Contains("gone", PathAdvice.JavaHome(Folder("old", create: false), inHome, Directory.Exists).Single());
    }

    private static CommandLookup Nothing(string command) => new(command, []);
    private PathSetup SetupAt(string home) => new(text => text.Replace("%USERPROFILE%", home).Replace("%ProgramFiles%", home), Directory.Exists, File.Exists, folder => Directory.Exists(folder) ? Directory.GetDirectories(folder) : []);

    [Fact]
    public void AnInstalledToolMissingFromPathIsOfferedAndOnePathEditAddsAll()
    {
        var home = Folder("home");
        var cargo = Folder(@"home\.cargo\bin");
        File.WriteAllText(Path.Combine(cargo, "cargo.exe"), "");
        var goTools = Folder(@"home\go\bin");
        var other = Folder("other");
        store.User = other;
        var tools = SetupAt(home).Find(Analyzer().Analyze(null, store.User), Nothing);

        var rust = tools.Single(x => x.Name == "Rust (Cargo)");
        Assert.Equal(ToolState.NotInPath, rust.State);
        Assert.Equal(cargo, rust.Folder);
        Assert.Equal(ToolState.NotInPath, tools.Single(x => x.Name == "Go tools").State);
        Assert.Equal(ToolState.NotInstalled, tools.Single(x => x.Name == "Bun").State);
        Assert.Equal(ToolState.NotInPath, tools[0].State);

        var change = Doctor().AddMany([cargo, goTools, cargo]);
        Assert.True(change.Changed);
        Assert.Equal(new[] { other, cargo, goTools }, store.User!.Split(';'));
        Assert.Equal(1, broadcasts);
        Assert.Contains("2 folders", change.Message);
        Doctor().Undo();
        Assert.Equal(other, store.User);
    }

    [Fact]
    public void AToolThatAlreadyRunsOrIsAlreadyListedIsNotOffered()
    {
        var home = Folder("home");
        var cargo = Folder(@"home\.cargo\bin");
        File.WriteAllText(Path.Combine(cargo, "cargo.exe"), "");
        var setup = SetupAt(home);

        var listed = setup.Find(Analyzer().Analyze(null, cargo.ToUpperInvariant() + "\\"), Nothing);
        Assert.Equal(ToolState.Working, listed.Single(x => x.Name == "Rust (Cargo)").State);

        var elsewhere = Folder("elsewhere");
        var runs = setup.Find(Analyzer().Analyze(null, ""), command => command == "cargo" ? new CommandLookup("cargo", [new(Path.Combine(elsewhere, "cargo.exe"), PathScope.User, 0, false)]) : Nothing(command));
        Assert.Equal(ToolState.Working, runs.Single(x => x.Name == "Rust (Cargo)").State);
    }

    [Fact]
    public void VersionedFoldersAreExpandedAndTheNewestWins()
    {
        var home = Folder("home");
        foreach (var name in new[] { "jdk-8", "jdk-25", "jdk-17" })
        {
            var bin = Folder($@"home\Java\{name}\bin");
            File.WriteAllText(Path.Combine(bin, "java.exe"), "");
        }
        var java = SetupAt(home).Find(Analyzer().Analyze(null, ""), Nothing).Single(x => x.Name == "Java (JDK)");
        Assert.Equal(Path.Combine(home, "Java", "jdk-25", "bin"), java.Folder, ignoreCase: true);
    }
}

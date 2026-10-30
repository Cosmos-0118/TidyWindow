using TidyWindow.Next.Core;
using Xunit;

namespace TidyWindow.Next.Tests;

public sealed class PathFinderTests
{
    private const string Programs = @"C:\PF";
    private const string Home = @"C:\Users\me";

    // A pretend disk: folder -> the files directly inside it. Parent folders appear on their own.
    private static Dictionary<string, string[]> Disk(params (string Folder, string[] Files)[] items)
    {
        var disk = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase) { [Programs] = [], [Home] = [] };
        foreach (var (folder, files) in items)
        {
            disk[folder] = files;
            for (var parent = Path.GetDirectoryName(folder); !string.IsNullOrEmpty(parent) && !disk.ContainsKey(parent); parent = Path.GetDirectoryName(parent)) disk[parent] = [];
        }
        return disk;
    }

    private static PathFinder Finder(Dictionary<string, string[]> disk) => new(
        text => text.Replace("%ProgramFiles%", Programs).Replace("%USERPROFILE%", Home),
        parent => disk.Keys.Where(k => string.Equals(Path.GetDirectoryName(k), parent, StringComparison.OrdinalIgnoreCase)),
        folder => disk.GetValueOrDefault(folder) ?? []);

    private static PathSnapshot InPath(params string[] folders) => new PathAnalyzer(x => x, _ => true).Analyze(null, string.Join(';', folders));

    [Fact]
    public void FindsToolFoldersThatAreNotInPathYet()
    {
        var disk = Disk((@"C:\PF\Go\bin", ["go.exe", "gofmt.exe"]), (@"C:\PF\Git\cmd", ["git.exe"]), (@"C:\PF\Junk\bin", ["unins000.exe"]), (@"C:\Users\me\.cargo\bin", ["cargo.exe", "rustc.exe", "notes.txt"]));
        var found = Finder(disk).Scan(InPath(@"C:\PF\Git\cmd\"), CancellationToken.None);

        Assert.Equal([@"C:\PF\Go\bin", @"C:\Users\me\.cargo\bin"], found.Select(x => x.Folder).Order().ToArray());
        Assert.Equal(["cargo", "rustc"], found.Single(x => x.Folder.EndsWith(".cargo\\bin")).Programs);
    }

    [Fact]
    public void LeavesOutNoisyFoldersAndAnythingTooDeep()
    {
        var disk = Disk((@"C:\Users\me\node_modules\bin", ["x.exe"]), (@"C:\Users\me\a\b\c\d\bin", ["deep.exe"]), (@"C:\Users\me\a\b\bin", ["near.exe"]));
        var found = Finder(disk).Scan(InPath(), CancellationToken.None);

        Assert.Equal([@"C:\Users\me\a\b\bin"], found.Select(x => x.Folder).ToArray());
    }

    [Fact]
    public void SuggestsOnlyTheUsualHomesOfCommandLineTools()
    {
        var disk = Disk((@"C:\PF\Tool\bin", ["tool.exe"]), (@"C:\PF\App", ["app.exe"]));
        var found = Finder(disk).Scan(InPath(), CancellationToken.None);

        Assert.Equal([@"C:\PF\Tool\bin"], PathFinder.Suggest(found).Select(x => x.Folder).ToArray());
        Assert.Equal([@"C:\PF\App"], PathFinder.Search(found, "app").Select(x => x.Folder).ToArray());
    }

    [Fact]
    public void SearchPutsAnExactProgramNameFirstAndNeedsEveryWord()
    {
        var disk = Disk((@"C:\PF\Python\bin", ["python.exe"]), (@"C:\PF\Other\bin", ["pythonw.exe", "pythonista.exe"]), (@"C:\PF\Ffmpeg\bin", ["ffmpeg.exe"]));
        var found = Finder(disk).Scan(InPath(), CancellationToken.None);

        Assert.Equal([@"C:\PF\Python\bin", @"C:\PF\Other\bin"], PathFinder.Search(found, "python").Select(x => x.Folder).ToArray());
        Assert.Equal([@"C:\PF\Ffmpeg\bin"], PathFinder.Search(found, "FFMPEG.exe").Select(x => x.Folder).ToArray());
        Assert.Empty(PathFinder.Search(found, "python zzz"));
        Assert.Empty(PathFinder.Search(found, "   "));
    }

    [Fact]
    public void DescribesAFolderThatTheWalkNeverSaw()
    {
        var disk = Disk((@"D:\tools\bin", ["thing.cmd", "readme.md"]));
        var candidate = Finder(disk).Describe(@"D:\tools\bin\");

        Assert.Equal(@"D:\tools\bin", candidate.Folder);
        Assert.Equal(["thing"], candidate.Programs);
        Assert.True(candidate.Strong);
    }

    [Fact]
    public void StopsWhenCancelled()
    {
        var disk = Disk((@"C:\PF\Go\bin", ["go.exe"]));
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        Assert.Throws<OperationCanceledException>(() => Finder(disk).Scan(InPath(), cancel.Token));
    }
}

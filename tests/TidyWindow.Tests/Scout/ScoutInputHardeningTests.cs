using System.Diagnostics;
using System.Text.RegularExpressions;
using TidyWindow.Next.Core;
using Xunit;

namespace TidyWindow.Next.Tests;

public sealed class ScoutInputHardeningTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "tw-scout-in-" + Guid.NewGuid().ToString("N")[..8]);
    public ScoutInputHardeningTests() => Directory.CreateDirectory(root);
    public void Dispose() { try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    private static ScoutEntry File(string path, bool folder = false) => new(path, folder, folder ? null : 10, null);

    [Theory]
    [InlineData("content:*secret*")]
    [InlineData("*|*")]
    [InlineData("!*.tmp")]
    [InlineData("<a*>")]
    [InlineData("regex:*(a+)+b*")]
    [InlineData("report size:>1mb*")]
    public void AWildcardTermCannotCarryIndexCommands(string text) => Assert.Throws<ArgumentException>(() => new ScoutQuery(text));

    [Fact]
    public void PlainTermsKeepThoseCharactersBecauseTheyAreQuotedLiterally()
    {
        var expression = new ScoutQuery("report|x:abc !y <z>").NameExpression(ScoutItemKind.All, null);
        foreach (var term in new[] { "nowildcards:\"report|x:abc\"", "nowildcards:\"!y\"", "nowildcards:\"<z>\"" }) Assert.Contains(term, expression);
        Assert.Contains("*.png", new ScoutQuery("*.png").NameExpression(ScoutItemKind.All, null));
    }

    [Fact]
    public void ADriveRootScopeIsWrittenWithOneBackslashAndCountsAsHome()
    {
        Assert.EndsWith(" \"C:\\\"", new ScoutQuery("x").NameExpression(ScoutItemKind.All, @"C:\"));
        var entries = new[] { File(@"D:\report.txt"), File(@"E:\report.txt") };
        Assert.Equal(@"E:\report.txt", new ScoutQuery("report", @"E:\").Rank(entries, ScoutItemKind.All)[0].FullPath);
    }

    [Fact]
    public void TheExactStemPassIsAnAnchoredRegexWithoutBackslashes()
    {
        Assert.Equal("regex:\"^readme([.][^.]+)?$\"", new ScoutQuery("readme").ExactStemExpression(ScoutItemKind.All));
        var expression = new ScoutQuery("budget 2026 (final)").ExactStemExpression(ScoutItemKind.Files)!;
        Assert.StartsWith("file: regex:\"", expression);
        Assert.DoesNotContain("\\", expression);
        var regex = new Regex(expression["file: regex:\"".Length..^1], RegexOptions.IgnoreCase);
        Assert.Matches(regex, "Budget 2026 (final).xlsx");
        Assert.Matches(regex, "budget 2026 (final)");
        Assert.DoesNotMatch(regex, "budget 2026 (final).tar.gz");
        Assert.DoesNotMatch(regex, "old budget 2026 (final).xlsx");
        Assert.Null(new ScoutQuery("*.png").ExactStemExpression(ScoutItemKind.All));
        Assert.Null(new ScoutQuery("a^b").ExactStemExpression(ScoutItemKind.All));
        Assert.Null(new ScoutQuery(@"docs\readme").ExactStemExpression(ScoutItemKind.All));
    }

    [Fact]
    public void AnAccentedFolderNameSatisfiesAPathTerm()
    {
        var entries = new[] { File(@"C:\Users\me\Café\report.txt"), File(@"C:\Users\me\Other\report.txt") };
        Assert.Equal(@"C:\Users\me\Café\report.txt", Assert.Single(new ScoutQuery("cafe report").Rank(entries, ScoutItemKind.All)).FullPath);
        Assert.Null(File(@"C:\plain\report.txt").SearchPath);
        Assert.Equal(@"c:\users\me\cafe\report.txt", entries[0].SearchPath);
    }

    [Fact]
    public async Task AJunctionIsListedButNeverWalkedInto()
    {
        var real = Directory.CreateDirectory(Path.Combine(root, "real")).FullName;
        System.IO.File.WriteAllText(Path.Combine(real, "inside.txt"), "x");
        var link = Path.Combine(root, "link");
        // A junction needs no special right to make, unlike a symbolic link; it is the kind of link discovery must not follow.
        using (var mklink = Process.Start(new ProcessStartInfo("cmd.exe") { ArgumentList = { "/c", "mklink", "/J", link, real }, CreateNoWindow = true, UseShellExecute = false })!)
        { mklink.WaitForExit(); Assert.Equal(0, mklink.ExitCode); }
        Assert.True(ScoutFolderIndex.IsLink(new DirectoryInfo(link)));
        Assert.False(ScoutFolderIndex.IsLink(new DirectoryInfo(real)));
        using var index = new ScoutFolderIndex();
        var inside = await index.SearchAsync(new("inside", root), null, CancellationToken.None);
        Assert.Equal(Path.Combine(real, "inside.txt"), Assert.Single(inside.Entries).FullPath);
        var entry = Assert.Single((await index.SearchAsync(new("link", root), null, CancellationToken.None)).Entries);
        Assert.True(entry.IsFolder); Assert.True(entry.IsReparse);
    }

    [Fact]
    public async Task AFolderThatAppearsWholeIsWalkedAndRenamesAndDeletionsFollowIt()
    {
        using var index = new ScoutFolderIndex();
        var first = await index.SearchAsync(new("leaf", root), null, CancellationToken.None);
        Assert.Empty(first.Entries); Assert.Null(first.Notice);
        var tree = Directory.CreateDirectory(Path.Combine(root, "tree", "deep")).FullName;
        System.IO.File.WriteAllText(Path.Combine(tree, "leaf.txt"), "x");
        var found = await Until(index, r => r.Entries.Count == 1);
        Assert.Equal(Path.Combine(tree, "leaf.txt"), found.Entries[0].FullPath);
        Assert.Null(found.Notice);
        Directory.Move(Path.Combine(root, "tree"), Path.Combine(root, "moved"));
        found = await Until(index, r => r.Entries.Count == 1 && r.Entries[0].FullPath.StartsWith(Path.Combine(root, "moved"), StringComparison.OrdinalIgnoreCase));
        Assert.Equal(Path.Combine(root, "moved", "deep", "leaf.txt"), found.Entries[0].FullPath);
        Directory.Delete(Path.Combine(root, "moved"), true);
        found = await Until(index, r => r.Entries.Count == 0);
        Assert.Null(found.Notice);
    }

    [Fact]
    public async Task AFolderMovedBeforeTheIndexHasSeenItIsStillWalked()
    {
        using var index = new ScoutFolderIndex();
        Assert.Empty((await index.SearchAsync(new("leaf", root), null, CancellationToken.None)).Entries);
        // Made and moved with no search in between, as happens when the index is slow to look: every change is read after the move.
        var tree = Directory.CreateDirectory(Path.Combine(root, "tree", "deep")).FullName;
        System.IO.File.WriteAllText(Path.Combine(tree, "leaf.txt"), "x");
        Directory.Move(Path.Combine(root, "tree"), Path.Combine(root, "moved"));
        var found = await Until(index, r => r.Entries.Count == 1);
        Assert.Equal(Path.Combine(root, "moved", "deep", "leaf.txt"), Assert.Single(found.Entries).FullPath);
    }

    // Waits for the watcher to deliver, with a generous bound (30 s, as a loaded CI runner can be slow to deliver file events); the outcome never depends on how quickly it does.
    private async Task<ScoutResult> Until(ScoutFolderIndex index, Func<ScoutResult, bool> ready)
    {
        ScoutResult result = null!;
        for (var i = 0; i < 300; i++)
        {
            result = await index.SearchAsync(new("leaf", root), null, CancellationToken.None);
            if (ready(result)) return result;
            await Task.Delay(100);
        }
        return result;
    }
}

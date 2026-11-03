using TidyWindow.Next.Core;
using Xunit;

namespace TidyWindow.Next.Tests;

/// <summary>The folder index on a real folder: what it finds, how it follows changes, and that a finished search is a whole one.</summary>
public sealed class ScoutFolderIndexTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "tidywindow-index-" + Guid.NewGuid().ToString("N"));
    public ScoutFolderIndexTests() => Directory.CreateDirectory(root);
    public void Dispose() { try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    private static async Task<ScoutResult> Until(ScoutFolderIndex index, string folder, string text, Func<ScoutResult, bool> done)
    {
        ScoutResult result = null!;
        for (var i = 0; i < 60; i++)
        {
            result = await index.SearchAsync(new(text, folder), null, CancellationToken.None);
            if (done(result)) break;
            await Task.Delay(100);
        }
        return result;
    }

    [Fact]
    public async Task ASearchThatReportsItselfCompleteHasEveryEntryTheWalkFound()
    {
        const int folders = 30, files = 400;
        for (var f = 0; f < folders; f++)
        {
            var folder = Directory.CreateDirectory(Path.Combine(root, "folder" + f)).FullName;
            for (var i = 0; i < files; i++) File.WriteAllText(Path.Combine(folder, $"item-{f}-{i}.txt"), "");
        }
        // The search used to look at the entries as they were a moment before the walk finished, then report the walk as finished.
        using var index = new ScoutFolderIndex();
        var result = await index.SearchAsync(new("item", root), null, CancellationToken.None);
        Assert.True(result.Complete);
        Assert.Equal(folders + folders * files, result.Checked);
    }

    [Fact]
    public async Task ARootGivenWithATrailingSeparatorGivesTheSamePathsAndFollowsChanges()
    {
        File.WriteAllText(Path.Combine(root, "alpha.txt"), "");
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        File.WriteAllText(Path.Combine(root, "sub", "beta.txt"), "");
        using var index = new ScoutFolderIndex();
        var found = (await index.SearchAsync(new("txt", root + Path.DirectorySeparatorChar), null, CancellationToken.None)).Entries.Select(x => x.FullPath).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal([Path.Combine(root, "alpha.txt"), Path.Combine(root, "sub", "beta.txt")], found);
        // A file that appears later lands in the folder the walk already knows, so it is listed once, not twice.
        File.WriteAllText(Path.Combine(root, "sub", "gamma.txt"), "");
        var after = await Until(index, root + Path.DirectorySeparatorChar, "gamma", x => x.Entries.Count > 0);
        Assert.Equal([Path.Combine(root, "sub", "gamma.txt")], after.Entries.Select(x => x.FullPath));
        Assert.Equal(3, (await index.SearchAsync(new("txt", root), null, CancellationToken.None)).Entries.Count);
    }

    [Fact]
    public async Task ARenamedFolderIsFoundAtItsNewPlaceWithEverythingInIt()
    {
        var old = Directory.CreateDirectory(Path.Combine(root, "photos")).FullName;
        Directory.CreateDirectory(Path.Combine(old, "2026"));
        File.WriteAllText(Path.Combine(old, "cover.txt"), "");
        File.WriteAllText(Path.Combine(old, "2026", "trip.txt"), "");
        using var index = new ScoutFolderIndex();
        Assert.Equal(2, (await index.SearchAsync(new("txt", root), null, CancellationToken.None)).Entries.Count);

        var renamed = Path.Combine(root, "pictures");
        Directory.Move(old, renamed);
        var result = await Until(index, root, "txt", x => x.Entries.All(e => e.FullPath.Contains("pictures")) && x.Entries.Count == 2);
        Assert.Equal([Path.Combine(renamed, "2026", "trip.txt"), Path.Combine(renamed, "cover.txt")], result.Entries.Select(x => x.FullPath).Order(StringComparer.Ordinal).ToArray());
        Assert.Empty((await index.SearchAsync(new("photos", root), null, CancellationToken.None)).Entries);
    }

    [Fact]
    public async Task ADeletedFolderTakesEverythingInItOutOfTheResults()
    {
        var doomed = Directory.CreateDirectory(Path.Combine(root, "doomed")).FullName;
        Directory.CreateDirectory(Path.Combine(doomed, "inner"));
        File.WriteAllText(Path.Combine(doomed, "one.txt"), "");
        File.WriteAllText(Path.Combine(doomed, "inner", "two.txt"), "");
        File.WriteAllText(Path.Combine(root, "kept.txt"), "");
        using var index = new ScoutFolderIndex();
        Assert.Equal(3, (await index.SearchAsync(new("txt", root), null, CancellationToken.None)).Entries.Count);
        Directory.Delete(doomed, true);
        var result = await Until(index, root, "txt", x => x.Entries.Count == 1);
        Assert.Equal([Path.Combine(root, "kept.txt")], result.Entries.Select(x => x.FullPath));
        Assert.Single((await index.SearchAsync(new("kept", root), null, CancellationToken.None)).Entries);
    }

    [Fact]
    public async Task NamesWithAccentsAndWideLettersAreFoundTheSameWayAsBefore()
    {
        foreach (var name in new[] { "Café.txt", "ＣＡＦＥ-wide.txt", "naïve résumé.txt", "plain.txt" }) File.WriteAllText(Path.Combine(root, name), "");
        using var index = new ScoutFolderIndex();
        var cafe = (await index.SearchAsync(new("cafe", root), null, CancellationToken.None)).Entries.Select(x => x.Name).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["Café.txt", "ＣＡＦＥ-wide.txt"], cafe);
        Assert.Equal(["naïve résumé.txt"], (await index.SearchAsync(new("resume naive", root), null, CancellationToken.None)).Entries.Select(x => x.Name));
    }
}

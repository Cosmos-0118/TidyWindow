using System.Diagnostics;
using TidyWindow.Next.Core;
using Xunit;
using Xunit.Abstractions;

namespace TidyWindow.Next.Tests;

public sealed class ScoutTests(ITestOutputHelper output) : IDisposable
{
    private readonly string fixture = Path.Combine(Path.GetTempPath(), "tidywindow-scout-" + Guid.NewGuid().ToString("N"));
    private ScoutEntry Entry(string name, bool folder = false) => new(Path.Combine(fixture, name), folder, folder ? null : 42, null);
    [Fact]
    public void RankingPrioritizesExactNamesThenPrefixesAndHandlesTypos()
    {
        var entries = new[] { Entry("old-report-2025.txt"), Entry("report-notes.txt"), Entry("report.txt"), Entry("repoort.txt"), Entry("unrelated.txt") };
        var result = new ScoutQuery("report").Rank(entries, ScoutItemKind.All);
        Assert.Equal("report.txt", result[0].Name); Assert.Equal("report-notes.txt", result[1].Name);
        Assert.DoesNotContain(result, x => x.Name == "unrelated.txt");
        Assert.Equal("report.txt", Assert.Single(new ScoutQuery("reprot").Rank([entries[2]], ScoutItemKind.All)).Name);
        var repeated = string.Join(" ", Enumerable.Repeat("report", 12));
        Assert.Equal(repeated + ".txt", new ScoutQuery(repeated).Rank([Entry("report.txt"), Entry(repeated + ".txt")], ScoutItemKind.All)[0].Name);
    }
    [Fact]
    public void SearchSupportsPathsQuotedPhrasesAndUnicode()
    {
        var entries = new[] { Entry(Path.Combine("year 2026", "Vacation Photos.jpg")), Entry("Café.txt"), Entry("Other.txt") };
        Assert.Single(new ScoutQuery("2026 \"vacation photos\"").Rank(entries, ScoutItemKind.All));
        Assert.Equal("Café.txt", Assert.Single(new ScoutQuery("Ｃａｆｅ").Rank(entries, ScoutItemKind.All)).Name);
        Assert.Empty(new ScoutQuery("2027 photos").Rank(entries, ScoutItemKind.All));
    }
    [Fact]
    public void CandidateQueriesNeverLetUserTextBecomeEverythingOperators()
    {
        var expression = new ScoutQuery("report|x:abc").NameExpression(ScoutItemKind.Files, fixture);
        Assert.StartsWith("file: ", expression);
        Assert.Contains("nowildcards:\"report|x:abc\"", expression);
        Assert.EndsWith("\"" + fixture + "\\\"", expression);
        var smart = new ScoutQuery("report").SmartExpression(ScoutItemKind.All, null);
        Assert.Contains("reprot", smart); Assert.Contains("r(?>[^e]*)e", smart);
        Assert.True(smart!.Length < 3000);
    }
    [Fact]
    public void ResultsAreBoundedFilteredAndCancellationIsObserved()
    {
        var entries = Enumerable.Range(0, 1000).Select(i => Entry("file" + i, i % 2 == 0)).ToArray();
        Assert.All(new ScoutQuery("file").Rank(entries, ScoutItemKind.Folders), x => Assert.True(x.IsFolder));
        Assert.All(new ScoutQuery("file").Rank(entries, ScoutItemKind.Files), x => Assert.False(x.IsFolder));
        Assert.Equal(150, new ScoutQuery("file").Rank(entries, ScoutItemKind.All).Count);
        Assert.Throws<OperationCanceledException>(() => new ScoutQuery("file").Rank(entries, ScoutItemKind.All, token: new(true)));
    }
    [Fact]
    public void EmptyQuotedAndExcessiveTermsAreRejectedWithoutSilentlyDroppingTerms()
    {
        Assert.Throws<ArgumentException>(() => new ScoutQuery("\"\""));
        Assert.Throws<ArgumentException>(() => new ScoutQuery(string.Join(" ", Enumerable.Repeat("word", 13))));
        Assert.Empty(new ScoutQuery("report").Rank([Entry("report.txt")], ScoutItemKind.All, 0));
        Assert.Throws<OperationCanceledException>(() => new ScoutQuery("report").Rank([], ScoutItemKind.All, token: new(true)));
    }
    [Fact]
    public async Task FolderChangesRefreshAndUnavailableRootsNeverReturnPreviousScope()
    {
        var first = Directory.CreateDirectory(Path.Combine(fixture, "first")).FullName;
        var second = Directory.CreateDirectory(Path.Combine(fixture, "second")).FullName;
        File.WriteAllText(Path.Combine(first, "report-old.txt"), "fixture");
        File.WriteAllText(Path.Combine(second, "report-new.txt"), "fixture");
        using var index = new ScoutFolderIndex();
        Assert.Equal("report-old.txt", Assert.Single((await index.SearchAsync(new("report", first), null, CancellationToken.None)).Entries).Name);
        Assert.Equal("report-new.txt", Assert.Single((await index.SearchAsync(new("report", second), null, CancellationToken.None)).Entries).Name);
        index.Refresh();
        var refreshed = await index.SearchAsync(new("report", first), null, CancellationToken.None);
        Assert.Equal("report-old.txt", Assert.Single(refreshed.Entries).Name);
        Directory.Delete(first, true);
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => index.SearchAsync(new("report", first), null, CancellationToken.None));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => index.SearchAsync(new("report", second), null, new(true)));
    }
    [Fact]
    public async Task RealFolderDiscoverySearchesHiddenEntriesWithoutReadingContentsAndReusesItsIndex()
    {
        Directory.CreateDirectory(Path.Combine(fixture, "nested"));
        File.WriteAllText(Path.Combine(fixture, "nested", "hello-report.txt"), "not a content search");
        File.WriteAllText(Path.Combine(fixture, "secret.txt"), "hello-report appears in content only");
        File.SetAttributes(Path.Combine(fixture, "secret.txt"), FileAttributes.Hidden);
        using var index = new ScoutFolderIndex();
        var result = await index.SearchAsync(new("hello-report", fixture), null, CancellationToken.None);
        Assert.True(result.Complete); Assert.Equal("hello-report.txt", Assert.Single(result.Entries).Name);
        var hidden = await index.SearchAsync(new("secret", fixture), null, CancellationToken.None);
        Assert.Equal("secret.txt", Assert.Single(hidden.Entries).Name);
        Assert.Equal(result.Checked, hidden.Checked);
    }
    [Fact]
    public async Task LiveFileCreationAndDeletionAreReconciled()
    {
        Directory.CreateDirectory(fixture);
        using var index = new ScoutFolderIndex();
        await index.SearchAsync(new("report", fixture), null, CancellationToken.None);
        var path = Path.Combine(fixture, "report.txt"); File.WriteAllText(path, "fixture");
        ScoutResult? found = null;
        for (var i = 0; i < 20; i++)
        { found = await index.SearchAsync(new("report", fixture), null, CancellationToken.None); if (found.Entries.Count > 0) break; await Task.Delay(50); }
        Assert.Single(found!.Entries);
        File.Delete(path);
        for (var i = 0; i < 20; i++)
        { found = await index.SearchAsync(new("report", fixture), null, CancellationToken.None); if (found.Entries.Count == 0) break; await Task.Delay(50); }
        Assert.Empty(found.Entries);
    }
    [Fact]
    public async Task WorkerFailureIsAVisibleErrorRatherThanNoMatches()
    {
        var service = new ScoutService(new FailedRunner(), Path.Combine(fixture, "missing-worker.exe"));
        var result = await service.SearchAsync(new("report", null), true, null, CancellationToken.None);
        Assert.NotNull(result.Error); Assert.Empty(result.Entries);
        service.Dispose();
    }
    [Fact]
    public void WarmRankingLatencyIsMeasuredOnTwoHundredThousandEntries()
    {
        var entries = Enumerable.Range(0, 200000).Select(i => Entry("document-" + i + ".txt")).Append(Entry("budget-report.pdf")).ToArray();
        var query = new ScoutQuery("budget report");
        query.Rank(entries, ScoutItemKind.All);
        var timer = Stopwatch.StartNew(); var result = query.Rank(entries, ScoutItemKind.All);
        output.WriteLine($"Warm local ranking: {entries.Length:N0} entries in {timer.Elapsed.TotalMilliseconds:F1} ms.");
        Assert.Equal("budget-report.pdf", result[0].Name);
    }
    public void Dispose()
    {
        if (!Directory.Exists(fixture)) return;
        foreach (var file in Directory.EnumerateFiles(fixture, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(fixture, true);
    }
    private sealed class FailedRunner : ICommandRunner
    { public Task<CommandResult> RunAsync(CommandSpec command, CancellationToken cancellationToken = default) => Task.FromResult(new CommandResult(1, "", "Worker failed")); }
}

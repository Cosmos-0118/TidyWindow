using System.Text.Json;
using TidyWindow.Next.Core;
using Xunit;

namespace TidyWindow.Next.Tests;

public sealed class ScoutHardeningTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "tw-scout-" + Guid.NewGuid().ToString("N")[..8]);
    private const string Home = @"C:\Users\me";
    public ScoutHardeningTests() => Directory.CreateDirectory(root);
    public void Dispose() { try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    private static ScoutEntry File(string path, bool folder = false) => new(path, folder, folder ? null : 10, null);

    [Fact]
    public void WildcardsAndBareExtensionsMatchNamesAsAWhole()
    {
        var entries = new[] { File(@"C:\a\photo.png"), File(@"C:\a\photo.png.txt"), File(@"C:\a\png.txt"), File(@"C:\a\logo.PNG"), File(@"C:\a\data.csv") };
        Assert.Equal(["logo.PNG", "photo.png"], new ScoutQuery("*.png").Rank(entries, ScoutItemKind.All).Select(x => x.Name).Order().ToArray());
        Assert.Equal(["logo.PNG", "photo.png"], new ScoutQuery(".png").Rank(entries, ScoutItemKind.All).Select(x => x.Name).Order().ToArray());
        Assert.Equal(["photo.png", "photo.png.txt"], new ScoutQuery("photo*").Rank(entries, ScoutItemKind.All).Select(x => x.Name).Order().ToArray());
        Assert.Equal(["data.csv"], new ScoutQuery("d?ta.csv").Rank(entries, ScoutItemKind.All).Select(x => x.Name).ToArray());
    }

    [Fact]
    public void WildcardsUseNativeQuerySyntaxAndStayNameOnlyInPathQueries()
    {
        var query = new ScoutQuery("*.png vacation");
        Assert.Contains("*.png", query.NameExpression(ScoutItemKind.All, null));
        Assert.DoesNotContain("nopath:", query.NameExpression(ScoutItemKind.All, null));
        Assert.Contains("nopath:*.png", query.PathExpression(ScoutItemKind.All, null));
        Assert.Null(query.ExactExpression(ScoutItemKind.All));
        Assert.Null(query.SmartExpression(ScoutItemKind.All, null));
    }

    [Fact]
    public void QueryTextCannotEscapeItsQuotesOrBecomeOperators()
    {
        var query = new ScoutQuery("a\"b !x <y> |z");
        var text = query.NameExpression(ScoutItemKind.All, null);
        foreach (var term in new[] { "nowildcards:\"ab\"", "nowildcards:\"!x\"", "nowildcards:\"<y>\"", "nowildcards:\"|z\"" }) Assert.Contains(term, text);
        Assert.Null(query.SmartExpression(ScoutItemKind.All, null));
        Assert.Equal("wholefilename:\"report\"", new ScoutQuery("report").ExactExpression(ScoutItemKind.All));
        Assert.StartsWith("folder: ", new ScoutQuery("report").ExactExpression(ScoutItemKind.Folders));
    }

    [Fact]
    public void NearMatchQueriesAreBoundedAndOnlyForSuitableTerms()
    {
        Assert.NotNull(new ScoutQuery("reprot").SmartExpression(ScoutItemKind.All, null));
        Assert.Null(new ScoutQuery("ab").SmartExpression(ScoutItemKind.All, null));
        Assert.Null(new ScoutQuery("one two three four").SmartExpression(ScoutItemKind.All, null));
        Assert.True(new ScoutQuery("internationalization").SmartExpression(ScoutItemKind.All, null)!.Length < 2500);
        var swapped = new ScoutQuery("reprot").SmartExpression(ScoutItemKind.All, null)!;
        Assert.Contains("*report*", swapped);
    }

    [Fact]
    public void YourOwnFoldersOutrankEquallyGoodMatchesInSystemFolders()
    {
        var entries = new[]
        {
            File(@"C:\Windows\System32\readme.txt"), File(@"C:\Program Files\Tool\readme.txt"), File(@"C:\Users\me\Documents\Project\readme.txt"),
            File(@"C:\Users\me\AppData\Local\Cache\readme.txt"), File(@"C:\Users\me\Desktop\readme.txt"), File(@"C:\Users\me\Project\node_modules\pkg\readme.txt")
        };
        var ranked = new ScoutQuery("readme", Home).Rank(entries, ScoutItemKind.All).Select(x => x.FullPath).ToArray();
        Assert.Equal(@"C:\Users\me\Desktop\readme.txt", ranked[0]);
        Assert.Equal(@"C:\Users\me\Documents\Project\readme.txt", ranked[1]);
        Assert.True(Array.IndexOf(ranked, @"C:\Windows\System32\readme.txt") > Array.IndexOf(ranked, @"C:\Users\me\Project\node_modules\pkg\readme.txt") - 1);
        Assert.Contains(@"C:\Windows\System32\readme.txt", ranked.TakeLast(2));
    }

    [Fact]
    public void LocationNeverOutweighsHowWellTheNameMatches()
    {
        var entries = new[] { File(@"C:\Windows\System32\report.txt"), File(@"C:\Users\me\Desktop\old-report-copy.txt") };
        Assert.Equal("report.txt", new ScoutQuery("report", Home).Rank(entries, ScoutItemKind.All)[0].Name);
        var prefix = new[] { File(@"C:\Windows\report-final.txt"), File(@"C:\Users\me\Desktop\my-report.txt") };
        Assert.Equal("report-final.txt", new ScoutQuery("report", Home).Rank(prefix, ScoutItemKind.All)[0].Name);
    }

    [Theory]
    [InlineData(150, "stderr", "runtime it needs was not found")]
    [InlineData(1, "You must install or update .NET to run this application.", "runtime it needs was not found")]
    [InlineData(2, "Everything64.dll could not be loaded: x", "Everything library")]
    [InlineData(-532462766, "", "code -532462766")]
    public void WorkerFailuresExplainThemselves(int code, string detail, string expected)
        => Assert.Contains(expected, ScoutWorkerErrors.Describe(code, detail));

    [Fact]
    public void JunkFoldersAreWalkedLast()
    {
        foreach (var name in new[] { "node_modules", ".git", "AppData", "__pycache__", "$Recycle.Bin" }) Assert.True(ScoutFolderIndex.IsLowPriority(name), name);
        foreach (var name in new[] { "Documents", "src", "Projects" }) Assert.False(ScoutFolderIndex.IsLowPriority(name), name);
    }

    [Fact]
    public async Task FolderSearchFindsFilesInOrdinaryFoldersBeforeToolingFolders()
    {
        Directory.CreateDirectory(Path.Combine(root, "node_modules", "pkg"));
        Directory.CreateDirectory(Path.Combine(root, "docs"));
        System.IO.File.WriteAllText(Path.Combine(root, "docs", "budget.txt"), "x");
        System.IO.File.WriteAllText(Path.Combine(root, "node_modules", "pkg", "budget.js"), "x");
        using var index = new ScoutFolderIndex();
        var result = await index.SearchAsync(new ScoutRequest("budget", root), null, CancellationToken.None);
        Assert.Equal(2, result.Entries.Count);
        Assert.True(result.Complete);
        Assert.EndsWith("budget.txt", new ScoutQuery("budget").Rank(result.Entries, ScoutItemKind.All, token: default)[0].FullPath);
    }

    // A stand-in helper written as a batch file: it answers every request line with a ready reply, numbering replies like the real one.
    private string Stub(string body)
    {
        var path = Path.Combine(root, "host" + Guid.NewGuid().ToString("N")[..4] + ".cmd");
        System.IO.File.WriteAllText(path, body);
        return path;
    }

    private const string Echo = "@echo off\r\nsetlocal enabledelayedexpansion\r\nset n=0\r\n:loop\r\nset line=\r\nset /p line=\r\nif \"!line!\"==\"\" goto :eof\r\nset /a n+=1\r\necho {\"Id\":!n!,\"Reply\":{\"Ready\":true,\"Entries\":[],\"Total\":7,\"Limited\":false,\"Error\":null,\"Indexing\":false}}\r\ngoto loop\r\n";

    [Fact]
    public async Task ALongRunningHelperAnswersManyRequestsFromOneProcess()
    {
        using var worker = new PersistentScoutWorker(Stub(Echo));
        for (var i = 0; i < 5; i++)
        {
            var reply = await worker.QueryAsync(i == 0 ? null : new ScoutRequest("x", null), false, CancellationToken.None);
            Assert.True(reply.Ready, reply.Error);
            Assert.Equal(7, reply.Total);
        }
    }

    [Fact]
    public async Task AHelperThatDiesIsReportedWithItsExitCodeAndReplacedOnTheNextRequest()
    {
        using var worker = new PersistentScoutWorker(Stub("@exit /b 7\r\n"));
        var reply = await worker.QueryAsync(null, false, CancellationToken.None);
        Assert.False(reply.Ready);
        Assert.Contains("code 7", reply.Error);
        var again = await worker.QueryAsync(null, false, CancellationToken.None);
        Assert.Contains("code 7", again.Error);
    }

    [Fact]
    public async Task AHelperThatCannotFindTheRuntimeGivesActionableAdvice()
    {
        using var worker = new PersistentScoutWorker(Stub("@echo You must install or update .NET to run this application.\r\n@exit /b 150\r\n"));
        var reply = await worker.QueryAsync(null, false, CancellationToken.None);
        Assert.Contains("runtime", reply.Error);
        Assert.Contains("Reinstall", reply.Error);
    }

    [Fact]
    public async Task AMissingHelperIsAMessageNotACrash()
    {
        using var worker = new PersistentScoutWorker(Path.Combine(root, "nothing.exe"));
        var reply = await worker.QueryAsync(null, false, CancellationToken.None);
        Assert.False(reply.Ready);
        Assert.Contains("unavailable", reply.Error);
    }

    [Fact]
    public async Task CancellingWhileTypingThrowsForTheCallerAndLeavesTheHelperUsable()
    {
        using var worker = new PersistentScoutWorker(Stub(Echo));
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.QueryAsync(new ScoutRequest("x", null), false, cancel.Token));
        Assert.True((await worker.QueryAsync(null, false, CancellationToken.None)).Ready);
    }

    [Fact]
    public async Task ExactResultsAreShownBeforeTheSlowerNearMatchSearchAndThenMergedIn()
    {
        var strict = new FakeWorker(new ScoutReply(true, [File(@"C:\Users\me\one.txt")], 1));
        var near = new FakeWorker(new ScoutReply(true, [File(@"C:\Users\me\reprot.txt")], 1)) { Fuzzy = true };
        using var service = new ScoutService(new ProcessRunner(), "unused", worker: strict, fuzzyWorker: near);
        var seen = new List<ScoutResult>();
        var result = await service.SearchAsync(new ScoutRequest("report", null), true, new Capture(seen), CancellationToken.None);
        var first = Assert.Single(seen);
        Assert.Contains("close matches", first.Engine);
        Assert.Single(first.Entries);
        Assert.Contains(result.Entries, x => x.Name == "reprot.txt");
        Assert.DoesNotContain("close matches", result.Engine);
        Assert.Equal(1, near.Calls);
    }

    [Fact]
    public async Task NearMatchSearchIsSkippedForExactHitsWildcardsAndFailures()
    {
        var near = new FakeWorker(new ScoutReply(true, [], 0)) { Fuzzy = true };
        using var exact = new ScoutService(new ProcessRunner(), "unused", worker: new FakeWorker(new ScoutReply(true, [File(@"C:\Users\me\report.txt")], 1)), fuzzyWorker: near);
        await exact.SearchAsync(new ScoutRequest("report", null), true, null, CancellationToken.None);
        using var glob = new ScoutService(new ProcessRunner(), "unused", worker: new FakeWorker(new ScoutReply(true, [], 0)), fuzzyWorker: near);
        await glob.SearchAsync(new ScoutRequest("*.zzz", null), true, null, CancellationToken.None);
        using var failed = new ScoutService(new ProcessRunner(), "unused", worker: new FakeWorker(new ScoutReply(false, [], Error: "down")), fuzzyWorker: near);
        var result = await failed.SearchAsync(new ScoutRequest("report", null), true, null, CancellationToken.None);
        Assert.Equal("down", result.Error);
        Assert.Equal(0, near.Calls);
    }

    [Fact]
    public async Task AFailedNearMatchSearchKeepsTheExactResults()
    {
        var strict = new FakeWorker(new ScoutReply(true, [File(@"C:\Users\me\one.txt")], 1));
        var near = new FakeWorker(new ScoutReply(false, [], Error: "slow")) { Fuzzy = true };
        using var service = new ScoutService(new ProcessRunner(), "unused", worker: strict, fuzzyWorker: near);
        var result = await service.SearchAsync(new ScoutRequest("on", null), true, null, CancellationToken.None);
        Assert.Single(result.Entries);
        Assert.Null(result.Error);
    }

    private sealed class FakeWorker(ScoutReply reply) : IScoutWorker
    {
        public bool Fuzzy { get; init; }
        public int Calls;
        public bool SupportsFuzzy => Fuzzy;
        public Task<ScoutReply> QueryAsync(ScoutRequest? request, bool fuzzy, CancellationToken token) { Calls++; return Task.FromResult(reply); }
        public void Dispose() { }
    }

    private sealed class Capture(List<ScoutResult> list) : IProgress<ScoutResult> { public void Report(ScoutResult value) => list.Add(value); }
}

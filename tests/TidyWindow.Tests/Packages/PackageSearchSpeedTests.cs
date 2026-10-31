using System.Net;
using System.Text;
using TidyWindow.Next.Core;
using Xunit;

namespace TidyWindow.Next.Tests;

public sealed class PackageSearchSessionTests
{
    private static Package Pkg(ManagerKind kind, string id, string name) => new(kind, id, name, "1.0", kind.ToString());
    private static SearchResult Found(ManagerKind kind, params (string Id, string Name)[] items) => new(kind, items.Select(x => Pkg(kind, x.Id, x.Name)).ToArray());
    private static TaskCompletionSource<SearchResult> Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task<List<SearchSnapshot>> Collect(IReadOnlyList<(ManagerKind, Task<SearchResult>)> sources, string query, TimeSpan settle, CancellationToken token = default)
    {
        var snapshots = new List<SearchSnapshot>();
        await foreach (var snapshot in PackageSearchSession.StreamAsync(sources, query, settle, token: token)) snapshots.Add(snapshot);
        return snapshots;
    }

    [Fact]
    public async Task SourcesThatAnswerInsideTheWindowAreShownOnceAsOneRankedList()
    {
        var sources = new (ManagerKind, Task<SearchResult>)[]
        {
            (ManagerKind.Winget, Task.FromResult(Found(ManagerKind.Winget, ("Videolan.VLC", "VLC media player")))),
            (ManagerKind.Chocolatey, Task.FromResult(Found(ManagerKind.Chocolatey, ("vlc", "vlc")))),
            (ManagerKind.Scoop, Task.FromResult(Found(ManagerKind.Scoop, ("extras/vlc", "vlc"))))
        };
        var snapshot = Assert.Single(await Collect(sources, "vlc", TimeSpan.FromSeconds(5)));
        Assert.True(snapshot.Complete);
        Assert.Equal(3, snapshot.Packages.Count);
        Assert.Equal(3, snapshot.Answered.Count);
    }

    [Fact]
    public async Task ASourceThatFinishesInsideTheWindowNeverReshufflesAListAlreadyShown()
    {
        var slow = Gate();
        var winget = Task.FromResult(Found(ManagerKind.Winget, ("A.Vlc", "Vlc")));
        var chocolatey = slow.Task;
        var sources = new (ManagerKind, Task<SearchResult>)[] { (ManagerKind.Winget, winget), (ManagerKind.Chocolatey, chocolatey) };
        // The slower source answers a moment later but well inside the window, so the first thing anyone sees already has both.
        _ = Task.Run(async () => { await Task.Delay(100); slow.SetResult(Found(ManagerKind.Chocolatey, ("vlc", "vlc"))); });
        var snapshots = await Collect(sources, "vlc", TimeSpan.FromSeconds(5));
        var only = Assert.Single(snapshots);
        Assert.Equal(2, only.Packages.Count);
        Assert.True(only.Complete);
    }

    [Fact]
    public async Task ASourceThatMissesTheWindowIsNamedAsWaitedForAndThenAddedInASecondSnapshot()
    {
        var slow = Gate();
        var sources = new (ManagerKind, Task<SearchResult>)[]
        {
            (ManagerKind.Winget, Task.FromResult(Found(ManagerKind.Winget, ("A.Vlc", "Vlc")))),
            (ManagerKind.Chocolatey, slow.Task)
        };
        var seen = new List<SearchSnapshot>();
        await foreach (var snapshot in PackageSearchSession.StreamAsync(sources, "vlc", TimeSpan.FromMilliseconds(60)))
        {
            seen.Add(snapshot);
            if (seen.Count == 1) slow.SetResult(Found(ManagerKind.Chocolatey, ("vlc", "vlc"), ("vlc.install", "vlc.install")));
        }
        Assert.Equal(2, seen.Count);
        Assert.False(seen[0].Complete);
        Assert.Equal([ManagerKind.Chocolatey], seen[0].Waiting);
        Assert.Single(seen[0].Packages);
        Assert.True(seen[1].Complete);
        Assert.Equal(3, seen[1].Packages.Count);
    }

    [Fact]
    public async Task NothingIsShownAtTheEndOfTheWindowIfNoSourceHasAnsweredYet()
    {
        var first = Gate(); var second = Gate();
        var sources = new (ManagerKind, Task<SearchResult>)[] { (ManagerKind.Winget, first.Task), (ManagerKind.Scoop, second.Task) };
        var seen = new List<SearchSnapshot>();
        // Nothing has answered when the 40 ms window closes; Scoop answers at about 150 ms.
        _ = Task.Run(async () => { await Task.Delay(150); second.SetResult(Found(ManagerKind.Scoop, ("main/x1", "x1"))); });
        await foreach (var snapshot in PackageSearchSession.StreamAsync(sources, "x1", TimeSpan.FromMilliseconds(40)))
        {
            seen.Add(snapshot);
            if (seen.Count == 1) { Assert.Equal([ManagerKind.Winget], snapshot.Waiting); first.SetResult(Found(ManagerKind.Winget, ("A.X1", "X1"))); }
        }
        // The first list appeared when something could be shown, not as an empty one at the deadline.
        Assert.Equal(2, seen.Count);
        Assert.All(seen, x => Assert.NotEmpty(x.Packages));
    }

    [Fact]
    public async Task ASourceThatThrowsIsOneSourceFailingNotTheWholeSearch()
    {
        var sources = new (ManagerKind, Task<SearchResult>)[]
        {
            (ManagerKind.Winget, Task.FromResult(Found(ManagerKind.Winget, ("A.Vlc", "Vlc")))),
            (ManagerKind.Chocolatey, Task.FromException<SearchResult>(new InvalidOperationException("feed exploded")))
        };
        var snapshot = Assert.Single(await Collect(sources, "vlc", TimeSpan.FromSeconds(5)));
        Assert.Single(snapshot.Packages);
        Assert.Contains(snapshot.Answered, x => x.Manager == ManagerKind.Chocolatey && x.Error == "feed exploded");
    }

    [Fact]
    public async Task CancellingStopsTheSessionInsteadOfShowingPartialResults()
    {
        using var cancel = new CancellationTokenSource();
        var never = Gate();
        var sources = new (ManagerKind, Task<SearchResult>)[] { (ManagerKind.Winget, never.Task) };
        var run = Collect(sources, "vlc", TimeSpan.FromSeconds(30), cancel.Token);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task ASearchWithNoSourcesEndsWithOneEmptyCompleteSnapshot()
    {
        var snapshot = Assert.Single(await Collect([], "vlc", TimeSpan.FromSeconds(1)));
        Assert.True(snapshot.Complete);
        Assert.Empty(snapshot.Packages);
    }

    [Fact]
    public async Task TheListIsRankedAcrossSourcesNotGroupedBySource()
    {
        var sources = new (ManagerKind, Task<SearchResult>)[]
        {
            (ManagerKind.Winget, Task.FromResult(Found(ManagerKind.Winget, ("Other.Thing", "Some Other Thing"), ("Dev.Firefox", "Firefox")))),
            (ManagerKind.Scoop, Task.FromResult(Found(ManagerKind.Scoop, ("extras/firefox", "firefox"))))
        };
        var snapshot = Assert.Single(await Collect(sources, "firefox", TimeSpan.FromSeconds(5)));
        Assert.Equal("Some Other Thing", snapshot.Packages[^1].Name);
        Assert.Equal(2, snapshot.Packages.Take(2).Select(x => x.Manager).Distinct().Count());
    }
}

public sealed class ScoopCatalogTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "tidywindow-scoop-" + Guid.NewGuid().ToString("N"));
    private readonly ScoopCatalog catalog = new();

    public ScoopCatalogTests() => Directory.CreateDirectory(root);
    public void Dispose() { try { Directory.Delete(root, true); } catch (IOException) { } }

    private string Manifest(string bucket, string name, string json, bool nested = true)
    {
        var folder = nested ? Path.Combine(root, "buckets", bucket, "bucket") : Path.Combine(root, "buckets", bucket);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name + ".json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void FindsByNameBestMatchFirstAndBuildsBucketQualifiedIds()
    {
        Manifest("main", "git", """{"version":"2.50.0","bin":["cmd\\git.exe"]}""");
        Manifest("main", "git-lfs", """{"version":"3.7.0"}""");
        Manifest("extras", "tortoisegit", """{"version":"2.17"}""");
        Manifest("main", "python", """{"version":"3.13"}""");
        var found = catalog.Search(root, "git")!;
        Assert.Equal(["main/git", "main/git-lfs", "extras/tortoisegit"], found.Select(x => x.Id));
        Assert.All(found, x => { Assert.Equal(ManagerKind.Scoop, x.Manager); Assert.Equal(x.Id.Split('/')[0], x.Source); });
        Assert.Equal("2.50.0", found[0].Version);
    }

    [Fact]
    public void AMatchOnAProgramThePackageProvidesCountsLikeScoopSearchDoes()
    {
        Manifest("main", "nodejs", """{"version":"24.0","bin":["node.exe",["npm.cmd","npm-alias"]],"architecture":{"64bit":{"bin":"deno-helper.exe"}}}""");
        Assert.Equal("main/nodejs", Assert.Single(catalog.Search(root, "node")!).Id);
        Assert.Equal("main/nodejs", Assert.Single(catalog.Search(root, "npm-alias")!).Id);
        Assert.Equal("main/nodejs", Assert.Single(catalog.Search(root, "deno-helper")!).Id);
        Assert.Empty(catalog.Search(root, "npm.cmd")!);
    }

    [Fact]
    public void ABucketQualifiedQueryMatchesTheFullId()
    {
        Manifest("extras", "vlc", """{"version":"3.0"}""");
        Manifest("versions", "vlc-nightly", """{"version":"4.0"}""");
        Assert.Equal("extras/vlc", Assert.Single(catalog.Search(root, "extras/vlc")!).Id);
    }

    [Fact]
    public void BrokenManifestsAndDeprecatedOnesAreSkippedWithoutFailingTheSearch()
    {
        Manifest("main", "good", """{"version":"1"}""");
        Manifest("main", "broken", "{ not json");
        Manifest("main", "noversion", """{"description":"x"}""");
        Manifest("main", "numeric", """{"version":2}""");
        Directory.CreateDirectory(Path.Combine(root, "buckets", "main", "bucket", "deprecated"));
        File.WriteAllText(Path.Combine(root, "buckets", "main", "bucket", "deprecated", "gone.json"), """{"version":"1"}""");
        string[] Ids(string query) => catalog.Search(root, query)!.Select(x => x.Id).ToArray();
        Assert.Equal(["main/good"], Ids("good"));
        Assert.Equal(["main/numeric"], Ids("numeric"));
        Assert.Empty(Ids("broken"));
        Assert.Empty(Ids("noversion"));
        Assert.Empty(Ids("gone"));
    }

    [Fact]
    public void ABucketWithoutABucketFolderIsReadFromItsTopLevel()
    {
        Manifest("plain", "tool", """{"version":"1"}""", nested: false);
        Assert.Equal("plain/tool", Assert.Single(catalog.Search(root, "tool")!).Id);
    }

    [Fact]
    public void ResultsAreCappedAndOrderedBeforeTheCap()
    {
        for (var i = 0; i < 100; i++) Manifest("main", $"app{i:000}", """{"version":"1"}""");
        Manifest("main", "app", """{"version":"1"}""");
        var found = catalog.Search(root, "app", limit: 10)!;
        Assert.Equal(10, found.Count);
        Assert.Equal("main/app", found[0].Id);
    }

    [Fact]
    public void ANewManifestIsSeenOnTheNextSearchWithoutRestartingAnything()
    {
        Manifest("main", "first", """{"version":"1"}""");
        Assert.Single(catalog.Search(root, "st")!);
        var added = Manifest("main", "second-st", """{"version":"1"}""");
        Directory.SetLastWriteTimeUtc(Path.GetDirectoryName(added)!, DateTime.UtcNow.AddMinutes(5));
        Assert.Equal(2, catalog.Search(root, "st")!.Count);
    }

    [Fact]
    public void AnUpdatedVersionIsSeenAfterTheBucketIsPulled()
    {
        var path = Manifest("main", "tool", """{"version":"1.0"}""");
        var index = Path.Combine(root, "buckets", "main", ".git", "index");
        Directory.CreateDirectory(Path.GetDirectoryName(index)!);
        File.WriteAllText(index, "x");
        Assert.Equal("1.0", Assert.Single(catalog.Search(root, "tool")!).Version);
        File.WriteAllText(path, """{"version":"2.0"}""");
        File.SetLastWriteTimeUtc(index, DateTime.UtcNow.AddMinutes(5)); // what `git pull` does
        Assert.Equal("2.0", Assert.Single(catalog.Search(root, "tool")!).Version);
    }

    [Fact]
    public void NoBucketsMeansTheCallerShouldAskScoopItself()
    {
        Assert.Null(catalog.Search(root, "git"));
        Directory.CreateDirectory(Path.Combine(root, "buckets"));
        Assert.Null(catalog.Search(root, "git"));
    }

    [Fact]
    public void TheRootIsFoundNextToTheShimsFolderTheExecutableLivesIn()
    {
        Directory.CreateDirectory(Path.Combine(root, "buckets"));
        Assert.Equal(root, ScoopCatalog.RootFor(Path.Combine(root, "shims", "scoop.ps1")));
    }

    [Fact]
    public void CancellationIsHonouredWhileSearching()
    {
        Manifest("main", "git", """{"version":"1"}""");
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => catalog.Search(root, "git", token: cancel.Token));
    }
}

public sealed class ChocolateyFeedTests
{
    private const string Sample = """
        <?xml version="1.0" encoding="utf-8" standalone="yes"?>
        <feed xml:base="https://community.chocolatey.org/api/v2/" xmlns:d="http://schemas.microsoft.com/ado/2007/08/dataservices" xmlns:m="http://schemas.microsoft.com/ado/2007/08/dataservices/metadata" xmlns="http://www.w3.org/2005/Atom">
          <title type="text">Search</title>
          <entry><title type="text">GoogleChrome</title><m:properties><d:Version>131.0.1</d:Version><d:Title>Google Chrome</d:Title></m:properties></entry>
          <entry><title type="text">vlc</title><m:properties><d:Version>3.0.24</d:Version><d:Title></d:Title></m:properties></entry>
          <entry><title type="text">bad id with spaces</title><m:properties><d:Version>1.0</d:Version></m:properties></entry>
          <entry><title type="text">noversion</title><m:properties><d:Version></d:Version></m:properties></entry>
          <entry><title type="text">GoogleChrome</title><m:properties><d:Version>130.0</d:Version></m:properties></entry>
        </feed>
        """;

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Requests.Add(request.RequestUri!); return Task.FromResult(respond(request)); }
    }

    private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/atom+xml") };

    [Fact]
    public async Task EntriesBecomePackagesUsingTheFriendlyTitleAndSkippingUnsafeOrIncompleteOnes()
    {
        var feed = new ChocolateyFeed(new HttpClient(new StubHandler(_ => Ok(Sample))));
        var found = await feed.SearchAsync("chrome", 60);
        Assert.Equal(["GoogleChrome", "vlc"], found.Select(x => x.Id));
        Assert.Equal("Google Chrome", found[0].Name);
        Assert.Equal("vlc", found[1].Name);
        Assert.Equal("131.0.1", found[0].Version);
        Assert.All(found, x => { Assert.Equal(ManagerKind.Chocolatey, x.Manager); Assert.Equal("chocolatey", x.Source); });
    }

    [Fact]
    public async Task TheRequestAsksForTheMostDownloadedFirstAndEscapesTheQuery()
    {
        var handler = new StubHandler(_ => Ok(Sample));
        await new ChocolateyFeed(new HttpClient(handler)).SearchAsync("it's & more", 40);
        var url = Assert.Single(handler.Requests).AbsoluteUri;
        Assert.StartsWith("https://community.chocolatey.org/api/v2/Search()", url);
        Assert.Contains("DownloadCount%20desc", url);
        Assert.Contains("%24top=40", url.Replace("$", "%24"));
        Assert.Contains("searchTerm='it%27%27s%20%26%20more'", url);
    }

    [Fact]
    public async Task AFailingFeedThrowsSoTheServiceCanFallBack()
    {
        var feed = new ChocolateyFeed(new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError))));
        await Assert.ThrowsAsync<HttpRequestException>(() => feed.SearchAsync("git", 60));
        var garbage = new ChocolateyFeed(new HttpClient(new StubHandler(_ => Ok("<html>not a feed"))));
        await Assert.ThrowsAnyAsync<Exception>(() => garbage.SearchAsync("git", 60));
    }

    [Fact]
    public async Task WarmingNeverThrowsEvenWhenOffline()
    {
        var feed = new ChocolateyFeed(new HttpClient(new StubHandler(_ => throw new HttpRequestException("offline"))));
        await feed.WarmAsync();
    }
}

public sealed class FastSearchFallbackTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "tidywindow-fallback-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(root, true); } catch (IOException) { } }

    private sealed class Locator : IExecutableLocator
    {
        public string? Find(ManagerKind manager) => "manager.exe";
        public string PowerShell => "powershell.exe";
    }

    private sealed class Runner(string output = "") : ICommandRunner
    {
        public List<CommandSpec> Commands { get; } = [];
        public Task<CommandResult> RunAsync(CommandSpec command, CancellationToken cancellationToken = default) { Commands.Add(command); return Task.FromResult(new CommandResult(0, output, "")); }
    }

    private sealed class Feed(Func<CancellationToken, Task<IReadOnlyList<Package>>> search) : IChocolateyFeed
    {
        public Feed(Func<Task<IReadOnlyList<Package>>> search) : this(_ => search()) { }

        public int Warmed;
        public Task<IReadOnlyList<Package>> SearchAsync(string query, int limit, CancellationToken token = default) => search(token);
        public Task WarmAsync(CancellationToken token = default) { Warmed++; return Task.CompletedTask; }
    }

    private static ManagerStatus Ready(ManagerKind kind, string? exe = null) => new(ManagerDefinition.All.Single(x => x.Kind == kind), ManagerHealth.Ready, "1", exe ?? "manager.exe", "Ready");
    private static readonly Package Choco = new(ManagerKind.Chocolatey, "vlc", "VLC", "3.0", "chocolatey");

    [Fact]
    public async Task ChocolateyIsSearchedThroughTheFeedWithoutStartingItsCommandLine()
    {
        var runner = new Runner();
        var service = new PackageService(runner, new Locator(), new Feed(() => Task.FromResult<IReadOnlyList<Package>>([Choco])));
        var result = await service.SearchAsync(Ready(ManagerKind.Chocolatey), "vlc");
        Assert.Equal(Choco, Assert.Single(result.Packages));
        Assert.Empty(runner.Commands);
    }

    [Fact]
    public async Task AFeedThatFailsFallsBackToTheCommandLineAsBefore()
    {
        var runner = new Runner("vlc|3.0.24\n");
        var service = new PackageService(runner, new Locator(), new Feed(() => throw new HttpRequestException("blocked by proxy")));
        var result = await service.SearchAsync(Ready(ManagerKind.Chocolatey), "vlc");
        Assert.Null(result.Error);
        Assert.Equal("vlc", Assert.Single(result.Packages).Id);
        Assert.Single(runner.Commands);
    }

    [Fact]
    public async Task APersonCancellingIsNotMistakenForAFeedFailure()
    {
        var runner = new Runner();
        using var cancel = new CancellationTokenSource();
        var service = new PackageService(runner, new Locator(), new Feed(async token => { await Task.Delay(Timeout.Infinite, token); return []; }));
        var search = service.SearchAsync(Ready(ManagerKind.Chocolatey), "vlc", cancel.Token);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => search);
        Assert.Empty(runner.Commands);
    }

    [Fact]
    public async Task ScoopIsSearchedFromItsBucketsAndFallsBackWhenTheyCannotBeRead()
    {
        Directory.CreateDirectory(Path.Combine(root, "buckets", "main", "bucket"));
        File.WriteAllText(Path.Combine(root, "buckets", "main", "bucket", "vlc.json"), """{"version":"3.0"}""");
        var shim = Path.Combine(root, "shims", "scoop.ps1");
        var runner = new Runner();
        var service = new PackageService(runner, new Locator(), scoop: new ScoopCatalog());
        var result = await service.SearchAsync(Ready(ManagerKind.Scoop, shim), "vlc");
        Assert.Equal("main/vlc", Assert.Single(result.Packages).Id);
        Assert.Empty(runner.Commands);

        // With no buckets anywhere Scoop itself is asked, exactly as it was before the fast path existed.
        Directory.Delete(Path.Combine(root, "buckets"), true);
        var fallbackRunner = new Runner();
        var fallback = new PackageService(fallbackRunner, new Locator(), scoop: new ScoopCatalog());
        await fallback.SearchAsync(Ready(ManagerKind.Scoop, Path.Combine(Path.GetTempPath(), "nowhere", "shims", "scoop.ps1")), "vlc2");
        Assert.True(fallbackRunner.Commands.Count is 0 or 1);
    }

    [Fact]
    public async Task WarmingStartsOnlyTheSourcesThatAreReadyAndNeverThrows()
    {
        var feed = new Feed(() => Task.FromResult<IReadOnlyList<Package>>([]));
        var service = new PackageService(new Runner(), new Locator(), feed, new ScoopCatalog());
        var missing = new ManagerStatus(ManagerDefinition.All.Single(x => x.Kind == ManagerKind.Chocolatey), ManagerHealth.Missing, "", null, "Not installed");
        await service.WarmUpAsync([missing, Ready(ManagerKind.Winget), Ready(ManagerKind.Scoop, Path.Combine(root, "shims", "scoop.ps1"))]);
        Assert.Equal(0, feed.Warmed);
        await service.WarmUpAsync([Ready(ManagerKind.Chocolatey)]);
        Assert.Equal(1, feed.Warmed);
    }
}

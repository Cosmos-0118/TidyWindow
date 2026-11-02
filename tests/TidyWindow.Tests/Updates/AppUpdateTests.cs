using System.Net;
using System.Text.Json;
using TidyWindow.Next.Core;
using Xunit;

namespace TidyWindow.Next.Tests;

public sealed class AppUpdateTests : IDisposable
{
    private const string Sample = """
    [
      {"tag_name":"v4.7.0","name":"Legacy","draft":false,"prerelease":false,"published_at":"2024-01-01T00:00:00Z","html_url":"https://github.com/Cosmos-0118/TidyWindow/releases/tag/v4.7.0","body":"legacy","assets":[]},
      {"tag_name":"v5.0","name":"Short","draft":false,"prerelease":false,"published_at":"2026-08-01T00:00:00Z","html_url":"https://github.com/Cosmos-0118/TidyWindow/releases/tag/v5.0","body":"short","assets":[]},
      {"tag_name":"v5.0.0","name":"Hidden","draft":true,"prerelease":false,"published_at":"2026-09-01T00:00:00Z","html_url":"https://github.com/Cosmos-0118/TidyWindow/releases/tag/v5.0.0","body":"draft","assets":[]},
      {"tag_name":"v5.2.0-beta","name":"Beta","draft":false,"prerelease":true,"published_at":"2026-11-01T00:00:00Z","html_url":"https://github.com/Cosmos-0118/TidyWindow/releases/tag/v5.2.0-beta","body":"beta","assets":[]},
      {"tag_name":"not-a-version","name":"Skipped","draft":false,"prerelease":false,"published_at":"2026-11-01T00:00:00Z","html_url":"https://github.com/Cosmos-0118/TidyWindow/releases/tag/skipped","body":"","assets":[]},
      {"tag_name":"v5.1.0","name":"TidyWindow 5.1.0","draft":false,"prerelease":false,"published_at":"2026-10-01T00:00:00Z","html_url":"https://github.com/Cosmos-0118/TidyWindow/releases/tag/v5.1.0","body":"## Changes\n\n- **Faster** search\n- Read the [notes](https://example.com)\n","assets":[
        {"name":"TidyWindow-Setup-9.9.9.exe","browser_download_url":"https://github.com/Cosmos-0118/TidyWindow/releases/download/v5.1.0/TidyWindow-Setup-9.9.9.exe","size":4},
        {"name":"TidyWindow-Setup-5.1.0.exe","browser_download_url":"https://github.com/Cosmos-0118/TidyWindow/releases/download/v5.1.0/TidyWindow-Setup-5.1.0.exe","size":6},
        {"name":"TidyWindow-5.1.0-win-x64.zip","browser_download_url":"https://github.com/Cosmos-0118/TidyWindow/releases/download/v5.1.0/TidyWindow-5.1.0-win-x64.zip","size":4},
        {"name":"notes.txt","browser_download_url":"https://github.com/Cosmos-0118/TidyWindow/releases/download/v5.1.0/notes.txt","size":1}
      ]}
    ]
    """;

    private readonly string folder = Path.Combine(Path.GetTempPath(), "tidywindow-update-" + Guid.NewGuid().ToString("N"));
    public AppUpdateTests() => Directory.CreateDirectory(folder);
    public void Dispose() { try { Directory.Delete(folder, true); } catch (IOException) { } }

    [Fact]
    public void ParseReleasesKeepsStableTidyWindow5ReleasesAndTheirAssets()
    {
        var release = Assert.Single(AppUpdateService.ParseReleases(Sample));
        Assert.Equal("v5.1.0", release.TagName);
        Assert.Equal(new Version(5, 1, 0), release.Version);
        Assert.Equal("TidyWindow 5.1.0", release.Title);
        Assert.Equal("https://github.com/Cosmos-0118/TidyWindow/releases/tag/v5.1.0", release.HtmlUrl);
        Assert.Equal("Changes\n\n• Faster search\n• Read the notes", release.CleanNotes);
        Assert.Equal("TidyWindow-Setup-5.1.0.exe", release.InstallerAsset!.Name);
        Assert.Equal(6, release.InstallerAsset.SizeBytes);
        Assert.Equal("TidyWindow-5.1.0-win-x64.zip", release.PortableAsset!.Name);
    }

    [Fact]
    public void ParseReleasesRejectsAResponseThatIsNotAList()
    {
        Assert.ThrowsAny<JsonException>(() => AppUpdateService.ParseReleases("{"));
        Assert.Throws<InvalidDataException>(() => AppUpdateService.ParseReleases("{}"));
    }

    [Fact]
    public void CleanMarkdownNotesTurnsReleaseTextIntoPlainLines()
    {
        Assert.Equal("", AppUpdateService.CleanMarkdownNotes("  "));
        Assert.Equal("Title\n\nkeep me\n\n• item", AppUpdateService.CleanMarkdownNotes("## Title\n\n```\nkeep me\n```\n\n---\n\n* item"));
    }

    [Fact]
    public void EvaluateReportsAnUpdateOnlyWhenGitHubIsNewer()
    {
        var older = Release(new Version(5, 0, 0));
        var newer = Release(new Version(5, 1, 0));
        using var installed = Offline(new Version(5, 0, 0, 9), installedBuild: true);
        var update = installed.Service.Evaluate([older, newer]);
        Assert.Equal(new Version(5, 0, 0), installed.Service.CurrentVersion);
        Assert.True(update.IsInstalledBuild);
        Assert.True(update.UpdateAvailable);
        Assert.Equal(newer.Version, update.LatestRelease!.Version);
        Assert.Equal([newer.Version, older.Version], update.RecentReleases.Select(x => x.Version));

        using var current = Offline(new Version(5, 1, 0), installedBuild: false);
        var same = current.Service.Evaluate([newer]);
        Assert.False(same.UpdateAvailable);
        Assert.False(same.IsInstalledBuild);

        using var ahead = Offline(new Version(5, 2, 0));
        Assert.False(ahead.Service.Evaluate([newer]).UpdateAvailable);

        var many = Enumerable.Range(0, 12).Select(i => Release(new Version(5, 0, i))).ToArray();
        var limited = installed.Service.Evaluate(many);
        Assert.Equal(10, limited.RecentReleases.Count);
        Assert.Equal(new Version(5, 0, 11), limited.LatestRelease!.Version);
    }

    [Fact]
    public async Task CheckAsyncReadsTheGitHubReleasesResponse()
    {
        var handler = new JsonHandler(Sample);
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var service = new AppUpdateService(http, new Version(5, 0, 0), () => true, folder);
        var result = await service.CheckAsync();
        Assert.Equal(AppUpdateService.ReleasesUrl, handler.Address!.AbsoluteUri);
        Assert.Equal("TidyWindow", handler.UserAgent);
        Assert.Contains("application/vnd.github+json", handler.Accept);
        Assert.True(result.UpdateAvailable);
        Assert.Equal("v5.1.0", result.LatestRelease!.TagName);
        Assert.True(result.IsInstalledBuild);
    }

    [Fact]
    public async Task CheckAsyncFailsWhenGitHubReturnsAnError()
    {
        using var http = new HttpClient(new StatusHandler(HttpStatusCode.Forbidden)) { Timeout = Timeout.InfiniteTimeSpan };
        var service = new AppUpdateService(http, new Version(5, 0, 0), () => true, folder);
        await Assert.ThrowsAsync<HttpRequestException>(() => service.CheckAsync());
    }

    [Fact]
    public async Task DownloadInstallerChecksTheSizeAndExecutableHeader()
    {
        var bytes = new byte[] { (byte)'M', (byte)'Z', 1, 2, 3, 4 };
        var seen = new List<DownloadProgress>();
        using var http = new HttpClient(new BytesHandler(bytes)) { Timeout = Timeout.InfiniteTimeSpan };
        var service = new AppUpdateService(http, new Version(5, 0, 0), () => true, folder);
        var asset = InstallerAsset(bytes.Length);
        var path = await service.DownloadInstallerAsync(asset, new Collect(seen));
        Assert.Equal(Path.Combine(folder, asset.Name), path);
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.False(File.Exists(path + ".part"));
        Assert.Equal(bytes.Length, Assert.Single(seen).BytesReceived);
        Assert.Equal(100, seen[0].Percent);
    }

    [Fact]
    public async Task DownloadPortableChecksTheZipHeader()
    {
        var bytes = new byte[] { (byte)'P', (byte)'K', 3, 4 };
        using var http = new HttpClient(new BytesHandler(bytes)) { Timeout = Timeout.InfiniteTimeSpan };
        var service = new AppUpdateService(http, new Version(5, 0, 0), () => false, folder);
        var asset = new AppReleaseAsset("TidyWindow-5.1.0-win-x64.zip", "https://objects.githubusercontent.com/TidyWindow-5.1.0-win-x64.zip", bytes.Length);
        var path = await service.DownloadPortableAsync(asset, null);
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task DownloadInstallerRejectsAShortOrNonExecutableFile()
    {
        var mismatch = new byte[] { (byte)'M', (byte)'Z', 1, 2 };
        using var shortHttp = new HttpClient(new BytesHandler(mismatch)) { Timeout = Timeout.InfiniteTimeSpan };
        var service = new AppUpdateService(shortHttp, new Version(5, 0, 0), () => true, folder);
        await Assert.ThrowsAsync<IOException>(() => service.DownloadInstallerAsync(InstallerAsset(10), null));
        Assert.Empty(Directory.GetFiles(folder));

        var text = "Hello!"u8.ToArray();
        using var textHttp = new HttpClient(new BytesHandler(text)) { Timeout = Timeout.InfiniteTimeSpan };
        var other = new AppUpdateService(textHttp, new Version(5, 0, 0), () => true, folder);
        await Assert.ThrowsAsync<InvalidDataException>(() => other.DownloadInstallerAsync(InstallerAsset(text.Length), null));
        Assert.Empty(Directory.GetFiles(folder));
    }

    [Fact]
    public async Task DownloadInstallerStopsWhenCancelledAndRemovesThePartialFile()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new StallHandler(started)) { Timeout = Timeout.InfiniteTimeSpan };
        var service = new AppUpdateService(http, new Version(5, 0, 0), () => true, folder);
        using var cancel = new CancellationTokenSource();
        var task = service.DownloadInstallerAsync(InstallerAsset(100), null, cancel.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await task);
        Assert.Empty(Directory.GetFiles(folder));
    }

    [Fact]
    public void TheChecksumGitHubListsForAnAssetIsKeptAndAnythingElseIsNot()
    {
        var sha = new string('a', 64);
        Assert.Equal(sha, AppUpdateService.Digest("sha256:" + sha.ToUpperInvariant()));
        Assert.Null(AppUpdateService.Digest(""));
        Assert.Null(AppUpdateService.Digest("md5:" + new string('a', 32)));
        Assert.Null(AppUpdateService.Digest("sha256:" + new string('g', 64)));
        var json = Sample.Replace("\"name\":\"TidyWindow-Setup-5.1.0.exe\",", "\"name\":\"TidyWindow-Setup-5.1.0.exe\",\"digest\":\"sha256:" + sha + "\",");
        var release = Assert.Single(AppUpdateService.ParseReleases(json));
        Assert.Equal(sha, release.InstallerAsset!.Sha256);
        Assert.Null(release.PortableAsset!.Sha256);
    }

    [Fact]
    public async Task DownloadInstallerRefusesAFileThatDoesNotMatchTheListedChecksumAndKeepsOneThatDoes()
    {
        var bytes = new byte[] { (byte)'M', (byte)'Z', 1, 2, 3, 4 };
        using var http = new HttpClient(new BytesHandler(bytes)) { Timeout = Timeout.InfiniteTimeSpan };
        var service = new AppUpdateService(http, new Version(5, 0, 0), () => true, folder);
        var good = InstallerAsset(bytes.Length) with { Sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)) };
        Assert.True(File.Exists(await service.DownloadInstallerAsync(good, null)));

        var bad = InstallerAsset(bytes.Length) with { Sha256 = new string('0', 64) };
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadInstallerAsync(bad, null));
        Assert.Contains("checksum", error.Message);
        Assert.False(File.Exists(Path.Combine(folder, bad.Name + ".part")));
    }

    [Fact]
    public async Task ARedirectIsFollowedOnlyWhenItStaysOnGitHub()
    {
        var bytes = new byte[] { (byte)'M', (byte)'Z', 1, 2, 3, 4 };
        using var stays = new HttpClient(new RedirectHandler("https://objects.githubusercontent.com/x/TidyWindow-Setup-5.1.0.exe", bytes)) { Timeout = Timeout.InfiniteTimeSpan };
        var path = await new AppUpdateService(stays, new Version(5, 0, 0), () => true, folder).DownloadInstallerAsync(InstallerAsset(bytes.Length), null);
        Assert.Equal(bytes, File.ReadAllBytes(path));

        using var leaves = new HttpClient(new RedirectHandler("https://example.com/TidyWindow-Setup-5.1.0.exe", bytes)) { Timeout = Timeout.InfiniteTimeSpan };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new AppUpdateService(leaves, new Version(5, 0, 0), () => true, folder).DownloadInstallerAsync(InstallerAsset(bytes.Length), null));
    }

    [Fact]
    public async Task CheckAsyncRefusesAnAnswerLargerThanAListOfReleasesCanBe()
    {
        using var http = new HttpClient(new JsonHandler("[" + new string(' ', (int)AppUpdateService.MaxApiBytes) + "]")) { Timeout = Timeout.InfiniteTimeSpan };
        await Assert.ThrowsAsync<InvalidDataException>(() => new AppUpdateService(http, new Version(5, 0, 0), () => true, folder).CheckAsync());
    }

    [Fact]
    public async Task ADownloadThatStopsArrivingIsGivenUpOnInsteadOfShowingABarThatNeverMoves()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new StallHandler(started)) { Timeout = Timeout.InfiniteTimeSpan };
        var service = new AppUpdateService(http, new Version(5, 0, 0), () => true, folder, stallTimeout: TimeSpan.FromMilliseconds(300));
        var error = await Assert.ThrowsAsync<TimeoutException>(() => service.DownloadInstallerAsync(InstallerAsset(100), null));
        Assert.Contains("stopped arriving", error.Message);
        Assert.Empty(Directory.GetFiles(folder));
    }

    [Fact]
    public async Task DownloadInstallerRejectsAddressesAndNamesThatAreNotTheRelease()
    {
        using var http = new HttpClient(new ThrowingHandler()) { Timeout = Timeout.InfiniteTimeSpan };
        var service = new AppUpdateService(http, new Version(5, 0, 0), () => true, folder);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DownloadInstallerAsync(new("TidyWindow-Setup-5.0.0.exe", "http://github.com/Cosmos-0118/file.exe", 1), null));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DownloadInstallerAsync(new("TidyWindow-Setup-5.0.0.exe", "https://example.com/file.exe", 1), null));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DownloadInstallerAsync(new("../TidyWindow-Setup-5.0.0.exe", "https://github.com/Cosmos-0118/file.exe", 1), null));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DownloadInstallerAsync(new("notes.txt", "https://github.com/Cosmos-0118/notes.txt", 1), null));
    }

    private OfflineClient Offline(Version current, bool installedBuild = true) => new(current, installedBuild, folder);

    private static AppRelease Release(Version version) => new(
        "v" + version, version, "TidyWindow " + version, "", "",
        new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero),
        "https://github.com/Cosmos-0118/TidyWindow/releases/tag/v" + version,
        InstallerAsset(6), null);

    private static AppReleaseAsset InstallerAsset(long size) => new(
        "TidyWindow-Setup-5.1.0.exe",
        "https://github.com/Cosmos-0118/TidyWindow/releases/download/v5.1.0/TidyWindow-Setup-5.1.0.exe",
        size);

    private sealed class OfflineClient : IDisposable
    {
        private readonly HttpClient http;
        public OfflineClient(Version current, bool installed, string folder)
        {
            http = new HttpClient(new ThrowingHandler()) { Timeout = Timeout.InfiniteTimeSpan };
            Service = new AppUpdateService(http, current, () => installed, folder);
        }
        public AppUpdateService Service { get; }
        public void Dispose() => http.Dispose();
    }

    private sealed class Collect(List<DownloadProgress> items) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value) => items.Add(value);
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("This test does not call GitHub.");
    }

    private sealed class StatusHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("no") });
    }

    private sealed class JsonHandler(string json) : HttpMessageHandler
    {
        public Uri? Address { get; private set; }
        public string? UserAgent { get; private set; }
        public string? Accept { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Address = request.RequestUri;
            UserAgent = request.Headers.UserAgent.ToString();
            Accept = request.Headers.Accept.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }
    }

    /// <summary>Answers every address but the target with a redirect to it, as GitHub does for release files.</summary>
    private sealed class RedirectHandler(string target, byte[] body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsoluteUri != target)
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                redirect.Headers.Location = new Uri(target);
                return Task.FromResult(redirect);
            }
            var content = new ByteArrayContent(body);
            content.Headers.ContentLength = body.Length;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private sealed class BytesHandler(byte[] body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var content = new ByteArrayContent(body);
            content.Headers.ContentLength = body.Length;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private sealed class StallHandler(TaskCompletionSource started) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = new StreamContent(new StallStream(started));
            content.Headers.ContentLength = 100;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }

        private sealed class StallStream(TaskCompletionSource started) : Stream
        {
            private int reads;
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
            {
                if (Interlocked.Increment(ref reads) == 1)
                {
                    buffer.Span[0] = (byte)'M';
                    buffer.Span[1] = (byte)'Z';
                    return 2;
                }
                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return 0;
            }
        }
    }
}

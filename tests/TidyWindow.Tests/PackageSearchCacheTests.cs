using TidyWindow.Next.Core;
using Xunit;

namespace TidyWindow.Next.Tests;

public sealed class PackageSearchCacheTests
{
    private static readonly SearchResult Result = new(ManagerKind.Winget, [new(ManagerKind.Winget, "Git.Git", "Git", "1", "winget")]);

    [Fact]
    public async Task RepeatSearchReusesResultsUntilExpiryOrRefresh()
    {
        var time = new TestClock();
        var cache = new PackageSearchCache(time);
        var calls = 0;
        Task<SearchResult> Fetch(CancellationToken _) { Interlocked.Increment(ref calls); return Task.FromResult(Result); }
        await cache.GetAsync("Git", Fetch);
        await cache.GetAsync("git", Fetch);
        Assert.Equal(1, calls);
        time.Now = time.Now.AddSeconds(61);
        await cache.GetAsync("Git", Fetch);
        Assert.Equal(2, calls);
        cache.Clear();
        await cache.GetAsync("Git", Fetch);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task ErrorsAreRetriedRatherThanCached()
    {
        var cache = new PackageSearchCache();
        var calls = 0;
        Task<SearchResult> Fetch(CancellationToken _) { calls++; return Task.FromResult(new SearchResult(ManagerKind.Winget, [], "offline")); }
        await cache.GetAsync("git", Fetch);
        await cache.GetAsync("git", Fetch);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task CancellingOneConsumerDoesNotCancelAnotherConsumersSharedSearch()
    {
        var cache = new PackageSearchCache();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<SearchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        async Task<SearchResult> Fetch(CancellationToken token)
        { Interlocked.Increment(ref calls); started.SetResult(); return await finish.Task.WaitAsync(token); }
        using var cancel = new CancellationTokenSource();
        var first = cache.GetAsync("git", Fetch, cancel.Token);
        await started.Task;
        var second = cache.GetAsync("git", Fetch);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        finish.SetResult(Result);
        Assert.Equal(Result, await second);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task AbandonedSearchCancelsItsProviderAndCanBeRetried()
    {
        var cache = new PackageSearchCache();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancel = new CancellationTokenSource();
        var search = cache.GetAsync("git", async token =>
        {
            started.SetResult();
            try { await Task.Delay(Timeout.Infinite, token); return Result; }
            finally { stopped.SetResult(); }
        }, cancel.Token);
        await started.Task;
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => search);
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(Result, await cache.GetAsync("git", _ => Task.FromResult(Result)));
    }

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-10-29T00:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }
}

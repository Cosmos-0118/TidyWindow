using TidyWindow.Next.Core;
using Xunit;

namespace TidyWindow.Next.Tests;

/// <summary>When Windows' administrator prompt is reported: only while TidyWindow has work, only after it has waited, and until it is gone.</summary>
public sealed class UacPromptWatcherTests
{
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(10);

    private static async Task<bool> Eventually(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(10);
        return condition();
    }

    [Fact]
    public async Task APromptThatStaysOpenWhileWorkRunsIsReportedOnceAndThenReportedGone()
    {
        var open = false; var events = new List<bool>();
        using var watcher = new UacPromptWatcher(() => open, () => true, Poll, TimeSpan.FromMilliseconds(50));
        watcher.Changed += x => { lock (events) events.Add(x); };
        open = true;
        Assert.True(await Eventually(() => { lock (events) return events.Count == 1; }));
        await Task.Delay(100);
        lock (events) Assert.Equal([true], events);
        open = false;
        Assert.True(await Eventually(() => { lock (events) return events.Count == 2; }));
        lock (events) Assert.Equal([true, false], events);
    }

    [Fact]
    public async Task APromptAnsweredAtOnceIsNeverReported()
    {
        var open = false; var events = 0;
        using var watcher = new UacPromptWatcher(() => open, () => true, Poll, TimeSpan.FromSeconds(5));
        watcher.Changed += _ => Interlocked.Increment(ref events);
        open = true; await Task.Delay(60); open = false; await Task.Delay(60);
        Assert.Equal(0, events);
    }

    [Fact]
    public async Task APromptThatOpensWhileTidyWindowIsIdleBelongsToSomethingElse()
    {
        var events = 0;
        using var watcher = new UacPromptWatcher(() => true, () => false, Poll, TimeSpan.FromMilliseconds(20));
        watcher.Changed += _ => Interlocked.Increment(ref events);
        await Task.Delay(200);
        Assert.Equal(0, events);
    }

    [Fact]
    public async Task AReportedPromptIsFollowedUntilItIsGoneEvenIfTheWorkEnded()
    {
        var working = true; var open = true; var events = new List<bool>();
        using var watcher = new UacPromptWatcher(() => open, () => working, Poll, TimeSpan.FromMilliseconds(30));
        watcher.Changed += x => { lock (events) events.Add(x); };
        Assert.True(await Eventually(() => { lock (events) return events.Count == 1; }));
        working = false; await Task.Delay(60);
        lock (events) Assert.Single(events);
        open = false;
        Assert.True(await Eventually(() => { lock (events) return events.Count == 2; }));
    }
}

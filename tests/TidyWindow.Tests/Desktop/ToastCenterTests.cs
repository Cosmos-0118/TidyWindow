using System.Collections.Concurrent;
using TidyWindow.Next.Core;
using TidyWindow.Next.Desktop;
using Xunit;

namespace TidyWindow.Next.Tests;

public sealed class ToastCenterTests
{
    // Stands in for the UI thread: whatever the center posts waits here until the test runs it.
    private readonly ConcurrentQueue<Action> ui = new();
    private void Pump() { while (ui.TryDequeue(out var action)) action(); }
    private bool Until(Func<bool> condition, int milliseconds = 3000) => SpinWait.SpinUntil(() => { Pump(); return condition(); }, milliseconds);

    [Fact]
    public void TheSameKeyRenewsTheToastInsteadOfAddingAnother()
    {
        using var center = new ToastCenter(ui.Enqueue);
        center.Show("k", ToastKind.Info, "Saved", "to disk");
        center.Show("k", ToastKind.Info, "Saved", "to disk");
        var item = Assert.Single(center.Items);
        Assert.Equal(2, item.Count);
        center.Show("k", ToastKind.Warning, "Could not save");
        Assert.Same(item, Assert.Single(center.Items));
        Assert.Equal(1, item.Count);
        Assert.Equal(ToastKind.Warning, item.Kind);
    }

    [Fact]
    public void PastTheLimitTheOldestSelfClosingToastGoesFirst()
    {
        using var center = new ToastCenter(ui.Enqueue);
        center.Show("keep", ToastKind.Warning, "Needs you", seconds: 0);
        for (var i = 0; i < 4; i++) center.Show("t" + i, ToastKind.Info, "Toast " + i);
        Assert.Equal(5, center.Items.Count);
        Assert.Equal(4, center.Items.Count(x => !x.Closing));
        Assert.False(center.Items.Single(x => x.Key == "keep").Closing);
        Assert.True(center.Items.Single(x => x.Key == "t0").Closing);
        Assert.True(center.Has(x => x == "keep"));
        Assert.False(center.Has(x => x == "t0"));
    }

    [Fact]
    public void AClosedToastIsRemovedAfterItsExit()
    {
        using var center = new ToastCenter(ui.Enqueue);
        center.Show("k", ToastKind.Info, "Done");
        var item = center.Items[0];
        center.Close(item);
        Assert.True(item.Closing);
        Assert.Single(center.Items);
        Assert.True(Until(() => center.Items.Count == 0));
    }

    [Fact]
    public void AToastThatRanOutIsClosedByTheTimer()
    {
        using var center = new ToastCenter(ui.Enqueue);
        center.Show("k", ToastKind.Info, "Gone soon", seconds: 0.05);
        var item = center.Items[0];
        Assert.True(Until(() => item.Closing));
        Assert.True(Until(() => center.Items.Count == 0));
    }

    [Fact]
    public void RemoveWhereClosesTheMatchingToasts()
    {
        using var center = new ToastCenter(ui.Enqueue);
        center.Show("a1", ToastKind.Info, "One"); center.Show("a2", ToastKind.Info, "Two"); center.Show("b1", ToastKind.Info, "Three");
        center.RemoveWhere(key => key.StartsWith('a'));
        Assert.False(center.Has(key => key.StartsWith('a')));
        Assert.True(center.Has(key => key == "b1"));
    }

    [Fact]
    public void WhileTheWindowIsNotInUseEveryToastWaits()
    {
        using var center = new ToastCenter(ui.Enqueue);
        center.Show("a", ToastKind.Info, "One"); center.Show("b", ToastKind.Info, "Two");
        center.Attentive = false; Pump();
        Assert.All(center.Items, x => Assert.True(x.Countdown.IsHeld));
        center.Show("c", ToastKind.Info, "Three");
        Assert.True(center.Items[2].Countdown.IsHeld);
        center.Attentive = true; Pump();
        Assert.All(center.Items, x => Assert.False(x.Countdown.IsHeld));
    }

    [Fact]
    public async Task AFailingActionIsReportedInsteadOfThrown()
    {
        var log = new ActivityLog();
        using var center = new ToastCenter(ui.Enqueue, log);
        center.Show("k", ToastKind.Success, "Cleaned", "", "Undo", () => throw new InvalidOperationException("the undo folder is gone"));
        var item = center.Items[0];
        await item.RunAsync();
        Assert.True(item.Closing);
        var report = Assert.Single(center.Items, x => x.Key == "action-failed");
        Assert.Equal(ToastKind.Warning, report.Kind);
        Assert.Contains("Undo", report.Detail);
        var entry = Assert.Single(log.Snapshot());
        Assert.Equal(ActivityLevel.Error, entry.Level);
        Assert.Contains("the undo folder is gone", entry.Detail);
    }

    [Fact]
    public async Task ACancelledActionIsNotAFailure()
    {
        var log = new ActivityLog();
        using var center = new ToastCenter(ui.Enqueue, log);
        center.Show("k", ToastKind.Info, "Working", "", "Stop", () => throw new OperationCanceledException());
        await center.Items[0].RunAsync();
        Assert.DoesNotContain(center.Items, x => x.Key == "action-failed");
        Assert.Empty(log.Snapshot());
    }

    [Fact]
    public async Task DisposingEndsTheTimerLoopCleanly()
    {
        for (var round = 0; round < 25; round++)
        {
            var center = new ToastCenter(ui.Enqueue);
            if (round % 2 == 1) await Task.Delay(3);
            center.Dispose();
            await center.Loop.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(center.Loop.IsCompletedSuccessfully);
        }
    }

    [Fact]
    public void NothingShowsAfterDispose()
    {
        var center = new ToastCenter(ui.Enqueue);
        center.Show("k", ToastKind.Info, "Early");
        center.Dispose();
        center.Show("later", ToastKind.Info, "Late");
        Assert.DoesNotContain(center.Items, x => x.Key == "later");
    }
}

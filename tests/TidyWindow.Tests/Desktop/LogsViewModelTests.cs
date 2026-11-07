using System.Collections.Concurrent;
using System.Collections.Specialized;
using Microsoft.UI.Xaml;
using TidyWindow.Next.Core;
using TidyWindow.Next.Desktop;
using Xunit;

namespace TidyWindow.Next.Tests;

public sealed class LogsViewModelTests
{
    // Stands in for the UI thread: whatever the view model posts waits here until the test runs it.
    private readonly ConcurrentQueue<Action> ui = new();
    private void Pump() { while (ui.TryDequeue(out var action)) action(); }
    private bool Until(Func<bool> condition, int milliseconds = 3000) => SpinWait.SpinUntil(() => { Pump(); return condition(); }, milliseconds);

    // Unique messages, or the log would fold them into one entry; every third is a warning and every other one carries a detail.
    private static ActivityLog Seeded(int count)
    {
        var log = new ActivityLog();
        for (var i = 0; i < count; i++) log.Write(i % 3 == 0 ? ActivityLevel.Warning : ActivityLevel.Info, ActivityArea.App, "entry " + i, i % 2 == 0 ? "needle in the detail" : null);
        return log;
    }

    [Fact]
    public void TypedTextWaitsUntilTypingPauses()
    {
        using var model = new LogsViewModel(Seeded(50), ui.Enqueue, TimeSpan.FromMilliseconds(30));
        var changes = new List<NotifyCollectionChangedAction>();
        model.Rows.CollectionChanged += (_, e) => changes.Add(e.Action);
        model.SetFilter(new ActivityFilter(Text: "needle"));
        Assert.Empty(changes);
        Assert.Equal(50, model.Rows.Count);
        Assert.True(Until(() => changes.Count > 0));
        Assert.Equal([NotifyCollectionChangedAction.Reset], changes);
        Assert.Equal(25, model.Rows.Count);
        Assert.Equal("25 of 50 entries", model.Summary);
    }

    [Fact]
    public void OnlyTheLastTextTypedIsApplied()
    {
        using var model = new LogsViewModel(Seeded(50), ui.Enqueue, TimeSpan.FromMilliseconds(30));
        var resets = 0;
        model.Rows.CollectionChanged += (_, _) => resets++;
        model.SetFilter(new ActivityFilter(Text: "entry 1"));
        model.SetFilter(new ActivityFilter(Text: "entry 12"));
        Assert.True(Until(() => resets > 0));
        Assert.Equal("entry 12", Assert.Single(model.Rows).Message);
        // Nothing else arrives for the text that was overtaken.
        Assert.False(Until(() => resets > 1, 150));
        Assert.Equal(1, resets);
    }

    [Fact]
    public void LevelAndAreaChangesApplyAtOnceWithOneReset()
    {
        using var model = new LogsViewModel(Seeded(500), ui.Enqueue);
        var changes = new List<NotifyCollectionChangedAction>();
        model.Rows.CollectionChanged += (_, e) => changes.Add(e.Action);
        model.SetFilter(new ActivityFilter(ActivityLevel.Warning));
        Assert.Equal([NotifyCollectionChangedAction.Reset], changes);
        Assert.Equal(167, model.Rows.Count);
        Assert.True(ui.IsEmpty);
        model.SetFilter(new ActivityFilter(ActivityLevel.Warning, ActivityArea.Install));
        Assert.Equal(2, changes.Count);
        Assert.Empty(model.Rows);
        Assert.Equal("No matching entries", model.EmptyTitle);
    }

    [Fact]
    public void RowsKeepTheirInstanceAndOpenStateAcrossAFilterChange()
    {
        using var model = new LogsViewModel(Seeded(20), ui.Enqueue, TimeSpan.Zero);
        var row = model.Rows.First(x => x.HasDetail);
        row.Toggle();
        Assert.Equal(Visibility.Visible, row.DetailVisibility);
        model.SetFilter(new ActivityFilter(Text: "needle"));
        Assert.Same(row, model.Rows.Single(x => x.Entry.Id == row.Entry.Id));
        Assert.Equal(Visibility.Visible, row.DetailVisibility);
        model.SetFilter(new ActivityFilter());
        Assert.Same(row, model.Rows.Single(x => x.Entry.Id == row.Entry.Id));
        Assert.Equal(20, model.Rows.Count);
    }

    [Fact]
    public void AnEntryWrittenLaterJoinsTheTopOnlyIfItMatches()
    {
        var log = Seeded(10);
        using var model = new LogsViewModel(log, ui.Enqueue, TimeSpan.Zero);
        model.SetFilter(new ActivityFilter(Text: "needle"));
        log.Write(ActivityLevel.Info, ActivityArea.Install, "fresh", "a needle here");
        log.Write(ActivityLevel.Info, ActivityArea.Install, "unrelated");
        Pump();
        Assert.Equal("fresh", model.Rows[0].Message);
        Assert.DoesNotContain(model.Rows, x => x.Message == "unrelated");
        Assert.Equal("6 of 12 entries", model.Summary);
    }

    [Fact]
    public void EachRowHasAVisibleCopyIconThatTurnsIntoATickWhenCopied()
    {
        using var model = new LogsViewModel(Seeded(1), ui.Enqueue);
        var row = model.Rows[0];
        // A blank glyph renders as an empty, invisible button.
        Assert.Equal("", row.CopyGlyph);
        row.SetCopied(true);
        Assert.Equal("", row.CopyGlyph);
        row.SetCopied(false);
        Assert.Equal("", row.CopyGlyph);
        Assert.NotEqual(row.Chevron, ToggledChevron(row));
    }

    private static string ToggledChevron(LogRow row) { row.Toggle(); return row.Chevron; }

    [Fact]
    public void ClearingTheLogEmptiesTheList()
    {
        var log = Seeded(10);
        using var model = new LogsViewModel(log, ui.Enqueue);
        log.Clear();
        Pump();
        Assert.Empty(model.Rows);
        Assert.Equal(Visibility.Visible, model.EmptyVisibility);
        Assert.Equal("Nothing to report", model.EmptyTitle);
    }
}

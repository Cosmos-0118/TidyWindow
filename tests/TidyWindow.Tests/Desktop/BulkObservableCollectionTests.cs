using System.Collections.Specialized;
using TidyWindow.Next.Desktop;
using Xunit;

namespace TidyWindow.Next.Tests;

public sealed class BulkObservableCollectionTests
{
    [Fact]
    public void ReplacingHundredsOfItemsRaisesOneNotification()
    {
        var list = new BulkObservableCollection<int>();
        var changes = new List<NotifyCollectionChangedAction>();
        list.CollectionChanged += (_, e) => changes.Add(e.Action);
        Assert.True(list.Reset(Enumerable.Range(0, 500)));
        Assert.Equal(NotifyCollectionChangedAction.Reset, Assert.Single(changes));
        Assert.Equal(500, list.Count);
    }

    [Fact]
    public void ReplacingWithTheSameItemsRaisesNothingSoTheListKeepsItsScrollPosition()
    {
        var list = new BulkObservableCollection<string>();
        list.Reset(["a", "b", "c"]);
        var raised = false;
        list.CollectionChanged += (_, _) => raised = true;
        Assert.False(list.Reset(["a", "b", "c"]));
        Assert.False(raised);
    }

    [Fact]
    public void OrderMattersWhenComparing()
    {
        var list = new BulkObservableCollection<string>();
        list.Reset(["a", "b"]);
        Assert.True(list.Reset(["b", "a"]));
        Assert.Equal(["b", "a"], list);
    }

    [Fact]
    public void ResettingToEmptyClearsTheList()
    {
        var list = new BulkObservableCollection<int>();
        list.Reset([1, 2]);
        Assert.True(list.Reset([]));
        Assert.Empty(list);
    }
}

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

    private sealed record Row(string Id, int Version);

    [Fact]
    public void SyncNeverRaisesAReset_SoAListControlKeepsItsScrollPosition()
    {
        var list = new BulkObservableCollection<string>();
        list.Reset(Enumerable.Range(0, 100).Select(x => x.ToString()).ToList());
        var actions = new List<NotifyCollectionChangedAction>();
        list.CollectionChanged += (_, e) => actions.Add(e.Action);
        // One row moved to the end, one removed, one added.
        var target = Enumerable.Range(0, 100).Select(x => x.ToString()).Where(x => x != "3" && x != "50").Append("3").Append("new").ToList();
        Assert.True(list.Sync(target));
        Assert.Equal(target, list);
        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, actions);
        Assert.True(actions.Count <= 6, $"expected a handful of changes, got {actions.Count}");
    }

    [Fact]
    public void SyncWithTheSameContentRaisesNothing()
    {
        var list = new BulkObservableCollection<string>();
        list.Reset(["a", "b", "c"]);
        var raised = false;
        list.CollectionChanged += (_, _) => raised = true;
        Assert.False(list.Sync(["a", "b", "c"]));
        Assert.False(raised);
    }

    [Fact]
    public void ARowWithANewInstanceButTheSameKeyIsReplacedWhereItStands()
    {
        var list = new BulkObservableCollection<Row>();
        list.Reset([new("a", 1), new("b", 1), new("c", 1)]);
        var actions = new List<NotifyCollectionChangedAction>();
        list.CollectionChanged += (_, e) => actions.Add(e.Action);
        Assert.True(list.Sync([new("a", 1), new("b", 2), new("c", 1)], x => x.Id));
        Assert.Equal(2, list[1].Version);
        Assert.Equal([NotifyCollectionChangedAction.Replace], actions);
    }

    [Fact]
    public void SyncKeepsTheInstancesItDoesNotNeedToChange()
    {
        var first = new Row("a", 1); var second = new Row("b", 1);
        var list = new BulkObservableCollection<Row>();
        list.Reset([first, second]);
        list.Sync([second, new("c", 1), first], x => x.Id);
        Assert.Same(second, list[0]);
        Assert.Same(first, list[2]);
    }

    [Fact]
    public void RepeatedKeysStillEndUpInTheRightOrder()
    {
        var list = new BulkObservableCollection<string>();
        list.Reset(["a", "a", "b"]);
        list.Sync(["b", "a", "a", "a"], x => x);
        Assert.Equal(["b", "a", "a", "a"], list);
        list.Sync(["a"]);
        Assert.Equal(["a"], list);
    }

    [Fact]
    public void SyncFromOrToEmptyIsASingleReset()
    {
        var list = new BulkObservableCollection<int>();
        var actions = new List<NotifyCollectionChangedAction>();
        list.CollectionChanged += (_, e) => actions.Add(e.Action);
        list.Sync(Enumerable.Range(0, 300));
        list.Sync([]);
        Assert.Equal([NotifyCollectionChangedAction.Reset, NotifyCollectionChangedAction.Reset], actions);
    }

    [Fact]
    public void SyncAlwaysMatchesTheTargetAndItsNotificationsReplayToTheSameList()
    {
        // Whatever the old and new content, a mirror that only applies the raised notifications must end up identical.
        var random = new Random(12345);
        for (var round = 0; round < 400; round++)
        {
            var list = new BulkObservableCollection<Row>();
            var mirror = new List<Row>();
            Row[] Make() => Enumerable.Range(0, random.Next(0, 25)).Select(_ => new Row("k" + random.Next(0, 15), random.Next(0, 3))).OrderBy(_ => random.Next()).ToArray();
            list.Reset(Make()); mirror.AddRange(list);
            list.CollectionChanged += (_, e) =>
            {
                switch (e.Action)
                {
                    case NotifyCollectionChangedAction.Add: mirror.Insert(e.NewStartingIndex, (Row)e.NewItems![0]!); break;
                    case NotifyCollectionChangedAction.Remove: mirror.RemoveAt(e.OldStartingIndex); break;
                    case NotifyCollectionChangedAction.Replace: mirror[e.NewStartingIndex] = (Row)e.NewItems![0]!; break;
                    case NotifyCollectionChangedAction.Move: var moved = mirror[e.OldStartingIndex]; mirror.RemoveAt(e.OldStartingIndex); mirror.Insert(e.NewStartingIndex, moved); break;
                    default: mirror.Clear(); mirror.AddRange(list); break;
                }
            };
            var target = Make();
            list.Sync(target, x => x.Id);
            Assert.Equal(target.Select(x => x.Id).OrderBy(x => x), list.Select(x => x.Id).OrderBy(x => x));
            Assert.Equal(target.Length, list.Count);
            Assert.True(list.Select(x => x.Id).SequenceEqual(target.Select(x => x.Id)), $"round {round}");
            Assert.True(mirror.SequenceEqual(list), $"round {round}: notifications did not replay");
        }
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

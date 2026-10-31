using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace TidyWindow.Next.Desktop;

/// <summary>An observable list that can swap its whole content with one change notification.
/// Clearing and re-adding hundreds of items one at a time makes a list control re-measure on every step.</summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    /// <returns>False when the content was already identical, so nothing was raised and the list keeps its scroll position.</returns>
    public bool Reset(IEnumerable<T> items)
    {
        var next = items as IReadOnlyList<T> ?? items.ToArray();
        if (next.Count == Count && next.SequenceEqual(this)) return false;
        Items.Clear();
        foreach (var item in next) Items.Add(item);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        return true;
    }

    /// <summary>
    /// Brings the list to <paramref name="items"/> with the smallest set of ordinary add, remove and replace notifications, instead of one Reset.
    /// A list control that gets a Reset throws away every row and starts again at the top, so use this whenever the person is looking at the list
    /// while its content changes underneath them (a refresh, or one row changing and moving). Use <see cref="Reset"/> when the content is meant to start over (a new search).
    /// </summary>
    /// <param name="key">What makes two items the same row. A row with a new instance but the same key is replaced where it stands. Defaults to the item itself.</param>
    /// <returns>False when nothing had to change.</returns>
    public bool Sync(IEnumerable<T> items, Func<T, object>? key = null)
    {
        var next = items as IReadOnlyList<T> ?? items.ToArray();
        // Nothing on screen to keep in place: one notification is cheaper than one per row.
        if (Count == 0 || next.Count == 0) return Reset(next);
        key ??= static item => item!;
        var wanted = Identify(next, key);
        var have = Identify(this, key);
        var wantedAt = new Dictionary<(object, int), int>();
        for (var i = 0; i < wanted.Count; i++) wantedAt[wanted[i]] = i;
        var changed = false;

        // Rows that are going away, from the end so the positions still to be visited do not move.
        for (var i = have.Count - 1; i >= 0; i--)
        {
            if (wantedAt.ContainsKey(have[i])) continue;
            RemoveAt(i); have.RemoveAt(i); changed = true;
        }
        // Of the rows that stay, the longest run already in the right order is left alone. Only the others (a row that moved) are taken out and put back,
        // so moving one row far away costs two changes, not one for every row it passed.
        var keep = LongestAscending(have.Select(x => wantedAt[x]).ToArray());
        for (var i = have.Count - 1; i >= 0; i--)
        {
            if (keep[i]) continue;
            RemoveAt(i); have.RemoveAt(i); changed = true;
        }
        // What is left is in target order, so walking the target fills in exactly the gaps.
        for (var i = 0; i < wanted.Count; i++)
        {
            // Tuple == compares the boxed keys by reference; Equals compares them by value.
            if (i < Count && have[i].Equals(wanted[i]))
            {
                if (!EqualityComparer<T>.Default.Equals(this[i], next[i])) { this[i] = next[i]; changed = true; }
                continue;
            }
            Insert(i, next[i]); have.Insert(i, wanted[i]); changed = true;
        }
        return changed;
    }

    /// <summary>Marks the elements of one longest strictly increasing subsequence.</summary>
    private static bool[] LongestAscending(int[] values)
    {
        var tails = new List<int>(); // tails[n] = index of the smallest possible last value of an increasing run of length n + 1
        var previous = new int[values.Length];
        for (var k = 0; k < values.Length; k++)
        {
            int low = 0, high = tails.Count;
            while (low < high)
            {
                var middle = (low + high) / 2;
                if (values[tails[middle]] < values[k]) low = middle + 1; else high = middle;
            }
            previous[k] = low > 0 ? tails[low - 1] : -1;
            if (low == tails.Count) tails.Add(k); else tails[low] = k;
        }
        var keep = new bool[values.Length];
        for (var k = tails.Count > 0 ? tails[^1] : -1; k >= 0; k = previous[k]) keep[k] = true;
        return keep;
    }

    /// <summary>A key and how many times it has already appeared, so a repeated key still identifies exactly one row.</summary>
    private static List<(object, int)> Identify(IEnumerable<T> items, Func<T, object> key)
    {
        var seen = new Dictionary<object, int>();
        var result = new List<(object, int)>();
        foreach (var item in items)
        {
            var k = key(item);
            seen[k] = seen.GetValueOrDefault(k) + 1;
            result.Add((k, seen[k] - 1));
        }
        return result;
    }
}

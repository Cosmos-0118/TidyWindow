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
}

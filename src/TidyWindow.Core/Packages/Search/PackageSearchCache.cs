namespace TidyWindow.Next.Core;

/// <summary>A bounded, one-minute cache with shared work and reference-counted cancellation.</summary>
public sealed class PackageSearchCache(TimeProvider? clock = null)
{
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly object gate = new();
    private readonly Dictionary<string, Entry> entries = new(StringComparer.OrdinalIgnoreCase);
    private sealed class Entry
    {
        public CancellationTokenSource Cancellation { get; } = new();
        public Task<SearchResult> Work { get; set; } = null!;
        public int Users { get; set; }
        public DateTimeOffset Expires { get; set; } = DateTimeOffset.MaxValue;
    }

    public async Task<SearchResult> GetAsync(string key, Func<CancellationToken, Task<SearchResult>> fetch, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        Entry entry;
        lock (gate)
        {
            foreach (var stale in entries.Where(x => x.Value.Work.IsCompleted && x.Value.Expires <= time.GetUtcNow()).Select(x => x.Key).ToArray()) entries.Remove(stale);
            if (!entries.TryGetValue(key, out entry!))
            {
                if (entries.Count >= 64)
                {
                    var oldest = entries.Where(x => x.Value.Work.IsCompleted).MinBy(x => x.Value.Expires);
                    if (oldest.Key is not null) entries.Remove(oldest.Key);
                }
                entry = new();
                entries[key] = entry;
                var created = entry;
                // Process startup and provider parsing must not run on the UI thread.
                entry.Work = Task.Run(() => FetchAsync(key, created, fetch));
                _ = entry.Work.ContinueWith(task =>
                {
                    _ = task.Exception; // Observe failures even when every waiter cancelled.
                    lock (gate) { if (created.Users == 0) created.Cancellation.Dispose(); }
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            entry.Users++;
        }
        try { return await entry.Work.WaitAsync(token).ConfigureAwait(false); }
        finally
        {
            lock (gate)
            {
                if (--entry.Users == 0)
                {
                    if (!entry.Work.IsCompleted)
                    {
                        RemoveIfCurrent(key, entry);
                        entry.Cancellation.Cancel();
                    }
                    else entry.Cancellation.Dispose();
                }
            }
        }
    }

    private async Task<SearchResult> FetchAsync(string key, Entry entry, Func<CancellationToken, Task<SearchResult>> fetch)
    {
        try
        {
            var result = await fetch(entry.Cancellation.Token).ConfigureAwait(false);
            lock (gate)
            {
                if (result.Error is not null) RemoveIfCurrent(key, entry);
                else entry.Expires = time.GetUtcNow().AddSeconds(result.Packages.Count == 0 ? 15 : 60);
            }
            return result;
        }
        catch { lock (gate) RemoveIfCurrent(key, entry); throw; }
    }

    public void Clear()
    {
        lock (gate) entries.Clear();
    }

    private void RemoveIfCurrent(string key, Entry entry)
    { if (entries.TryGetValue(key, out var current) && ReferenceEquals(current, entry)) entries.Remove(key); }
}

using System.Runtime.CompilerServices;

namespace TidyWindow.Next.Core;

/// <summary>What a search can show right now. <see cref="Waiting"/> lists the sources that have not answered; an empty list means the results are final.</summary>
public sealed record SearchSnapshot(IReadOnlyList<Package> Packages, IReadOnlyList<SearchResult> Answered, IReadOnlyList<ManagerKind> Waiting)
{
    public bool Complete => Waiting.Count == 0;
}

/// <summary>
/// Gathers the sources of one search into results that do not change under the reader. Everything that answers within the settle time is ranked together and shown once,
/// so a fast source never paints a list that a slower one then reshuffles. Only a source that is really slow arrives later, and the snapshot says so while it is waited for.
/// </summary>
public static class PackageSearchSession
{
    // Long enough for a source's slow-but-normal answer (Chocolatey's server can take about 2 seconds for a search it has not seen), short enough that a stuck one does not hold the list back.
    public static readonly TimeSpan DefaultSettle = TimeSpan.FromMilliseconds(2500);

    public static async IAsyncEnumerable<SearchSnapshot> StreamAsync(IReadOnlyList<(ManagerKind Manager, Task<SearchResult> Work)> sources, string query, TimeSpan settle,
        TimeProvider? clock = null, [EnumeratorCancellation] CancellationToken token = default)
    {
        var pending = sources.Select(x => (x.Manager, Work: Guarded(x.Manager, x.Work, token))).ToList();
        var answered = new List<SearchResult>();

        // The settle window: wait for every source, but never longer than the window, so one stuck source cannot hold the others back.
        using (var timer = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            var window = Task.Delay(settle, clock ?? TimeProvider.System, timer.Token);
            await Task.WhenAny(Task.WhenAll(pending.Select(x => x.Work)), window).ConfigureAwait(false);
            timer.Cancel();
        }
        token.ThrowIfCancellationRequested();
        Collect(pending, answered);
        if (answered.Count > 0 || pending.Count == 0) yield return await SnapshotAsync(sources, pending, answered, query).ConfigureAwait(false);

        while (pending.Count > 0)
        {
            await Task.WhenAny(pending.Select(x => x.Work)).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            Collect(pending, answered);
            yield return await SnapshotAsync(sources, pending, answered, query).ConfigureAwait(false);
        }
    }

    private static void Collect(List<(ManagerKind Manager, Task<SearchResult> Work)> pending, List<SearchResult> answered)
    {
        foreach (var done in pending.Where(x => x.Work.IsCompleted).ToArray())
        {
            answered.Add(done.Work.Result);
            pending.Remove(done);
        }
    }

    private static async Task<SearchSnapshot> SnapshotAsync(IReadOnlyList<(ManagerKind Manager, Task<SearchResult> Work)> sources, List<(ManagerKind Manager, Task<SearchResult> Work)> pending, List<SearchResult> answered, string query)
    {
        var results = answered.ToArray();
        // Ranking compares every result with every other one, so it stays off the thread that draws the list.
        var ranked = await Task.Run(() => PackageRanker.Rank(results.SelectMany(x => x.Packages), query)).ConfigureAwait(false);
        var waiting = sources.Select(x => x.Manager).Where(kind => pending.Any(x => x.Manager == kind)).ToArray();
        return new(ranked, results, waiting);
    }

    // A source that throws is one source failing, not the whole search; only cancellation is allowed through.
    private static async Task<SearchResult> Guarded(ManagerKind manager, Task<SearchResult> work, CancellationToken token)
    {
        try { return await work.ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested) { return new(manager, [], ex.Message); }
    }
}

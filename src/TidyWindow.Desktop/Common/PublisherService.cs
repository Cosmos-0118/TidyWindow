using System.Collections.Concurrent;
using TidyWindow.Next.Core;

namespace TidyWindow.Next.Desktop;

/// <summary>
/// Names the company behind an installed app with the same <see cref="PublisherFinder"/> the Startup page uses. The search runs on the thread pool
/// and every answer is kept, so a page that asks again gets it straight away.
/// </summary>
public sealed class PublisherService(IStartupSystem system)
{
    private static readonly Lazy<PublisherService> shared = new(() => new(new WindowsStartupSystem()));
    public static PublisherService Shared => shared.Value;

    private static readonly FileFacts NoFile = new(false, "", "", "", StartupTrust.Unknown);
    private readonly PublisherFinder finder = new(system);
    private readonly ConcurrentDictionary<string, Task<FoundPublisher>> answers = new();
    // The finder keeps small caches of its own, so one search runs at a time. Waiting for a turn costs no thread: a few hundred apps asking at once
    // used to park a thread-pool thread each on a lock, which starves everything else on a PC with few cores.
    private readonly SemaphoreSlim gate = new(1, 1);

    /// <summary>The publisher if it has already been found.</summary>
    public bool TryGet(ManagedPackage package, out FoundPublisher publisher)
    {
        if (answers.TryGetValue(package.Key, out var task) && task.IsCompletedSuccessfully) { publisher = task.Result; return true; }
        publisher = FoundPublisher.None;
        return false;
    }

    public Task<FoundPublisher> FindAsync(ManagedPackage package) =>
        answers.TryGetValue(package.Key, out var known) ? known : answers.GetOrAdd(package.Key, _ => FindInTurnAsync(package));

    private async Task<FoundPublisher> FindInTurnAsync(ManagedPackage package)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try { return await Task.Run(() => Find(package)).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    private FoundPublisher Find(ManagedPackage package)
    {
        try { return finder.Find(package.Name, system.Expand(InstalledHints.PackagePath(package)), NoFile); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException) { return FoundPublisher.None; }
    }
}

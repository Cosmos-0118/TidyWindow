using System.Collections.Concurrent;
using System.IO.Enumeration;

namespace TidyWindow.Next.Core;

/// <summary>A bounded, reusable index of one accessible folder. No content reads, and links (junctions, symbolic links, mount points) are never followed.</summary>
public sealed class ScoutFolderIndex : IDisposable
{
    private const int Capacity = 400000;
    /// <summary>How far the folders that appeared whole since the last search are walked, per search, before the index admits it may be missing part of them.</summary>
    private const int NewTreeBudget = 5000;
    private static readonly EnumerationOptions Shallow = new() { RecurseSubdirectories = false, IgnoreInaccessible = false, ReturnSpecialDirectories = false, AttributesToSkip = FileAttributes.Offline };
    private readonly object gate = new();
    private ScoutEntryStore store = new();
    private readonly ConcurrentQueue<(string Path, string? Old)> changes = new();
    private CancellationTokenSource cancellation = new();
    private Task? building;
    private FileSystemWatcher? watcher;
    private string? root;
    private int epoch;
    private int skipped;
    private bool limited;
    private bool missedChanges;
    private bool disposed;

    /// <summary>What one tracked change turned out to be once the file system was asked: what is at the path now (null when it is gone or cannot be indexed),
    /// the contents of a folder that appeared whole, and whether anything about it could not be read.</summary>
    private sealed record Update(string Path, string? Old, bool Gone, ScoutEntry? Current, List<ScoutEntry>? Children, bool Lost, int Inaccessible);

    public async Task<ScoutResult> SearchAsync(ScoutRequest request, IProgress<ScoutResult>? progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var query = new ScoutQuery(request.Query);
        var path = Path.GetFullPath(request.Root ?? throw new ArgumentException("Choose a folder."));
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException("The selected folder is unavailable.");
        Task build; int generation;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!path.Equals(root, StringComparison.OrdinalIgnoreCase)) Start(path);
            build = building!; generation = epoch;
        }
        while (true)
        {
            token.ThrowIfCancellationRequested();
            // The file system is asked about the tracked changes outside the lock, so the watcher is never held up while that happens.
            var updates = Reconcile();
            ScoutEntryStore.Snapshot snapshot; int errors; bool capped; bool dirty; bool complete;
            lock (gate)
            {
                if (generation != epoch || !path.Equals(root, StringComparison.OrdinalIgnoreCase)) throw new OperationCanceledException("The search folder changed.", token);
                // Asked before the entries are taken: a result can only be called complete if everything the walk found is in it.
                complete = build.IsCompleted;
                Apply(updates); snapshot = store.Capture(); errors = skipped; capped = limited; dirty = missedChanges;
            }
            // The search reads the entries where they are, outside the lock, so the walkers can keep adding to them meanwhile.
            var matches = query.Rank(new ScoutStoreSource(snapshot), request.Kind, token: token);
            // Changes still queued are applied by the next search. Only changes that were really lost make the index say it may be out of date.
            var result = new ScoutResult(matches, "Folder index", snapshot.Live, errors, complete, capped,
                Notice: dirty ? "Some changes could not be tracked. Refresh the folder index." : null);
            progress?.Report(result);
            if (complete) { await build.ConfigureAwait(false); return result; }
            await Task.WhenAny(build, Task.Delay(250, token)).ConfigureAwait(false);
        }
    }
    public void Refresh()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            cancellation.Cancel(); watcher?.Dispose(); watcher = null;
            root = null; building = null; store = new(); changes.Clear(); epoch++;
        }
    }
    private void Start(string path)
    {
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException("The selected folder is unavailable.");
        var oldCancellation = cancellation; oldCancellation.Cancel(); watcher?.Dispose(); cancellation = new();
        var token = cancellation.Token; root = path; store = new(); skipped = 0; limited = false; missedChanges = false; epoch++;
        while (changes.TryDequeue(out _)) { }
        try
        {
            watcher = new(path) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName, InternalBufferSize = 65536 };
            watcher.Created += (_, e) => TrackChange(e.FullPath, null, token);
            watcher.Deleted += (_, e) => TrackChange(e.FullPath, null, token);
            watcher.Renamed += (_, e) => TrackChange(e.FullPath, e.OldFullPath, token);
            watcher.Error += (_, _) => { lock (gate) { if (!token.IsCancellationRequested) missedChanges = true; } };
            watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { missedChanges = true; watcher?.Dispose(); watcher = null; }
        // Folders are known by their path without a closing separator, which is how a change reports the folder it happened in.
        var folder = Path.TrimEndingDirectorySeparator(path); var folderId = store.RegisterDirectory(folder);
        building = Task.Run(() => Walk(path, folder, folderId, token), CancellationToken.None);
        _ = building.ContinueWith(_ => oldCancellation.Dispose(), TaskScheduler.Default);
    }
    private void TrackChange(string path, string? old, CancellationToken token)
    {
        lock (gate)
        {
            if (disposed || token.IsCancellationRequested) return;
            if (changes.Count >= 10000) { missedChanges = true; return; }
            changes.Enqueue((path, old));
        }
    }
    /// <summary>Makes a folder known to the index, or -1 when this walk has been given up on.</summary>
    private int Register(string folder, CancellationToken token)
    {
        lock (gate) return token.IsCancellationRequested ? -1 : store.RegisterDirectory(folder);
    }
    private void Walk(string root, string folder, int folderId, CancellationToken token)
    {
        // Folders of tooling and caches are walked only after everything else, so the entry cap is spent on files people look for.
        var directories = new ConcurrentQueue<(string Path, int Id)>(); directories.Enqueue((folder, folderId));
        var deferred = new ConcurrentQueue<(string Path, int Id)>();
        var active = 1; var mainActive = 1;
        var workers = Enumerable.Range(0, Math.Clamp(Environment.ProcessorCount / 2, 2, 4)).Select(_ => Task.Run(() =>
        {
            // What the walker finds goes into this batch as it is enumerated: no object and no string is made for a file, only for a folder to walk into.
            var batch = new ScoutBatch();
            while (!token.IsCancellationRequested && Volatile.Read(ref active) > 0)
            {
                var main = true;
                if (!directories.TryDequeue(out var directory))
                {
                    if (Volatile.Read(ref mainActive) == 0 && deferred.TryDequeue(out directory)) main = false;
                    else { Thread.Sleep(2); continue; }
                }
                try
                {
                    var items = new FileSystemEnumerable<int>(directory.Path, (ref FileSystemEntry e) => batch.Add(directory.Id, ref e), Shallow);
                    foreach (var added in items)
                    {
                        if (token.IsCancellationRequested) return;
                        if (batch.IsWalkable(added))
                        {
                            var name = batch.NameOf(added);
                            var child = Path.Join(directory.Path, name);
                            var childId = Register(child, token);
                            if (childId < 0) return;
                            Interlocked.Increment(ref active);
                            if (!main || IsLowPriority(name)) deferred.Enqueue((child, childId));
                            else { Interlocked.Increment(ref mainActive); directories.Enqueue((child, childId)); }
                        }
                        if (batch.Count >= 256 && !Publish(batch, root, token)) return;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                { lock (gate) { if (!token.IsCancellationRequested) skipped++; } }
                finally { Interlocked.Decrement(ref active); if (main) Interlocked.Decrement(ref mainActive); }
            }
            Publish(batch, root, token);
        })).ToArray();
        Task.WaitAll(workers);
    }
    private static readonly HashSet<string> LowPriority = new(StringComparer.OrdinalIgnoreCase)
    { "node_modules", ".git", ".hg", ".svn", "__pycache__", "$Recycle.Bin", "AppData", ".nuget", ".gradle", ".cache", "site-packages", ".venv", "venv" };
    private static readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> LowPriorityByName = LowPriority.GetAlternateLookup<ReadOnlySpan<char>>();
    internal static bool IsLowPriority(string name) => LowPriority.Contains(name);
    private static bool IsLowPriority(ReadOnlySpan<char> name) => LowPriorityByName.Contains(name);
    private bool Publish(ScoutBatch batch, string path, CancellationToken token)
    {
        lock (gate)
        {
            if (token.IsCancellationRequested || path != root) return false;
            for (var i = 0; i < batch.Count; i++)
            {
                if (store.Live >= Capacity) { limited = true; cancellation.Cancel(); return false; }
                store.Add(batch, i);
            }
        }
        batch.Clear(); return true;
    }

    /// <summary>One entry of a tree that appeared while the index was live. Only a folder with the reparse attribute is asked whether it is a link, one extra read each.</summary>
    private static ScoutEntry Transform(ref FileSystemEntry e) =>
        new(e.ToFullPath(), e.IsDirectory, e.IsDirectory ? null : e.Length, e.LastWriteTimeUtc, e.IsDirectory && (e.Attributes & FileAttributes.ReparsePoint) != 0 && IsLink(e.ToFileSystemInfo()));
    /// <summary>True for a symbolic link, junction or mount point. A cloud placeholder (OneDrive) carries the reparse attribute too, but has no link target, so it is walked like any folder.</summary>
    internal static bool IsLink(FileSystemInfo info)
    {
        try { return info.LinkTarget is not null; }
        // What cannot be read is not followed: a loop through an unreadable junction would be worse than one folder missed.
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
    }

    // ---- changes seen by the watcher ----

    /// <summary>Takes a batch of tracked changes and asks the file system what each is now. Runs outside the lock; <see cref="Apply"/> puts the answers in.</summary>
    private List<Update> Reconcile()
    {
        var updates = new List<Update>();
        var budget = NewTreeBudget;
        while (updates.Count < 2000 && changes.TryDequeue(out var change)) updates.Add(Resolve(change.Path, change.Old, ref budget));
        return updates;
    }
    private static Update Resolve(string path, string? old, ref int budget)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.Offline) != 0) return new(path, old, false, null, null, false, 0);
            if ((attributes & FileAttributes.Directory) == 0)
            {
                var file = new FileInfo(path);
                return new(path, old, false, new(path, false, file.Length, file.LastWriteTimeUtc), null, false, 0);
            }
            var directory = new DirectoryInfo(path);
            var entry = new ScoutEntry(path, true, null, directory.LastWriteTimeUtc, (attributes & FileAttributes.ReparsePoint) != 0 && IsLink(directory));
            // A renamed folder keeps its contents under the new name; a link is never walked. A folder that appeared whole (moved in, unpacked) sends one
            // event for itself and none for what is inside it, so it is walked now, within the budget.
            if (old is not null || entry.IsReparse) return new(path, old, false, entry, null, false, 0);
            var children = new List<ScoutEntry>(); var inaccessible = 0;
            var complete = WalkTree(path, children, ref budget, ref inaccessible);
            return new(path, old, false, entry, children, !complete, inaccessible);
        }
        catch (FileNotFoundException) { return new(path, old, true, null, null, false, 0); }
        catch (DirectoryNotFoundException) { return new(path, old, true, null, null, false, 0); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new(path, old, false, null, null, true, 0); }
    }
    /// <summary>Lists a tree that appeared while the index was live. False when the budget ran out first, so part of it is still unknown.</summary>
    private static bool WalkTree(string path, List<ScoutEntry> into, ref int budget, ref int inaccessible)
    {
        var pending = new Stack<string>(); pending.Push(path);
        while (pending.TryPop(out var directory))
        {
            try
            {
                foreach (var entry in new FileSystemEnumerable<ScoutEntry>(directory, Transform, Shallow))
                {
                    if (--budget < 0) return false;
                    into.Add(entry);
                    if (entry.IsFolder && !entry.IsReparse) pending.Push(entry.FullPath);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { inaccessible++; }
        }
        return true;
    }
    private void Apply(List<Update> updates)
    {
        foreach (var update in updates)
        {
            if (update.Lost) missedChanges = true;
            skipped += update.Inaccessible;
            if (update.Old is { } old) store.Move(old, update.Path);
            if (update.Gone) { store.RemoveTree(update.Path); continue; }
            if (update.Current is null || !Upsert(update.Current)) continue;
            if (update.Children is not null) foreach (var child in update.Children) if (!Upsert(child)) break;
        }
    }
    private bool Upsert(ScoutEntry entry)
    {
        if (store.Live >= Capacity && !store.Contains(entry.FullPath)) { limited = true; return false; }
        store.Upsert(entry.FullPath, entry.IsFolder, entry.Size, (entry.Modified ?? default).UtcTicks, entry.IsReparse);
        return true;
    }
    public void Dispose() { lock (gate) { disposed = true; cancellation.Cancel(); watcher?.Dispose(); } }
}

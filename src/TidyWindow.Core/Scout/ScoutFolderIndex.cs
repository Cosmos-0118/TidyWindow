using System.Collections.Concurrent;
using System.IO.Enumeration;

namespace TidyWindow.Next.Core;

/// <summary>A bounded, reusable index of one accessible folder. No content reads or junction traversal.</summary>
public sealed class ScoutFolderIndex : IDisposable
{
    private const int Capacity = 400000;
    private readonly object gate = new();
    private ScoutEntryStore store = new();
    private readonly ConcurrentQueue<(string Path, string? Old)> changes = new();
    private CancellationTokenSource cancellation = new();
    private Task? building;
    private FileSystemWatcher? watcher;
    private string? root;
    private int skipped;
    private bool limited;
    private bool missedChanges;
    private bool disposed;

    public async Task<ScoutResult> SearchAsync(ScoutRequest request, IProgress<ScoutResult>? progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var query = new ScoutQuery(request.Query);
        var path = Path.GetFullPath(request.Root ?? throw new ArgumentException("Choose a folder."));
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException("The selected folder is unavailable.");
        Task build;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!path.Equals(root, StringComparison.OrdinalIgnoreCase)) Start(path);
            build = building!;
        }
        while (true)
        {
            token.ThrowIfCancellationRequested();
            ScoutEntryStore.Snapshot snapshot; int errors; bool capped; bool dirty; bool complete;
            lock (gate)
            {
                if (!path.Equals(root, StringComparison.OrdinalIgnoreCase)) throw new OperationCanceledException("The search folder changed.", token);
                // Asked before the entries are taken: a result can only be called complete if everything the walk found is in it.
                complete = build.IsCompleted;
                ApplyChanges(); snapshot = store.Capture(); errors = skipped; capped = limited; dirty = missedChanges || !changes.IsEmpty;
            }
            // The search reads the entries where they are, outside the lock, so the walkers can keep adding to them meanwhile.
            var matches = query.Rank(new ScoutStoreSource(snapshot), request.Kind, token: token);
            var result = new ScoutResult(matches, "Folder index", snapshot.Live, errors, complete, capped,
                dirty ? "Some changes could not be tracked. Refresh the folder index." : null);
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
            root = null; building = null; store = new(); changes.Clear();
        }
    }
    private void Start(string path)
    {
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException("The selected folder is unavailable.");
        var oldCancellation = cancellation; oldCancellation.Cancel(); watcher?.Dispose(); cancellation = new();
        var token = cancellation.Token; root = path; store = new(); skipped = 0; limited = false; missedChanges = false;
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
                    var items = new FileSystemEnumerable<int>(directory.Path, (ref FileSystemEntry e) => batch.Add(directory.Id, ref e), new EnumerationOptions
                    { RecurseSubdirectories = false, IgnoreInaccessible = false, ReturnSpecialDirectories = false, AttributesToSkip = FileAttributes.Offline });
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
    private void ApplyChanges()
    {
        var count = 0;
        while (count++ < 2000 && changes.TryDequeue(out var change))
        {
            if (change.Old is { } old) store.Move(old, change.Path);
            try
            {
                var attributes = File.GetAttributes(change.Path);
                if ((attributes & FileAttributes.Offline) != 0) continue;
                var folder = attributes.HasFlag(FileAttributes.Directory);
                if (store.Live < Capacity || store.Contains(change.Path))
                {
                    var info = folder ? (FileSystemInfo)new DirectoryInfo(change.Path) : new FileInfo(change.Path);
                    store.Upsert(change.Path, folder, info is FileInfo file ? file.Length : null, info.LastWriteTimeUtc.Ticks, attributes.HasFlag(FileAttributes.ReparsePoint));
                    if (folder && change.Old is null) missedChanges = true; // A new/moved tree needs a refresh to discover all children.
                }
                else limited = true;
            }
            catch (FileNotFoundException) { store.RemoveTree(change.Path); }
            catch (DirectoryNotFoundException) { store.RemoveTree(change.Path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { missedChanges = true; }
        }
    }
    public void Dispose() { lock (gate) { disposed = true; cancellation.Cancel(); watcher?.Dispose(); } }
}

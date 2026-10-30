using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Enumeration;

namespace TidyWindow.Next.Core;

/// <summary>A bounded, reusable index of one accessible folder. No content reads or junction traversal.</summary>
public sealed class ScoutFolderIndex : IDisposable
{
    private const int Capacity = 400000;
    private readonly object gate = new();
    private readonly Dictionary<string, ScoutEntry> entries = new(StringComparer.OrdinalIgnoreCase);
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
            ScoutEntry[] snapshot; int errors; bool capped; bool dirty;
            lock (gate)
            {
                if (!path.Equals(root, StringComparison.OrdinalIgnoreCase)) throw new OperationCanceledException("The search folder changed.", token);
                ApplyChanges(); snapshot = entries.Values.ToArray(); errors = skipped; capped = limited; dirty = missedChanges || !changes.IsEmpty;
            }
            var matches = query.Rank(snapshot, request.Kind, token: token);
            var result = new ScoutResult(matches, "Folder index", snapshot.Length, errors, build.IsCompleted, capped,
                dirty ? "Some changes could not be tracked. Refresh the folder index." : null);
            progress?.Report(result);
            if (build.IsCompleted) { await build.ConfigureAwait(false); return result; }
            await Task.WhenAny(build, Task.Delay(250, token)).ConfigureAwait(false);
        }
    }
    public void Refresh()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            cancellation.Cancel(); watcher?.Dispose(); watcher = null;
            root = null; building = null; entries.Clear(); changes.Clear();
        }
    }
    private void Start(string path)
    {
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException("The selected folder is unavailable.");
        var oldCancellation = cancellation; oldCancellation.Cancel(); watcher?.Dispose(); cancellation = new();
        var token = cancellation.Token; root = path; entries.Clear(); skipped = 0; limited = false; missedChanges = false;
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
        building = Task.Run(() => Walk(path, token), CancellationToken.None);
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
    private void Walk(string path, CancellationToken token)
    {
        // Folders of tooling and caches are walked only after everything else, so the entry cap is spent on files people look for.
        var directories = new ConcurrentQueue<string>(); directories.Enqueue(path);
        var deferred = new ConcurrentQueue<string>();
        var active = 1; var mainActive = 1;
        var workers = Enumerable.Range(0, Math.Clamp(Environment.ProcessorCount / 2, 2, 4)).Select(_ => Task.Run(() =>
        {
            var batch = new List<ScoutEntry>(256);
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
                    var items = new FileSystemEnumerable<ScoutEntry>(directory, (ref FileSystemEntry e) =>
                        new(e.ToFullPath(), e.IsDirectory, e.IsDirectory ? null : e.Length, e.LastWriteTimeUtc, e.Attributes.HasFlag(FileAttributes.ReparsePoint)), new EnumerationOptions
                        { RecurseSubdirectories = false, IgnoreInaccessible = false, ReturnSpecialDirectories = false, AttributesToSkip = FileAttributes.Offline });
                    foreach (var entry in items)
                    {
                        if (token.IsCancellationRequested) return;
                        if (entry.IsFolder && !entry.IsReparse)
                        {
                            Interlocked.Increment(ref active);
                            if (!main || IsLowPriority(entry.Name)) deferred.Enqueue(entry.FullPath);
                            else { Interlocked.Increment(ref mainActive); directories.Enqueue(entry.FullPath); }
                        }
                        batch.Add(entry);
                        if (batch.Count >= 256 && !Publish(batch, path, token)) return;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                { lock (gate) { if (!token.IsCancellationRequested) skipped++; } }
                finally { Interlocked.Decrement(ref active); if (main) Interlocked.Decrement(ref mainActive); }
            }
            Publish(batch, path, token);
        })).ToArray();
        Task.WaitAll(workers);
    }
    private static readonly HashSet<string> LowPriority = new(StringComparer.OrdinalIgnoreCase)
    { "node_modules", ".git", ".hg", ".svn", "__pycache__", "$Recycle.Bin", "AppData", ".nuget", ".gradle", ".cache", "site-packages", ".venv", "venv" };
    internal static bool IsLowPriority(string name) => LowPriority.Contains(name);
    private bool Publish(List<ScoutEntry> batch, string path, CancellationToken token)
    {
        lock (gate)
        {
            if (token.IsCancellationRequested || path != root) return false;
            foreach (var entry in batch)
            {
                if (entries.Count >= Capacity) { limited = true; cancellation.Cancel(); return false; }
                entries[entry.FullPath] = entry;
            }
        }
        batch.Clear(); return true;
    }
    private void ApplyChanges()
    {
        var count = 0;
        while (count++ < 2000 && changes.TryDequeue(out var change))
        {
            if (change.Old is { } old)
                foreach (var key in entries.Keys.Where(x => x.Equals(old, StringComparison.OrdinalIgnoreCase) || x.StartsWith(old + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)).ToArray())
                { var value = entries[key]; entries.Remove(key); var renamed = change.Path + key[old.Length..]; entries[renamed] = new(renamed, value.IsFolder, value.Size, value.Modified, value.IsReparse); }
            try
            {
                var attributes = File.GetAttributes(change.Path);
                if ((attributes & FileAttributes.Offline) != 0) continue;
                var folder = attributes.HasFlag(FileAttributes.Directory);
                if (entries.Count < Capacity || entries.ContainsKey(change.Path))
                {
                    var info = folder ? (FileSystemInfo)new DirectoryInfo(change.Path) : new FileInfo(change.Path);
                    entries[change.Path] = new(change.Path, folder, info is FileInfo file ? file.Length : null, info.LastWriteTimeUtc, attributes.HasFlag(FileAttributes.ReparsePoint));
                    if (folder && change.Old is null) missedChanges = true; // A new/moved tree needs a refresh to discover all children.
                }
                else limited = true;
            }
            catch (FileNotFoundException) { RemoveTree(change.Path); }
            catch (DirectoryNotFoundException) { RemoveTree(change.Path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { missedChanges = true; }
        }
    }
    private void RemoveTree(string path)
    {
        foreach (var key in entries.Keys.Where(x => x.Equals(path, StringComparison.OrdinalIgnoreCase) || x.StartsWith(path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)).ToArray()) entries.Remove(key);
    }
    public void Dispose() { lock (gate) { disposed = true; cancellation.Cancel(); watcher?.Dispose(); } }
}

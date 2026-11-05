using System.Diagnostics;
using System.IO.Enumeration;

namespace TidyWindow.Next.Core;

/// <summary>
/// Removes what a scan found. Only files that match their rule are touched, links are never followed, and a file that is
/// open, protected or already gone is skipped and counted instead of forced. Rules that ask for it move files into the
/// undo folder instead of deleting them. A clean that is stopped part-way returns what it had done by then, marked as stopped.
/// <para>
/// Built to be gentle on small machines. All of it, including the preparation, runs off the caller's thread, so a window never waits on a disk. Files are
/// deleted as the folder is listed (nothing is collected first, so a million files cost no memory), by a few workers matched to the disk, in Windows'
/// background mode so the PC stays usable. Progress is reported several times a second, so a long clean never looks stuck.
/// </para>
/// </summary>
public sealed class CleanupCleaner(CleanupContext context, CleanupQuarantine? quarantine = null)
{
    private static readonly long ProgressIntervalMs = 250;

    public Task<CleanupResult> CleanAsync(IReadOnlyList<CleanupFinding> findings, IProgress<CleanupProgress>? progress = null, CancellationToken cancellationToken = default) =>
        // The first part of the work is listing folders and checking them. It runs on a pool thread, never on the window's.
        Task.Run(() => Clean(findings, progress, cancellationToken), CancellationToken.None);

    private CleanupResult Clean(IReadOnlyList<CleanupFinding> findings, IProgress<CleanupProgress>? progress, CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        var totals = new Totals();
        var batch = findings.Any(x => x.Rule.Quarantine && x.HasContent) ? quarantine?.Begin() : null;
        var now = context.Now();
        var protectedPaths = CleanupPaths.Protected(context);
        var units = new List<Unit>();
        var recycleBins = new List<CleanupFinding>();

        foreach (var finding in findings)
        {
            if (!finding.HasContent || finding.Skipped is not null) continue;
            var rule = finding.Rule;
            // System actions belong to the elevated helper; files that need administrator rights are never attempted without them.
            if (rule.Action is not (CleanupAction.Files or CleanupAction.RecycleBin)) continue;
            if (finding.NeedsAdmin) { Interlocked.Increment(ref totals.Denied); continue; }
            if (rule.Action == CleanupAction.RecycleBin) { recycleBins.Add(finding); continue; }
            if (rule.Quarantine && batch is null) { Interlocked.Increment(ref totals.Failed); continue; }
            var filter = new CleanupFilter(rule, now);
            var state = new RuleProgress();
            var own = new List<Unit>();
            for (var i = 0; i < finding.Roots.Length; i++)
            {
                var root = finding.Roots[i];
                // The scan may be minutes old: confirm the folder is still a real, allowed folder before touching it.
                if (!CleanupPaths.IsSafeRoot(root, rule, protectedPaths) || !CleanupPaths.IsPlainFolder(root)) continue;
                own.Add(new Unit(finding, state, root, i, root, filter, Recurse: false));
                if (rule.Recurse)
                    foreach (var folder in TopFolders(root)) own.Add(new Unit(finding, state, root, i, folder, filter, Recurse: true));
            }
            state.Pending = own.Count;
            units.AddRange(own);
        }

        var load = CleanupLoad.ForClean(context, units.Select(x => x.Root));
        var gate = new Gate(progress, totals);
        // A rule is reported once its last piece is done.
        var options = new ParallelOptions { MaxDegreeOfParallelism = load.Workers, CancellationToken = cancellationToken };
        var stopped = false;
        try
        {
            // One background scope per worker, not per folder: the mode is set once on each thread and undone when that worker has no more folders.
            Parallel.ForEach(units, options, () => CleanupIo.Background.Begin(load.Background), (unit, _, _, scope) =>
            {
                Sweep(unit, batch, totals, gate, cancellationToken);
                if (Interlocked.Decrement(ref unit.State.Pending) == 0) progress?.Report(new CleanupProgress(unit.Finding.Rule.Name, Interlocked.Read(ref totals.Freed), Volatile.Read(ref totals.Removed)));
                return scope;
            }, scope => scope.Dispose());
            // The Recycle Bin is emptied by Windows in one call that cannot be interrupted, so it goes last: a stop never has to wait for it.
            foreach (var finding in recycleBins)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (EmptyRecycleBin(load.Background)) { Interlocked.Add(ref totals.Freed, finding.Bytes); Interlocked.Add(ref totals.Removed, finding.Files); }
                else Interlocked.Increment(ref totals.Failed);
                progress?.Report(new CleanupProgress(finding.Rule.Name, Interlocked.Read(ref totals.Freed), Volatile.Read(ref totals.Removed)));
            }
        }
        // Whatever was removed before the stop stays removed, so it is still counted; the result says the clean did not finish.
        catch (OperationCanceledException) { stopped = true; }
        finally
        {
            // Even a cancelled clean must record what it already moved, or those files could never be restored.
            batch?.Commit();
        }
        return new CleanupResult(totals.Freed, totals.Removed, totals.InUse, totals.Denied, totals.Failed, batch is { Count: > 0 } ? batch.Id : null, batch?.Bytes ?? 0, timer.Elapsed, stopped);
    }

    /// <summary>Windows' shell wants a single-threaded apartment for the Recycle Bin call, and the call can take minutes on a big bin, so it gets a thread of its own.</summary>
    private static bool EmptyRecycleBin(bool background)
    {
        var emptied = false;
        var thread = new Thread(() => { using var scope = CleanupIo.Background.Begin(background); emptied = CleanupNative.EmptyRecycleBin(); }) { IsBackground = true, Name = "Recycle Bin" };
        if (OperatingSystem.IsWindows()) thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return emptied;
    }

    private static IEnumerable<string> TopFolders(string root)
    {
        var options = CleanupFilter.Options(false);
        return new FileSystemEnumerable<string>(root, (ref FileSystemEntry entry) => entry.ToFullPath(), options) { ShouldIncludePredicate = (ref FileSystemEntry entry) => entry.IsDirectory };
    }

    private void Sweep(Unit unit, CleanupQuarantine.Batch? batch, Totals totals, Gate gate, CancellationToken token)
    {
        var rule = unit.Finding.Rule;
        var wholeFolders = rule.Masks.Length == 0;
        var cutoff = rule.MinAgeDays > 0 ? context.Now().AddDays(-rule.MinAgeDays).Ticks : long.MaxValue;
        // The top-level folders were listed a moment ago. One swapped for a link since then must not be walked: the walk skips links it meets inside
        // a folder, but not the folder it starts from. A folder that has simply gone is nothing to clean; a link in its place counts as a failure.
        if (unit.Recurse && !CleanupPaths.IsPlainFolder(unit.Folder))
        {
            if (Directory.Exists(unit.Folder)) Interlocked.Increment(ref totals.Failed);
            return;
        }
        var filter = unit.Filter;
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var emptied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Deleting inside a folder refreshes its own timestamp, so whether it is old enough is decided before anything is removed.
        var folderIsOld = unit.Recurse && wholeFolders && Directory.GetLastWriteTimeUtc(unit.Folder).Ticks < cutoff;
        var enumerable = new FileSystemEnumerable<(string Path, long Length, bool IsDirectory)>(unit.Folder,
            (ref FileSystemEntry entry) => (entry.ToFullPath(), entry.IsDirectory ? 0 : entry.Length, entry.IsDirectory), CleanupFilter.Options(unit.Recurse))
        {
            ShouldIncludePredicate = (ref FileSystemEntry entry) => entry.IsDirectory ? wholeFolders && unit.Recurse && entry.LastWriteTimeUtc.UtcTicks < cutoff : filter.Matches(ref entry)
        };
        string? lastParent = null;
        try
        {
            // Each file goes as the listing reaches it. Removing entries already returned does not disturb a directory listing, and nothing is held in memory.
            foreach (var (path, length, isDirectory) in enumerable)
            {
                token.ThrowIfCancellationRequested();
                if (isDirectory) { folders.Add(path); continue; }
                switch (rule.Quarantine ? Hold(batch!, unit, (path, length)) : Delete(path))
                {
                    case Outcome.Removed:
                        Interlocked.Add(ref totals.Freed, length); Interlocked.Increment(ref totals.Removed);
                        if (wholeFolders && unit.Recurse)
                        {
                            // Most files share the folder of the one before, so a new string is made only when the folder changes.
                            var cut = path.LastIndexOf(Path.DirectorySeparatorChar);
                            if (cut > 0 && (lastParent is null || !path.AsSpan(0, cut).Equals(lastParent, StringComparison.OrdinalIgnoreCase))) emptied.Add(lastParent = path[..cut]);
                        }
                        break;
                    case Outcome.Held: Interlocked.Increment(ref totals.Removed); break;
                    case Outcome.InUse: Interlocked.Increment(ref totals.InUse); break;
                    case Outcome.Denied: Interlocked.Increment(ref totals.Denied); break;
                    case Outcome.Failed: Interlocked.Increment(ref totals.Failed); break;
                }
                gate.Tick(rule.Name);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Interlocked.Increment(ref totals.Failed); }

        // Folders emptied by the clean go too, deepest first; a folder that still holds something refuses and stays.
        // That includes folders that were too new to remove on their own but only held files that were old enough.
        foreach (var directory in emptied)
            for (var parent = directory; parent is not null && CleanupPaths.IsInside(parent, unit.Root); parent = Path.GetDirectoryName(parent)) folders.Add(parent);
        if (!rule.Quarantine && folders.Count > 0)
            foreach (var folder in folders.OrderByDescending(x => x.Length)) CleanupIo.TryRemoveDirectory(folder);
        if (!rule.Quarantine && folderIsOld && unit.Folder != unit.Root) CleanupIo.TryRemoveDirectory(unit.Folder);
    }

    private enum Outcome { Removed, Held, InUse, Denied, Failed, Gone }

    /// <summary>
    /// Windows refuses to move or delete a file another program has open with a sharing or lock violation, or because it is mapped into memory.
    /// That is a file in use. Anything else that goes wrong (a full drive, a name too long) is a failure, and is not dressed up as "in use".
    /// </summary>
    private static bool IsInUse(IOException ex) => (ex.HResult & 0xFFFF) is 32 or 33 or 1224;

    private static Outcome Delete(string path) => CleanupIo.DeleteFile(path) switch
    {
        DeleteResult.Removed => Outcome.Removed,
        DeleteResult.Gone => Outcome.Gone,
        DeleteResult.InUse => Outcome.InUse,
        DeleteResult.Denied => Outcome.Denied,
        _ => Outcome.Failed
    };

    private static Outcome Hold(CleanupQuarantine.Batch batch, Unit unit, (string Path, long Length) file)
    {
        try { batch.Hold(unit.Finding.Rule.Id, unit.RootIndex, unit.Root, file.Path, file.Length); return Outcome.Held; }
        catch (UnauthorizedAccessException) { return Outcome.Denied; }
        catch (FileNotFoundException) { return Outcome.Gone; }
        catch (DirectoryNotFoundException) { return Outcome.Gone; }
        // Holding moves the file to the undo folder, which may be on another drive: a drive that is full, or an undo folder that cannot be made, is a failure, not a file in use.
        catch (IOException ex) { return IsInUse(ex) ? Outcome.InUse : Outcome.Failed; }
    }

    /// <summary>Lets a running tally out a few times a second, from whichever worker gets there first, however many files are being removed.</summary>
    private sealed class Gate(IProgress<CleanupProgress>? progress, Totals totals)
    {
        private long next;
        public void Tick(string name)
        {
            if (progress is null) return;
            var now = Environment.TickCount64;
            var due = Volatile.Read(ref next);
            if (now < due || Interlocked.CompareExchange(ref next, now + ProgressIntervalMs, due) != due) return;
            progress.Report(new CleanupProgress(name, Interlocked.Read(ref totals.Freed), Volatile.Read(ref totals.Removed), Partial: true));
        }
    }

    private sealed class Totals { public long Freed; public int Removed, InUse, Denied, Failed; }
    private sealed class RuleProgress { public int Pending; }
    private sealed record Unit(CleanupFinding Finding, RuleProgress State, string Root, int RootIndex, string Folder, CleanupFilter Filter, bool Recurse);
}

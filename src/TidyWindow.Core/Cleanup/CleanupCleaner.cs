using System.Diagnostics;
using System.IO.Enumeration;

namespace TidyWindow.Next.Core;

/// <summary>
/// Removes what a scan found. Only files that match their rule are touched, links are never followed, and a file that is
/// open, protected or already gone is skipped and counted instead of forced. Rules that ask for it move files into the
/// undo folder instead of deleting them. A clean that is stopped part-way returns what it had done by then, marked as stopped.
/// </summary>
public sealed class CleanupCleaner(CleanupContext context, CleanupQuarantine? quarantine = null)
{
    private const int InnerParallelThreshold = 1500;

    public async Task<CleanupResult> CleanAsync(IReadOnlyList<CleanupFinding> findings, IProgress<CleanupProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var timer = Stopwatch.StartNew();
        var totals = new Totals();
        var batch = findings.Any(x => x.Rule.Quarantine && x.HasContent) ? quarantine?.Begin() : null;
        var now = context.Now();
        var protectedPaths = CleanupPaths.Protected(context);
        var units = new List<Unit>();

        foreach (var finding in findings)
        {
            if (!finding.HasContent || finding.Skipped is not null) continue;
            var rule = finding.Rule;
            // System actions belong to the elevated helper; files that need administrator rights are never attempted without them.
            if (rule.Action is not (CleanupAction.Files or CleanupAction.RecycleBin)) continue;
            if (finding.NeedsAdmin) { Interlocked.Increment(ref totals.Denied); continue; }
            if (rule.Action == CleanupAction.RecycleBin)
            {
                if (CleanupNative.EmptyRecycleBin()) { Interlocked.Add(ref totals.Freed, finding.Bytes); Interlocked.Add(ref totals.Removed, finding.Files); }
                else Interlocked.Increment(ref totals.Failed);
                progress?.Report(new CleanupProgress(rule.Name, totals.Freed, totals.Removed));
                continue;
            }
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

        // A rule is reported once its last piece is done.
        var options = new ParallelOptions { MaxDegreeOfParallelism = context.Parallelism, CancellationToken = cancellationToken };
        var stopped = false;
        try
        {
            await Task.Run(() => Parallel.ForEach(units, options, unit =>
            {
                Sweep(unit, batch, totals, cancellationToken);
                if (Interlocked.Decrement(ref unit.State.Pending) == 0) progress?.Report(new CleanupProgress(unit.Finding.Rule.Name, Interlocked.Read(ref totals.Freed), Volatile.Read(ref totals.Removed)));
            }), cancellationToken).ConfigureAwait(false);
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

    private static IEnumerable<string> TopFolders(string root)
    {
        var options = CleanupFilter.Options(false);
        return new FileSystemEnumerable<string>(root, (ref FileSystemEntry entry) => entry.ToFullPath(), options) { ShouldIncludePredicate = (ref FileSystemEntry entry) => entry.IsDirectory };
    }

    private void Sweep(Unit unit, CleanupQuarantine.Batch? batch, Totals totals, CancellationToken token)
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
        var files = new List<(string Path, long Length)>();
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var emptied = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        // Deleting inside a folder refreshes its own timestamp, so whether it is old enough is decided before anything is removed.
        var folderIsOld = unit.Recurse && wholeFolders && Directory.GetLastWriteTimeUtc(unit.Folder).Ticks < cutoff;
        var enumerable = new FileSystemEnumerable<(string Path, long Length, bool IsDirectory)>(unit.Folder,
            (ref FileSystemEntry entry) => (entry.ToFullPath(), entry.IsDirectory ? 0 : entry.Length, entry.IsDirectory), CleanupFilter.Options(unit.Recurse))
        {
            ShouldIncludePredicate = (ref FileSystemEntry entry) => entry.IsDirectory ? wholeFolders && unit.Recurse && entry.LastWriteTimeUtc.UtcTicks < cutoff : filter.Matches(ref entry)
        };
        try
        {
            foreach (var (path, length, isDirectory) in enumerable)
            {
                if (isDirectory) folders.Add(path); else files.Add((path, length));
                if ((files.Count & 0x3FF) == 0) token.ThrowIfCancellationRequested();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Interlocked.Increment(ref totals.Failed); }

        void Remove((string Path, long Length) file)
        {
            if (token.IsCancellationRequested) return;
            switch (rule.Quarantine ? Hold(batch!, unit, file) : Delete(file.Path))
            {
                case Outcome.Removed:
                    Interlocked.Add(ref totals.Freed, file.Length); Interlocked.Increment(ref totals.Removed);
                    if (wholeFolders && unit.Recurse && Path.GetDirectoryName(file.Path) is { } parent) emptied.TryAdd(parent, 0);
                    break;
                case Outcome.Held: Interlocked.Increment(ref totals.Removed); break;
                case Outcome.InUse: Interlocked.Increment(ref totals.InUse); break;
                case Outcome.Denied: Interlocked.Increment(ref totals.Denied); break;
                case Outcome.Failed: Interlocked.Increment(ref totals.Failed); break;
            }
        }

        if (files.Count >= InnerParallelThreshold)
            Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = Math.Min(8, context.Parallelism), CancellationToken = token }, Remove);
        else foreach (var file in files) Remove(file);

        // Folders emptied by the clean go too, deepest first; a folder that still holds something refuses and stays.
        // That includes folders that were too new to remove on their own but only held files that were old enough.
        foreach (var directory in emptied.Keys)
            for (var parent = directory; parent is not null && CleanupPaths.IsInside(parent, unit.Root); parent = Path.GetDirectoryName(parent)) folders.Add(parent);
        if (!rule.Quarantine && folders.Count > 0)
        {
            foreach (var folder in folders.OrderByDescending(x => x.Length))
            {
                try { Directory.Delete(folder, recursive: false); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        if (!rule.Quarantine && folderIsOld && unit.Folder != unit.Root)
        {
            try { Directory.Delete(unit.Folder, recursive: false); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private enum Outcome { Removed, Held, InUse, Denied, Failed, Gone }

    /// <summary>
    /// Windows refuses to move or delete a file another program has open with a sharing or lock violation, or because it is mapped into memory.
    /// That is a file in use. Anything else that goes wrong (a full drive, a name too long) is a failure, and is not dressed up as "in use".
    /// </summary>
    private static bool IsInUse(IOException ex) => (ex.HResult & 0xFFFF) is 32 or 33 or 1224;

    private static Outcome Delete(string path)
    {
        try { File.Delete(path); return Outcome.Removed; }
        catch (UnauthorizedAccessException)
        {
            // Read-only files are common in caches; clear the flag and try once more. Real permission problems stay denied.
            try { File.SetAttributes(path, FileAttributes.Normal); File.Delete(path); return Outcome.Removed; }
            catch (UnauthorizedAccessException) { return Outcome.Denied; }
            catch (FileNotFoundException) { return Outcome.Gone; }
            catch (IOException ex) { return IsInUse(ex) ? Outcome.InUse : Outcome.Failed; }
        }
        catch (FileNotFoundException) { return Outcome.Gone; }
        catch (DirectoryNotFoundException) { return Outcome.Gone; }
        catch (IOException ex) { return IsInUse(ex) ? Outcome.InUse : Outcome.Failed; }
    }

    private static Outcome Hold(CleanupQuarantine.Batch batch, Unit unit, (string Path, long Length) file)
    {
        try { batch.Hold(unit.Finding.Rule.Id, unit.RootIndex, unit.Root, file.Path, file.Length); return Outcome.Held; }
        catch (UnauthorizedAccessException) { return Outcome.Denied; }
        catch (FileNotFoundException) { return Outcome.Gone; }
        catch (DirectoryNotFoundException) { return Outcome.Gone; }
        // Holding moves the file to the undo folder, which may be on another drive: a drive that is full, or an undo folder that cannot be made, is a failure, not a file in use.
        catch (IOException ex) { return IsInUse(ex) ? Outcome.InUse : Outcome.Failed; }
    }

    private sealed class Totals { public long Freed; public int Removed, InUse, Denied, Failed; }
    private sealed class RuleProgress { public int Pending; }
    private sealed record Unit(CleanupFinding Finding, RuleProgress State, string Root, int RootIndex, string Folder, CleanupFilter Filter, bool Recurse);
}

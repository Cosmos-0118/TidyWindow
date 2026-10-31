using System.Collections.Concurrent;
using System.IO.Enumeration;

namespace TidyWindow.Next.Core;

/// <summary>
/// Measures what each rule would remove without touching anything. Every folder is split into its top-level
/// subfolders and the pieces are walked in parallel; one allocation-free pass per piece counts files and bytes.
/// A rule's finding is reported the moment its last piece finishes, so the list fills in while the scan runs.
/// </summary>
public sealed class CleanupScanner(CleanupContext context)
{
    public async Task<IReadOnlyList<CleanupFinding>> ScanAsync(IReadOnlyList<CleanupRule> rules, IProgress<CleanupFinding>? progress = null, CancellationToken cancellationToken = default)
    {
        var findings = new CleanupFinding?[rules.Count];
        var running = new Lazy<IReadOnlySet<string>>(context.RunningProcesses);
        var pieces = new ConcurrentBag<Piece>();
        var now = context.Now();
        var options = new ParallelOptions { MaxDegreeOfParallelism = context.Parallelism, CancellationToken = cancellationToken };

        void Report(int index, CleanupFinding finding) { findings[index] = finding; progress?.Report(finding); }
        CleanupFinding Finish(int index, RuleState state)
        {
            var rule = rules[index];
            var active = state.Files > 0 && rule.Processes.Any(running.Value.Contains);
            return new CleanupFinding(rule, state.Roots, state.Bytes, state.Files, state.Unreadable, active, NeedsAdmin: rule.Admin && !context.IsAdministrator);
        }

        await Task.Run(() =>
        {
            var roots = CleanupPaths.Plan(rules, context);
            // Cheap pass: list each folder's top level, so the expensive walks below can be shared out evenly.
            Parallel.For(0, rules.Count, options, index =>
            {
                var rule = rules[index];
                // Administrator areas are measured too: reading is allowed, and showing the size lets the user decide before any permission prompt.
                var needsAdmin = rule.Admin && !context.IsAdministrator;
                switch (rule.Action)
                {
                    case CleanupAction.RecycleBin:
                        var (bytes, items) = CleanupNative.QueryRecycleBin();
                        Report(index, new CleanupFinding(rule, [], bytes, (int)Math.Min(items, int.MaxValue), 0, false, NeedsAdmin: needsAdmin));
                        return;
                    case CleanupAction.ComponentStore:
                    case CleanupAction.DiskCleanup:
                        // Windows' own handlers do the work and report no sizes up front; a handler this Windows lacks means nothing to offer.
                        if (rule.Action == CleanupAction.DiskCleanup && !rule.Handlers.Any(handler => context.HandlerFolders(handler) is not null)) { Report(index, new CleanupFinding(rule, [], 0, 0, 0, false, NeedsAdmin: needsAdmin)); return; }
                        Report(index, new CleanupFinding(rule, [], 0, 0, 0, false, NeedsAdmin: needsAdmin, Unmeasured: true));
                        return;
                    case CleanupAction.Hibernation:
                        var length = HibernationFileSize();
                        Report(index, new CleanupFinding(rule, [], length, length > 0 ? 1 : 0, 0, false, NeedsAdmin: needsAdmin));
                        return;
                }
                var state = new RuleState(roots[index]);
                var filter = new CleanupFilter(rule, now);
                var folders = new List<string>();
                foreach (var root in state.Roots)
                {
                    var top = new Tally(root, filter, recurse: false, rule.Recurse ? folders : null, cancellationToken);
                    top.Run();
                    state.Add(top);
                }
                state.Pending = folders.Count;
                if (folders.Count == 0) { Report(index, Finish(index, state)); return; }
                foreach (var folder in folders) pieces.Add(new Piece(index, state, folder, filter));
            });
        }, cancellationToken).ConfigureAwait(false);

        await Parallel.ForEachAsync(pieces, options, (piece, token) =>
        {
            var tally = new Tally(piece.Folder, piece.Filter, recurse: true, null, token);
            tally.Run();
            piece.State.Add(tally);
            if (piece.State.Complete()) Report(piece.Rule, Finish(piece.Rule, piece.State));
            return ValueTask.CompletedTask;
        }).ConfigureAwait(false);

        return findings.Select((finding, index) => finding ?? new CleanupFinding(rules[index], [], 0, 0, 0, false)).ToArray();

        long HibernationFileSize()
        {
            var drive = context.Variable("SYSTEMDRIVE");
            if (string.IsNullOrWhiteSpace(drive)) return 0;
            try { var file = new FileInfo(Path.Combine(drive + Path.DirectorySeparatorChar, "hiberfil.sys")); return file.Exists ? file.Length : 0; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
        }
    }

    private sealed class RuleState(string[] roots)
    {
        public string[] Roots { get; } = roots;
        public long Bytes;
        public int Files;
        public int Unreadable;
        public int Pending;
        public void Add(Tally tally)
        {
            Interlocked.Add(ref Bytes, tally.Bytes);
            Interlocked.Add(ref Files, tally.Files);
            Interlocked.Add(ref Unreadable, tally.Errors);
        }
        public bool Complete() => Interlocked.Decrement(ref Pending) == 0;
    }

    private sealed record Piece(int Rule, RuleState State, string Folder, CleanupFilter Filter);

    /// <summary>Adds up the matching files of one folder. Names, sizes and times come from the directory listing itself, so there is no extra lookup per file and nothing is allocated per entry.</summary>
    private sealed class Tally(string folder, CleanupFilter filter, bool recurse, List<string>? subfolders, CancellationToken token)
        : FileSystemEnumerator<byte>(folder, CleanupFilter.Options(recurse))
    {
        private int seen;
        public long Bytes;
        public int Files;
        public int Errors;

        public void Run()
        {
            try { while (MoveNext()) { } }
            finally { Dispose(); }
        }

        protected override byte TransformEntry(ref FileSystemEntry entry) => 0;

        protected override bool ShouldIncludeEntry(ref FileSystemEntry entry)
        {
            if ((++seen & 0x3FF) == 0) token.ThrowIfCancellationRequested();
            if (entry.IsDirectory) { subfolders?.Add(entry.ToFullPath()); return false; }
            if (filter.Matches(ref entry)) { Bytes += entry.Length; Files++; }
            return false;
        }

        protected override bool ContinueOnError(int error) { Errors++; return true; }
    }
}

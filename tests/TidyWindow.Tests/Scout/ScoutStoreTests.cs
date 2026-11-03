using System.Diagnostics;
using TidyWindow.Next.Core;
using Xunit;
using Xunit.Abstractions;

namespace TidyWindow.Next.Tests;

/// <summary>
/// The folder index keeps its entries in a compact store instead of one object and one path string each. These tests hold it to the old behavior:
/// a search over the store must return exactly what the original ranking code returned for the same entries, in the same order.
/// </summary>
public sealed class ScoutStoreTests(ITestOutputHelper output)
{
    private const string Home = @"C:\Users\me";

    // ---- a repeatable pile of entries that looks like a real profile: deep folders, mixed case, accents, wide letters, junk folders ----
    private static readonly string[] Roots = [@"C:\Users\me\Documents", @"C:\Users\me\Desktop", @"C:\Users\me\Downloads", @"C:\Users\me\AppData\Local", @"C:\Users\me\Project\node_modules", @"C:\Program Files\Tool", @"C:\Windows\System32"];
    private static readonly string[] Words = ["report", "Budget", "photo", "vacation", "Café", "ＣＡＦＥ", "notes", "invoice", "résumé", "setup", "readme", "Draft", "archive", "2026", "final", "copy", "data", "image", "Straße", "config", "logo", "backup", "my file", "x"];
    private static readonly string[] Extensions = [".txt", ".md", ".png", ".JPG", ".pdf", ".exe", ".msi", ".zip", ".docx", ".csv", ".log", ""];

    private static (ScoutEntry[] Entries, ScoutEntryStore Store) Corpus(int count)
    {
        var random = new Random(1234);
        var folders = new List<string>(Roots);
        var entries = new List<ScoutEntry>(count);
        var store = new ScoutEntryStore();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var origin = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        while (entries.Count < count)
        {
            var parent = folders[random.Next(folders.Count)];
            var folder = random.Next(7) == 0;
            var name = Words[random.Next(Words.Length)] + (random.Next(3) == 0 ? "-" + Words[random.Next(Words.Length)] : "") + (random.Next(4) == 0 ? " " + random.Next(100) : "") + (folder ? "" : Extensions[random.Next(Extensions.Length)]);
            var path = Path.Combine(parent, name);
            if (!seen.Add(path)) continue;
            var modified = origin.AddMinutes(random.Next(0, 1_000_000));
            long? size = folder ? null : random.Next(5) == 0 ? 0 : random.NextInt64(1, 3_000_000_000);
            var entry = new ScoutEntry(path, folder, size, modified, IsReparse: false);
            entries.Add(entry);
            store.Upsert(path, folder, size, modified.UtcTicks, false);
            if (folder && folders.Count < 4000) folders.Add(path);
        }
        return (entries.ToArray(), store);
    }

    private static readonly string[] Queries =
    [
        "report", "Report", "budget report", "photo", "vacation photo", "cafe", "Ｃａｆｅ", "résumé", "resume", "strasse", "reprot", "bugdet", "rdme", "rme", "xyz", "x", "2026",
        "my file", "\"my file\"", "readme.md", "notes 2026", "Documents report", "node_modules logo", "final copy backup", "setup.exe", ".png", "*.pdf", "photo*", "d?ta", "rep*t",
        "kind:image", "kind:image photo", "ext:txt|md", "ext:pdf report", "kind:folder", "kind:file report", "size:>1mb", "size:empty", "size:small report", "size:huge", "size:10kb..1mb photo",
        "date:30d", "date:2025", "after:2026-01-01 report", "before:1y", "in:documents", "in:documents report", "in:\"program files\" setup", "name:report", "name:cafe", "-draft report", "report -copy",
        "sort:newest report", "sort:oldest report", "sort:largest kind:file", "sort:smallest photo", "sort:name report", "sort:name kind:folder", "sort:newest kind:image", "kind:video|image sort:largest", "sort:oldest in:desktop"
    ];

    private static string Show(IEnumerable<ScoutEntry> entries) => string.Join('\n', entries.Select(x => $"{x.FullPath}|{x.IsFolder}|{x.Size}|{x.Modified?.UtcTicks}|{x.IsReparse}"));

    [Fact]
    public void ASearchOverTheStoreReturnsExactlyWhatASearchOverTheEntriesDoes()
    {
        var (entries, store) = Corpus(60000);
        var snapshot = store.Capture();
        Assert.Equal(entries.Length, snapshot.Live);
        foreach (var text in Queries)
            foreach (var kind in Enum.GetValues<ScoutItemKind>())
            {
                var query = new ScoutQuery(text, Home);
                var expected = query.Rank(entries, kind);
                var actual = query.Rank(new ScoutStoreSource(snapshot), kind);
                Assert.True(Show(expected) == Show(actual), $"'{text}' ({kind}) gave different results over the store");
            }
    }

    [Fact]
    public void TheRankingStillMatchesTheOriginalImplementationEntryForEntry()
    {
        var (entries, store) = Corpus(40000);
        var snapshot = store.Capture();
        var compared = 0;
        foreach (var text in Queries)
            foreach (var kind in Enum.GetValues<ScoutItemKind>())
            {
                var query = new ScoutQuery(text, Home);
                if (query.HasWildcards) continue; // the original reads its wildcard patterns from private state
                var expected = Legacy.Rank(query, Home, entries, kind);
                Assert.True(Show(expected) == Show(query.Rank(entries, kind)), $"'{text}' ({kind}) changed for entries");
                Assert.True(Show(expected) == Show(query.Rank(new ScoutStoreSource(snapshot), kind)), $"'{text}' ({kind}) changed for the store");
                compared++;
            }
        Assert.True(compared > 100);
    }

    [Fact]
    public void ResultsAreTheSameWhateverTheLimit()
    {
        var (entries, store) = Corpus(8000);
        var snapshot = store.Capture();
        foreach (var limit in new[] { 1, 7, 150, 5000, 100000 })
            foreach (var text in new[] { "report", "sort:largest kind:file", "kind:image sort:oldest", "sort:name photo" })
            {
                var query = new ScoutQuery(text, Home);
                Assert.Equal(Show(Legacy.Rank(query, Home, entries, ScoutItemKind.All, limit)), Show(query.Rank(new ScoutStoreSource(snapshot), ScoutItemKind.All, limit)));
            }
    }

    [Fact]
    public void TheStoreKeepsEntriesInASmallFractionOfTheMemory()
    {
        const int count = 200000;
        var random = new Random(7);
        var paths = new string[count];
        var folders = new List<string> { @"C:\Users\me" };
        for (var i = 0; i < count; i++)
        {
            var folder = i % 8 == 0;
            paths[i] = Path.Combine(folders[random.Next(folders.Count)], "item-" + i + (folder ? "" : ".txt"));
            if (folder) folders.Add(paths[i]);
        }
        GC.Collect(); var before = GC.GetTotalMemory(true);
        var store = new ScoutEntryStore();
        for (var i = 0; i < count; i++) store.Upsert(paths[i], i % 8 == 0, i % 8 == 0 ? null : i, 1_000_000L * i, false);
        GC.Collect(); var used = GC.GetTotalMemory(true) - before;
        GC.KeepAlive(store); GC.KeepAlive(paths); GC.KeepAlive(folders);
        output.WriteLine($"{count:N0} entries in {used / 1048576.0:F1} MB = {used / (double)count:F0} bytes each (a ScoutEntry with its path string is about 300).");
        Assert.True(used / count < 160, $"{used / count} bytes per entry");
    }

    [Fact]
    public async Task ASearchCanReadWhileEntriesAreStillBeingAdded()
    {
        // As in the folder index: changes and taking a snapshot happen under one lock, and the search itself runs outside it.
        var store = new ScoutEntryStore(); var gate = new object();
        var stop = false;
        var writer = Task.Run(() =>
        {
            for (var i = 0; !stop && i < 300000; i++)
                lock (gate)
                {
                    store.Upsert(@"C:\data\folder" + i % 50 + @"\report-" + i + ".txt", false, i, 1000 + i, false);
                    if (i % 997 == 0) store.RemoveTree(@"C:\data\folder" + (i / 997) % 50);
                }
        });
        var query = new ScoutQuery("report");
        var timer = Stopwatch.StartNew();
        while (!writer.IsCompleted && timer.Elapsed < TimeSpan.FromSeconds(8))
        {
            ScoutEntryStore.Snapshot snapshot;
            lock (gate) snapshot = store.Capture();
            Assert.All(query.Rank(new ScoutStoreSource(snapshot), ScoutItemKind.All), x => Assert.Contains("report-", x.FullPath));
        }
        stop = true; await writer;
    }

    [Fact]
    public void ChangesReplaceRemoveAndMoveEntries()
    {
        var store = new ScoutEntryStore();
        store.Upsert(@"C:\r\docs", true, null, 1, false);
        store.Upsert(@"C:\r\docs\a.txt", false, 10, 1, false);
        store.Upsert(@"C:\r\docs\sub", true, null, 1, false);
        store.Upsert(@"C:\r\docs\sub\b.txt", false, 20, 1, false);
        store.Upsert(@"C:\r\other.txt", false, 30, 1, false);
        // The same path again replaces the entry instead of adding a second one, whatever the case.
        store.Upsert(@"C:\r\DOCS\A.TXT", false, 99, 5, false);
        Assert.Equal(5, store.Capture().Live);
        Assert.Equal([@"C:\r\docs\A.TXT"], Find(store, "a.txt").Select(x => x.FullPath));
        Assert.Equal(99, Find(store, "a.txt")[0].Size);

        // A folder that is renamed takes everything inside it along.
        store.Move(@"C:\r\docs", @"C:\r\papers");
        store.Upsert(@"C:\r\papers", true, null, 2, false);
        Assert.Equal([@"C:\r\papers\sub\b.txt"], Find(store, "b.txt").Select(x => x.FullPath));
        Assert.Equal(5, store.Capture().Live);
        Assert.Contains(Find(store, "papers"), x => x.IsFolder);
        Assert.Empty(Find(store, "docs"));

        // A file that is moved to another folder.
        store.Move(@"C:\r\other.txt", @"C:\r\papers\other.txt");
        store.Upsert(@"C:\r\papers\other.txt", false, 30, 3, false);
        Assert.Equal([@"C:\r\papers\other.txt"], Find(store, "other.txt").Select(x => x.FullPath));

        // A folder that is deleted takes its entries out of every search, and one that comes back is found again.
        store.RemoveTree(@"C:\r\papers\sub");
        Assert.Empty(Find(store, "b.txt")); Assert.Empty(Find(store, "sub"));
        Assert.Equal(3, store.Capture().Live);
        store.Upsert(@"C:\r\papers\sub", true, null, 4, false);
        store.Upsert(@"C:\r\papers\sub\b.txt", false, 21, 4, false);
        Assert.Equal([@"C:\r\papers\sub\b.txt"], Find(store, "b.txt").Select(x => x.FullPath));
        Assert.True(store.Contains(@"C:\r\papers\sub\b.txt")); Assert.False(store.Contains(@"C:\r\papers\sub\c.txt"));
    }

    private static IReadOnlyList<ScoutEntry> Find(ScoutEntryStore store, string text) => new ScoutQuery(text).Rank(new ScoutStoreSource(store.Capture()), ScoutItemKind.All);

    /// <summary>The ranking as it was before the folder index kept its entries compactly, kept here to compare against.</summary>
    private static class Legacy
    {
        private static readonly string[] Junk = ["\\node_modules\\", "\\.git\\", "\\$Recycle.Bin\\", "\\Windows\\", "\\WinSxS\\", "\\Program Files", "\\ProgramData\\", "\\msys64\\", "\\site-packages\\", "\\.nuget\\", "\\.gradle\\", "\\__pycache__\\", "\\.cache\\"];
        private static readonly string[] Personal = ["\\Desktop\\", "\\Documents\\", "\\Downloads\\", "\\Pictures\\", "\\Videos\\", "\\Music\\", "\\OneDrive"];

        public static IReadOnlyList<ScoutEntry> Rank(ScoutQuery query, string home, IEnumerable<ScoutEntry> entries, ScoutItemKind kind, int limit = 150)
        {
            if (limit <= 0) return [];
            if (query.Terms.Count == 0 || query.Filter.Sort != ScoutSort.Best) return Ordered(query, home, entries, kind, limit);
            var top = new PriorityQueue<(ScoutEntry Entry, int Score), (int Score, int Order)>(); var index = 0;
            foreach (var entry in entries)
            {
                index++;
                if (kind == ScoutItemKind.Files && entry.IsFolder || kind == ScoutItemKind.Folders && !entry.IsFolder) continue;
                if (!query.Filter.Allows(entry)) continue;
                var score = Score(query, home, entry); if (score < 0) continue;
                var item = (entry, score); var priority = (score, -index);
                if (top.Count < limit) top.Enqueue(item, priority);
                else if (priority.CompareTo(Peek(top)) > 0) { top.Dequeue(); top.Enqueue(item, priority); }
            }
            return top.UnorderedItems.Select(x => x.Element).OrderByDescending(x => x.Score).ThenBy(x => x.Entry.FullPath, StringComparer.OrdinalIgnoreCase).Select(x => x.Entry).ToArray();
        }

        private static (int Score, int Order) Peek(PriorityQueue<(ScoutEntry Entry, int Score), (int Score, int Order)> queue) { queue.TryPeek(out _, out var priority); return priority; }

        private static IReadOnlyList<ScoutEntry> Ordered(ScoutQuery query, string home, IEnumerable<ScoutEntry> entries, ScoutItemKind kind, int limit)
        {
            var matches = new List<(ScoutEntry Entry, int Score)>();
            foreach (var entry in entries)
            {
                if (kind == ScoutItemKind.Files && entry.IsFolder || kind == ScoutItemKind.Folders && !entry.IsFolder) continue;
                if (!query.Filter.Allows(entry)) continue;
                var score = query.Terms.Count == 0 ? 0 : Score(query, home, entry); if (score < 0) continue;
                matches.Add((entry, score));
            }
            var ordered = query.Filter.Sort switch
            {
                ScoutSort.Oldest => matches.OrderBy(x => x.Entry.Modified ?? DateTimeOffset.MaxValue),
                ScoutSort.Largest => matches.OrderByDescending(x => x.Entry.Size ?? -1),
                ScoutSort.Smallest => matches.OrderBy(x => x.Entry.Size ?? long.MaxValue),
                ScoutSort.Name => matches.OrderBy(x => x.Entry.SearchName, StringComparer.Ordinal),
                _ => matches.OrderByDescending(x => x.Entry.Modified ?? DateTimeOffset.MinValue)
            };
            return ordered.ThenByDescending(x => x.Score).ThenBy(x => x.Entry.FullPath, StringComparer.OrdinalIgnoreCase).Take(limit).Select(x => x.Entry).ToArray();
        }

        private static int Score(ScoutQuery query, string home, ScoutEntry entry)
        {
            var whole = ScoutQuery.Normalize(query.Filter.Plain.Trim('"'));
            var name = entry.SearchName; var stem = Path.GetFileNameWithoutExtension(name);
            if (name == whole || !entry.IsFolder && stem == whole) return ScoutQuery.ExactScore + Bonus(home, entry.FullPath);
            var path = ""; var score = 0;
            foreach (var term in query.Terms)
            {
                var at = name.IndexOf(term, StringComparison.Ordinal);
                if (at >= 0) { score += at == 0 ? 1000 : at > 0 && !char.IsLetterOrDigit(name[at - 1]) ? 800 : 600; continue; }
                path = path.Length == 0 ? entry.FullPath : path;
                if (path.Contains(term, StringComparison.OrdinalIgnoreCase)) { score += 100; continue; }
                if (term.Length >= 3 && IsSubsequence(term, stem)) { score += 60; continue; }
                if (term.Length >= 4 && stem.Length <= 80 && TypoWord(term, stem)) { score += 40; continue; }
                return -1;
            }
            return score + Bonus(home, entry.FullPath);
        }

        private static int Bonus(string home, string path)
        {
            var bonus = 0;
            if (path.StartsWith(home, StringComparison.OrdinalIgnoreCase) && path.Length > home.Length && path[home.Length] == Path.DirectorySeparatorChar)
            {
                bonus += 40;
                foreach (var folder in Personal) if (path.Contains(folder, StringComparison.OrdinalIgnoreCase)) { bonus += 30; break; }
                if (path.Contains("\\AppData\\", StringComparison.OrdinalIgnoreCase)) bonus -= 40;
            }
            foreach (var folder in Junk) if (path.Contains(folder, StringComparison.OrdinalIgnoreCase)) { bonus -= 60; break; }
            var depth = 0; foreach (var c in path) if (c == Path.DirectorySeparatorChar) depth++;
            return bonus + Math.Max(0, 20 - depth);
        }

        private static bool IsSubsequence(string needle, string text)
        {
            var i = 0; foreach (var c in text) if (i < needle.Length && c == needle[i]) i++;
            return i == needle.Length;
        }

        private static bool TypoWord(string term, string stem)
        {
            if (DistanceAtMostOne(term, stem)) return true;
            foreach (var range in stem.AsSpan().SplitAny(" ._-()[]"))
                if (DistanceAtMostOne(term, stem.AsSpan()[range])) return true;
            return false;
        }

        private static bool DistanceAtMostOne(ReadOnlySpan<char> left, ReadOnlySpan<char> right)
        {
            if (Math.Abs(left.Length - right.Length) > 1) return false;
            var i = 0; var j = 0; var edits = 0;
            while (i < left.Length && j < right.Length)
            {
                if (left[i] == right[j]) { i++; j++; continue; }
                if (++edits > 1) return false;
                if (i + 1 < left.Length && j + 1 < right.Length && left[i] == right[j + 1] && left[i + 1] == right[j]) { i += 2; j += 2; }
                else if (left.Length > right.Length) i++;
                else if (right.Length > left.Length) j++;
                else { i++; j++; }
            }
            return edits + (i < left.Length || j < right.Length ? 1 : 0) <= 1;
        }
    }
}

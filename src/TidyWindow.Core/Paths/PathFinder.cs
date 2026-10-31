namespace TidyWindow.Next.Core;

/// <summary>A folder that holds programs and could be added to PATH. <paramref name="Strong"/> marks the usual homes of command-line tools (bin, cmd, Scripts, shims).</summary>
public sealed record FolderCandidate(string Folder, IReadOnlyList<string> Programs, bool Strong, int Score);

/// <summary>
/// Looks for folders worth adding to PATH. One walk through the places programs usually live collects every folder that holds programs
/// and is not in PATH yet; suggestions and searches then just filter that list, so typing in the search box is instant.
/// </summary>
public sealed class PathFinder(Func<string, string> expand, Func<string, IEnumerable<string>> subdirectories, Func<string, IEnumerable<string>> files)
{
    // Folders looked at per place, so one huge folder cannot use up the whole search.
    private const int Budget = 6000;

    // Where programs usually live, and how many levels down is worth looking (Program Files\Vendor\App\bin is three).
    private static readonly (string Root, int Depth)[] Roots =
    [
        (@"%ProgramFiles%", 3), (@"%ProgramFiles(x86)%", 3), (@"%LOCALAPPDATA%\Programs", 3), (@"%LOCALAPPDATA%\Microsoft\WinGet", 3),
        (@"%USERPROFILE%", 3), (@"%APPDATA%", 2), (@"%ProgramData%", 3), (@"%SystemDrive%\", 3),
    ];

    // Folders that hold no tools worth adding, or are far too big and noisy to be worth walking.
    private static readonly HashSet<string> Skip = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", ".git", ".svn", ".hg", "__pycache__", "WindowsApps", "WinSxS", "Temp", "Cache", "Caches", "Code Cache", "GPUCache", "CachedData",
        "Package Cache", "Installer", "Documents", "Downloads", "Desktop", "Pictures", "Music", "Videos", "OneDrive", "AppData", "Windows", "Users",
        "Program Files", "Program Files (x86)", "ProgramData", "$Recycle.Bin", "System Volume Information", "Recovery", "PerfLogs", "MSOCache",
        "Common Files", "Windows Kits", "Reference Assemblies", "Windows Defender", "Windows Mail", "Windows NT", "WindowsPowerShell", "Internet Explorer",
        ".vscode", ".cache", ".npm", ".gradle", ".m2", ".nuget", ".docker", ".rustup",
    };

    private static readonly HashSet<string> StrongNames = new(StringComparer.OrdinalIgnoreCase) { "bin", "cmd", "scripts", "shims", "links", "sbin" };
    private static readonly string[] Extensions = [".exe", ".cmd", ".bat", ".com"];
    private static readonly string[] Noise = ["unins", "uninstall", "setup", "update", "install", "crash", "vc_redist"];

    /// <summary>Every folder under the usual places that holds programs and is not in PATH yet.</summary>
    public IReadOnlyList<FolderCandidate> Scan(PathSnapshot snapshot, CancellationToken cancel)
    {
        var inPath = snapshot.Search.Select(x => PathAnalyzer.Key(x.Folder)).ToHashSet();
        var seen = new HashSet<string>();
        var found = new List<FolderCandidate>();
        foreach (var (template, depth) in Roots)
        {
            var root = expand(template);
            if (root.Contains('%')) continue;
            var left = Budget;
            List<string> level = [root];
            for (var d = 1; d <= depth && level.Count > 0 && left > 0; d++)
            {
                var next = new List<string>();
                foreach (var parent in level)
                    foreach (var folder in subdirectories(parent))
                    {
                        cancel.ThrowIfCancellationRequested();
                        if (left <= 0) break;
                        var name = Path.GetFileName(folder);
                        if (Skip.Contains(name)) continue;
                        var key = PathAnalyzer.Key(folder);
                        if (!seen.Add(key)) continue;
                        left--;
                        next.Add(folder);
                        if (!inPath.Contains(key) && Describe(folder) is { Programs.Count: > 0 } candidate) found.Add(candidate);
                    }
                level = next;
            }
        }
        return found;
    }

    /// <summary>A folder the person typed or picked, whether or not the walk found it.</summary>
    public FolderCandidate Describe(string folder) => Make(folder.TrimEnd('\\').Length > 2 ? folder.TrimEnd('\\') : folder);

    private FolderCandidate Make(string folder)
    {
        var programs = files(folder)
            .Where(x => Extensions.Contains(Path.GetExtension(x), StringComparer.OrdinalIgnoreCase))
            .Select(x => Path.GetFileNameWithoutExtension(x))
            .Where(x => x.Length > 0 && !Noise.Any(n => x.StartsWith(n, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .Take(200)
            .ToList();
        var strong = StrongNames.Contains(Path.GetFileName(folder));
        return new(folder, programs, strong, (strong ? 10 : 0) + Math.Min(programs.Count, 5));
    }

    /// <summary>What to offer before anything is typed: the folders where command-line tools normally live.</summary>
    public static IReadOnlyList<FolderCandidate> Suggest(IEnumerable<FolderCandidate> all, int limit = 30) =>
        all.Where(x => x.Strong).OrderByDescending(x => x.Score).ThenBy(x => x.Folder, StringComparer.OrdinalIgnoreCase).Take(limit).ToList();

    /// <summary>Folders matching every word typed, by a program inside them or by the folder's own name. Exact program names rank first.</summary>
    public static IReadOnlyList<FolderCandidate> Search(IEnumerable<FolderCandidate> all, string query, int limit = 40)
    {
        var words = query.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => x.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? x[..^4] : x)
            .Where(x => x.Length > 0).ToList();
        if (words.Count == 0) return [];
        return all.Select(x => (Folder: x, Rank: Rank(x, words))).Where(x => x.Rank > 0)
            .OrderByDescending(x => x.Rank).ThenBy(x => x.Folder.Folder.Length).Take(limit).Select(x => x.Folder).ToList();
    }

    private static int Rank(FolderCandidate candidate, List<string> words)
    {
        var total = 0;
        var leaf = Path.GetFileName(candidate.Folder);
        foreach (var word in words)
        {
            var best = 0;
            foreach (var program in candidate.Programs)
            {
                if (program.Equals(word, StringComparison.OrdinalIgnoreCase)) { best = 30; break; }
                if (program.StartsWith(word, StringComparison.OrdinalIgnoreCase)) best = Math.Max(best, 18);
                else if (program.Contains(word, StringComparison.OrdinalIgnoreCase)) best = Math.Max(best, 10);
            }
            if (candidate.Folder.Contains(word, StringComparison.OrdinalIgnoreCase)) best = Math.Max(best, leaf.Contains(word, StringComparison.OrdinalIgnoreCase) ? 9 : 6);
            if (best == 0) return 0;
            total += best;
        }
        return total + candidate.Score;
    }
}

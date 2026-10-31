using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TidyWindow.Next.Core;

public enum ScoutItemKind { All, Files, Folders }
public sealed record ScoutEntry(string FullPath, bool IsFolder, long? Size, DateTimeOffset? Modified, bool IsReparse = false)
{
    public string Name => Path.GetFileName(FullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    private string? searchName;
    [System.Text.Json.Serialization.JsonIgnore] public string SearchName => searchName ??= ScoutQuery.Normalize(Name);
}
public sealed record ScoutRequest(string Query, string? Root, ScoutItemKind Kind = ScoutItemKind.All);
public sealed record ScoutReply(bool Ready, IReadOnlyList<ScoutEntry> Entries, long Total = 0, bool Limited = false, string? Error = null, bool Indexing = false);
public sealed record ScoutResult(IReadOnlyList<ScoutEntry> Entries, string Engine, long Checked, int Skipped, bool Complete, bool Limited = false, string? Error = null);

public sealed partial class ScoutQuery
{
    public const int ExactScore = 20000;
    public string Text { get; }
    public IReadOnlyList<string> Terms { get; }
    private readonly string whole;
    private readonly Dictionary<string, Regex> globs = new();
    private readonly string? home;
    /// <param name="home">The user's profile folder. Files there rank above equally good matches in system folders.</param>
    public ScoutQuery(string text, string? home = null)
    {
        this.home = string.IsNullOrWhiteSpace(home) ? null : Path.TrimEndingDirectorySeparator(home);
        Text = text.Trim();
        if (Text.Length is < 1 or > 200 || Text.Any(char.IsControl)) throw new ArgumentException("Enter a name or path between 1 and 200 characters.");
        Terms = Words().Matches(Text).Select(x => Normalize(x.Value.Trim('"'))).Where(x => x.Length > 0).Select(Shorthand).ToArray();
        foreach (var term in Terms)
            if (term.AsSpan().IndexOfAny('*', '?') >= 0) globs[term] = new Regex("^" + GlobPattern(term) + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
        if (Terms.Count is < 1 or > 12) throw new ArgumentException("Enter between 1 and 12 search terms; quote a phrase to keep it together.");
        whole = Normalize(Text.Trim('"'));
    }
    /// <summary>A bare extension such as .png means every file ending that way.</summary>
    private static string Shorthand(string term) =>
        term.Length is >= 2 and <= 9 && term[0] == '.' && term.Skip(1).All(char.IsLetterOrDigit) ? "*" + term : term;
    private static string GlobPattern(string glob) => Regex.Escape(glob).Replace("\\*", ".*").Replace("\\?", ".");
    public bool HasWildcards => globs.Count > 0;
    public static string Normalize(string text) => string.Concat(text.Normalize(NormalizationForm.FormKD).EnumerateRunes()
        .Where(x => Rune.GetUnicodeCategory(x) != UnicodeCategory.NonSpacingMark).Select(x => Rune.ToLowerInvariant(x).ToString())).Normalize(NormalizationForm.FormC);
    private static string Prefix(ScoutItemKind kind) => kind == ScoutItemKind.Files ? "file: " : kind == ScoutItemKind.Folders ? "folder: " : "";
    // File names cannot contain a quote, so dropping one never loses a match and keeps user text from escaping its quotes.
    private static string Literal(string term) => "\"" + term.Replace("\"", "") + "\"";
    private static string Scope(string? root) => root is null ? "" : " " + Literal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + "\\");

    // All user input is quoted literal text or a wildcard, never an Everything operator.
    private IEnumerable<string> Pieces(bool inPath) => Terms.Select(term => globs.ContainsKey(term)
        ? (inPath ? "nopath:" : "") + term.Replace("\"", "")
        : "nowildcards:" + Literal(term));

    /// <summary>Matches in file and folder names. The quickest query, and the one most searches need.</summary>
    public string NameExpression(ScoutItemKind kind, string? root) => Prefix(kind) + string.Join(" ", Pieces(false)) + Scope(root);

    /// <summary>Matches anywhere in the full path, so terms may fall in folder names. Slower; used for several words or when names alone find little.</summary>
    public string PathExpression(ScoutItemKind kind, string? root) => Prefix(kind) + string.Join(" ", Pieces(true)) + Scope(root);

    /// <summary>The whole text as one exact file or folder name; null when the text cannot be a name.</summary>
    public string? ExactExpression(ScoutItemKind kind) =>
        globs.Count > 0 || Text.Contains('"') ? null : Prefix(kind) + "wholefilename:" + Literal(Text.Trim('"'));

    /// <summary>Close matches: missing, extra, wrong or swapped letters, and abbreviations. Null when the query is not suited to it.
    /// Variants are native wildcards, which Everything evaluates far faster than one large pattern.</summary>
    public string? SmartExpression(ScoutItemKind kind, string? root)
    {
        if (globs.Count > 0 || Terms.Count > 3 || Terms.Any(term => term.Length is < 3 or > 24 || term.IndexOfAny(['"', '<', '>', '|', '!', '*', '?']) >= 0)) return null;
        var groups = Terms.Select(term => "<" + string.Join("|", SmartVariants(term)) + ">");
        return Prefix(kind) + string.Join(" ", groups) + Scope(root);
    }

    private static IEnumerable<string> SmartVariants(string term)
    {
        var variants = new List<string> { "*" + term + "*" };
        // Abbreviation: the letters appear in order, as in rdme for readme.
        variants.Add("regex:\"" + Regex.Escape(term[..1]) + string.Concat(term.Skip(1).Select(c => "(?>[^" + Regex.Escape(c.ToString()).Replace("]", "\\]").Replace("-", "\\-") + "]*)" + Regex.Escape(c.ToString()))) + "\"");
        if (term.Length >= 4)
        {
            // Swapped neighbours are the commonest typo, so they are always tried; the other edits only for short terms, where each costs little.
            for (var i = 0; i + 1 < term.Length; i++) variants.Add("*" + term[..i] + term[i + 1] + term[i] + term[(i + 2)..] + "*");
            if (term.Length <= 10)
                for (var i = 0; i < term.Length; i++)
                {
                    variants.Add("*" + term.Remove(i, 1) + "*");
                    variants.Add("*" + term[..i] + "?" + term[(i + 1)..] + "*");
                    variants.Add("*" + term[..i] + "?" + term[i..] + "*");
                }
        }
        return variants.Distinct();
    }
    public int Score(ScoutEntry entry)
    {
        var name = entry.SearchName; var stem = Path.GetFileNameWithoutExtension(name);
        if (name == whole || !entry.IsFolder && stem == whole) return ExactScore + Bonus(entry.FullPath);
        var path = ""; var score = 0;
        foreach (var term in Terms)
        {
            if (globs.TryGetValue(term, out var glob))
            {
                if (!glob.IsMatch(name)) return -1;
                score += 900; continue;
            }
            var at = name.IndexOf(term, StringComparison.Ordinal);
            if (at >= 0) { score += at == 0 ? 1000 : at > 0 && !char.IsLetterOrDigit(name[at - 1]) ? 800 : 600; continue; }
            path = path.Length == 0 ? entry.FullPath : path;
            if (path.Contains(term, StringComparison.OrdinalIgnoreCase)) { score += 100; continue; }
            if (term.Length >= 3 && IsSubsequence(term, stem)) { score += 60; continue; }
            if (term.Length >= 4 && stem.Length <= 80 && TypoWord(term, stem)) { score += 40; continue; }
            return -1;
        }
        return score + Bonus(entry.FullPath);
    }

    private static readonly string[] Junk = ["\\node_modules\\", "\\.git\\", "\\$Recycle.Bin\\", "\\Windows\\", "\\WinSxS\\", "\\Program Files", "\\ProgramData\\", "\\msys64\\", "\\site-packages\\", "\\.nuget\\", "\\.gradle\\", "\\__pycache__\\", "\\.cache\\"];
    private static readonly string[] Personal = ["\\Desktop\\", "\\Documents\\", "\\Downloads\\", "\\Pictures\\", "\\Videos\\", "\\Music\\", "\\OneDrive"];
    /// <summary>Small adjustments that only separate matches of the same quality: your own folders first, system and tooling folders last, shallower before deeper.</summary>
    private int Bonus(string path)
    {
        var bonus = 0;
        if (home is not null && path.StartsWith(home, StringComparison.OrdinalIgnoreCase) && path.Length > home.Length && path[home.Length] == Path.DirectorySeparatorChar)
        {
            bonus += 40;
            foreach (var folder in Personal) if (path.Contains(folder, StringComparison.OrdinalIgnoreCase)) { bonus += 30; break; }
            if (path.Contains("\\AppData\\", StringComparison.OrdinalIgnoreCase)) bonus -= 40;
        }
        foreach (var folder in Junk) if (path.Contains(folder, StringComparison.OrdinalIgnoreCase)) { bonus -= 60; break; }
        var depth = 0; foreach (var c in path) if (c == Path.DirectorySeparatorChar) depth++;
        return bonus + Math.Max(0, 20 - depth);
    }
    public IReadOnlyList<ScoutEntry> Rank(IEnumerable<ScoutEntry> entries, ScoutItemKind kind, int limit = 150, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (limit <= 0) return [];
        var top = new PriorityQueue<(ScoutEntry Entry, int Score), (int Score, int Order)>(); var index = 0;
        foreach (var entry in entries)
        {
            if ((index++ & 1023) == 0) token.ThrowIfCancellationRequested();
            if (kind == ScoutItemKind.Files && entry.IsFolder || kind == ScoutItemKind.Folders && !entry.IsFolder) continue;
            var score = Score(entry); if (score < 0) continue;
            var item = (entry, score); var priority = (score, -index);
            if (top.Count < limit) top.Enqueue(item, priority);
            else if (priority.CompareTo(top.PeekPriority()) > 0) { top.Dequeue(); top.Enqueue(item, priority); }
        }
        return top.UnorderedItems.Select(x => x.Element).OrderByDescending(x => x.Score).ThenBy(x => x.Entry.FullPath, StringComparer.OrdinalIgnoreCase).Select(x => x.Entry).ToArray();
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
    [GeneratedRegex("\"[^\"]+\"|\\S+")]
    private static partial Regex Words();
}

internal static class ScoutQueueExtensions
{
    public static TPriority PeekPriority<TElement, TPriority>(this PriorityQueue<TElement, TPriority> queue)
    { queue.TryPeek(out _, out var priority); return priority!; }
}

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
    public ScoutQuery(string text)
    {
        Text = text.Trim();
        if (Text.Length is < 1 or > 200 || Text.Any(char.IsControl)) throw new ArgumentException("Enter a name or path between 1 and 200 characters.");
        Terms = Words().Matches(Text).Select(x => Normalize(x.Value.Trim('"'))).Where(x => x.Length > 0).ToArray();
        if (Terms.Count is < 1 or > 12) throw new ArgumentException("Enter between 1 and 12 search terms; quote a phrase to keep it together.");
        whole = Normalize(Text.Trim('"'));
    }
    public static string Normalize(string text) => string.Concat(text.Normalize(NormalizationForm.FormKD).EnumerateRunes()
        .Where(x => Rune.GetUnicodeCategory(x) != UnicodeCategory.NonSpacingMark).Select(x => Rune.ToLowerInvariant(x).ToString())).Normalize(NormalizationForm.FormC);
    public string EverythingExpression(ScoutItemKind kind, string? root, bool smart = false)
    {
        // All user input is a literal regular expression, never Everything query operators.
        var terms = Terms.Select(term => "regex:\"" + (smart && Terms.Count <= 3 && term.Length is >= 3 and <= 24 ? SmartPattern(term) : Regex.Escape(term)).Replace("\"", "\\x22") + "\"");
        var scope = root is null ? "" : " path:regex:\"^" + Regex.Escape(Path.GetFullPath(root).TrimEnd('\\') + "\\").Replace("\"", "\\x22") + "\"";
        return (kind == ScoutItemKind.Files ? "file: " : kind == ScoutItemKind.Folders ? "folder: " : "") + string.Join(" ", terms) + scope;
    }
    private static string SmartPattern(string term)
    {
        // Atomic gap matches select the earliest next character without exponential backtracking.
        var abbreviation = Regex.Escape(term[..1]) + string.Concat(term.Skip(1).Select(c => "(?>[^" + Regex.Escape(c.ToString()).Replace("]", "\\]").Replace("-", "\\-") + "]*)" + Regex.Escape(c.ToString())));
        var variants = new HashSet<string> { Regex.Escape(term), abbreviation };
        if (term.Length >= 4)
            for (var i = 0; i < term.Length; i++)
            {
                variants.Add(Regex.Escape(term.Remove(i, 1)));
                variants.Add(Regex.Escape(term[..i]) + "." + Regex.Escape(term[(i + 1)..]));
                variants.Add(Regex.Escape(term[..i]) + "." + Regex.Escape(term[i..]));
                if (i + 1 < term.Length) variants.Add(Regex.Escape(term[..i] + term[i + 1] + term[i] + term[(i + 2)..]));
            }
        return "(?:" + string.Join("|", variants) + ")";
    }
    public int Score(ScoutEntry entry)
    {
        var name = entry.SearchName; var stem = Path.GetFileNameWithoutExtension(name);
        if (name == whole || !entry.IsFolder && stem == whole) return ExactScore;
        var path = ""; var score = 0;
        foreach (var term in Terms)
        {
            var at = name.IndexOf(term, StringComparison.Ordinal);
            if (at >= 0) { score += at == 0 ? 1000 : at > 0 && !char.IsLetterOrDigit(name[at - 1]) ? 800 : 600; continue; }
            path = path.Length == 0 ? entry.FullPath : path;
            if (path.Contains(term, StringComparison.OrdinalIgnoreCase)) { score += 100; continue; }
            if (term.Length >= 3 && IsSubsequence(term, stem)) { score += 60; continue; }
            if (term.Length >= 4 && stem.Length <= 80 && TypoWord(term, stem)) { score += 40; continue; }
            return -1;
        }
        return score;
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

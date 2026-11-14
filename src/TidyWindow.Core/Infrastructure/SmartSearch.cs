using System.Globalization;
using System.Text;

namespace TidyWindow.Next.Core;

/// <summary>A piece of text a search looks in. A match in a heavier field (a name) outranks one in a lighter field (a description).</summary>
public readonly record struct SearchField(string Text, double Weight = 1);

/// <summary>
/// Forgiving search for short lists of things a person describes in their own words. Every word typed must find something (so more words
/// narrow the list), but a word may match as a whole word, the start of one, part of one, a close misspelling ("blutooth"), letters of a word
/// in order ("bluetth"), or a related word ("wifi" finds network fixes). Plurals and endings are ignored. Results are ranked best first.
/// </summary>
public static class SmartSearch
{
    // Words people use for the problem that the catalogs describe differently. Kept small and about the things on these pages.
    private static readonly Dictionary<string, string[]> Related = new(StringComparer.Ordinal)
    {
        ["wifi"] = ["wireless", "network", "internet", "connection"],
        ["internet"] = ["network", "connection", "dns", "online", "wifi"],
        ["online"] = ["internet", "network"],
        ["offline"] = ["internet", "network", "connection"],
        ["network"] = ["internet", "connection", "wifi"],
        ["dns"] = ["internet", "network", "flush", "address"],
        ["sound"] = ["audio", "speaker", "volume"],
        ["audio"] = ["sound", "speaker", "volume"],
        ["speaker"] = ["audio", "sound"],
        ["volume"] = ["audio", "sound"],
        ["mic"] = ["microphone", "audio"],
        ["microphone"] = ["mic", "audio"],
        ["headphone"] = ["audio", "bluetooth", "sound"],
        ["printer"] = ["print", "printing", "spooler"],
        ["print"] = ["printer", "spooler"],
        ["scanner"] = ["scan", "hardware", "device"],
        ["bluetooth"] = ["bt", "wireless", "headphone"],
        ["bt"] = ["bluetooth"],
        ["camera"] = ["webcam", "video"],
        ["webcam"] = ["camera", "video"],
        ["usb"] = ["hardware", "device", "drive", "scan"],
        ["device"] = ["hardware"],
        ["hardware"] = ["device", "usb"],
        ["frozen"] = ["stuck", "hang", "freeze", "unresponsive"],
        ["freeze"] = ["stuck", "hang", "frozen", "unresponsive"],
        ["hang"] = ["stuck", "freeze", "frozen", "unresponsive"],
        ["stuck"] = ["frozen", "hang", "freeze", "unresponsive"],
        ["unresponsive"] = ["stuck", "frozen", "hang"],
        ["slow"] = ["performance", "hang", "stuck"],
        ["blank"] = ["black", "empty", "display", "screen"],
        ["black"] = ["blank", "display", "screen"],
        ["flicker"] = ["display", "screen", "refresh"],
        ["screen"] = ["display", "monitor"],
        ["monitor"] = ["display", "screen"],
        ["display"] = ["screen", "monitor", "refresh"],
        ["copy"] = ["clipboard", "paste"],
        ["paste"] = ["clipboard", "copy"],
        ["clipboard"] = ["copy", "paste"],
        ["screenshot"] = ["snipping", "capture", "screen"],
        ["snip"] = ["snipping", "screenshot"],
        ["taskbar"] = ["explorer", "start", "tray"],
        ["tray"] = ["taskbar", "icon"],
        ["start"] = ["taskbar", "menu"],
        ["menu"] = ["start", "taskbar"],
        ["explorer"] = ["taskbar", "folder", "file"],
        ["folder"] = ["explorer", "preview", "thumbnail"],
        ["thumbnail"] = ["preview", "folder"],
        ["preview"] = ["thumbnail", "folder"],
        ["icon"] = ["icons", "cache", "refresh"],
        ["update"] = ["upgrade", "patch", "windows"],
        ["store"] = ["microsoft", "app"],
        ["app"] = ["application", "store"],
        ["disk"] = ["drive", "storage", "chkdsk"],
        ["drive"] = ["disk", "storage"],
        ["clock"] = ["time", "date", "sync"],
        ["time"] = ["clock", "date", "sync"],
        ["keyboard"] = ["typing", "keys", "input"],
        ["typing"] = ["keyboard", "keys", "input", "emoji"],
        ["emoji"] = ["typing", "keyboard", "panel"],
        ["notification"] = ["toast", "popup", "alert"],
        ["popup"] = ["notification", "toast"],
        ["linux"] = ["wsl", "ubuntu", "terminal"],
        ["wsl"] = ["linux", "ubuntu"],
        ["corrupt"] = ["damaged", "broken", "repair", "sfc", "dism"],
        ["broken"] = ["corrupt", "damaged", "repair"],
        ["repair"] = ["fix", "restore", "corrupt"],
        ["reset"] = ["restart", "refresh", "clear"],
        ["restart"] = ["reset", "restarting"],
        ["clear"] = ["clean", "flush", "empty", "delete"],
        ["login"] = ["sign", "password", "credential"],
        ["signin"] = ["sign", "login", "credential"],
        ["password"] = ["sign", "login", "credential"],
        ["memory"] = ["ram", "vmmem"],
        ["ram"] = ["memory"]
    };

    /// <summary>The words of an item's searchable text, split and stemmed once so a search box does not redo that for every keystroke. Keep one per item.</summary>
    public sealed class Prepared
    {
        internal readonly (string[] Words, double Weight, string Joined)[] Fields;
        public Prepared(IReadOnlyList<SearchField> fields) =>
            Fields = fields.Where(f => !string.IsNullOrWhiteSpace(f.Text)).Select(f => { var words = Words(f.Text); return (words, f.Weight, string.Join(' ', words)); }).ToArray();
    }

    /// <summary>How well the text answers the query: 0 when it does not, and larger the better. An empty query matches everything.</summary>
    /// <param name="related">Whether a word may also find the words people use for the same thing ("wifi" finds network). Turn it off for lists of names, where it only adds noise.</param>
    public static double Score(string query, IReadOnlyList<SearchField> fields, bool related = true) => Score(Tokens(query), query, new Prepared(fields), related);

    public static double Score(string query, Prepared index, bool related = true) => Score(Tokens(query), query, index, related);

    /// <summary>The items that answer the query, best first and otherwise in their original order. All of them when the query is empty.</summary>
    public static List<T> Rank<T>(IEnumerable<T> items, string query, Func<T, IReadOnlyList<SearchField>> fields, bool related = true)
    {
        var tokens = Tokens(query);
        if (tokens.Count == 0) return items.ToList();
        return items.Select(item => (Item: item, Score: Score(tokens, query, new Prepared(fields(item)), related))).Where(x => x.Score > 0).OrderByDescending(x => x.Score).Select(x => x.Item).ToList();
    }

    /// <summary>Like <c>Rank</c>, for items that keep their own <see cref="Prepared"/> between searches.</summary>
    public static List<T> RankPrepared<T>(IEnumerable<T> items, string query, Func<T, Prepared> prepared, bool related = true)
    {
        var tokens = Tokens(query);
        if (tokens.Count == 0) return items.ToList();
        return items.Select(item => (Item: item, Score: Score(tokens, query, prepared(item), related))).Where(x => x.Score > 0).OrderByDescending(x => x.Score).Select(x => x.Item).ToList();
    }

    public static bool IsEmpty(string query) => Tokens(query).Count == 0;

    private static double Score(List<string> tokens, string query, Prepared index, bool related)
    {
        if (tokens.Count == 0) return 1;
        var prepared = index.Fields;
        var total = 0.0;
        foreach (var token in tokens)
        {
            var best = 0.0;
            foreach (var (words, weight, _) in prepared) best = Math.Max(best, Best(token, words, related) * weight);
            if (best == 0) return 0;
            total += best;
        }
        // Typing the whole phrase as it is written is the strongest hint there is.
        var phrase = string.Join(' ', tokens);
        if (tokens.Count > 1)
            foreach (var (_, weight, joined) in prepared)
                if (joined.Contains(phrase, StringComparison.Ordinal)) total += 2 * weight;
        return total;
    }

    private static double Best(string token, string[] words, bool related)
    {
        var best = Match(token, words);
        if (best >= 1 || !related) return best;
        if (Related.TryGetValue(token, out var others) || Related.TryGetValue(Unstem(token), out others))
            foreach (var other in others) best = Math.Max(best, 0.7 * Match(Stem(other), words));
        return best;
    }

    private static double Match(string token, string[] words)
    {
        var best = 0.0;
        foreach (var word in words)
        {
            double score;
            if (word == token) score = 1;
            else if (word.StartsWith(token, StringComparison.Ordinal)) score = token.Length >= 2 ? 0.9 : 0.5;
            else if (token.Length >= 3 && word.Contains(token, StringComparison.Ordinal)) score = 0.7;
            else score = Fuzzy(token, word);
            if (score > best) best = score;
            if (best >= 1) break;
        }
        // A word written in two parts ("Wi-Fi", "log in") is still the word typed as one ("wifi", "login").
        for (var i = 0; i + 1 < words.Length && best < 0.95; i++)
        {
            var joined = words[i] + words[i + 1];
            if (joined == token) best = Math.Max(best, 0.95);
            else if (token.Length >= 3 && joined.StartsWith(token, StringComparison.Ordinal)) best = Math.Max(best, 0.8);
        }
        // The first letters of consecutive words ("vsc" for Visual Studio Code). Only in short text such as a name: in a long description it would find things by chance.
        if (token.Length >= 3 && best < 0.75 && words.Length <= 6 && IsInitials(token, words)) best = 0.75;
        return best;
    }

    private static bool IsInitials(string token, string[] words)
    {
        for (var start = 0; start + token.Length <= words.Length; start++)
        {
            var i = 0;
            while (i < token.Length && words[start + i].Length > 0 && words[start + i][0] == token[i]) i++;
            if (i == token.Length) return true;
        }
        return false;
    }

    // A close misspelling, or the letters of the word in order. Short words are never guessed at: "net" must not become "set".
    private static double Fuzzy(string token, string word)
    {
        if (token.Length < 4) return 0;
        var allowed = token.Length <= 5 ? 1 : 2;
        // The word as typed so far counts too, so a misspelled start of a longer word still finds it.
        var head = word.Length > token.Length ? word[..token.Length] : word;
        var distance = Math.Min(Distance(token, word, allowed), Distance(token, head, allowed));
        if (distance <= allowed) return 0.65 - 0.1 * distance;
        if (token[0] == word[0] && word.Length <= token.Length * 2 + 2 && IsSubsequence(token, word)) return 0.35;
        return 0;
    }

    private static bool IsSubsequence(string small, string big)
    {
        var at = 0;
        foreach (var c in big)
            if (at < small.Length && c == small[at]) at++;
        return at == small.Length;
    }

    /// <summary>Optimal string alignment distance (a swap of two neighbouring letters counts once), or a number above the limit when it is further than that.</summary>
    private static int Distance(string a, string b, int limit)
    {
        if (Math.Abs(a.Length - b.Length) > limit || b.Length > 256) return limit + 1;
        Span<int> beforePrevious = stackalloc int[b.Length + 1];
        Span<int> previous = stackalloc int[b.Length + 1];
        Span<int> current = stackalloc int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) previous[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            var smallest = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1), previous[j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1]) current[j] = Math.Min(current[j], beforePrevious[j - 2] + 1);
                if (current[j] < smallest) smallest = current[j];
            }
            if (smallest > limit) return limit + 1;
            previous.CopyTo(beforePrevious);
            current.CopyTo(previous);
        }
        return previous[b.Length];
    }

    private static List<string> Tokens(string query) => Words(query).Distinct().ToList();

    private static string[] Words(string text)
    {
        var words = new List<string>();
        var current = new StringBuilder();
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(c)) current.Append(char.ToLowerInvariant(c));
            else if (current.Length > 0) { words.Add(Stem(current.ToString())); current.Clear(); }
        }
        if (current.Length > 0) words.Add(Stem(current.ToString()));
        return words.ToArray();
    }

    // Plurals and common endings, so "notifications" finds "notification" and "restarting" finds "restart".
    private static string Stem(string word)
    {
        if (word.Length > 5 && word.EndsWith("ies", StringComparison.Ordinal)) return word[..^3] + "y";
        if (word.Length > 5 && word.EndsWith("ing", StringComparison.Ordinal)) return word[..^3];
        if (word.Length > 4 && word.EndsWith("ed", StringComparison.Ordinal)) return word[..^2];
        if (word.Length > 4 && word.EndsWith("es", StringComparison.Ordinal)) return word[..^2];
        if (word.Length > 3 && word.EndsWith('s') && !word.EndsWith("ss", StringComparison.Ordinal)) return word[..^1];
        return word;
    }

    // The related-word table is written in plain words; a stemmed token may have lost an ending the table key still has.
    private static string Unstem(string token) => Related.Keys.FirstOrDefault(key => Stem(key) == token) ?? token;
}

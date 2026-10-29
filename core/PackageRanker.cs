using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TidyWindow.Next.Core;

/// <summary>Ranks the live candidate set; never infers package identity or changes install IDs.</summary>
public static partial class PackageRanker
{
    public static IReadOnlyList<Package> Rank(IEnumerable<Package> packages, string query)
    {
        var terms = Tokens(query).Distinct(StringComparer.Ordinal).ToArray();
        var documents = packages.DistinctBy(x => x.Key).Select(x => new Document(x)).ToArray();
        if (terms.Length == 0 || documents.Length == 0) return documents.Select(x => x.Package).ToArray();
        var compactQuery = string.Concat(terms);
        var averages = new[] { 0, 1, 2 }.Select(field => Math.Max(1, documents.Average(x => x.Fields[field].Length))).ToArray();
        var idf = terms.Select(term =>
        {
            var frequency = documents.Count(x => x.Fields.Any(field => field.Contains(term, StringComparer.Ordinal)));
            return Math.Log(1 + (documents.Length - frequency + 0.5) / (frequency + 0.5));
        }).ToArray();

        return documents.Select(document =>
        {
            // Explicit IDs and exact product names cannot be displaced by term frequency.
            var exactId = string.Equals(query.Trim(), document.Package.Id, StringComparison.OrdinalIgnoreCase);
            var exactProduct = string.Concat(document.Fields[0]) == compactQuery || string.Concat(document.Fields[1]) == compactQuery;
            var coverage = terms.Select(term => document.Fields.Max(field => BestMatch(term, field))).ToArray();
            var tier = exactId ? 6 : exactProduct ? 5 : coverage.All(x => x == 1) ? 4 :
                coverage.All(x => x >= 0.8) ? 3 : coverage.All(x => x >= 0.6) ? 2 : coverage.All(x => x > 0) ? 1 : 0;
            double score = 0;
            double[] weights = [3, 2.5, 0.5]; // Product name and ID leaf outweigh publisher/bucket tokens.
            for (var field = 0; field < document.Fields.Length; field++)
            {
                for (var term = 0; term < terms.Length; term++)
                {
                    var quality = BestMatch(terms[term], document.Fields[field]);
                    if (quality == 0) continue;
                    var frequency = Math.Max(1, document.Fields[field].Count(x => x == terms[term]));
                    // BM25 term saturation and length normalization (k1=1.2, b=0.75).
                    score += weights[field] * idf[term] * quality * frequency * 2.2 /
                        (frequency + 1.2 * (0.25 + 0.75 * document.Fields[field].Length / averages[field]));
                }
            }
            return (document.Package, Tier: tier, Score: score);
        }).OrderByDescending(x => x.Tier).ThenByDescending(x => x.Score)
          .ThenBy(x => x.Package.Name, StringComparer.OrdinalIgnoreCase)
          .ThenBy(x => x.Package.Id, StringComparer.OrdinalIgnoreCase)
          .ThenBy(x => x.Package.Manager).Select(x => x.Package).ToArray();
    }

    private sealed class Document
    {
        public Package Package { get; }
        public string[][] Fields { get; }
        public Document(Package package)
        {
            Package = package;
            var leaf = package.Id[(Math.Max(package.Id.LastIndexOf('.'), package.Id.LastIndexOf('/')) + 1)..];
            Fields = [Tokens(package.Name), Tokens(leaf), Tokens(package.Id)];
        }
    }

    private static string[] Tokens(string text)
    {
        text = CamelBoundary().Replace(text[..Math.Min(text.Length, 1024)], " ").Normalize(NormalizationForm.FormKD);
        var result = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark) continue;
            // Preserve C++ and C#: punctuation must not collapse these into the same query.
            result.Append(Rune.IsLetterOrDigit(rune) || rune.Value is '+' or '#' ? Rune.ToLowerInvariant(rune).ToString() : " ");
        }
        return result.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    private static double BestMatch(string term, string[] tokens)
    {
        double best = 0;
        foreach (var token in tokens)
        {
            if (token == term) return 1;
            if (token.StartsWith(term, StringComparison.Ordinal)) best = Math.Max(best, 0.85);
            else if (token.Contains(term, StringComparison.Ordinal)) best = Math.Max(best, 0.6);
            else if (best < 0.6 && term.Length >= 4 && term.Length <= 64 && token.Length <= 64)
            {
                var limit = term.Length >= 8 ? 2 : 1;
                var distance = EditDistance(term, token, limit);
                if (distance <= limit) best = Math.Max(best, 0.5 * (1.0 - (double)distance / Math.Max(term.Length, token.Length)));
            }
        }
        return best;
    }

    // Bounded optimal-string-alignment Damerau–Levenshtein: adjacent transpositions count as one typo.
    private static int EditDistance(string left, string right, int limit)
    {
        if (Math.Abs(left.Length - right.Length) > limit) return limit + 1;
        Span<int> previousPrevious = stackalloc int[right.Length + 1];
        Span<int> previous = stackalloc int[right.Length + 1];
        Span<int> current = stackalloc int[right.Length + 1];
        for (var j = 0; j <= right.Length; j++) previous[j] = j;
        for (var i = 1; i <= left.Length; i++)
        {
            current[0] = i;
            var minimum = i;
            for (var j = 1; j <= right.Length; j++)
            {
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (left[i - 1] == right[j - 1] ? 0 : 1));
                if (i > 1 && j > 1 && left[i - 1] == right[j - 2] && left[i - 2] == right[j - 1])
                    current[j] = Math.Min(current[j], previousPrevious[j - 2] + 1);
                minimum = Math.Min(minimum, current[j]);
            }
            if (minimum > limit) return limit + 1;
            previous.CopyTo(previousPrevious);
            current.CopyTo(previous);
        }
        return previous[right.Length];
    }

    [GeneratedRegex(@"(?<=[\p{Ll}\p{Nd}])(?=\p{Lu})|(?<=\p{Lu})(?=\p{Lu}\p{Ll})", RegexOptions.CultureInvariant)]
    private static partial Regex CamelBoundary();
}

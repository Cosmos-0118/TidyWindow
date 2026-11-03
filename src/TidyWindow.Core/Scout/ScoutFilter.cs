using System.Globalization;
using System.Text.RegularExpressions;

namespace TidyWindow.Next.Core;

public enum ScoutSort { Best, Newest, Oldest, Largest, Smallest, Name }

/// <summary>
/// The filters in a Scout search, as typed: <c>kind:image|video</c> (a kind of file, or folder / file), <c>ext:png|jpg</c>, <c>size:&gt;100mb</c>
/// (also &lt;, &gt;=, &lt;=, =, <c>1mb..10mb</c>, empty, tiny, small, medium, large, huge), <c>date:</c> / <c>modified:</c> (today, yesterday, week, month, year,
/// 7d, 2w, 3m, 1y, 2026-10-25, 2026-10, 2026), <c>after:</c>, <c>before:</c>, <c>in:</c> (a folder name or path), <c>name:</c>, <c>sort:</c> (newest, oldest, largest,
/// smallest, name) and <c>-word</c> to leave out whatever has it. <c>|</c> inside a value means "or". Whatever is left is the text to search for.
/// A filter that is not understood stays part of that text, so a typo never quietly matches everything.
/// </summary>
public sealed partial class ScoutFilter
{
    private sealed record KindTest(bool Folders, bool Files, IReadOnlyList<ScoutCategory> Categories, IReadOnlyList<string> Extensions, bool Not);
    private sealed record SizeTest(long Min, long Max, bool Not);
    private sealed record DateTest(DateTimeOffset From, DateTimeOffset To, bool Not);
    private sealed record TextTest(string Field, IReadOnlyList<string> Choices, bool Not);

    private readonly List<KindTest> kinds = [];
    private readonly List<SizeTest> sizes = [];
    private readonly List<DateTest> dates = [];
    private readonly List<TextTest> texts = [];
    private readonly List<string> chips = [];

    public static readonly ScoutFilter Empty = new();

    /// <summary>The search text left after the filters were taken out.</summary>
    public string Plain { get; private set; } = "";
    public ScoutSort Sort { get; private set; }
    public bool IsEmpty => kinds.Count == 0 && sizes.Count == 0 && dates.Count == 0 && texts.Count == 0 && Sort == ScoutSort.Best;
    /// <summary>Whether at least one filter asks for something, so a search can stand on filters alone. Only leaving things out does not.</summary>
    public bool Narrows => kinds.Any(x => !x.Not) || sizes.Any(x => !x.Not) || dates.Any(x => !x.Not) || texts.Any(x => !x.Not);
    public bool HasDates => dates.Any(x => !x.Not);
    /// <summary>One short phrase for each thing understood, so the person can see how the search was read.</summary>
    public IReadOnlyList<string> Chips => chips;
    /// <summary>The folder text the results must contain, so the search can look inside paths rather than names.</summary>
    public IReadOnlyList<IReadOnlyList<string>> RequiredPaths => [.. texts.Where(x => x.Field == "path" && !x.Not).Select(x => x.Choices)];

    [GeneratedRegex(@"(?<not>-)?(?:(?<field>[A-Za-z]+):)?(?:""(?<quoted>[^""]*)""|(?<word>\S+))")]
    private static partial Regex Token();

    [GeneratedRegex(@"^(?<number>\d+(?:\.\d+)?)\s*(?<unit>b|kb|k|mb|m|gb|g|tb|t)?$", RegexOptions.IgnoreCase)]
    private static partial Regex Amount();

    [GeneratedRegex(@"^(?<op><=|>=|<|>|=)?\s*(?<rest>.+)$")]
    private static partial Regex Comparison();

    [GeneratedRegex(@"^(?<count>\d{1,3})(?<unit>d|w|m|y)$", RegexOptions.IgnoreCase)]
    private static partial Regex Relative();

    [GeneratedRegex("^[a-z0-9_+-]{1,12}$")]
    private static partial Regex Extension();

    public static ScoutFilter Parse(string? text, DateTimeOffset? now = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return Empty;
        var at = (now ?? DateTimeOffset.Now).ToLocalTime();
        var rest = new List<string>();
        var filter = new ScoutFilter();
        foreach (Match match in Token().Matches(text))
        {
            var not = match.Groups["not"].Success;
            var field = match.Groups["field"].Success ? match.Groups["field"].Value.ToLowerInvariant() : "";
            var quoted = match.Groups["quoted"].Success;
            var value = quoted ? match.Groups["quoted"].Value : match.Groups["word"].Value;
            // A dash only means "leave out" in front of a word or a quote; a lone dash or a double dash is part of a name.
            if (not && !quoted && field.Length == 0 && (value.Length == 0 || value[0] == '-')) { rest.Add(match.Value); continue; }
            if (field.Length > 0 && filter.TryField(field, value, not, at)) continue;
            if (not && field.Length == 0 && value.Length > 0)
            {
                filter.texts.Add(new("", [value], true));
                filter.chips.Add($"Without “{value}”");
                continue;
            }
            rest.Add(match.Value);
        }
        filter.Plain = string.Join(' ', rest);
        return filter;
    }

    private bool TryField(string field, string value, bool not, DateTimeOffset now)
    {
        var no = not ? "Not " : "";
        var choices = value.Split(['|', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (choices.Length == 0) return false;
        switch (field)
        {
            case "kind" or "type" or "is":
            {
                var folders = false; var files = false; var categories = new List<ScoutCategory>();
                foreach (var part in choices)
                {
                    switch (part.ToLowerInvariant())
                    {
                        case "folder" or "folders" or "dir" or "directory" or "directories": folders = true; continue;
                        case "file" or "files": files = true; continue;
                    }
                    if (ScoutCategories.Find(part) is not { } category) return false;
                    if (!categories.Contains(category)) categories.Add(category);
                }
                kinds.Add(new(folders, files, categories, [], not));
                var names = (folders ? ["folders"] : Array.Empty<string>()).Concat(files ? ["files"] : []).Concat(categories.Select(x => x.Label.ToLowerInvariant()));
                chips.Add($"{no}Type: {string.Join(" or ", names)}");
                return true;
            }
            case "ext" or "extension" or "filetype":
            {
                var found = new List<string>();
                foreach (var part in choices)
                {
                    var extension = part.TrimStart('*').TrimStart('.').ToLowerInvariant();
                    if (!Extension().IsMatch(extension)) return false;
                    if (!found.Contains(extension)) found.Add(extension);
                }
                kinds.Add(new(false, false, [], found, not));
                chips.Add($"{no}Extension: {string.Join(" or ", found.Select(x => "." + x))}");
                return true;
            }
            case "size":
            {
                if (SizeOf(value, not) is not { } size) return false;
                sizes.Add(size);
                chips.Add($"{no}Size {Describe(size)}");
                return true;
            }
            case "date" or "modified" or "mod" or "on" or "changed":
                if (!Span(value, now, out var from, out var to, out var label)) return false;
                dates.Add(new(from, to, not));
                chips.Add($"{no}Modified: {label}");
                return true;
            case "after" or "since":
                if (!Span(value, now, out var start, out _, out var after)) return false;
                dates.Add(new(start, DateTimeOffset.MaxValue, not));
                chips.Add($"{no}Modified after {after}");
                return true;
            case "before" or "until":
                if (!Span(value, now, out var begin, out _, out var before)) return false;
                dates.Add(new(DateTimeOffset.MinValue, begin, not));
                chips.Add($"{no}Modified before {before}");
                return true;
            case "in" or "path" or "under":
                texts.Add(new("path", choices, not));
                chips.Add($"{no}In: {string.Join(" or ", choices.Select(x => $"“{x}”"))}");
                return true;
            case "name":
                texts.Add(new("name", choices, not));
                chips.Add($"{no}Name: {string.Join(" or ", choices.Select(x => $"“{x}”"))}");
                return true;
            case "sort" or "order" or "by":
            {
                if (not) return false;
                var sort = choices[0].ToLowerInvariant() switch
                {
                    "best" or "relevance" => ScoutSort.Best,
                    "newest" or "new" or "recent" or "latest" => ScoutSort.Newest,
                    "oldest" or "old" => ScoutSort.Oldest,
                    "largest" or "biggest" or "large" or "big" => ScoutSort.Largest,
                    "smallest" or "small" => ScoutSort.Smallest,
                    "name" or "az" or "alphabetical" => ScoutSort.Name,
                    _ => (ScoutSort?)null
                };
                if (sort is not { } chosen) return false;
                Sort = chosen;
                if (chosen != ScoutSort.Best) chips.Add($"Sorted by {chosen.ToString().ToLowerInvariant()}");
                return true;
            }
            default:
                return false;
        }
    }

    private static SizeTest? SizeOf(string value, bool not)
    {
        value = value.Trim();
        switch (value.ToLowerInvariant())
        {
            case "empty" or "zero": return new(0, 0, not);
            case "tiny": return new(1, (10L << 10) - 1, not);
            case "small": return new(10L << 10, (1L << 20) - 1, not);
            case "medium": return new(1L << 20, (100L << 20) - 1, not);
            case "large": return new(100L << 20, (1L << 30) - 1, not);
            case "huge": return new(1L << 30, long.MaxValue, not);
        }
        var dots = value.IndexOf("..", StringComparison.Ordinal);
        if (dots > 0)
            return Bytes(value[..dots]) is { } low && Bytes(value[(dots + 2)..]) is { } high && low <= high ? new(low, high, not) : null;
        if (Comparison().Match(value) is not { Success: true } match || Bytes(match.Groups["rest"].Value) is not { } bytes) return null;
        return match.Groups["op"].Value switch
        {
            ">" => new(bytes + 1, long.MaxValue, not),
            ">=" => new(bytes, long.MaxValue, not),
            "<" => bytes == 0 ? null : new(0, bytes - 1, not),
            "<=" => new(0, bytes, not),
            _ => new(bytes, bytes, not)
        };
    }

    private static long? Bytes(string text)
    {
        if (Amount().Match(text.Trim()) is not { Success: true } match) return null;
        var number = double.Parse(match.Groups["number"].Value, CultureInfo.InvariantCulture);
        var scale = match.Groups["unit"].Value.ToLowerInvariant() switch { "kb" or "k" => 1L << 10, "mb" or "m" => 1L << 20, "gb" or "g" => 1L << 30, "tb" or "t" => 1L << 40, _ => 1L };
        var bytes = number * scale;
        return bytes is >= 0 and < 1e15 ? (long)bytes : null;
    }

    private static string Describe(SizeTest size) =>
        size.Min == 0 && size.Max == 0 ? "is empty"
        : size.Max == long.MaxValue ? $"at least {ArchiveQuery.Format(size.Min)}"
        : size.Min == 0 ? $"up to {ArchiveQuery.Format(size.Max)}"
        : size.Min == size.Max ? $"exactly {ArchiveQuery.Format(size.Min)}"
        : $"{ArchiveQuery.Format(size.Min)} to {ArchiveQuery.Format(size.Max)}";

    /// <summary>A day, month, year, named period or "the last N days / weeks / months / years" as the span of time it covers.</summary>
    private static bool Span(string value, DateTimeOffset now, out DateTimeOffset from, out DateTimeOffset to, out string label)
    {
        if (Relative().Match(value.Trim()) is { Success: true } match && int.Parse(match.Groups["count"].Value, CultureInfo.InvariantCulture) is var count and > 0)
        {
            var day = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, now.Offset);
            to = day.AddDays(1);
            (from, label) = char.ToLowerInvariant(match.Groups["unit"].Value[0]) switch
            {
                'd' => (day.AddDays(1 - count), count == 1 ? "today" : $"the last {count} days"),
                'w' => (day.AddDays(1 - 7 * count), count == 1 ? "the last 7 days" : $"the last {count} weeks"),
                'm' => (day.AddMonths(-count).AddDays(1), count == 1 ? "the last month" : $"the last {count} months"),
                _ => (day.AddYears(-count).AddDays(1), count == 1 ? "the last year" : $"the last {count} years")
            };
            return true;
        }
        return ArchiveQuery.Period(value, now, out from, out to, out label);
    }

    /// <summary>Whether a file or folder passes every filter.</summary>
    public bool Allows(ScoutEntry entry)
    {
        foreach (var test in kinds) if (Matches(test, entry) == test.Not) return false;
        foreach (var test in sizes)
        {
            if (entry.Size is not { } size) return false;
            if ((size >= test.Min && size <= test.Max) == test.Not) return false;
        }
        foreach (var test in dates)
        {
            if (entry.Modified is not { } when) return false;
            if ((when >= test.From && when < test.To) == test.Not) return false;
        }
        foreach (var test in texts)
        {
            var found = test.Choices.Any(x => test.Field switch
            {
                "name" => entry.SearchName.Contains(ScoutQuery.Normalize(x), StringComparison.Ordinal),
                "path" => entry.FullPath.Contains(x, StringComparison.OrdinalIgnoreCase),
                _ => entry.SearchName.Contains(ScoutQuery.Normalize(x), StringComparison.Ordinal) || entry.FullPath.Contains(x, StringComparison.OrdinalIgnoreCase)
            });
            if (found == test.Not) return false;
        }
        return true;
    }

    private static bool Matches(KindTest test, ScoutEntry entry)
    {
        if (entry.IsFolder) return test.Folders;
        if (test.Files) return true;
        var name = entry.Name;
        var extension = Path.GetExtension(name).TrimStart('.').ToLowerInvariant();
        if (extension.Length == 0) return false;
        if (test.Extensions.Contains(extension)) return true;
        if (test.Categories.Count == 0) return false;
        var stem = Path.GetFileNameWithoutExtension(name);
        foreach (var category in test.Categories) if (category.Contains(extension, stem)) return true;
        return false;
    }

    /// <summary>
    /// The same filters as Everything search terms, so the index narrows the candidates itself instead of handing back the first few thousand.
    /// It may return a little more than <see cref="Allows"/> (an <c>.exe</c> that is not called setup); never less. Everything here is built from
    /// validated extensions and numbers, never from typed text.
    /// </summary>
    public string Native()
    {
        var parts = new List<string>();
        foreach (var test in kinds)
        {
            var list = test.Extensions.Concat(test.Categories.SelectMany(x => test.Not ? x.Extensions : x.PossibleExtensions)).Distinct().ToArray();
            if (test.Folders && list.Length == 0 && !test.Files) parts.Add(test.Not ? "file:" : "folder:");
            else if (test.Files && list.Length == 0 && !test.Folders) parts.Add(test.Not ? "folder:" : "file:");
            else if (!test.Folders && !test.Files && list.Length > 0) parts.Add((test.Not ? "!ext:" : "ext:") + string.Join(';', list));
        }
        foreach (var test in sizes.Where(x => !x.Not))
            parts.Add(test.Min == test.Max ? $"size:{test.Min}" : test.Max == long.MaxValue ? $"size:>={test.Min}" : test.Min == 0 ? $"size:<={test.Max}" : $"size:{test.Min}..{test.Max}");
        foreach (var test in dates.Where(x => !x.Not))
        {
            var from = test.From == DateTimeOffset.MinValue ? null : test.From.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var last = test.To == DateTimeOffset.MaxValue ? null : test.To.ToLocalTime().AddTicks(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (from is not null && last is not null) parts.Add($"dm:{from}..{last}");
            else if (from is not null) parts.Add($"dm:>={from}");
            else if (last is not null) parts.Add($"dm:<={last}");
        }
        return string.Join(' ', parts);
    }
}

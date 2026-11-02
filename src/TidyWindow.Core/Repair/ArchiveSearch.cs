using System.Globalization;
using System.Text.RegularExpressions;

namespace TidyWindow.Next.Core;

[Flags]
public enum ArchiveKinds { None = 0, Registry = 1, Shortcut = 2, Folder = 4, Setup = 8 }

public enum ArchiveSort { Newest, Oldest, Largest, Name }

/// <summary>What a search looks at in one repair or one quarantined item. <see cref="Bytes"/> is -1 when the size is not known (yet).</summary>
public sealed record ArchiveEntry(string Title, string Names, string Paths, DateTimeOffset When, ArchiveKinds Kinds, long Bytes, bool Admin);

/// <summary>
/// The search box of the Undo and Quarantine pages. Plain words must all be found (in the repair's name, what it changed, where, and its date);
/// <c>"a phrase"</c> is kept together; <c>-word</c> leaves out whatever has it. <c>field:value</c> filters narrow it further, and <c>|</c> inside a value means "or":
/// <c>kind:folder|shortcut</c>, <c>repair:</c>, <c>name:</c>, <c>path:</c>, <c>date:</c> (today, yesterday, week, month, year, 2026-10-25, 2026-10, 2026),
/// <c>before:</c> and <c>after:</c> (a date), <c>size:&gt;100mb</c> (also &lt;, &gt;=, &lt;=, =; b, kb, mb, gb) and <c>admin:yes</c>.
/// A filter it does not understand is searched for as plain text, so nothing silently matches everything.
/// </summary>
public sealed partial class ArchiveQuery
{
    private sealed record Term(string Text, bool Not, string Field);
    private sealed record SizeTest(string Op, long Bytes, bool Not);
    private sealed record DateTest(DateTimeOffset From, DateTimeOffset To, bool Not);

    private readonly List<Term> words = [];
    private readonly List<(ArchiveKinds Kinds, bool Not)> kinds = [];
    private readonly List<DateTest> dates = [];
    private readonly List<SizeTest> sizes = [];
    private readonly List<(bool Admin, bool Not)> admins = [];
    private readonly List<string> chips = [];

    public static readonly ArchiveQuery Empty = new();

    public bool IsEmpty => words.Count == 0 && kinds.Count == 0 && dates.Count == 0 && sizes.Count == 0 && admins.Count == 0;

    /// <summary>One short sentence for each thing the search understood, so the person can see how it was read.</summary>
    public IReadOnlyList<string> Chips => chips;

    [GeneratedRegex(@"(?<not>-)?(?:(?<field>[A-Za-z]+):)?(?:""(?<quoted>[^""]*)""|(?<word>\S+))")]
    private static partial Regex Token();

    public static ArchiveQuery Parse(string? text, DateTimeOffset? now = null)
    {
        var query = new ArchiveQuery();
        if (string.IsNullOrWhiteSpace(text)) return query;
        var at = (now ?? DateTimeOffset.Now).ToLocalTime();
        foreach (Match match in Token().Matches(text))
        {
            var not = match.Groups["not"].Success;
            var field = match.Groups["field"].Success ? match.Groups["field"].Value.ToLowerInvariant() : "";
            var value = match.Groups["quoted"].Success ? match.Groups["quoted"].Value : match.Groups["word"].Value;
            if (value.Length == 0 && !match.Groups["quoted"].Success) continue;
            if (field.Length > 0 && query.TryField(field, value, not, at)) continue;
            // Not a filter this knows: the whole token, as typed, is plain text.
            var plain = field.Length > 0 ? field + ":" + value : value;
            if (plain.Length == 0) continue;
            query.words.Add(new(plain, not, ""));
            query.chips.Add(not ? $"Without “{plain}”" : $"Has “{plain}”");
        }
        return query;
    }

    private bool TryField(string field, string value, bool not, DateTimeOffset now)
    {
        var no = not ? "Not " : "";
        switch (field)
        {
            case "kind" or "type":
            {
                var found = ArchiveKinds.None;
                foreach (var part in value.Split(['|', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var kind = part.ToLowerInvariant() switch
                    {
                        "folder" or "folders" or "dir" => ArchiveKinds.Folder,
                        "shortcut" or "shortcuts" or "link" or "links" or "lnk" => ArchiveKinds.Shortcut,
                        "registry" or "key" or "keys" or "reg" => ArchiveKinds.Registry,
                        "setup" or "installer" or "setups" => ArchiveKinds.Setup,
                        _ => ArchiveKinds.None
                    };
                    if (kind == ArchiveKinds.None) return false;
                    found |= kind;
                }
                if (found == ArchiveKinds.None) return false;
                kinds.Add((found, not));
                chips.Add($"{no}Type: {string.Join(" or ", Enum.GetValues<ArchiveKinds>().Where(x => x != ArchiveKinds.None && found.HasFlag(x)).Select(x => x.ToString().ToLowerInvariant()))}");
                return true;
            }
            case "repair" or "title" or "name" or "path":
                if (value.Length == 0) return false;
                words.Add(new(value, not, field == "title" ? "repair" : field));
                chips.Add($"{no}{(field == "title" ? "Repair" : char.ToUpperInvariant(field[0]) + field[1..])}: “{value}”");
                return true;
            case "date" or "on":
                if (!Period(value, now, out var from, out var to, out var label)) return false;
                dates.Add(new(from, to, not));
                chips.Add($"{no}Date: {label}");
                return true;
            case "after" or "since":
                if (!Period(value, now, out var start, out _, out var after)) return false;
                dates.Add(new(start, DateTimeOffset.MaxValue, not));
                chips.Add($"{no}After {after}");
                return true;
            case "before" or "until":
                if (!Period(value, now, out _, out _, out var before, out var begin)) return false;
                dates.Add(new(DateTimeOffset.MinValue, begin, not));
                chips.Add($"{no}Before {before}");
                return true;
            case "size":
                if (SizeTestOf(value, not) is not { } size) return false;
                sizes.Add(size);
                chips.Add($"{no}Size {size.Op} {Format(size.Bytes)}");
                return true;
            case "admin":
                if (value.ToLowerInvariant() is not ("yes" or "true" or "no" or "false")) return false;
                admins.Add((value.ToLowerInvariant() is "yes" or "true", not));
                chips.Add($"{no}{(value.ToLowerInvariant() is "yes" or "true" ? "Needs admin" : "No admin needed")}");
                return true;
            default:
                return false;
        }
    }

    [GeneratedRegex(@"^(?<op><=|>=|<|>|=)\s*(?<number>\d+(?:\.\d+)?)\s*(?<unit>b|kb|mb|gb)?$", RegexOptions.IgnoreCase)]
    private static partial Regex SizeToken();

    private static SizeTest? SizeTestOf(string value, bool not)
    {
        if (SizeToken().Match(value.Trim()) is not { Success: true } match) return null;
        var number = double.Parse(match.Groups["number"].Value, CultureInfo.InvariantCulture);
        var scale = match.Groups["unit"].Value.ToLowerInvariant() switch { "kb" => 1L << 10, "mb" => 1L << 20, "gb" => 1L << 30, _ => 1L };
        return new(match.Groups["op"].Value, (long)(number * scale), not);
    }

    private static string Format(long bytes) => bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.#} GB" : bytes >= 1L << 20 ? $"{bytes / (double)(1L << 20):0.#} MB" : bytes >= 1L << 10 ? $"{bytes / 1024.0:0.#} KB" : $"{bytes} B";

    private static bool Period(string value, DateTimeOffset now, out DateTimeOffset from, out DateTimeOffset to, out string label) => Period(value, now, out from, out to, out label, out _);

    /// <summary>A day, month, year or named period as the span of time it covers, in the person's own time zone.</summary>
    private static bool Period(string value, DateTimeOffset now, out DateTimeOffset from, out DateTimeOffset to, out string label, out DateTimeOffset begin)
    {
        var day = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, now.Offset);
        from = to = begin = default; label = "";
        switch (value.Trim().ToLowerInvariant())
        {
            case "today": from = day; to = day.AddDays(1); label = "today"; break;
            case "yesterday": from = day.AddDays(-1); to = day; label = "yesterday"; break;
            case "week": from = day.AddDays(-6); to = day.AddDays(1); label = "the last 7 days"; break;
            case "month": from = day.AddDays(-29); to = day.AddDays(1); label = "the last 30 days"; break;
            case "year": from = new(now.Year, 1, 1, 0, 0, 0, now.Offset); to = from.AddYears(1); label = now.Year.ToString(CultureInfo.InvariantCulture); break;
            default:
            {
                var text = value.Trim();
                if (DateTime.TryParseExact(text, "yyyy-M-d", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) { from = Local(d, now); to = from.AddDays(1); label = d.ToString("d MMM yyyy", CultureInfo.CurrentCulture); }
                else if (DateTime.TryParseExact(text, "yyyy-M", CultureInfo.InvariantCulture, DateTimeStyles.None, out var m)) { from = Local(m, now); to = from.AddMonths(1); label = m.ToString("MMMM yyyy", CultureInfo.CurrentCulture); }
                else if (text.Length == 4 && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var y) && y is > 1999 and < 2200) { from = Local(new DateTime(y, 1, 1), now); to = from.AddYears(1); label = text; }
                else return false;
                break;
            }
        }
        begin = from;
        return true;
    }

    private static DateTimeOffset Local(DateTime date, DateTimeOffset now) => new(date.Year, date.Month, date.Day, 0, 0, 0, now.Offset);

    public bool Matches(ArchiveEntry entry)
    {
        if (IsEmpty) return true;
        var when = entry.When.ToLocalTime();
        foreach (var (found, not) in kinds) if ((entry.Kinds & found) != 0 == not) return false;
        foreach (var test in dates) if ((when >= test.From && when < test.To) == test.Not) return false;
        foreach (var (admin, not) in admins) if ((entry.Admin == admin) == not) return false;
        foreach (var test in sizes)
        {
            if (entry.Bytes < 0) return false;
            var ok = test.Op switch { ">" => entry.Bytes > test.Bytes, ">=" => entry.Bytes >= test.Bytes, "<" => entry.Bytes < test.Bytes, "<=" => entry.Bytes <= test.Bytes, _ => entry.Bytes == test.Bytes };
            if (ok == test.Not) return false;
        }
        foreach (var word in words)
        {
            // Several choices ("a|b") inside one filter mean "either", for the fields that name something.
            var choices = word.Field.Length > 0 ? word.Text.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : [word.Text];
            var found = choices.Any(x => In(word.Field, x, entry, when));
            if (found == word.Not) return false;
        }
        return true;
    }

    private static bool In(string field, string text, ArchiveEntry entry, DateTimeOffset when) => field switch
    {
        "repair" => Has(entry.Title, text),
        "name" => Has(entry.Names, text),
        "path" => Has(entry.Paths, text) || Has(entry.Names, text),
        _ => Has(entry.Title, text) || Has(entry.Names, text) || Has(entry.Paths, text) || Has(entry.Kinds.ToString(), text)
             || Has(when.ToString("d MMMM yyyy dddd yyyy-MM-dd", CultureInfo.CurrentCulture), text)
    };

    private static bool Has(string haystack, string needle) => haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Where a date falls in the lists: Today, Yesterday, Earlier this week, Last week, Earlier this month, then each month, then each year.</summary>
public static class ArchiveDates
{
    public static (int Order, string Label) Bucket(DateTimeOffset when, DateTimeOffset now)
    {
        var day = when.ToLocalTime().Date;
        var today = now.ToLocalTime().Date;
        if (day >= today) return (0, "Today");
        if (day == today.AddDays(-1)) return (1, "Yesterday");
        var weekStart = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        if (day >= weekStart) return (2, "Earlier this week");
        if (day >= weekStart.AddDays(-7)) return (3, "Last week");
        if (day.Year == today.Year && day.Month == today.Month) return (4, "Earlier this month");
        var monthsAgo = (today.Year - day.Year) * 12 + today.Month - day.Month;
        if (monthsAgo < 12) return (100 + monthsAgo, day.ToString("MMMM yyyy", CultureInfo.CurrentCulture));
        return (1000 + today.Year - day.Year, day.Year.ToString(CultureInfo.InvariantCulture));
    }
}

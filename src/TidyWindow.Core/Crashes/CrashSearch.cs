using System.Globalization;
using System.Text;

namespace TidyWindow.Next.Core;

/// <summary>
/// What the person typed, understood. Plain words must all match (anywhere in an event, with typos forgiven); "quoted phrases" must match exactly; a leading
/// minus excludes. <c>app:</c> <c>module:</c> <c>code:</c> <c>kind:</c> <c>source:</c> <c>version:</c> <c>path:</c> <c>pid:</c> narrow one field, and <c>since:</c> / <c>before:</c>
/// narrow by time (<c>since:7d</c>, <c>since:yesterday</c>, <c>before:2026-10-01</c>). Several values for the same field are alternatives.
/// </summary>
public sealed class CrashQuery
{
    private static readonly Dictionary<string, string> Fields = new(StringComparer.OrdinalIgnoreCase)
    {
        ["app"] = "app", ["name"] = "app", ["program"] = "app", ["exe"] = "app",
        ["module"] = "module", ["dll"] = "module", ["in"] = "module",
        ["code"] = "code", ["error"] = "code", ["exception"] = "code", ["stop"] = "code",
        ["kind"] = "kind", ["type"] = "kind", ["is"] = "kind",
        ["source"] = "source", ["provider"] = "source",
        ["version"] = "version", ["ver"] = "version", ["v"] = "version",
        ["path"] = "path", ["pid"] = "pid",
        ["since"] = "since", ["after"] = "since", ["last"] = "since", ["within"] = "since",
        ["before"] = "before", ["until"] = "before", ["older"] = "before"
    };

    private readonly record struct Term(string Text, bool Phrase);

    private readonly List<Term> words = [], notWords = [];
    private readonly Dictionary<string, List<string>> fields = new(StringComparer.OrdinalIgnoreCase), notFields = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> chips = [];

    public static CrashQuery Parse(string? text, DateTimeOffset? now = null)
    {
        var query = new CrashQuery();
        var clock = now ?? DateTimeOffset.Now;
        foreach (var (negate, field, value, quoted) in Tokens(text ?? ""))
        {
            if (value.Length == 0) continue;
            var known = field is not null && Fields.TryGetValue(field, out var canonical) ? canonical : null;
            if (known is "since" or "before")
            {
                if (TimeValue(value, clock) is { } moment)
                {
                    if (known == "since") query.Since = query.Since is { } s && s > moment ? s : moment; else query.Until = query.Until is { } u && u < moment ? u : moment;
                    query.chips.Add($"{(known == "since" ? "since" : "before")} {Describe(moment, clock)}");
                    continue;
                }
            }
            else if (known is not null)
            {
                var target = negate ? query.notFields : query.fields;
                if (!target.TryGetValue(known, out var list)) target[known] = list = [];
                list.Add(value);
                query.chips.Add($"{(negate ? "not " : "")}{known}: {value}");
                continue;
            }
            // An unknown "word:" is just text, so a search for "c:\windows" or "ntdll.dll:" still works.
            var text2 = field is not null && known is null or "since" or "before" ? $"{field}:{value}" : value;
            (negate ? query.notWords : query.words).Add(new(text2.ToLowerInvariant(), quoted));
            if (negate) query.chips.Add($"not “{text2}”");
        }
        return query;
    }

    public DateTimeOffset? Since { get; private set; }
    public DateTimeOffset? Until { get; private set; }
    public bool HasWords => words.Count > 0;
    public bool IsEmpty => words.Count == 0 && notWords.Count == 0 && fields.Count == 0 && notFields.Count == 0 && Since is null && Until is null;
    /// <summary>The filters understood, in words, so the person can see how the search was read.</summary>
    public IReadOnlyList<string> Chips => chips;

    internal IReadOnlyList<(string Text, bool Phrase)> Words => words.Select(x => (x.Text, x.Phrase)).ToList();
    internal IReadOnlyList<(string Text, bool Phrase)> NotWords => notWords.Select(x => (x.Text, x.Phrase)).ToList();
    internal IReadOnlyDictionary<string, List<string>> Included => fields;
    internal IReadOnlyDictionary<string, List<string>> Excluded => notFields;

    private static IEnumerable<(bool Negate, string? Field, string Value, bool Quoted)> Tokens(string text)
    {
        var i = 0;
        while (i < text.Length)
        {
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            if (i >= text.Length) yield break;
            var negate = false;
            if (text[i] == '-' && i + 1 < text.Length && !char.IsWhiteSpace(text[i + 1])) { negate = true; i++; }
            var value = new StringBuilder();
            string? field = null;
            var quoted = false;
            while (i < text.Length && !char.IsWhiteSpace(text[i]))
            {
                var c = text[i];
                if (c == '"')
                {
                    quoted = true; i++;
                    while (i < text.Length && text[i] != '"') value.Append(text[i++]);
                    if (i < text.Length) i++;
                    continue;
                }
                // Only the first colon names a field, and only when something plain comes before it (so "c:\x" stays a path).
                if (c == ':' && field is null && !quoted && value.Length > 1 && value.ToString().All(char.IsLetter)) { field = value.ToString(); value.Clear(); i++; continue; }
                value.Append(c); i++;
            }
            yield return (negate, field, value.ToString().Trim(), quoted);
        }
    }

    private static DateTimeOffset? TimeValue(string value, DateTimeOffset now)
    {
        value = value.Trim().ToLowerInvariant();
        var today = new DateTimeOffset(now.Date, now.Offset);
        switch (value)
        {
            case "today": return today;
            case "yesterday": return today.AddDays(-1);
            case "week": return now.AddDays(-7);
            case "month": return now.AddDays(-30);
            case "year": return now.AddDays(-365);
        }
        var digits = new string(value.TakeWhile(char.IsDigit).ToArray());
        var unit = value[digits.Length..].Trim();
        if (digits.Length > 0 && int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n < 100000)
        {
            var relative = unit switch
            {
                "m" or "min" or "mins" or "minute" or "minutes" => now.AddMinutes(-n),
                "h" or "hr" or "hrs" or "hour" or "hours" => now.AddHours(-n),
                "d" or "day" or "days" => now.AddDays(-n),
                "w" or "wk" or "week" or "weeks" => now.AddDays(-7.0 * n),
                "mo" or "month" or "months" => now.AddMonths(-n),
                "y" or "yr" or "year" or "years" => now.AddYears(-n),
                _ => (DateTimeOffset?)null
            };
            if (relative is not null) return relative;
        }
        // Only something shaped like a date is tried as one, so a bare number is not read as a day of the month.
        return value.Any(c => c is '-' or '/' or ':') && DateTime.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out var date) ? new DateTimeOffset(date) : null;
    }

    private static string Describe(DateTimeOffset moment, DateTimeOffset now)
    {
        var age = now - moment;
        return age < TimeSpan.FromHours(1) ? $"{Math.Max(1, (int)age.TotalMinutes)} min ago" : age < TimeSpan.FromDays(2) ? $"{(int)age.TotalHours} h ago" : age < TimeSpan.FromDays(60) ? $"{(int)age.TotalDays} days ago" : moment.ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture);
    }
}

/// <summary>Searches a fixed list of events. Everything a search needs is worked out once up front, so typing is a scan over short strings.</summary>
public sealed class CrashSearchIndex
{
    private sealed class Entry(CrashEvent e)
    {
        public readonly CrashEvent Event = e;
        public readonly string App = (e.AppName + " " + e.FileName).ToLowerInvariant();
        public readonly string Module = e.Module.ToLowerInvariant();
        public readonly string Code = (e.Code + " " + e.Code.Replace("0x", "", StringComparison.OrdinalIgnoreCase) + " " + e.CodeName).ToLowerInvariant();
        public readonly string Rest = string.Join(' ', e.AppPath, e.ModulePath, e.Headline, e.Managed, Clip(e.Detail, 600), e.Kind.Label(), e.Kind.Synonyms(), e.Source, e.AppVersion, e.ModuleVersion, e.ProcessId, e.ReportId).ToLowerInvariant();
        public string[]? Words;
    }

    private readonly List<Entry> entries;

    public CrashSearchIndex(IEnumerable<CrashEvent> events)
    {
        entries = events.Select(x => new Entry(x)).ToList();
    }

    public int Count => entries.Count;

    /// <param name="from">The oldest an event may be, from the page's own time range.</param>
    /// <returns>Matches, best first when words were typed and newest first otherwise.</returns>
    public IReadOnlyList<CrashEvent> Search(CrashQuery query, DateTimeOffset? from = null)
    {
        var scored = new List<(CrashEvent Event, int Score)>();
        foreach (var entry in entries)
        {
            var e = entry.Event;
            if (from is { } start && e.Time < start) continue;
            if (query.Since is { } since && e.Time < since) continue;
            if (query.Until is { } until && e.Time > until) continue;
            if (!FieldsMatch(entry, query)) continue;
            if (query.NotWords.Any(w => Hit(entry, w.Text))) continue;
            var score = 0;
            var all = true;
            foreach (var (text, phrase) in query.Words)
            {
                var s = Score(entry, text, phrase);
                if (s == 0) { all = false; break; }
                score += s;
            }
            if (all) scored.Add((e, score));
        }
        return (query.HasWords ? scored.OrderByDescending(x => x.Score).ThenByDescending(x => x.Event.Time) : scored.OrderByDescending(x => x.Event.Time)).Select(x => x.Event).ToList();
    }

    private static bool Hit(Entry e, string text) => e.App.Contains(text) || e.Module.Contains(text) || e.Code.Contains(text) || e.Rest.Contains(text);

    private static int Score(Entry e, string text, bool phrase)
    {
        if (e.App == text || e.App.Split(' ').Contains(text)) return 100;
        if (e.App.StartsWith(text, StringComparison.Ordinal)) return 80;
        if (e.App.Contains(text)) return 60;
        if (e.Module.Contains(text) || e.Code.Contains(text)) return 40;
        if (e.Rest.Contains(text)) return 20;
        if (phrase || text.Length < 4 || text.Any(char.IsDigit)) return 0;
        // A typo: a whole word that is one or two edits away.
        var limit = text.Length <= 7 ? 1 : 2;
        e.Words ??= Split(e.App + " " + e.Module + " " + e.Code + " " + e.Rest);
        return e.Words.Any(w => Math.Abs(w.Length - text.Length) <= limit && Within(w, text, limit)) ? 8 : 0;
    }

    private static string[] Split(string text) => text.Split([' ', '.', '_', '-', '\\', '/', ':', ',', '(', ')', '"', '\''], StringSplitOptions.RemoveEmptyEntries).Where(x => x.Length >= 3).Distinct().ToArray();

    private static bool FieldsMatch(Entry entry, CrashQuery query)
    {
        foreach (var (field, values) in query.Included) if (!values.Any(v => One(entry, field, v))) return false;
        foreach (var (field, values) in query.Excluded) if (values.Any(v => One(entry, field, v))) return false;
        return true;
    }

    private static bool One(Entry entry, string field, string value)
    {
        var e = entry.Event;
        var v = value.ToLowerInvariant();
        return field switch
        {
            "app" => entry.App.Contains(v),
            "module" => entry.Module.Contains(v) || e.ModulePath.Contains(v, StringComparison.OrdinalIgnoreCase),
            "code" => entry.Code.Contains(v.Replace("0x", "", StringComparison.Ordinal)),
            "source" => e.Source.Contains(v, StringComparison.OrdinalIgnoreCase),
            "version" => e.AppVersion.Contains(v, StringComparison.OrdinalIgnoreCase) || e.ModuleVersion.Contains(v, StringComparison.OrdinalIgnoreCase),
            "path" => e.AppPath.Contains(v, StringComparison.OrdinalIgnoreCase) || e.ModulePath.Contains(v, StringComparison.OrdinalIgnoreCase),
            "pid" => e.ProcessId == v,
            "kind" => KindMatches(e, v),
            _ => false
        };
    }

    private static bool KindMatches(CrashEvent e, string v) => v.Replace(" ", "").Replace("-", "") switch
    {
        "crash" or "crashes" or "crashed" => e.Kind == CrashKind.AppCrash,
        "hang" or "hangs" or "hung" or "freeze" or "frozen" or "notresponding" => e.Kind == CrashKind.AppHang,
        "start" or "startup" or "launch" or "couldnotstart" or "notstarting" => e.Kind == CrashKind.AppStartFailure,
        "app" or "apps" or "program" => e.Kind.IsApp(),
        "windows" or "system" => e.Kind.IsWindows(),
        "bsod" or "bluescreen" or "bugcheck" => e.Kind == CrashKind.BlueScreen,
        "shutdown" or "power" or "unexpected" => e.Kind == CrashKind.UnexpectedShutdown,
        "service" or "services" => e.Kind == CrashKind.ServiceFailure,
        "driver" or "gpu" or "display" => e.Kind == CrashKind.DriverReset,
        "hardware" or "whea" => e.Kind == CrashKind.HardwareError,
        "disk" or "storage" => e.Kind == CrashKind.StorageError,
        "memory" or "ram" => e.Kind == CrashKind.LowMemory,
        "managed" or "dotnet" or ".net" or "net" => e.Managed.Length > 0,
        _ => e.Kind.Label().Contains(v, StringComparison.OrdinalIgnoreCase)
    };

    private static string Clip(string text, int length) => text.Length <= length ? text : text[..length];

    // Bounded optimal-string-alignment distance: a swapped pair of letters counts as one typo.
    internal static bool Within(string left, string right, int limit)
    {
        if (Math.Abs(left.Length - right.Length) > limit) return false;
        var previous2 = new int[right.Length + 1];
        var previous = Enumerable.Range(0, right.Length + 1).ToArray();
        var current = new int[right.Length + 1];
        for (var i = 1; i <= left.Length; i++)
        {
            current[0] = i;
            var best = current[0];
            for (var j = 1; j <= right.Length; j++)
            {
                var cost = left[i - 1] == right[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1), previous[j - 1] + cost);
                if (i > 1 && j > 1 && left[i - 1] == right[j - 2] && left[i - 2] == right[j - 1]) current[j] = Math.Min(current[j], previous2[j - 2] + 1);
                best = Math.Min(best, current[j]);
            }
            if (best > limit) return false;
            (previous2, previous, current) = (previous, current, previous2);
        }
        return previous[right.Length] <= limit;
    }
}

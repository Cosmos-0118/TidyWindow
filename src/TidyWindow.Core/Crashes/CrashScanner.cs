using System.Diagnostics;
using System.Globalization;

namespace TidyWindow.Next.Core;

/// <summary>
/// Reads new crash records from Windows into the <see cref="CrashHistory"/>. The first scan looks back 90 days; every later one reads only what happened since
/// the last, so opening the page costs a few milliseconds of Windows' own filtering. Nothing here runs on its own.
/// </summary>
public sealed class CrashScanner(ICrashEventSource source, CrashHistory history, Func<DateTimeOffset>? clock = null)
{
    public static readonly TimeSpan FirstLookBack = TimeSpan.FromDays(90);
    // A record written a moment before a scan ended can be listed a moment after; reading a little earlier loses nothing, and a record already held is ignored.
    private static readonly TimeSpan Overlap = TimeSpan.FromMinutes(5);
    private const int PerQuery = 4000;
    private readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.Now);

    /// <param name="full">Read the whole look-back again instead of only what is new.</param>
    public CrashScan Scan(CancellationToken token, bool full = false)
    {
        var timer = Stopwatch.StartNew();
        var started = now();
        var since = (full ? null : history.ScannedUpTo) is { } last ? last - Overlap : started - FirstLookBack;
        var problems = new List<string>();
        var found = new List<CrashEvent>();
        var complete = true;
        foreach (var (log, specs) in Queries())
        {
            token.ThrowIfCancellationRequested();
            try
            {
                foreach (var raw in source.Read(log, Query(specs, since), PerQuery, token))
                    if (CrashParser.Parse(raw) is { } e) found.Add(e);
            }
            catch (EventSourceException ex) when (ex.Missing) { /* this PC has no such log */ }
            catch (EventSourceException ex)
            {
                complete = false;
                problems.Add(ex.Code == 5 ? $"Windows would not let TidyWindow read the {log} log." : ex.Message);
            }
        }
        var added = history.Add(found);
        // Only a scan that read everything moves the mark, so a log that failed is tried again next time.
        if (complete) history.MarkScanned(started);
        return new(added, problems, timer.Elapsed);
    }

    // One query per log for the plain sources, and one of its own for the report records, which are narrowed by report type.
    private static IEnumerable<(string Log, CrashSource[] Specs)> Queries()
    {
        foreach (var group in CrashSources.All.Where(x => x.EventNames is null).GroupBy(x => x.Log)) yield return (group.Key, group.ToArray());
        foreach (var spec in CrashSources.All.Where(x => x.EventNames is not null)) yield return (spec.Log, [spec]);
    }

    /// <summary>The XPath Windows evaluates, so only matching events are ever copied out of the log.</summary>
    internal static string Query(IReadOnlyCollection<CrashSource> specs, DateTimeOffset since)
    {
        var matches = string.Join(" or ", specs.Select(x => $"(Provider[@Name='{x.Provider}'] and ({string.Join(" or ", x.Ids.Select(id => $"EventID={id}"))}))"));
        var time = since.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        var names = specs.Count == 1 && specs.First().EventNames is { } list
            ? $" and EventData[{string.Join(" or ", list.Select(n => $"Data[@Name='EventName']='{n}'"))}]"
            : "";
        return $"*[System[({matches}) and TimeCreated[@SystemTime>='{time}']]{names}]";
    }
}

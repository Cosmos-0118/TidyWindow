namespace TidyWindow.Next.Core;

/// <summary>Turns the saved records into what a person reads: one entry per real event, then per program, then a few plain observations.</summary>
public static class CrashDigest
{
    private static readonly TimeSpan SameCrash = TimeSpan.FromSeconds(20), SameShutdown = TimeSpan.FromMinutes(10), Loop = TimeSpan.FromMinutes(10), Burst = TimeSpan.FromMinutes(10);

    // Files nearly every program loads, so a crash "in" one says nothing about what went wrong.
    private static readonly HashSet<string> Generic = new(StringComparer.OrdinalIgnoreCase)
    {
        "ntdll.dll", "kernelbase.dll", "kernel32.dll", "ucrtbase.dll", "msvcrt.dll", "vcruntime140.dll", "vcruntime140_1.dll", "msvcp140.dll",
        "combase.dll", "rpcrt4.dll", "user32.dll", "win32u.dll", "unknown", ""
    };

    /// <summary>
    /// Windows often records one event several times: a .NET crash is written by both the runtime and Windows, a blue screen by two components, an unclean
    /// shutdown by two more. This keeps one of each, with the details of all, and attaches each crash's Windows report folder. Newest first.
    /// </summary>
    public static IReadOnlyList<CrashEvent> Merge(IEnumerable<CrashEvent> stored)
    {
        var all = stored.OrderBy(x => x.Time).ToList();
        var folders = all.Where(x => x.Kind == CrashKind.Report && x.ReportId.Length > 0 && x.ReportFolder.Length > 0)
            .GroupBy(x => x.ReportId, StringComparer.OrdinalIgnoreCase).ToDictionary(x => x.Key, x => x.Last().ReportFolder, StringComparer.OrdinalIgnoreCase);
        var visible = all.Where(x => x.Kind != CrashKind.Report).ToList();

        var managed = visible.Where(IsManagedRecord).ToList();
        var used = new HashSet<string>();
        var merged = new List<CrashEvent>();
        foreach (var e in visible.Where(x => !IsManagedRecord(x)))
        {
            var current = e;
            if (e.Kind == CrashKind.AppCrash && !IsManagedRecord(e)
                && managed.FirstOrDefault(m => !used.Contains(m.Id) && m.AppKey == e.AppKey && (m.Time - e.Time).Duration() <= SameCrash) is { } twin)
            {
                used.Add(twin.Id);
                current = e with { Managed = twin.Managed, Detail = Combine(e.Detail, twin.Detail) };
            }
            if (current.ReportFolder.Length == 0 && current.ReportId.Length > 0 && folders.TryGetValue(current.ReportId, out var folder)) current = current with { ReportFolder = folder };
            merged.Add(current);
        }
        // A .NET crash Windows never reported on its own still counts.
        merged.AddRange(managed.Where(x => !used.Contains(x.Id)));

        return CollapseBursts(DropRepeatedShutdowns(merged)).OrderByDescending(x => x.Time).ToList();
    }

    // A failing device or driver can log the same problem dozens of times in a minute. That is one problem, shown once with how often it repeated.
    // App crashes are left alone: three crashes in a row is the information.
    private static IEnumerable<CrashEvent> CollapseBursts(IEnumerable<CrashEvent> events)
    {
        var single = events.Where(x => x.Kind is CrashKind.AppCrash or CrashKind.AppHang or CrashKind.BlueScreen or CrashKind.UnexpectedShutdown);
        foreach (var e in single) yield return e;
        var groups = events.Where(x => x.Kind is not (CrashKind.AppCrash or CrashKind.AppHang or CrashKind.BlueScreen or CrashKind.UnexpectedShutdown))
            .GroupBy(x => (x.Kind, x.Source, x.EventId, x.Code, x.App));
        foreach (var group in groups)
        {
            CrashEvent? last = null;
            var count = 0;
            foreach (var e in group.OrderBy(x => x.Time))
            {
                if (last is not null && e.Time - last.Time > Burst) { yield return last with { Repeats = count }; count = 0; }
                last = e; count++;
            }
            if (last is not null) yield return last with { Repeats = count };
        }
    }

    private static bool IsManagedRecord(CrashEvent e) => e.Kind == CrashKind.AppCrash && e.EventId == 1026 && e.Source == ".NET Runtime";
    private static string Combine(string first, string second) => first.Length == 0 ? second : second.Length == 0 ? first : first + "\n" + second;

    // The record with the most to say wins: the blue screen report (it names the dump), then the power record that carries a stop code, then the bare ones.
    private static IEnumerable<CrashEvent> DropRepeatedShutdowns(List<CrashEvent> events)
    {
        int Rank(CrashEvent e) => e.Kind == CrashKind.BlueScreen ? (e.Source.Contains("SystemErrorReporting", StringComparison.OrdinalIgnoreCase) ? 3 : 2) : e.Source == "EventLog" ? 0 : 1;
        var stops = events.Where(x => x.Kind is CrashKind.BlueScreen or CrashKind.UnexpectedShutdown).OrderByDescending(Rank).ThenBy(x => x.Time).ToList();
        var kept = new List<CrashEvent>();
        foreach (var e in stops)
        {
            var twin = kept.Any(k => (k.Time - e.Time).Duration() <= SameShutdown && (k.Code == e.Code || e.Code.Length == 0 || k.Code.Length == 0));
            if (!twin) kept.Add(e);
        }
        return events.Where(x => x.Kind is not (CrashKind.BlueScreen or CrashKind.UnexpectedShutdown)).Concat(kept);
    }

    /// <summary>One entry per program that crashed or froze, most trouble first. <paramref name="days"/> sets how many days the small history chart covers, ending today.</summary>
    public static IReadOnlyList<CrashApp> Apps(IEnumerable<CrashEvent> events, DateTimeOffset now, int days)
    {
        var today = now.Date;
        return events.Where(x => x.Kind.IsApp() && x.AppKey.Length > 0)
            .GroupBy(x => x.AppKey)
            .Select(group =>
            {
                var items = group.OrderByDescending(x => x.Time).ToList();
                var daily = new int[Math.Max(1, days)];
                foreach (var e in items)
                {
                    var ago = (today - e.Time.ToOffset(now.Offset).Date).Days;
                    if (ago >= 0 && ago < daily.Length) daily[daily.Length - 1 - ago]++;
                }
                var signatures = items.Where(x => x.Kind == CrashKind.AppCrash)
                    .GroupBy(x => (Module: x.Module.ToLowerInvariant(), x.Code))
                    .Select(g => new CrashSignature(g.First().Module, g.Key.Code, g.First().CodeName, g.Count(), g.Max(x => x.Time)))
                    .OrderByDescending(x => x.Count).ThenByDescending(x => x.Last).Take(8).ToList();
                return new CrashApp(group.Key, items[0].AppName, items.Select(x => x.AppPath).FirstOrDefault(x => x.Length > 0) ?? "",
                    items.Count(x => x.Kind == CrashKind.AppCrash), items.Count(x => x.Kind == CrashKind.AppHang),
                    items[^1].Time, items[0].Time, items, signatures,
                    items.Select(x => x.AppVersion).Where(x => x.Length > 0).Distinct().ToList(), daily);
            })
            .OrderByDescending(x => x.Total).ThenByDescending(x => x.Last).ToList();
    }

    /// <summary>What the whole list shows that no single row does, most useful first and never more than four.</summary>
    public static IReadOnlyList<CrashInsight> Insights(IEnumerable<CrashEvent> events)
    {
        var list = events.ToList();
        var found = new List<CrashInsight>();
        var crashes = list.Where(x => x.Kind == CrashKind.AppCrash).ToList();

        var shared = crashes.Where(x => !Generic.Contains(x.Module)).GroupBy(x => x.Module, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Module: g.Key, Apps: g.Select(x => x.AppKey).Distinct().Count())).Where(x => x.Apps >= 3).OrderByDescending(x => x.Apps).FirstOrDefault();
        if (shared.Module is not null)
            found.Add(new($"{shared.Apps} different apps crashed in {shared.Module}",
                $"{shared.Module} is shared by those programs, so the real problem is probably in it (often a driver or a plug-in they all load) rather than in each program. Update or reinstall whatever installed it."));

        var repeat = crashes.GroupBy(x => (x.AppKey, Module: x.Module.ToLowerInvariant(), x.Code)).Select(g => (Group: g, Count: g.Count())).Where(x => x.Count >= 3).OrderByDescending(x => x.Count).FirstOrDefault();
        if (repeat.Group is not null)
        {
            var first = repeat.Group.First();
            var cause = new CrashSignature(first.Module, first.Code, first.CodeName, repeat.Count, first.Time).Label;
            found.Add(new($"{first.AppName} keeps crashing the same way", $"{repeat.Count} crashes, each in {cause}. An update, a reinstall, or turning off an add-on it uses is the likeliest fix."));
        }

        foreach (var app in crashes.GroupBy(x => x.AppKey))
        {
            var times = app.Select(x => x.Time).OrderBy(x => x).ToList();
            var burst = Enumerable.Range(0, times.Count).Select(i => times.Skip(i).TakeWhile(t => t - times[i] <= Loop).Count()).DefaultIfEmpty(0).Max();
            if (burst < 3) continue;
            found.Add(new($"{app.First().AppName} crashed {burst} times within minutes", "It probably crashes again as soon as it starts. Try starting it without restoring its last session, or reinstalling it."));
            break;
        }

        var blue = list.Count(x => x.Kind == CrashKind.BlueScreen);
        if (blue >= 2)
        {
            var common = list.Where(x => x.Kind == CrashKind.BlueScreen && x.CodeName.Length > 0).GroupBy(x => x.CodeName).OrderByDescending(g => g.Count()).FirstOrDefault();
            found.Add(new($"Windows showed a blue screen {blue} times", common is { } c && c.Count() > 1 ? $"{c.Key} came up {c.Count()} times. See its entry for what that usually means." : "Repeated blue screens usually come from a driver, memory or a disk. See each entry for the stop code."));
        }
        else if (list.Count(x => x.Kind == CrashKind.UnexpectedShutdown) >= 3)
            found.Add(new($"Windows stopped without shutting down {list.Count(x => x.Kind == CrashKind.UnexpectedShutdown)} times", "Repeated power losses or hard resets point to the power supply or battery, overheating, or a freeze that ended with the power button."));
        if (list.Count(x => x.Kind == CrashKind.DriverReset) >= 3)
            found.Add(new($"The graphics driver reset {list.Count(x => x.Kind == CrashKind.DriverReset)} times", "Update or cleanly reinstall the graphics driver, and check temperatures under load."));
        if (list.Count(x => x.Kind == CrashKind.StorageError) >= 2)
            found.Add(new("Windows reported disk errors", "Check the disk's health (SMART) and back up what matters before anything else."));

        return found.Take(4).ToList();
    }
}

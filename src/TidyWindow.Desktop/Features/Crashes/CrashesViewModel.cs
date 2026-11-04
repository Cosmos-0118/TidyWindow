using System.Diagnostics;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using TidyWindow.Next.Core;

namespace TidyWindow.Next.Desktop;

public enum CrashTab { Apps, Windows, Timeline }
public enum CrashRange { Day, Week, Month, Quarter }
public enum CrashFilter { All, Crashes, Hangs }
public enum CrashSort { Most, Recent, Name }

public sealed record CrashFact(string Label, string Value, bool Mono)
{
    public Visibility MonoVisibility => Mono ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PlainVisibility => Mono ? Visibility.Collapsed : Visibility.Visible;
}

/// <summary>One bar of the small per-day chart on an app's row.</summary>
public sealed record DayBar(double Height, double Opacity, string Tip);

public sealed record SignatureRow(string Count, string Label);

public static class CrashTime
{
    public static string Stamp(DateTimeOffset time)
    {
        var local = time.ToLocalTime();
        var today = DateTime.Now.Date;
        return local.Date == today ? $"Today {local:t}" : local.Date == today.AddDays(-1) ? $"Yesterday {local:t}" : local.Year == today.Year ? $"{local:MMM d}, {local:t}" : $"{local:d MMM yyyy}, {local:t}";
    }

    public static string Ago(DateTimeOffset time, DateTimeOffset now)
    {
        var age = now - time;
        if (age < TimeSpan.FromMinutes(1)) return "just now";
        if (age < TimeSpan.FromHours(1)) return $"{(int)age.TotalMinutes} min ago";
        if (age < TimeSpan.FromHours(24)) return age.TotalHours < 2 ? "1 hour ago" : $"{(int)age.TotalHours} hours ago";
        var days = (now.ToLocalTime().Date - time.ToLocalTime().Date).Days;
        return days <= 1 ? "yesterday" : days < 30 ? $"{days} days ago" : time.ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture);
    }
}

public sealed class CrashEventRow(CrashEvent entry, bool lightTheme) : ObservableState
{
    private bool light = lightTheme, expanded, copied, technical;
    private IReadOnlyList<CrashFact>? facts, technicalFacts;

    public CrashEvent Event { get; } = entry;
    public string Id => Event.Id;
    public string Headline => Event.Headline;
    public string Sub => string.Join(" · ", new[] { Event.Kind.Label(), CrashTime.Stamp(Event.Time), Event.Repeats > 1 ? $"{Event.Repeats} times" : "" }.Where(x => x.Length > 0));
    public string Exact => Event.Time.ToLocalTime().ToString("F");
    public string Explanation => Event.Explanation;
    public Visibility ExplanationVisibility => Explanation.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public string Repeats => $"×{Event.Repeats}";
    public Visibility RepeatsVisibility => Event.Repeats > 1 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ReportVisibility => Event.ReportFolder.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ProgramVisibility => Event.Kind.IsApp() && Event.AppPath.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DumpVisibility => Event.DumpFile.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Brush DotBrush => BrushCache.Rgb(Event.Kind switch
    {
        CrashKind.AppCrash or CrashKind.BlueScreen or CrashKind.HardwareError or CrashKind.StorageError => light ? 0xB3261E : 0xF2A59E,
        _ => light ? 0x865500 : 0xF4BF6E
    });

    public bool Expanded => expanded;
    public Visibility DetailsVisibility => expanded ? Visibility.Visible : Visibility.Collapsed;
    public string Chevron => expanded ? "" : "";
    public string CopyGlyph => copied ? "" : "";
    public string DetailsName => $"Details: {Headline}";
    public void Toggle() { expanded = !expanded; Changed(nameof(Expanded)); Changed(nameof(DetailsVisibility)); Changed(nameof(Chevron)); }
    public void SetCopied(bool value) { copied = value; Changed(nameof(CopyGlyph)); }
    public void SetLightTheme(bool value) { if (light == value) return; light = value; Changed(nameof(DotBrush)); }
    // A screen reader announces a list item by its string form.
    public override string ToString() => $"{Headline}. {Sub}";

    /// <summary>What most people need: which program, what failed and the error. Built only when the row is opened.</summary>
    public IReadOnlyList<CrashFact> Facts => facts ??= BuildFacts(primary: true);

    /// <summary>The rest, for someone filing a bug report: offsets, process, file paths, report numbers.</summary>
    public IReadOnlyList<CrashFact> TechnicalFacts => technicalFacts ??= BuildFacts(primary: false);

    public Visibility TechnicalVisibility => technical ? Visibility.Visible : Visibility.Collapsed;
    public string TechnicalLabel => technical ? "Hide technical details" : "Technical details";
    public void ToggleTechnical() { technical = !technical; Changed(nameof(TechnicalVisibility)); Changed(nameof(TechnicalLabel)); }

    private List<CrashFact> BuildFacts(bool primary)
    {
        var e = Event;
        var list = new List<CrashFact>();
        void Add(bool main, string label, string value, bool mono = false) { if (main == primary && value.Length > 0) list.Add(new(label, value, mono)); }
        Add(true, e.Kind.IsApp() ? "Program" : "Name", e.Kind.IsApp() && e.AppVersion.Length > 0 ? $"{e.App}, version {e.AppVersion}" : e.App);
        Add(true, "Failed in", e.Module.Length > 0 ? (e.ModuleVersion.Length > 0 ? $"{e.Module}, version {e.ModuleVersion}" : e.Module) : "");
        Add(true, e.Kind == CrashKind.BlueScreen ? "Stop code" : "Error code", e.Code.Length > 0 ? (e.CodeName.Length > 0 ? $"{e.Code} · {e.CodeName}" : e.Code) : "", true);
        Add(true, "Exception", e.Managed);
        Add(true, "Details", e.Detail, e.Managed.Length > 0);
        Add(false, "When", Exact);
        Add(false, "Offset", e.Offset, true);
        Add(false, "Process", e.ProcessId);
        Add(false, "Program file", e.AppPath, true);
        Add(false, "Module file", e.ModulePath, true);
        Add(false, "Memory dump", e.DumpFile, true);
        Add(false, "Recorded by", $"{e.Source}, event {e.EventId}");
        Add(false, "Report", e.ReportId, true);
        return list;
    }
}

public sealed class CrashAppRow(CrashApp app, int max, bool lightTheme) : ObservableState
{
    private const int FirstEvents = 8;
    private CrashApp current = app;
    private int biggest = max;
    private bool expanded, showAll, copied, light = lightTheme;
    private DateTimeOffset now = DateTimeOffset.Now;
    private Microsoft.UI.Xaml.Media.ImageSource? icon;

    public CrashApp App => current;
    public string Key => current.Key;
    public string Name => current.Name;
    public string Path => current.Path;
    public ImageSource? Icon => icon;
    public IconQuery IconQuery => new(current.Name, ExePath: current.Path);
    public string Count => current.Total.ToString("N0");
    public double Bar => biggest == 0 ? 0 : (double)current.Total / biggest;
    public string Sub => string.Join(" · ", new[]
    {
        current.Crashes > 0 ? $"{current.Crashes:N0} crash{(current.Crashes == 1 ? "" : "es")}" : "",
        current.Hangs > 0 ? $"{current.Hangs:N0} not responding" : "",
        current.StartFailures > 0 ? $"{current.StartFailures:N0} could not start" : "",
        $"last {CrashTime.Ago(current.Last, now)}"
    }.Where(x => x.Length > 0));
    public string AutomationName => $"{Name}: {current.Total} problems";
    public string DetailsName => $"Details for {Name}";
    public Visibility PathVisibility => Path.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public string Versions => current.Versions.Count == 0 ? "" : current.Versions.Count == 1 ? $"Version {current.Versions[0]}" : $"Versions seen: {string.Join(", ", current.Versions.Take(5))}{(current.Versions.Count > 5 ? ", …" : "")}";
    public Visibility VersionsVisibility => Versions.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public IReadOnlyList<SignatureRow> Signatures => current.Signatures.Select(x => new SignatureRow($"{x.Count}×", x.Label)).ToList();
    public Visibility SignaturesVisibility => current.Signatures.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public IReadOnlyList<DayBar> Days
    {
        get
        {
            var peak = Math.Max(1, current.Daily.Max());
            var today = now.Date;
            return current.Daily.Select((n, i) =>
            {
                var day = today.AddDays(i - (current.Daily.Count - 1));
                return new DayBar(n == 0 ? 2 : 4 + 18.0 * n / peak, n == 0 ? 0.3 : 1, n == 0 ? $"None on {day:ddd d MMM}" : $"{n} on {day:ddd d MMM}");
            }).ToList();
        }
    }

    public bool Expanded => expanded;
    public Visibility DetailsVisibility => expanded ? Visibility.Visible : Visibility.Collapsed;
    public string Chevron => expanded ? "" : "";
    public BulkObservableCollection<CrashEventRow> Events { get; } = new();
    public string MoreText => $"Show all {current.Events.Count:N0} events";
    public Visibility MoreVisibility => expanded && !showAll && current.Events.Count > FirstEvents ? Visibility.Visible : Visibility.Collapsed;

    public string CopyGlyph => copied ? "" : "";
    public void SetCopied(bool value) { copied = value; Changed(nameof(CopyGlyph)); }
    public void SetIcon(ImageSource? image) { icon = image; Changed(nameof(Icon)); }
    public void SetLightTheme(bool value) { light = value; foreach (var row in Events) row.SetLightTheme(value); }
    public void Toggle() { expanded = !expanded; LoadEvents(); Notify(nameof(Expanded), nameof(DetailsVisibility), nameof(Chevron), nameof(MoreVisibility)); }
    public void ShowAll() { showAll = true; LoadEvents(); Notify(nameof(MoreVisibility)); }

    public void Update(CrashApp app, int max, DateTimeOffset clock)
    {
        current = app; biggest = max; now = clock;
        LoadEvents();
        Notify(nameof(App), nameof(Name), nameof(Path), nameof(Count), nameof(Bar), nameof(Sub), nameof(AutomationName), nameof(DetailsName), nameof(PathVisibility), nameof(Versions), nameof(VersionsVisibility),
            nameof(Signatures), nameof(SignaturesVisibility), nameof(Days), nameof(MoreText), nameof(MoreVisibility));
    }

    // Built only while the row is open, and kept in place when a refresh changes the list underneath.
    private void LoadEvents()
    {
        if (!expanded) { Events.Reset([]); return; }
        var known = Events.ToDictionary(x => x.Id);
        Events.Sync(current.Events.Take(showAll ? 500 : FirstEvents).Select(x => known.TryGetValue(x.Id, out var row) ? row : new CrashEventRow(x, light)), x => x.Id);
    }

    private void Notify(params string[] names) { foreach (var name in names) Changed(name); }
    public override string ToString() => $"{Name}. {Sub}";
}

/// <summary>
/// Crashes, freezes and Windows problems, read from Windows' own records when the page is opened or refreshed. Nothing is watched or polled: the history
/// is a small file, a scan reads only what is new on a low-priority thread, and the lists only build the rows that are open.
/// </summary>
public sealed class CrashesViewModel(string folder, ICrashEventSource source, ActivityLog log, ToastCenter toasts, IconService? iconService = null, Func<DateTimeOffset>? clock = null) : ObservableState, IDisposable
{
    private const int RowLimit = 500, StaleMinutes = 2;
    private readonly CancellationTokenSource lifetime = new();
    private readonly IconService icons = iconService ?? IconService.Shared;
    private readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.Now);
    private readonly Dictionary<string, CrashAppRow> appRows = [];
    private readonly Dictionary<string, CrashEventRow> eventRows = [];
    private CrashHistory? history;
    private CrashScanner? scanner;
    private CrashSearchIndex? index;
    private IReadOnlyList<CrashEvent> merged = [];
    private CancellationTokenSource? pending;
    private Task? loading;
    private bool scanning, loaded, light, listening;
    private string query = "", status = "", problem = "";
    private DateTimeOffset lastScan = DateTimeOffset.MinValue;
    private CrashTab tab = CrashTab.Apps;
    private CrashRange range = CrashRange.Month;
    private CrashFilter filter = CrashFilter.All;
    private CrashSort sort = CrashSort.Most;
    private int unseen, windowsCount, appCount, timelineCount, hiddenRows;
    private IReadOnlyList<CrashEvent> shown = [];

    public BulkObservableCollection<CrashAppRow> Apps { get; } = new();
    public BulkObservableCollection<CrashEventRow> WindowsEvents { get; } = new();
    public BulkObservableCollection<CrashEventRow> Timeline { get; } = new();
    public BulkObservableCollection<CrashInsight> Insights { get; } = new();

    /// <summary>Problems recorded since the page was last opened. Drives the dot on the sidebar.</summary>
    public int Unseen { get => unseen; private set { if (Set(ref unseen, value)) { Changed(nameof(UnseenText)); Changed(nameof(UnseenVisibility)); } } }
    public string UnseenText => unseen == 0 ? "" : unseen > 99 ? "99+" : unseen.ToString();
    public Visibility UnseenVisibility => unseen > 0 ? Visibility.Visible : Visibility.Collapsed;

    public bool Loaded => loaded;
    public bool Busy => scanning || !loaded;
    public bool CanScan => !scanning && loaded;
    public string ScanLabel => scanning ? "Checking…" : "Refresh";
    public bool Overlay => !loaded || scanning && history?.Count == 0;
    public string OverlayText => loaded ? "Reading Windows’ crash records…" : "Opening saved history…";
    public string Status => status;
    public string Problem => problem;
    public Visibility ProblemVisibility => problem.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public string StorageWarning => history?.StorageError is { } error ? "History can’t be saved to disk, so it will only last until TidyWindow closes. " + error : "";
    public Visibility StorageWarningVisibility => StorageWarning.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public string AppsTab => loaded ? appCount.ToString("N0") : "–";
    public string WindowsTab => loaded ? windowsCount.ToString("N0") : "–";
    public string TimelineTab => loaded ? timelineCount.ToString("N0") : "–";

    public Visibility AppsVisibility => tab == CrashTab.Apps ? Visibility.Visible : Visibility.Collapsed;
    public Visibility WindowsVisibility => tab == CrashTab.Windows ? Visibility.Visible : Visibility.Collapsed;
    public Visibility TimelineVisibility => tab == CrashTab.Timeline ? Visibility.Visible : Visibility.Collapsed;
    public Visibility FilterVisibility => tab == CrashTab.Windows ? Visibility.Collapsed : Visibility.Visible;
    public Visibility SortVisibility => tab == CrashTab.Apps ? Visibility.Visible : Visibility.Collapsed;
    public Visibility InsightsVisibility => Insights.Count > 0 && ParsedQuery.IsEmpty ? Visibility.Visible : Visibility.Collapsed;

    public Visibility EmptyVisibility => loaded && ActiveCount == 0 ? Visibility.Visible : Visibility.Collapsed;
    private bool Unfiltered => ParsedQuery.IsEmpty && filter == CrashFilter.All;
    public string EmptyGlyph => merged.Count == 0 || Unfiltered ? "" : "";
    public string EmptyTitle => merged.Count == 0 ? "No crashes found"
        : !Unfiltered ? "Nothing matches"
        : tab switch { CrashTab.Apps => "No app crashes in this time range", CrashTab.Windows => "No Windows problems in this time range", _ => "Nothing recorded in this time range" };
    public string EmptyText => merged.Count == 0
        ? "Windows has not recorded any app crashes, freezes or serious system problems in the time it keeps. That is good news."
        : Unfiltered ? "Choose a longer time range to look further back."
        : "Try fewer words, a longer time range, or remove a filter. Typos are forgiven; app:, kind:, code: and since: narrow a search.";
    public string MoreRowsNote => hiddenRows > 0 ? $"Showing the newest {RowLimit:N0} of {RowLimit + hiddenRows:N0}. Search or shorten the time range to narrow the list." : "";
    public Visibility MoreRowsVisibility => hiddenRows > 0 && tab != CrashTab.Apps ? Visibility.Visible : Visibility.Collapsed;
    public string Understood => ParsedQuery.Chips.Count == 0 ? "" : "Looking for " + string.Join(" · ", ParsedQuery.Chips);
    public Visibility UnderstoodVisibility => ParsedQuery.Chips.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public string Query { get => query; set { if (Set(ref query, value)) Schedule(); } }
    public CrashTab Tab { get => tab; set { if (Set(ref tab, value)) Apply(); } }
    public CrashRange Range { get => range; set { if (Set(ref range, value)) Apply(); } }
    public CrashFilter Filter { get => filter; set { if (Set(ref filter, value)) Apply(); } }
    public CrashSort Sort { get => sort; set { if (Set(ref sort, value)) Apply(); } }

    private CrashQuery ParsedQuery => CrashQuery.Parse(query, now());
    private int ActiveCount => tab switch { CrashTab.Apps => Apps.Count, CrashTab.Windows => WindowsEvents.Count, _ => Timeline.Count };
    private DateTimeOffset RangeStart => now() - range switch { CrashRange.Day => TimeSpan.FromDays(1), CrashRange.Week => TimeSpan.FromDays(7), CrashRange.Quarter => TimeSpan.FromDays(90), _ => TimeSpan.FromDays(30) };

    public void SetLightTheme(bool value)
    {
        if (light == value) return;
        light = value;
        foreach (var row in eventRows.Values) row.SetLightTheme(value);
        foreach (var row in appRows.Values) row.SetLightTheme(value);
    }

    /// <summary>Called when the page is shown: opens the saved history at once, reads anything new unless that was done a moment ago, and marks everything seen.</summary>
    public async Task OpenAsync()
    {
        await EnsureLoadedAsync();
        MarkSeen();
        if (now() - lastScan > TimeSpan.FromMinutes(StaleMinutes)) await ScanAsync(full: false, quiet: false);
    }

    /// <summary>A one-time look at launch, so the sidebar can say something new happened. Reads the same small amount a refresh does and shows nothing but the count.</summary>
    public async Task CheckQuietlyAsync()
    {
        await EnsureLoadedAsync();
        await ScanAsync(full: false, quiet: true);
        UpdateUnseen();
    }

    public Task RefreshAsync() => ScanAsync(full: false, quiet: false);
    public Task ReadEverythingAgainAsync() => ScanAsync(full: true, quiet: false);

    public async Task ClearHistoryAsync()
    {
        await EnsureLoadedAsync();
        history!.Clear();
        lastScan = DateTimeOffset.MinValue;
        await RebuildAsync();
        status = "Saved history cleared";
        Apply();
        Changed(nameof(Status));
        log.Write(ActivityLevel.Info, ActivityArea.App, "Saved crash history cleared", "Windows’ own event log was not touched.");
        await ScanAsync(full: false, quiet: false);
    }

    /// <summary>Closes an observation for good. It comes back only if the situation changes enough to be worded differently.</summary>
    public void DismissInsight(CrashInsight insight)
    {
        history?.Dismiss(insight.Title);
        Insights.Remove(insight);
        Changed(nameof(InsightsVisibility));
    }

    private Task EnsureLoadedAsync() => loading ??= LoadAsync();

    private async Task LoadAsync()
    {
        try
        {
            history = await Task.Run(() => new CrashHistory(folder), lifetime.Token);
            scanner = new CrashScanner(source, history);
            await RebuildAsync();
        }
        catch (OperationCanceledException) { return; }
        loaded = true;
        if (history.SeenUpTo is null) history.MarkSeen(now());
        NotifyState();
        Apply();
    }

    private async Task RebuildAsync()
    {
        var stored = history!.All();
        (merged, index) = await Task.Run(() =>
        {
            var events = CrashDigest.Merge(stored);
            return (events, new CrashSearchIndex(events));
        }, lifetime.Token);
    }

    private async Task ScanAsync(bool full, bool quiet)
    {
        if (scanning || lifetime.IsCancellationRequested || scanner is null || history is null) return;
        scanning = true; NotifyState();
        try
        {
            var result = await LowImpact.RunAsync(() => scanner.Scan(lifetime.Token, full));
            lastScan = now();
            if (result.Added > 0 || full) await RebuildAsync();
            problem = result.Problems.Count == 0 ? "" : string.Join(" ", result.Problems.Distinct());
            status = $"Checked {now():h:mm tt} · {merged.Count:N0} events kept";
            if (result.Problems.Count > 0) log.Write(ActivityLevel.Warning, ActivityArea.App, "Crash records could not all be read", problem);
            if (!quiet) Apply(); else UpdateUnseen();
            foreach (var name in new[] { nameof(Status), nameof(Problem), nameof(ProblemVisibility), nameof(StorageWarning), nameof(StorageWarningVisibility) }) Changed(name);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!quiet) toasts.Show("crash-scan", ToastKind.Warning, "Crash records could not be read", ToastCenter.Flat(ex.Message));
            log.Write(ActivityLevel.Warning, ActivityArea.App, "Crash scan could not finish", ex.Message);
        }
        finally { scanning = false; NotifyState(); }
    }

    private void MarkSeen()
    {
        history?.MarkSeen(now());
        Unseen = 0;
    }

    private void UpdateUnseen()
    {
        var seen = history?.SeenUpTo ?? now();
        Unseen = merged.Count(x => x.Time > seen && x.Kind is not CrashKind.ServiceFailure);
    }

    private async void Schedule()
    {
        pending?.Cancel();
        var mine = pending = new CancellationTokenSource();
        // Typing is answered after a short pause, so a fast typist does not rebuild the list on every key.
        try { await Task.Delay(140, mine.Token); }
        catch (TaskCanceledException) { return; }
        if (!lifetime.IsCancellationRequested) Apply();
    }

    private void NotifyState()
    {
        foreach (var name in new[] { nameof(Loaded), nameof(Busy), nameof(CanScan), nameof(ScanLabel), nameof(Overlay), nameof(OverlayText), nameof(AppsTab), nameof(WindowsTab), nameof(TimelineTab) }) Changed(name);
    }

    private void Apply()
    {
        if (!loaded || index is null) return;
        var stamp = now();
        var parsed = ParsedQuery;
        var matches = index.Search(parsed, RangeStart);
        shown = filter == CrashFilter.All ? matches : matches.Where(x => x.Kind == (filter == CrashFilter.Crashes ? CrashKind.AppCrash : CrashKind.AppHang)).ToList();

        windowsCount = shown.Count(x => x.Kind.IsWindows());
        timelineCount = shown.Count;

        var apps = CrashDigest.Apps(shown, stamp, 14);
        appCount = apps.Count;
        var max = apps.Count == 0 ? 0 : apps.Max(x => x.Total);
        IEnumerable<CrashApp> ordered = sort switch
        {
            CrashSort.Recent => apps.OrderByDescending(x => x.Last),
            CrashSort.Name => apps.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase),
            _ => apps
        };
        var appList = ordered.Select(x => Reuse(x, max, stamp)).ToList();
        Apps.Sync(appList, x => x.Key);

        var windows = shown.Where(x => x.Kind.IsWindows()).ToList();
        WindowsEvents.Sync(windows.Take(RowLimit).Select(EventRow), x => x.Id);
        Timeline.Sync(shown.Take(RowLimit).Select(EventRow), x => x.Id);
        hiddenRows = tab switch { CrashTab.Windows => Math.Max(0, windows.Count - RowLimit), CrashTab.Timeline => Math.Max(0, shown.Count - RowLimit), _ => 0 };

        // The row cache only holds rows that can still be shown.
        var alive = new HashSet<string>(shown.Select(x => x.Id));
        foreach (var gone in eventRows.Keys.Where(x => !alive.Contains(x)).ToList()) eventRows.Remove(gone);
        var aliveApps = new HashSet<string>(apps.Select(x => x.Key));
        foreach (var gone in appRows.Keys.Where(x => !aliveApps.Contains(x)).ToList()) appRows.Remove(gone);

        var start = RangeStart;
        Insights.Sync(CrashDigest.Insights(merged.Where(x => x.Time >= start)).Where(x => history?.IsDismissed(x.Title) != true), x => x.Title);

        foreach (var name in new[]
        {
            nameof(AppsTab), nameof(WindowsTab), nameof(TimelineTab),
            nameof(AppsVisibility), nameof(WindowsVisibility), nameof(TimelineVisibility), nameof(FilterVisibility), nameof(SortVisibility), nameof(InsightsVisibility),
            nameof(EmptyVisibility), nameof(EmptyGlyph), nameof(EmptyTitle), nameof(EmptyText), nameof(MoreRowsNote), nameof(MoreRowsVisibility), nameof(Understood), nameof(UnderstoodVisibility), nameof(Overlay)
        }) Changed(name);
        LoadIcons();
    }

    private CrashAppRow Reuse(CrashApp app, int max, DateTimeOffset clock)
    {
        if (appRows.TryGetValue(app.Key, out var row)) { row.Update(app, max, clock); return row; }
        row = new CrashAppRow(app, max, light);
        if (icons.TryGet(row.IconQuery, out var cached) && cached is not null) row.SetIcon(cached);
        appRows[app.Key] = row;
        return row;
    }

    private CrashEventRow EventRow(CrashEvent e)
    {
        if (!eventRows.TryGetValue(e.Id, out var row)) eventRows[e.Id] = row = new CrashEventRow(e, light);
        return row;
    }

    // Real icons make a list of unfamiliar file names readable at a glance. They arrive a moment after the list.
    private void LoadIcons()
    {
        if (!listening) { IconService.DarkChanged += ReloadIcons; listening = true; }
        foreach (var row in Apps.Where(x => x.Icon is null).Take(60)) _ = LoadIconAsync(row);
    }

    private void ReloadIcons() { foreach (var row in Apps.Take(60)) _ = LoadIconAsync(row); }

    private async Task LoadIconAsync(CrashAppRow row)
    {
        try
        {
            var image = await icons.LoadAsync(row.IconQuery, lifetime.Token);
            if (image is not null) row.SetIcon(image);
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Everything shown on the current tab as plain text, for a support request.</summary>
    public string Report()
    {
        var text = new System.Text.StringBuilder();
        text.AppendLine($"TidyWindow crash report, {now():yyyy-MM-dd HH:mm}");
        text.AppendLine($"{shown.Count(x => x.Kind == CrashKind.AppCrash)} crashes, {shown.Count(x => x.Kind == CrashKind.AppHang)} not responding, {windowsCount} Windows problems, {appCount} apps");
        if (ParsedQuery.Chips.Count > 0) text.AppendLine("Search: " + query);
        text.AppendLine();
        text.Append(tab == CrashTab.Apps ? string.Join("\n", Apps.Take(30).Select(x => CrashReport.Format(x.App))) : CrashReport.Format((tab == CrashTab.Windows ? shown.Where(x => x.Kind.IsWindows()) : shown).Take(200)));
        return text.ToString();
    }

    public void ShowFile(string path, string missing)
    {
        if (path.Length == 0 || !File.Exists(path)) { toasts.Show("crash-open", ToastKind.Info, missing); return; }
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { toasts.Show("crash-open", ToastKind.Warning, "Could not open Explorer", ToastCenter.Flat(ex.Message)); }
    }

    public void OpenReport(CrashEvent e)
    {
        if (e.ReportFolder.Length == 0 || !Directory.Exists(e.ReportFolder)) { toasts.Show("crash-open", ToastKind.Info, "Windows no longer keeps this report", "It removes old reports on its own. The details TidyWindow saved are still here."); return; }
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{e.ReportFolder}\"") { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { toasts.Show("crash-open", ToastKind.Warning, "Could not open Explorer", ToastCenter.Flat(ex.Message)); }
    }

    public void SearchWeb(CrashEvent e)
    {
        try { Process.Start(new ProcessStartInfo("https://duckduckgo.com/?q=" + Uri.EscapeDataString(CrashReport.SearchText(e))) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { toasts.Show("crash-open", ToastKind.Warning, "Could not open the browser", ToastCenter.Flat(ex.Message)); }
    }

    public void Dispose()
    {
        IconService.DarkChanged -= ReloadIcons;
        pending?.Cancel();
        lifetime.Cancel();
        lifetime.Dispose();
    }
}

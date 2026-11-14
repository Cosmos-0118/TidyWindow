using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using TidyWindow.Next.Core;

namespace TidyWindow.Next.Desktop;

public sealed class LogRow(ActivityEntry entry, bool lightTheme) : ObservableState
{
    private bool light = lightTheme;
    private bool expanded;
    private bool copied;
    public ActivityEntry Entry { get; private set; } = entry;
    public string Message => Entry.Message;
    public string Detail => Entry.Detail ?? "";
    public bool HasDetail => Entry.Detail is not null;
    public Visibility DetailVisibility => HasDetail && expanded ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ChevronVisibility => HasDetail ? Visibility.Visible : Visibility.Collapsed;
    // Segoe Fluent Icons code points, written as escapes: the raw private-use characters are invisible and were once lost in an edit.
    public string CopyGlyph => copied ? "" : ""; // CheckMark : Copy
    public string Chevron => expanded ? "" : ""; // ChevronUp : ChevronDown
    public string Exact => Entry.Time.ToLocalTime().ToString("F");
    public string Meta => string.Join(" · ", new[] { Entry.Area.ToString(), Stamp(Entry.Time) });

    // Every entry is a card: the level sets its colour, icon and label, and the most serious ones are the loudest.
    public string Label => Entry.Level.ToString();
    public string CountText => $"×{Entry.Count}";
    public Visibility CountVisibility => Entry.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
    // Info / CompletedSolid / Warning / ErrorBadge in Segoe Fluent Icons.
    public string BadgeGlyph => Entry.Level switch { ActivityLevel.Success => "\uE930", ActivityLevel.Warning => "\uE7BA", ActivityLevel.Error => "\uEA39", _ => "\uE946" };
    public Brush CardBrush => Tone(light ? (0xF7F7F8, 0xF2FAF5, 0xFDF6E9, 0xFDF2F1) : (0x1E1E21, 0x1A2620, 0x2A2216, 0x2B1B1A));
    public Brush CardBorder => Tone(light ? (0xE4E4E7, 0xC9E6D4, 0xEED9AE, 0xF3CBC8) : (0x2F2F34, 0x2B4A38, 0x57431F, 0x5C2C29));
    public Brush IconBrush => Tone(light ? (0xE9E9EC, 0xD9EFE2, 0xF9E6BE, 0xFBDAD7) : (0x2E2E33, 0x25402F, 0x473618, 0x4A2321));
    public Brush ChipBrush => Tone(light ? (0xEDEDF0, 0xE3F2E9, 0xF8EBCF, 0xFAE3E1) : (0x2A2A2E, 0x22362B, 0x3A2E19, 0x3A2220));
    public Brush OutputBrush => BrushCache.Rgb(light ? 0xFFFFFF : 0x161616);

    // The first line of an update or install failure is "Manager · package id", then "Exit code: n", then the reason and the manager's output.
    public string Source => Lines().FirstOrDefault(x => x.Contains(" · ") && !x.StartsWith("Exit code", StringComparison.OrdinalIgnoreCase)) ?? "";
    public string ExitCode => Lines().FirstOrDefault(x => x.StartsWith("Exit code:", StringComparison.OrdinalIgnoreCase))?["Exit code:".Length..].Trim() is { Length: > 0 } code ? "Exit " + code : "";
    public Visibility SourceVisibility => Source.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ExitCodeVisibility => ExitCode.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public string Output => string.Join("\n", Lines().Where(x => x != Source && !x.StartsWith("Exit code:", StringComparison.OrdinalIgnoreCase)));
    public Visibility OutputVisibility => Output.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    private IEnumerable<string> Lines() => Detail.Split('\n').Select(x => x.TrimEnd()).Where(x => x.Length > 0);
    private SolidColorBrush Tone((int Info, int Success, int Warning, int Error) c) => BrushCache.Rgb(Entry.Level switch { ActivityLevel.Success => c.Success, ActivityLevel.Warning => c.Warning, ActivityLevel.Error => c.Error, _ => c.Info });
    public Brush DotBrush => BrushCache.Rgb(Entry.Level switch
    {
        ActivityLevel.Success => light ? 0x1C703F : 0xA4E8C1,
        ActivityLevel.Warning => light ? 0x865500 : 0xF4BF6E,
        ActivityLevel.Error => light ? 0xB3261E : 0xF2A59E,
        _ => light ? 0x626262 : 0xA4A4AD
    });

    public void Toggle() { if (!HasDetail) return; expanded = !expanded; Changed(nameof(DetailVisibility)); Changed(nameof(Chevron)); }
    public void SetCopied(bool value) { copied = value; Changed(nameof(CopyGlyph)); }
    public void SetLightTheme(bool value) { light = value; foreach (var name in new[] { nameof(DotBrush), nameof(CardBrush), nameof(CardBorder), nameof(IconBrush), nameof(ChipBrush), nameof(OutputBrush) }) Changed(name); }
    public void Update(ActivityEntry value)
    {
        Entry = value;
        foreach (var name in new[] { nameof(Message), nameof(Detail), nameof(HasDetail), nameof(DetailVisibility), nameof(ChevronVisibility), nameof(Chevron), nameof(Exact), nameof(Meta), nameof(DotBrush), nameof(CountText), nameof(CountVisibility), nameof(Source), nameof(ExitCode), nameof(SourceVisibility), nameof(ExitCodeVisibility), nameof(Output), nameof(OutputVisibility) }) Changed(name);
    }
    // Screen readers announce a list item by its string form.
    public override string ToString() => $"{Entry.Level}: {Message}. {Meta}";

    private static string Stamp(DateTimeOffset time)
    {
        var local = time.ToLocalTime();
        var today = DateTime.Now.Date;
        return local.Date == today ? $"Today {local:t}" : local.Date == today.AddDays(-1) ? $"Yesterday {local:t}" : local.Year == today.Year ? $"{local:MMM d}, {local:t}" : $"{local:d MMM yyyy}, {local:t}";
    }
}

public sealed class LogsViewModel : ObservableState, IDisposable
{
    /// <summary>How long typing in the search box is left to settle before the rows are rebuilt.</summary>
    public static readonly TimeSpan TypingSettle = TimeSpan.FromMilliseconds(150);
    private readonly ActivityLog log;
    private readonly Action<Action> post;
    private readonly TimeSpan settle;
    private Dictionary<string, LogRow> byId = [];
    private ActivityFilter filter = new();
    private CancellationTokenSource? typing;
    private bool light;
    /// <summary>Newest first. Swapped as a whole when the filter changes, so the list control hears one change rather than one per row.</summary>
    public BulkObservableCollection<LogRow> Rows { get; } = [];

    /// <param name="post">Runs an action on the UI thread; the log can be written from any thread.</param>
    /// <param name="settle">How long typed text waits before the list is rebuilt; zero rebuilds at once. <see cref="TypingSettle"/> when not given.</param>
    public LogsViewModel(ActivityLog log, Action<Action> post, TimeSpan? settle = null)
    {
        this.log = log; this.post = post; this.settle = settle ?? TypingSettle;
        log.Written += OnWritten; log.Cleared += OnCleared;
        Reload();
    }

    public string Summary => Rows.Count == log.Count ? Plural(Rows.Count) : $"{Rows.Count} of {Plural(log.Count)}";
    public string StorageWarning => log.StorageError is { } error ? "Activity can’t be saved to disk, so it will be lost when TidyWindow closes. " + error : "";
    public Visibility StorageWarningVisibility => log.StorageError is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility EmptyVisibility => Rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ListVisibility => Rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    public string EmptyTitle => log.Count == 0 ? "Nothing to report" : "No matching entries";
    public string EmptyDetail => log.Count == 0 ? "Installs, setup results and problems appear here. Routine activity is not recorded." : "Change the filters to see more.";
    public IReadOnlyList<ActivityEntry> Visible => Rows.Select(x => x.Entry).Reverse().ToArray();

    /// <summary>
    /// Narrows the list. A change of level or area applies at once. Typed text waits until typing pauses, because rebuilding hundreds of
    /// rows on every keystroke is what makes a search box feel stuck; the last text typed is the one that is applied.
    /// </summary>
    public void SetFilter(ActivityFilter value)
    {
        typing?.Cancel(); typing = null;
        var typed = value with { Text = filter.Text } == filter;
        filter = value;
        if (!typed || settle <= TimeSpan.Zero) { Reload(); return; }
        var wait = typing = new CancellationTokenSource();
        _ = Task.Delay(settle, wait.Token).ContinueWith(_ => post(() => { if (ReferenceEquals(typing, wait)) { typing = null; Reload(); } }), TaskContinuationOptions.OnlyOnRanToCompletion);
    }

    public void SetLightTheme(bool value) { if (light == value) return; light = value; foreach (var row in Rows) row.SetLightTheme(value); }

    private static string Plural(int count) => count == 1 ? "1 entry" : $"{count} entries";
    private void OnWritten(ActivityEntry entry) => post(() => Apply(entry));
    private void OnCleared() => post(Reload);

    // A row that is still shown keeps its instance, so an entry the person opened stays open while the filter changes around it.
    private void Reload()
    {
        var kept = new Dictionary<string, LogRow>();
        var rows = new List<LogRow>();
        foreach (var entry in log.Snapshot().Reverse().Where(filter.Matches))
        {
            if (byId.TryGetValue(entry.Id, out var row)) { if (!ReferenceEquals(row.Entry, entry)) row.Update(entry); }
            else row = new LogRow(entry, light);
            kept[entry.Id] = row; rows.Add(row);
        }
        byId = kept;
        Rows.Reset(rows);
        Refreshed();
    }

    // A repeat updates its row and moves it to the top rather than adding another.
    private void Apply(ActivityEntry entry)
    {
        byId.TryGetValue(entry.Id, out var row);
        if (!filter.Matches(entry))
        {
            if (row is not null) { Rows.Remove(row); byId.Remove(entry.Id); }
        }
        else if (row is null)
        {
            row = new LogRow(entry, light);
            byId[entry.Id] = row; Rows.Insert(0, row);
        }
        else
        {
            row.Update(entry);
            var index = Rows.IndexOf(row);
            if (index > 0) Rows.Move(index, 0);
        }
        Refreshed();
    }

    private void Refreshed()
    {
        foreach (var name in new[] { nameof(Summary), nameof(StorageWarning), nameof(StorageWarningVisibility), nameof(EmptyVisibility), nameof(ListVisibility), nameof(EmptyTitle), nameof(EmptyDetail) }) Changed(name);
    }

    public void Dispose() { typing?.Cancel(); log.Written -= OnWritten; log.Cleared -= OnCleared; }
}

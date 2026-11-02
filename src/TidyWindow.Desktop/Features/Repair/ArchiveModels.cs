using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TidyWindow.Next.Core;

namespace TidyWindow.Next.Desktop;

/// <summary>The label above one day, week or month of the list, with how much is under it.</summary>
public sealed record ArchiveHeader(string Label, int Count, string Size)
{
    public string Right => (Count == 1 ? "1 item" : $"{Count:N0} items") + (Size.Length > 0 ? " · " + Size : "");
}

/// <summary>The colour of a label in an opened row. One colour per kind of thing, so a long list can be read at a glance.</summary>
public enum Tone { None, Blue, Violet, Green, Amber }

/// <summary>One line of an opened row: a coloured label ("Registry key") and what it is about (the path).</summary>
public sealed record DetailLine(string Label, string Text, Tone Tone)
{
    public const string Down = "";
    public const string Up = "";
}

/// <summary>Picks the colours of an opened row's line from its tone.</summary>
public sealed class DetailTemplateSelector : DataTemplateSelector
{
    public DataTemplate? Plain { get; set; }
    public DataTemplate? Blue { get; set; }
    public DataTemplate? Violet { get; set; }
    public DataTemplate? Green { get; set; }
    public DataTemplate? Amber { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) => item is DetailLine line
        ? line.Tone switch { Tone.Blue => Blue, Tone.Violet => Violet, Tone.Green => Green, Tone.Amber => Amber, _ => Plain }
        : Plain;
    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) => SelectTemplateCore(item);
}

/// <summary>One finished repair, kept so it can be undone later.</summary>
public sealed class HistoryRow : ObservableState
{
    private bool open;

    public HistoryRow(RepairBatch batch)
    {
        Batch = batch;
        var names = batch.Keys.Select(x => x.Path[(x.Path.LastIndexOf('\\') + 1)..])
            .Concat(batch.Shortcuts.Where(x => x.Data is not null).Select(x => Path.GetFileNameWithoutExtension(x.Path)))
            .Concat(batch.SetAside.Select(x => Path.GetFileName(x.Original))).ToList();
        var kinds = (batch.Keys.Count > 0 ? ArchiveKinds.Registry : 0) | (batch.PutBack.Count > 0 ? ArchiveKinds.Setup : 0)
            | (batch.Shortcuts.Any(x => x.Data is not null) ? ArchiveKinds.Shortcut : 0) | (batch.SetAside.Count > 0 || batch.Shortcuts.Any(x => x.Data is null) ? ArchiveKinds.Folder : 0);
        var paths = batch.Keys.Select(x => x.Path).Concat(batch.PutBack).Concat(batch.Shortcuts.Select(x => x.Path)).Concat(batch.SetAside.Select(x => x.Original));
        Weight = batch.Keys.Count + batch.PutBack.Count + batch.Shortcuts.Count + batch.SetAside.Count;
        Machine = batch.Keys.Any(x => InstallRepair.IsMachine(x.Path)) || batch.SetAside.Any(x => RepairFolders.IsMachine(x.Original))
            || batch.Shortcuts.Any(x => ShortcutPlaces.Roots().Any(r => r.Machine && ShortcutPlaces.Under(x.Path, r.Path)));
        Entry = new(batch.Title, string.Join("; ", names), string.Join("; ", paths), batch.Created, kinds, -1, Machine);
    }

    public RepairBatch Batch { get; }
    public ArchiveEntry Entry { get; }
    /// <summary>How much the repair changed, for sorting by "Most changes".</summary>
    public int Weight { get; }
    public bool Machine { get; }
    public string Title => Batch.Title;
    public string When => Batch.Created.ToLocalTime().ToString("d MMM yyyy, h:mm tt");
    public string Time => Batch.Created.ToLocalTime().ToString("h:mm tt");
    public string Sub => $"{Count} · {Time}";
    public string Count => Describe(Batch.Keys.Count, Batch.PutBack.Count, Batch.Shortcuts.Count(x => x.Data is not null), Batch.Shortcuts.Count(x => x.Data is null), Batch.SetAside.Count);
    public Visibility AdminVisibility => Machine ? Visibility.Visible : Visibility.Collapsed;
    public string AutomationName => $"Undo {Title}, {When}";

    public bool IsOpen { get => open; set { if (Set(ref open, value)) { Changed(nameof(DetailsVisibility)); Changed(nameof(Chevron)); Changed(nameof(ToggleName)); } } }
    public Visibility DetailsVisibility => open ? Visibility.Visible : Visibility.Collapsed;
    public string Chevron => open ? DetailLine.Up : DetailLine.Down;
    public string ToggleName => open ? $"Hide what {Title} changed" : $"Show what {Title} changed";

    /// <summary>What the repair changed, one line each (the first sixty), each kind in its own colour. Built only when the row is opened.</summary>
    public IReadOnlyList<DetailLine> Lines => lines ??= BuildLines();
    private IReadOnlyList<DetailLine>? lines;

    private List<DetailLine> BuildLines()
    {
        var all = new List<DetailLine>();
        all.AddRange(Batch.Keys.Select(x => new DetailLine("Registry key", x.Path, Tone.Blue)));
        all.AddRange(Batch.PutBack.Select(x => new DetailLine("Setup program", x, Tone.Violet)));
        all.AddRange(Batch.Shortcuts.Where(x => x.Data is not null).Select(x => new DetailLine("Shortcut", x.Path, Tone.Green)));
        all.AddRange(Batch.Shortcuts.Where(x => x.Data is null).Select(x => new DetailLine("Empty folder", x.Path, Tone.Amber)));
        all.AddRange(Batch.SetAside.Select(x => new DetailLine("Kept folder", x.Original, Tone.Amber)));
        var shown = all.Take(60).ToList();
        if (all.Count > 60) shown.Add(new DetailLine("", $"…and {all.Count - 60:N0} more", Tone.None));
        return shown;
    }

    /// <summary>"3 registry keys", "1 setup program", "2 shortcuts" or a mix: what a repair changed, or what an undo put back.</summary>
    public static string Describe(int keys, int files, int shortcuts = 0, int folders = 0, int moved = 0)
    {
        var parts = new List<string>();
        if (keys > 0) parts.Add(keys == 1 ? "1 registry key" : $"{keys} registry keys");
        if (files > 0) parts.Add(files == 1 ? "1 setup program" : $"{files} setup programs");
        if (shortcuts > 0) parts.Add(shortcuts == 1 ? "1 shortcut" : $"{shortcuts} shortcuts");
        if (folders > 0) parts.Add(folders == 1 ? "1 empty folder" : $"{folders} empty folders");
        if (moved > 0) parts.Add(moved == 1 ? "1 leftover folder" : $"{moved} leftover folders");
        return parts.Count == 0 ? "nothing" : string.Join(" and ", parts);
    }
}

/// <summary>One thing a repair removed and is keeping: a folder it set aside, or a shortcut file it removed. The person decides to keep it (put it back) or remove it for good; a folder is deleted by itself after 30 days.</summary>
public sealed class QuarantineRow : ObservableState
{
    private long bytes = -1;
    private bool selected;
    private bool open;
    private readonly Action? changed;

    public QuarantineRow(QuarantineItem item, Action? selectionChanged = null)
    {
        Item = item;
        changed = selectionChanged;
        Entry = BuildEntry();
    }

    public QuarantineItem Item { get; }
    public ArchiveEntry Entry { get; private set; }
    public string Name => Item.Name;
    public string Original => Item.Original;
    public string Glyph => Item.Kind == QuarantineKind.Folder ? "" : "";
    public string Kind => Item.Kind == QuarantineKind.Folder ? "Folder" : "Shortcut";
    public string When => Item.Created.ToLocalTime().ToString("d MMM yyyy, h:mm tt");
    public string Detail => $"{Kind} · {Item.Created.ToLocalTime():h:mm tt} · {Item.Title}";
    public long Bytes => bytes;
    public string Size => bytes <= 0 ? "" : Format(bytes);
    public Visibility AdminVisibility => Item.Folder is not null && RepairFolders.IsMachine(Item.Original) ? Visibility.Visible : Visibility.Collapsed;
    public string KeepAutomationName => $"Keep {Name}: put it back where it was";
    public string RemoveAutomationName => $"Remove {Name} for good";
    public string SelectAutomationName => $"Select {Name}";

    /// <summary>Folders are deleted for good 30 days after the repair that set them aside; a shortcut file waits in the repair's Undo.</summary>
    public string ExpiresText
    {
        get
        {
            if (Item.Folder is null) return "";
            var days = (int)Math.Ceiling((RepairHistory.ExpiresAt(Item.Created) - DateTimeOffset.Now).TotalDays);
            return days <= 0 ? "Deleted soon" : days == 1 ? "Deleted tomorrow" : $"Deleted in {days} days";
        }
    }

    public bool IsOpen { get => open; set { if (Set(ref open, value)) { Changed(nameof(DetailsVisibility)); Changed(nameof(Chevron)); Changed(nameof(ToggleName)); } } }
    public Visibility DetailsVisibility => open ? Visibility.Visible : Visibility.Collapsed;
    public string Chevron => open ? DetailLine.Up : DetailLine.Down;
    public string ToggleName => open ? $"Hide details of {Name}" : $"Show details of {Name}";

    /// <summary>Where it was, when and by which repair it was set aside, how big it is and when it goes. Built when the row is opened, so a size that was measured meanwhile is included.</summary>
    public IReadOnlyList<DetailLine> Lines
    {
        get
        {
            var lines = new List<DetailLine>
            {
                new("Was at", Item.Original, Tone.Blue),
                new("Removed", $"{When}, by {Item.Title}", Tone.Violet),
            };
            if (bytes > 0) lines.Add(new("Size", Format(bytes), Tone.Green));
            lines.Add(Item.Folder is null
                ? new("Kept in", "this repair's Undo. Put back here, or undo the repair.", Tone.Amber)
                : new("Deleted", $"{RepairHistory.ExpiresAt(Item.Created).ToLocalTime():d MMM yyyy}, unless you put it back first", Tone.Amber));
            return lines;
        }
    }

    public bool IsSelected { get => selected; set { if (Set(ref selected, value)) changed?.Invoke(); } }

    public void SetBytes(long value) { bytes = value; Entry = BuildEntry(); Changed(nameof(Size)); Changed(nameof(Bytes)); Changed(nameof(Lines)); }

    private ArchiveEntry BuildEntry() => new(Item.Title, Item.Name, Item.Original, Item.Created, Item.Kind == QuarantineKind.Folder ? ArchiveKinds.Folder : ArchiveKinds.Shortcut, bytes, Item.Folder is not null && RepairFolders.IsMachine(Item.Original));

    public static string Format(long size) => size >= 1L << 30 ? $"{size / (double)(1L << 30):0.0} GB" : size >= 1L << 20 ? $"{size / (double)(1L << 20):0.#} MB" : size >= 1L << 10 ? $"{size / 1024.0:0} KB" : $"{size} B";
}

/// <summary>The search box, filters and sort of one list, and what the list says about itself (how many are shown, whether anything matched).</summary>
public sealed class ArchiveState(string noun, string[] sortOptions) : ObservableState
{
    private static readonly string[] Whens = ["", "date:today", "date:week", "date:month"];
    private static readonly string[] Types = ["", "kind:folder", "kind:shortcut", "kind:registry", "kind:setup"];
    private string text = "";
    private int sort, when, type;
    private ArchiveQuery query = ArchiveQuery.Empty, shown = ArchiveQuery.Empty;
    private string summary = "";
    private bool empty = true, noMatch;

    /// <summary>Raised when the search, a filter or the sort changed, so the list can be built again.</summary>
    public event Action? Refiltered;

    public string Noun { get; } = noun;
    public string[] SortOptions { get; } = sortOptions;
    public string[] WhenOptions { get; } = ["Any time", "Today", "Last 7 days", "Last 30 days"];
    public string[] TypeOptions { get; } = ["All types", "Folders", "Shortcuts", "Registry keys", "Setup programs"];
    public BulkObservableCollection<string> Chips { get; } = new();

    public string Text { get => text; set { if (Set(ref text, value ?? "")) Rebuild(); } }
    public int SortIndex { get => sort; set { if (Set(ref sort, Math.Max(0, value))) Rebuild(); } }
    public int WhenIndex { get => when; set { if (Set(ref when, Math.Max(0, value))) Rebuild(); } }
    public int TypeIndex { get => type; set { if (Set(ref type, Math.Max(0, value))) Rebuild(); } }

    public ArchiveSort Sort => (ArchiveSort)Math.Min(sort, 3);
    public bool Filtering => text.Trim().Length > 0 || when > 0 || type > 0;
    public Visibility ClearVisibility => Filtering ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ChipsVisibility => Chips.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public string Summary => summary;
    public Visibility EmptyVisibility => empty ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NoMatchVisibility => noMatch ? Visibility.Visible : Visibility.Collapsed;
    public string NoMatchText => $"Nothing matches “{text.Trim()}”";

    public bool Matches(ArchiveEntry entry) => query.Matches(entry);

    private void Rebuild()
    {
        // What the person typed, with the two drop-downs added as filters of their own; the chips show only what was typed.
        query = ArchiveQuery.Parse(string.Join(' ', text, Whens[Math.Min(when, Whens.Length - 1)], Types[Math.Min(type, Types.Length - 1)]));
        shown = ArchiveQuery.Parse(text);
        Chips.Reset(shown.Chips.ToList());
        Changed(nameof(Filtering)); Changed(nameof(ClearVisibility)); Changed(nameof(ChipsVisibility)); Changed(nameof(NoMatchText));
        Refiltered?.Invoke();
    }

    public void Clear()
    {
        text = ""; when = 0; type = 0;
        Changed(nameof(Text)); Changed(nameof(WhenIndex)); Changed(nameof(TypeIndex));
        Rebuild();
    }

    /// <summary>Called after each build of the list: how many exist, how many are shown, and what to say.</summary>
    public void Report(int total, int count, string extra)
    {
        empty = total == 0;
        noMatch = total > 0 && count == 0;
        var word = total == 1 ? Noun.TrimEnd('s') : Noun;
        summary = total == 0 ? "" : Filtering ? $"Showing {count:N0} of {total:N0} {word}" : $"{total:N0} {word}";
        if (extra.Length > 0 && total > 0) summary += " · " + extra;
        Changed(nameof(Summary)); Changed(nameof(EmptyVisibility)); Changed(nameof(NoMatchVisibility));
    }
}

/// <summary>Builds the rows of a list: filtered, sorted, and (when sorted by date) under a label for each day, week or month.</summary>
public static class ArchiveList
{
    public static List<object> Build<T>(IEnumerable<T> rows, ArchiveSort sort, DateTimeOffset now, Func<T, DateTimeOffset> when, Func<T, long> weight, Func<T, string> name, Func<IReadOnlyList<T>, string> size) where T : class
    {
        var list = rows.ToList();
        if (sort is ArchiveSort.Largest or ArchiveSort.Name)
        {
            var ordered = sort == ArchiveSort.Largest ? list.OrderByDescending(weight) : list.OrderBy(name, StringComparer.CurrentCultureIgnoreCase);
            return [.. ordered.ThenByDescending(when)];
        }
        var oldest = sort == ArchiveSort.Oldest;
        var groups = list.GroupBy(x => ArchiveDates.Bucket(when(x), now)).ToList();
        var ordering = oldest ? groups.OrderByDescending(g => g.Key.Order) : groups.OrderBy(g => g.Key.Order);
        var result = new List<object>(list.Count + groups.Count);
        foreach (var group in ordering)
        {
            var items = (oldest ? group.OrderBy(when) : group.OrderByDescending(when)).ToList();
            result.Add(new ArchiveHeader(group.Key.Label, items.Count, size(items)));
            result.AddRange(items);
        }
        return result;
    }
}

/// <summary>Picks the look of a row: a label for a group, or a repair, or a quarantined item.</summary>
public sealed class ArchiveTemplateSelector : DataTemplateSelector
{
    public DataTemplate? Header { get; set; }
    public DataTemplate? Repair { get; set; }
    public DataTemplate? Quarantined { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) => item switch { ArchiveHeader => Header, HistoryRow => Repair, QuarantineRow => Quarantined, _ => null };
    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) => SelectTemplateCore(item);
}

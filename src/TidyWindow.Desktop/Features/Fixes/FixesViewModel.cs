using System.ComponentModel;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using TidyWindow.Next.Core;

namespace TidyWindow.Next.Desktop;

public enum FixesTab { Windows, Network, Devices, Apps }

public sealed class FixOptionRow(FixOption option, Action changed) : ObservableState
{
    private bool on = option.Default;
    public string Id => option.Id;
    public string Label => option.Label;
    public string Detail => option.Detail;
    public Visibility DetailVisibility => option.Detail.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public bool IsOn { get => on; set { if (Set(ref on, value)) changed(); } }
}

public sealed class FixRow : ObservableState
{
    private bool expanded;
    private QueueEntry? entry;
    private FixRunNote? note;
    private IReadOnlyList<StartupFact>? facts;

    public FixRow(FixItem item)
    {
        Item = item;
        Options = item.Options.Select(x => new FixOptionRow(x, RebuildFacts)).ToList();
    }

    public FixItem Item { get; }
    public string Id => Item.Id;
    public string Name => Item.Name;
    public string Summary => Item.Summary;
    public IReadOnlyList<FixOptionRow> Options { get; }
    public Visibility OptionsVisibility => Options.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public IReadOnlyList<string> SelectedOptions => Options.Where(x => x.IsOn).Select(x => x.Id).ToList();

    public string Glyph => char.ConvertFromUtf32(int.TryParse(Item.Icon, System.Globalization.NumberStyles.HexNumber, null, out var code) ? code : 0xE90F);

    public Visibility AdminVisibility => Item.Scope == FixScope.Machine ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CarefulVisibility => Item.Risk == FixRisk.Careful ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RestartVisibility => Item.Restart != FixRestart.Never ? Visibility.Visible : Visibility.Collapsed;
    public string RestartText => Item.Restart == FixRestart.Always ? "Restart" : "May restart";
    public string RestartTip => Item.Restart == FixRestart.Always ? "Windows needs a restart to finish this fix" : "Windows may ask for a restart when this finishes";

    private static DateTimeOffset BootTime => DateTimeOffset.Now - TimeSpan.FromMilliseconds(Environment.TickCount64);
    /// <summary>The last run needed a restart and Windows has not been restarted since.</summary>
    public bool NeedsRestart => note is { Outcome: nameof(ActionOutcome.RestartRequired) } && note.When > BootTime;
    public Visibility RestartNowVisibility => NeedsRestart && !Busy ? Visibility.Visible : Visibility.Collapsed;

    public bool Queued => entry?.State == QueueItemState.Waiting;
    public bool Running => entry?.State == QueueItemState.Running;
    public bool Busy => Queued || Running;
    public bool CanRun => !Busy;
    public string ButtonText => Running ? "Running…" : Queued ? "Queued" : "Run";
    public string AutomationName => $"Run {Name}";
    public string DetailsName => $"Details for {Name}";

    /// <summary>One line about what is happening now, or how the last run went.</summary>
    public string Live
    {
        get
        {
            if (Queued) return "Waiting for its turn in the queue.";
            if (Running)
            {
                var line = LastLine(entry!.Output);
                return line.Length > 0 ? line : entry.Notice.Length > 0 ? entry.Notice : "Starting…";
            }
            if (entry is { State: QueueItemState.Finished, Result: { } result }) return result.Message;
            return "";
        }
    }

    public Visibility LiveVisibility => Live.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public bool Working => Busy;

    public string LastText => note is null || entry is { State: QueueItemState.Finished } ? "" : $"Last run: {Words(note.Outcome)} · {note.When.ToLocalTime():d MMM, h:mm tt}";
    public Visibility LastVisibility => LastText.Length > 0 && !Busy ? Visibility.Visible : Visibility.Collapsed;
    private bool LastWentWell => note?.Outcome is nameof(ActionOutcome.Completed) or nameof(ActionOutcome.RestartRequired);
    /// <summary>The dot beside the last run: green when it worked, amber when it did not, so the state shows without reading.</summary>
    public Visibility LastGoodVisibility => LastWentWell ? Visibility.Visible : Visibility.Collapsed;
    public Visibility LastWarnVisibility => LastWentWell ? Visibility.Collapsed : Visibility.Visible;

    private static string Words(string outcome) => outcome switch
    {
        nameof(ActionOutcome.Completed) => "finished",
        nameof(ActionOutcome.RestartRequired) => "finished, restart needed",
        nameof(ActionOutcome.Unverified) => "finished with warnings",
        _ => "did not finish"
    };

    private static string LastLine(string output)
    {
        var lines = PackageOutputParser.Clean(output).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length == 0 ? "" : lines[^1];
    }

    public bool Expanded => expanded;
    public Visibility DetailsVisibility => expanded ? Visibility.Visible : Visibility.Collapsed;
    public string Chevron => expanded ? "" : "";
    public void ToggleDetails() { expanded = !expanded; Changed(nameof(Expanded)); Changed(nameof(DetailsVisibility)); Changed(nameof(Chevron)); }

    public IReadOnlyList<StartupFact> Facts => facts ??= BuildFacts();

    private void RebuildFacts() { facts = null; Changed(nameof(Facts)); }

    private List<StartupFact> BuildFacts()
    {
        var list = new List<StartupFact>();
        void Add(string label, string value, bool mono = false) { if (value.Length > 0) list.Add(new(label, value, mono)); }
        Add("What to know", Item.Effect);
        Add("How long", Item.Duration);
        Add("Needs", Item.Scope == FixScope.Machine ? "Administrator permission. The output still shows here." : "Nothing special. Runs as you.");
        // Only the steps the ticked options switch on, exactly as the runner will take them.
        var plan = FixPlan.Build(Item, SelectedOptions);
        Add("Steps", string.Join("\n", plan.Select((step, i) => $"{i + 1}. {step.Title}")), true);
        Add("Commands", string.Join("\n", plan.Select((step, i) => $"{i + 1}. {FixPlan.Describe(step)}")), true);
        if (note is { Detail.Length: > 0 }) Add("Last run", $"{Words(note.Outcome)} · {note.When.ToLocalTime():d MMM, h:mm tt}\n{note.Detail}", true);
        Add("Source", string.Join(", ", Item.Sources));
        return list;
    }

    public void Update(QueueEntry? latest, FixRunNote? last)
    {
        entry = latest;
        // The last run's details are part of the facts, so they are rebuilt when a new run is recorded.
        var noteChanged = !Equals(note, last);
        note = last;
        if (noteChanged) RebuildFacts();
        foreach (var name in new[]
        {
            nameof(RestartNowVisibility), nameof(Queued), nameof(Running), nameof(Busy), nameof(CanRun), nameof(ButtonText), nameof(Live), nameof(LiveVisibility), nameof(Working), nameof(LastText), nameof(LastVisibility), nameof(LastGoodVisibility), nameof(LastWarnVisibility)
        }) Changed(name);
    }
}

public sealed class FixesViewModel : ObservableState, IDisposable
{
    private readonly IReadOnlyList<FixRow> all;
    private readonly OperationQueueViewModel queue;
    private readonly FixRunStore store;
    private readonly ToastCenter toasts;
    private readonly Action openQueue;
    private FixesTab tab = FixesTab.Windows;
    private string query = "";
    private long seenCompletions = -1;
    private IReadOnlyList<FixRunNote> notes = [];

    public FixesViewModel(IReadOnlyList<FixItem> catalog, OperationQueueViewModel queue, FixRunStore store, ToastCenter toasts, Action openQueue)
    {
        all = catalog.Select(x => new FixRow(x)).ToList();
        this.queue = queue; this.store = store; this.toasts = toasts; this.openQueue = openQueue;
        queue.Refreshed += Refresh;
        Refresh();
        ApplyFilter();
    }

    public BulkObservableCollection<FixRow> Rows { get; } = new();

    public string WindowsCount => Count(FixGroup.Windows);
    public string NetworkCount => Count(FixGroup.Network);
    public string DevicesCount => Count(FixGroup.Devices);
    public string AppsCount => Count(FixGroup.Apps);
    private string Count(FixGroup group) => all.Count(x => x.Item.Group == group).ToString("N0");

    public string TabNote => tab switch
    {
        FixesTab.Windows => "Repairs for Windows itself: system files, updates, the drive, the clock, search, fonts and WMI.",
        FixesTab.Network => "When the internet or a network connection will not work properly, or other PCs do not show up.",
        FixesTab.Devices => "Printers, sound, cameras and hardware that Windows does not see or use properly.",
        _ => "Windows apps and parts of the desktop that misbehave: Store, Start menu, icons, OneDrive, Windows Security and WinGet."
    };

    public string FooterNote => "Fixes run one at a time in the Queue, where you can watch each step as it happens. Each one is a short list of standard Windows commands with a plain description; nothing runs that is not on this page. Anything that needs administrator permission asks once, and its output still shows live.";

    public string RunningText
    {
        get
        {
            var running = all.Count(x => x.Running); var waiting = all.Count(x => x.Queued);
            return running + waiting == 0 ? "" : $"{running:N0} running · {waiting:N0} waiting";
        }
    }

    public Visibility EmptyVisibility => Rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public string EmptyTitle => "No matches";
    public string EmptyText => "Try a different word, such as printer, internet, sound or update.";

    public string Query { get => query; set { if (Set(ref query, value)) ApplyFilter(); } }
    public FixesTab Tab { get => tab; set { if (Set(ref tab, value)) ApplyFilter(); } }

    private void ApplyFilter()
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        bool Matches(FixRow row) => words.All(w =>
            row.Name.Contains(w, StringComparison.CurrentCultureIgnoreCase) || row.Summary.Contains(w, StringComparison.CurrentCultureIgnoreCase) ||
            row.Item.Effect.Contains(w, StringComparison.CurrentCultureIgnoreCase) || row.Item.Steps.Any(s => s.Title.Contains(w, StringComparison.CurrentCultureIgnoreCase)));
        var group = tab switch { FixesTab.Windows => FixGroup.Windows, FixesTab.Network => FixGroup.Network, FixesTab.Devices => FixGroup.Devices, _ => FixGroup.Apps };
        // While searching, every group is searched, so a word finds its fix whatever tab is open.
        Rows.Reset(all.Where(x => words.Length > 0 || x.Item.Group == group).Where(Matches).ToList());
        foreach (var name in new[] { nameof(TabNote), nameof(EmptyVisibility) }) Changed(name);
    }

    /// <summary>Brings every row up to date with the queue: what is waiting, what is running and its latest line of output.</summary>
    private void Refresh()
    {
        var completions = queue.Snapshot.Completions;
        if (completions != seenCompletions) { seenCompletions = completions; try { notes = store.All(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { notes = []; } }
        foreach (var row in all) row.Update(queue.FindFix(row.Id), notes.FirstOrDefault(x => string.Equals(x.Id, row.Id, StringComparison.OrdinalIgnoreCase)));
        Changed(nameof(RunningText));
    }

    /// <summary>What to confirm before a fix with a trade-off runs. Null when no confirmation is needed.</summary>
    public static string? Warning(FixRow row) => row.Item.Risk == FixRisk.Careful ? row.Item.Effect : null;

    /// <summary>Adds the fix to the queue. Returns false when it is already queued or running.</summary>
    public bool Run(FixRow row)
    {
        if (row.Busy) return false;
        queue.Enqueue([PackageOperation.ForFix(row.Item, row.SelectedOptions)]);
        toasts.Show("fix-" + row.Id, ToastKind.Info, $"{row.Name} added to the queue",
            row.Item.Scope == FixScope.Machine ? "It will ask for administrator permission when it starts." : "It starts as soon as nothing else is running.", "Watch output", () => { openQueue(); return Task.CompletedTask; }, 8);
        Refresh();
        return true;
    }

    public void OpenQueue() => openQueue();

    /// <summary>Restarts Windows after a 30 second countdown, which the toast can cancel.</summary>
    public void RestartWindows()
    {
        var shutdown = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "shutdown.exe");
        try
        {
            Start(shutdown, "/r", "/t", "30", "/c", "TidyWindow: restarting to finish a fix.");
            toasts.Show("fix-restart", ToastKind.Warning, "Windows will restart in 30 seconds", "Save your work now.", "Cancel restart", () => { CancelRestart(shutdown); return Task.CompletedTask; }, 30);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            toasts.Show("fix-restart", ToastKind.Warning, "Couldn’t restart Windows", "Restart it from the Start menu.", seconds: 8);
        }
    }

    private void CancelRestart(string shutdown)
    {
        try { Start(shutdown, "/a"); toasts.Show("fix-restart", ToastKind.Info, "Restart cancelled", seconds: 5); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { }
    }

    private static void Start(string program, params string[] arguments)
    {
        var info = new ProcessStartInfo(program) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        Process.Start(info)?.Dispose();
    }

    public void Dispose() => queue.Refreshed -= Refresh;
}

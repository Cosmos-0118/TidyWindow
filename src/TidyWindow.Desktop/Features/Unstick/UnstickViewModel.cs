using System.Diagnostics;
using Microsoft.UI.Xaml;
using TidyWindow.Next.Core;

namespace TidyWindow.Next.Desktop;

/// <summary>One action with its button, what happened the last time it ran, and whether the thing it fixes is running now.</summary>
/// <param name="fix">For an action that runs a fix from the Fixes catalog: that fix, so the page can show the exact commands before anything starts.</param>
public sealed class UnstickRow(UnstickAction action, bool hasHint, FixItem? fix = null) : ObservableState
{
    private bool busy, active, expanded, good, showGroup;
    private string result = "", took = "", lastDetail = "";
    private IReadOnlyList<StartupFact>? facts;
    private IReadOnlyList<SearchField>? searchFields;

    public UnstickAction Action { get; } = action;
    public string Name => Action.Name;
    public string When => Action.When;
    public string Glyph => char.ConvertFromUtf32(int.Parse(Action.Glyph, System.Globalization.NumberStyles.HexNumber));
    public bool Admin => Action.Admin;
    public Visibility AdminVisibility => Admin ? Visibility.Visible : Visibility.Collapsed;
    public string AutomationName => $"{Action.Button}: {Name}";
    public string DetailsName => $"Details for {Name}";

    /// <summary>The tab this action lives on, shown beside it while a search lists actions from every tab.</summary>
    public string GroupText => UnstickViewModel.TabName(Action.Group);
    public Visibility GroupVisibility => showGroup ? Visibility.Visible : Visibility.Collapsed;
    public void ShowGroup(bool value) { if (showGroup == value) return; showGroup = value; Changed(nameof(GroupVisibility)); }

    /// <summary>What a search looks in, heaviest first: the name, then the symptom, then what it does and the programs it closes.</summary>
    public IReadOnlyList<SearchField> SearchFields => searchFields ??=
    [
        new(Name, 4), new(Action.Id.Replace('-', ' '), 2), new(Action.When, 2), new(Action.Does, 1.2), new(Action.Notice, 0.8),
        new(string.Join(' ', Action.Targets), 1), new(GroupText, 0.8), new(UnstickViewModel.SectionTitle(Action.Group), 0.6)
    ];

    public bool CanRun => !busy;
    public string ButtonText => busy ? "Running…" : Action.Button;
    public bool Busy { get => busy; set { if (Set(ref busy, value)) { Changed(nameof(CanRun)); Changed(nameof(ButtonText)); Changed(nameof(ResultVisibility)); } } }

    /// <summary>Whether the programs this action closes are running now, so the person can see if anything is there to unstick.</summary>
    public string Hint => !hasHint ? "" : active ? "Running now" : "Not running";
    public Visibility HintVisibility => hasHint ? Visibility.Visible : Visibility.Collapsed;
    public void SetActive(bool value) { if (Set(ref active, value, nameof(Hint))) { } }

    public string Result => result;
    public string Took => took;
    public Visibility ResultVisibility => result.Length > 0 && !busy ? Visibility.Visible : Visibility.Collapsed;
    public Visibility GoodVisibility => good ? Visibility.Visible : Visibility.Collapsed;
    public Visibility WarnVisibility => good ? Visibility.Collapsed : Visibility.Visible;

    public void Finished(ActionResult outcome, TimeSpan elapsed, DateTime when)
    {
        result = outcome.Message; good = outcome.Ok;
        took = (outcome.Ok ? "Done in " : "Stopped after ") + (elapsed.TotalSeconds < 0.1 ? "under 0.1 s" : $"{elapsed.TotalSeconds:0.0} s") + $" · {when:h:mm tt}";
        // What the run did is kept with the row, so opening it shows the same account that went to Logs.
        lastDetail = outcome.Detail;
        facts = null;
        foreach (var name in new[] { nameof(Result), nameof(Took), nameof(ResultVisibility), nameof(GoodVisibility), nameof(WarnVisibility), nameof(Facts) }) Changed(name);
    }

    public Visibility DetailsVisibility => expanded ? Visibility.Visible : Visibility.Collapsed;
    public string Chevron => expanded ? "" : "";
    public void ToggleDetails() { expanded = !expanded; Changed(nameof(DetailsVisibility)); Changed(nameof(Chevron)); }

    public IReadOnlyList<StartupFact> Facts => facts ??= BuildFacts();

    private List<StartupFact> BuildFacts()
    {
        var list = new List<StartupFact>
        {
            new("When", Action.When, false),
            new("What it does", Action.Does, false),
            new("You will notice", Action.Notice, false),
            new("How long", Action.Speed, false)
        };
        // A fix-backed action runs reviewed commands from the Fixes catalog; they are shown whole, so nothing runs that the page did not say.
        if (fix is not null)
            list.Add(new("Commands", string.Join("\n", FixPlan.Build(fix, FixPlan.DefaultOptions(fix)).Select((step, i) => $"{i + 1}. {FixPlan.Describe(step)}")), true));
        list.Add(new("Needs", Admin ? "Administrator permission. Windows asks the first time; the helper then stays open until you close TidyWindow, so the next one does not ask again." : "Nothing special. It runs as you and never asks for administrator permission.", false));
        if (lastDetail.Length > 0) list.Add(new("Last run", $"{took}\n{lastDetail}", true));
        return list;
    }
}

public sealed record UnstickSection(string Title, string Note, IReadOnlyList<UnstickRow> Rows);

public sealed class UnstickViewModel : ObservableState, IDisposable
{
    private readonly UnstickRunner runner;
    private readonly ActivityLog log;
    private readonly ToastCenter toasts;
    private readonly CancellationTokenSource lifetime = new();
    // One administrator action at a time: they share the helper, and a second permission prompt would be confusing.
    private readonly SemaphoreSlim adminTurn = new(1, 1);
    private readonly IReadOnlyList<UnstickRow> rows;
    private UnstickGroup tab = UnstickGroup.Desktop;
    private string query = "";
    private IReadOnlyList<UnstickRow> matches = [];
    private IReadOnlyList<UnstickSection> shown = [];

    public UnstickViewModel(UnstickRunner runner, ActivityLog log, ToastCenter toasts, IReadOnlyList<FixItem>? catalog = null)
    {
        this.runner = runner; this.log = log; this.toasts = toasts;
        catalog ??= FixCatalog.Load();
        rows = UnstickCatalog.All.Where(runner.Available)
            .Select(x => new UnstickRow(x, x.Targets.Count > 0, x.FixId is null ? null : catalog.FirstOrDefault(f => f.Id.Equals(x.FixId, StringComparison.OrdinalIgnoreCase)))).ToList();
        Sections = Groups.Select(g => new UnstickSection(SectionTitle(g), SectionNote(g), rows.Where(r => r.Action.Group == g).ToList())).Where(s => s.Rows.Count > 0).ToList();
        Rebuild();
    }

    private static readonly UnstickGroup[] Groups = [UnstickGroup.Desktop, UnstickGroup.Input, UnstickGroup.Network, UnstickGroup.Hardware];

    public static string TabName(UnstickGroup group) => group switch { UnstickGroup.Desktop => "Desktop", UnstickGroup.Input => "Typing", UnstickGroup.Network => "Network", _ => "Hardware" };

    public static string SectionTitle(UnstickGroup group) => group switch
    {
        UnstickGroup.Desktop => "Desktop and taskbar",
        UnstickGroup.Input => "Typing, clipboard and pop-ups",
        UnstickGroup.Network => "Network and developer",
        _ => "Hardware and services (administrator)"
    };

    private static string SectionNote(UnstickGroup group) => group switch
    {
        UnstickGroup.Desktop => "When the taskbar, Start, search or the screen itself misbehaves.",
        UnstickGroup.Input => "When keys, copy and paste, screenshots, notifications or sign-in prompts get stuck.",
        UnstickGroup.Network => "Quick kicks for DNS and the Linux subsystem.",
        _ => "These restart a Windows service, so Windows asks for administrator permission the first time. They still run at once, outside the queue."
    };

    /// <summary>Every group with something in it, whatever tab is open. The close guard looks here for an action still running.</summary>
    public IReadOnlyList<UnstickSection> Sections { get; }

    /// <summary>What the page lists now: the open tab's group, or, while searching, one list of the best matches from every tab.</summary>
    public IReadOnlyList<UnstickSection> Shown { get => shown; private set => Set(ref shown, value); }

    // While searching, each tab's number is how many of its actions match, so the tabs show where the answers are.
    private bool Searching => !SmartSearch.IsEmpty(query);
    public string DesktopCount => Count(UnstickGroup.Desktop);
    public string InputCount => Count(UnstickGroup.Input);
    public string NetworkCount => Count(UnstickGroup.Network);
    public string HardwareCount => Count(UnstickGroup.Hardware);
    private string Count(UnstickGroup group) => (Searching ? matches : rows).Count(x => x.Action.Group == group).ToString("N0");

    public Visibility EmptyVisibility => Searching && matches.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public string EmptyTitle => "No matches";
    public string EmptyText => "Every tab was searched. Try a different word, such as taskbar, clipboard, sound or dns; small typos are forgiven.";

    public string Query { get => query; set { if (Set(ref query, value ?? "")) Rebuild(); } }
    public UnstickGroup Tab { get => tab; set { if (Set(ref tab, value)) Rebuild(); } }

    private void Rebuild()
    {
        var searching = Searching;
        matches = searching ? SmartSearch.Rank(rows, query, x => x.SearchFields) : [];
        foreach (var row in rows) row.ShowGroup(searching);
        if (searching)
            Shown = matches.Count == 0 ? [] : [new UnstickSection("Matches in every tab", $"{matches.Count:N0} {(matches.Count == 1 ? "action matches" : "actions match")} “{query.Trim()}”, best match first. The numbers on the tabs count the matches in each.", matches)];
        else
            Shown = Sections.Where(s => s.Rows.Count > 0 && s.Rows[0].Action.Group == tab).ToList();
        foreach (var name in new[] { nameof(DesktopCount), nameof(InputCount), nameof(NetworkCount), nameof(HardwareCount), nameof(EmptyVisibility) }) Changed(name);
    }

    public string FooterNote => "These run the moment you press them and finish in a few seconds. Nothing is queued, deleted or reset. Only the Hardware tab asks for administrator permission, once. Every press is written to Logs: what it was about to do, each thing it did to Windows, and how it came out. For slower repairs such as rebuilding the icon cache or the search index, use Fixes.";

    /// <summary>Looks at which of the programs are running now. Cheap enough to do each time the page opens.</summary>
    public async Task LoadAsync()
    {
        if (lifetime.IsCancellationRequested) return;
        var states = await Task.Run(() => rows.Where(x => x.Action.Targets.Count > 0).Select(x => (Row: x, Active: runner.IsActive(x.Action))).ToList()).ConfigureAwait(true);
        foreach (var (row, active) in states) row.SetActive(active);
    }

    public async Task RunAsync(UnstickRow row)
    {
        if (row.Busy || lifetime.IsCancellationRequested) return;
        row.Busy = true;
        var clock = Stopwatch.StartNew();
        ActionResult result;
        var waited = false;
        // A fix-backed action is logged as it starts by the fix itself, with its commands; saying it twice would only be noise.
        if (row.Action.FixId is null)
            log.Write(ActivityLevel.Info, ActivityArea.App, $"Unstick started: {row.Name}",
                $"What it does: {row.Action.Does}\nYou will notice: {row.Action.Notice}\nRuns as you, without administrator permission.");
        try
        {
            if (row.Admin) { await adminTurn.WaitAsync(lifetime.Token).ConfigureAwait(true); waited = true; clock.Restart(); }
            result = await Task.Run(() => runner.RunAsync(row.Action.Id, lifetime.Token)).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            row.Busy = false;
            log.Write(ActivityLevel.Warning, ActivityArea.App, $"Unstick stopped: {row.Name}", "TidyWindow was closing, so it was stopped before it finished.");
            return;
        }
        catch (Exception ex) { result = new(false, ex.Message, "It stopped with an unexpected error:\n" + ex); }
        finally { if (waited) adminTurn.Release(); }
        clock.Stop();
        row.Busy = false;
        row.Finished(result, clock.Elapsed, DateTime.Now);
        var detail = $"{result.Message}\nTook {(clock.Elapsed.TotalSeconds < 0.1 ? "under 0.1" : clock.Elapsed.TotalSeconds.ToString("0.0"))} s.";
        if (result.Detail.Length > 0) detail += "\n\nWhat it did:\n" + result.Detail;
        log.Write(result.Ok ? ActivityLevel.Success : ActivityLevel.Warning, ActivityArea.App, result.Ok ? $"Unstick finished: {row.Name}" : $"Unstick did not finish: {row.Name}", detail);
        toasts.Show("unstick-" + row.Action.Id, result.Ok ? ToastKind.Success : ToastKind.Warning, result.Ok ? row.Name : $"{row.Name} did not finish", ToastCenter.Flat(result.Message), seconds: result.Ok ? 5 : 8);
        if (row.Action.Targets.Count > 0) row.SetActive(await Task.Run(() => runner.IsActive(row.Action)).ConfigureAwait(true));
    }

    public void Dispose() { lifetime.Cancel(); lifetime.Dispose(); adminTurn.Dispose(); }
}

using System.Diagnostics;
using Microsoft.UI.Xaml;
using TidyWindow.Next.Core;

namespace TidyWindow.Next.Desktop;

/// <summary>One action with its button, what happened the last time it ran, and whether the thing it fixes is running now.</summary>
public sealed class UnstickRow(UnstickAction action, bool hasHint) : ObservableState
{
    private bool busy, active, expanded, good;
    private string result = "", took = "";
    private IReadOnlyList<StartupFact>? facts;

    public UnstickAction Action { get; } = action;
    public string Name => Action.Name;
    public string When => Action.When;
    public string Glyph => char.ConvertFromUtf32(int.Parse(Action.Glyph, System.Globalization.NumberStyles.HexNumber));
    public bool Admin => Action.Admin;
    public Visibility AdminVisibility => Admin ? Visibility.Visible : Visibility.Collapsed;
    public string AutomationName => $"{Action.Button}: {Name}";
    public string DetailsName => $"Details for {Name}";

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
        foreach (var name in new[] { nameof(Result), nameof(Took), nameof(ResultVisibility), nameof(GoodVisibility), nameof(WarnVisibility) }) Changed(name);
    }

    public Visibility DetailsVisibility => expanded ? Visibility.Visible : Visibility.Collapsed;
    public string Chevron => expanded ? "" : "";
    public void ToggleDetails() { expanded = !expanded; Changed(nameof(DetailsVisibility)); Changed(nameof(Chevron)); }

    public IReadOnlyList<StartupFact> Facts => facts ??=
    [
        new("When", Action.When, false),
        new("What it does", Action.Does, false),
        new("You will notice", Action.Notice, false),
        new("How long", Action.Speed, false),
        new("Needs", Admin ? "Administrator permission. Windows asks the first time; the helper then stays open until you close TidyWindow, so the next one does not ask again." : "Nothing special. It runs as you and never asks for administrator permission.", false)
    ];
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

    public UnstickViewModel(UnstickRunner runner, ActivityLog log, ToastCenter toasts)
    {
        this.runner = runner; this.log = log; this.toasts = toasts;
        rows = UnstickCatalog.All.Where(runner.Available).Select(x => new UnstickRow(x, x.Targets.Count > 0)).ToList();
        Sections = new (UnstickGroup, string, string)[]
        {
            (UnstickGroup.Desktop, "Desktop and taskbar", "When the taskbar, Start, search or the screen itself misbehaves."),
            (UnstickGroup.Input, "Typing, clipboard and pop-ups", "When keys, copy and paste, screenshots, notifications or sign-in prompts get stuck."),
            (UnstickGroup.Network, "Network and developer", "Quick kicks for DNS and the Linux subsystem."),
            (UnstickGroup.Hardware, "Hardware and services (administrator)", "These restart a Windows service, so Windows asks for administrator permission the first time. They still run at once, outside the queue.")
        }.Select(g => new UnstickSection(g.Item2, g.Item3, rows.Where(r => r.Action.Group == g.Item1).ToList())).Where(s => s.Rows.Count > 0).ToList();
    }

    public IReadOnlyList<UnstickSection> Sections { get; }

    public string FooterNote => "These run the moment you press them and finish in a few seconds. Nothing is queued, deleted or reset. Only the last group asks for administrator permission, once. For slower repairs such as rebuilding the icon cache or the search index, use Fixes.";

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
        try
        {
            if (row.Admin) { await adminTurn.WaitAsync(lifetime.Token).ConfigureAwait(true); waited = true; clock.Restart(); }
            result = await Task.Run(() => runner.RunAsync(row.Action.Id, lifetime.Token)).ConfigureAwait(true);
        }
        catch (OperationCanceledException) { row.Busy = false; return; }
        catch (Exception ex) { result = new(false, ex.Message); }
        finally { if (waited) adminTurn.Release(); }
        clock.Stop();
        row.Busy = false;
        row.Finished(result, clock.Elapsed, DateTime.Now);
        log.Write(result.Ok ? ActivityLevel.Success : ActivityLevel.Warning, ActivityArea.App, $"Unstick: {row.Name}", result.Message);
        toasts.Show("unstick-" + row.Action.Id, result.Ok ? ToastKind.Success : ToastKind.Warning, result.Ok ? row.Name : $"{row.Name} did not finish", ToastCenter.Brief(result.Message), seconds: result.Ok ? 5 : 8);
        if (row.Action.Targets.Count > 0) row.SetActive(await Task.Run(() => runner.IsActive(row.Action)).ConfigureAwait(true));
    }

    public void Dispose() { lifetime.Cancel(); lifetime.Dispose(); adminTurn.Dispose(); }
}

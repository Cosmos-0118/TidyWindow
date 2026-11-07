using System.Diagnostics;
using Microsoft.UI.Xaml;
using TidyWindow.Next.Core;

namespace TidyWindow.Next.Desktop;

public enum ChangeKind { None, Better, Worse }

/// <summary>One test of a check: the number, and when there is an earlier check to compare with, how it moved and where the rounds of both fell.</summary>
public sealed class MetricRow
{
    private readonly ProbeKind kind;
    private readonly ProbeMetric metric;
    private readonly ProbeChange? change;

    public MetricRow(ProbeKind kind, ProbeMetric metric, ProbeChange? change, ProbeMetric? baseline)
    {
        this.kind = kind; this.metric = metric; this.change = change;
        (Before, After) = Bands(baseline, metric);
    }

    public string Name => kind.Name;
    public string What => kind.What;
    public string Number => ProbeRunner.Format(metric.Value);
    public string Unit => kind.Unit;
    public string Direction => kind.HigherIsBetter ? "Higher is better" : "Lower is better";
    public bool HasChange => change is not null;

    public ChangeKind Kind => change?.Verdict switch { ProbeVerdict.Better => ChangeKind.Better, ProbeVerdict.Worse => ChangeKind.Worse, _ => ChangeKind.None };
    public string ChangeText => change is null ? "" : change.Verdict switch
    {
        ProbeVerdict.Better => $"{Math.Abs(change.Percent):0.#}% faster",
        ProbeVerdict.Worse => $"{Math.Abs(change.Percent):0.#}% slower",
        _ => "No clear change"
    };

    /// <summary>Why the page said what it said about this test, so "no clear change" is never a bare verdict.</summary>
    public string Detail => change is null ? $"Rounds differed by {metric.Spread:0.#}%" : change.Verdict switch
    {
        _ when change.Disturbed => $"Moved {Math.Abs(change.Percent):0.#}%, but other programs were busy, so it says little.",
        ProbeVerdict.Better or ProbeVerdict.Worse => "Clear of the normal wobble. The rounds of the two checks do not overlap.",
        _ when !change.Separate && Math.Abs(change.Percent) >= change.Needed => $"Moved {Math.Abs(change.Percent):0.#}%, but the rounds of the two checks overlap, so it may be noise.",
        _ => $"Moved {Math.Abs(change.Percent):0.#}%. It takes more than {change.Needed:0.#}% to count."
    };

    public string Was => change is null ? "" : $"Before: {ProbeRunner.Format(change.Before)} {kind.Unit}";

    public RangeBand? Before { get; }
    public RangeBand? After { get; }

    public Visibility BetterVisibility => Kind == ChangeKind.Better ? Visibility.Visible : Visibility.Collapsed;
    public Visibility WorseVisibility => Kind == ChangeKind.Worse ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SameVisibility => HasChange && Kind == ChangeKind.None ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ChangeVisibility => HasChange ? Visibility.Visible : Visibility.Collapsed;
    public Visibility BarVisibility => After is not null ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Both checks on one scale, a little wider than the widest round, so neither band touches the edge.</summary>
    private static (RangeBand? Before, RangeBand? After) Bands(ProbeMetric? before, ProbeMetric after)
    {
        if (after.Rounds is not { Count: > 0 } now) return (null, null);
        // Without an earlier check there is one track: just how far apart this check's own rounds were.
        var was = before?.Rounds is { Count: > 0 } kept ? kept : null;
        var low = Math.Min(was?.Min() ?? now.Min(), now.Min()); var high = Math.Max(was?.Max() ?? now.Max(), now.Max());
        var pad = Math.Max((high - low) * 0.12, Math.Abs(high) * 0.01);
        low -= pad; high += pad;
        double At(double x) => (x - low) / (high - low);
        return (was is null ? null : new(At(was.Min()), At(was.Max()), At(before!.Value)), new(At(now.Min()), At(now.Max()), At(after.Value)));
    }
}

/// <summary>One earlier check, and whether it can serve as the one to compare with.</summary>
public sealed class CheckHistoryRow(ProbeRun source, bool comparable, bool isBaseline)
{
    public ProbeRun Run => source;
    public string When => LabViewModel.When(source);
    public string Plan => source.Plan;
    public string Memory => source.MemoryIntegrity;
    public string Wake => Number("wake");
    public string Work => Number("throughput");
    public string Copy => Number("copy");
    public string Access => Number("random");
    public Visibility CanCompareVisibility => comparable && !isBaseline ? Visibility.Visible : Visibility.Collapsed;
    public Visibility BaselineVisibility => isBaseline ? Visibility.Visible : Visibility.Collapsed;
    public Visibility OldVisibility => comparable ? Visibility.Collapsed : Visibility.Visible;
    /// <summary>Checks made by an older version of the tests stay visible but fade, since they cannot be compared.</summary>
    public double Fade => comparable ? 1 : 0.55;

    private string Number(string id) => source.Metrics.FirstOrDefault(x => x.Id == id) is { } m ? ProbeRunner.Format(m.Value) : "–";
}

/// <summary>An earlier check offered as the one to compare with.</summary>
public sealed class BaselineChoice(ProbeRun source)
{
    public ProbeRun Run => source;
    public string Label => $"{LabViewModel.When(source)} · {source.Plan}";
    public override string ToString() => Label;
}

/// <summary>An app with a window, and the three choices for how much processor time it gets first.</summary>
public sealed class AppRow(AppProcess app) : ObservableState
{
    private ProcessPriorityClass? priority = app.Priority;
    private bool changed;

    public AppProcess App { get; } = app;
    public string Name => App.Name;
    public string Title => App.Title.Length > 70 ? App.Title[..70] + "…" : App.Title;
    public string Memory => $"{App.MemoryMegabytes:N0} MB";
    public bool CanChange => priority is not null;
    /// <summary>Each row needs its own group, or choosing in one row would clear the choice in another.</summary>
    public string GroupName => "priority-" + App.Id;
    public bool IsEase => priority is ProcessPriorityClass.BelowNormal or ProcessPriorityClass.Idle;
    public bool IsNormal => priority is ProcessPriorityClass.Normal;
    public bool IsBoost => priority is ProcessPriorityClass.AboveNormal or ProcessPriorityClass.High or ProcessPriorityClass.RealTime;
    public Visibility ChangedVisibility => changed ? Visibility.Visible : Visibility.Collapsed;

    public void Update(ProcessPriorityClass? now, bool isChanged)
    {
        priority = now; changed = isChanged;
        foreach (var name in new[] { nameof(CanChange), nameof(IsEase), nameof(IsNormal), nameof(IsBoost), nameof(ChangedVisibility) }) Changed(name);
    }
}

public sealed class LabViewModel : ObservableState, IDisposable
{
    private const string MemoryIntegrityKey = @"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity";
    private const int ExpectedSeconds = 20;

    private readonly PowerMode power;
    private readonly LabStore store;
    private readonly ProcessBoost boost;
    private readonly ITuneSystem system;
    private readonly ActivityLog log;
    private readonly ToastCenter toasts;
    private readonly Action openTune;
    private readonly bool hasBattery;
    private CancellationTokenSource? checking;
    private PowerState? state;
    private IReadOnlyList<ProbeRun> runs = [];
    private DateTimeOffset? pinned;
    private bool running, powerBusy, loaded, historyOpen = true;
    private double progress;
    private string progressTitle = "", progressDetail = "", progressLeft = "", memoryIntegrity = "";
    private string contextPlan = "", contextMemory = "", contextWhen = "", contextBusy = "", headline = "", subline = "", nudge = "";
    private ChangeKind verdict;
    private bool hasBaseline, sameSettings;
    private BaselineChoice? selectedBaseline;

    public LabViewModel(PowerMode power, LabStore store, ProcessBoost boost, ITuneSystem system, ActivityLog log, ToastCenter toasts, Action openTune, bool hasBattery)
    {
        this.power = power; this.store = store; this.boost = boost; this.system = system; this.log = log; this.toasts = toasts; this.openTune = openTune; this.hasBattery = hasBattery;
    }

    public BulkObservableCollection<MetricRow> Metrics { get; } = new();
    public BulkObservableCollection<CheckHistoryRow> History { get; } = new();
    public BulkObservableCollection<BaselineChoice> Baselines { get; } = new();
    public BulkObservableCollection<string> Differences { get; } = new();
    public BulkObservableCollection<string> Notices { get; } = new();
    public BulkObservableCollection<AppRow> Apps { get; } = new();

    public static string When(ProbeRun run) => run.When.ToLocalTime().ToString("d MMM, h:mm tt");

    // ---- Check ----

    public bool Running { get => running; private set { if (Set(ref running, value)) { Changed(nameof(CanRun)); Changed(nameof(RunText)); Changed(nameof(RunningVisibility)); Changed(nameof(IdleVisibility)); Changed(nameof(NoResultVisibility)); Changed(nameof(ResultVisibility)); Changed(nameof(NudgeVisibility)); } } }
    public bool CanRun => !running;
    public string RunText => running ? "Checking…" : Metrics.Count > 0 ? "Check again" : "Run check";
    public Visibility RunningVisibility => running ? Visibility.Visible : Visibility.Collapsed;
    public Visibility IdleVisibility => running ? Visibility.Collapsed : Visibility.Visible;
    public double Progress { get => progress; private set => Set(ref progress, value); }
    public string ProgressTitle { get => progressTitle; private set => Set(ref progressTitle, value); }
    public string ProgressDetail { get => progressDetail; private set => Set(ref progressDetail, value); }
    public string ProgressLeft { get => progressLeft; private set => Set(ref progressLeft, value); }

    public string ContextPlan { get => contextPlan; private set => Set(ref contextPlan, value); }
    public string ContextMemory { get => contextMemory; private set => Set(ref contextMemory, value); }
    public string ContextWhen { get => contextWhen; private set => Set(ref contextWhen, value); }
    public string ContextBusy { get => contextBusy; private set { if (Set(ref contextBusy, value)) Changed(nameof(ContextBusyVisibility)); } }
    public Visibility ContextBusyVisibility => contextBusy.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public string Headline { get => headline; private set => Set(ref headline, value); }
    public string Subline { get => subline; private set => Set(ref subline, value); }
    public Visibility VerdictBetterVisibility => hasBaseline && verdict == ChangeKind.Better ? Visibility.Visible : Visibility.Collapsed;
    public Visibility VerdictWorseVisibility => hasBaseline && verdict == ChangeKind.Worse ? Visibility.Visible : Visibility.Collapsed;
    public Visibility VerdictNeutralVisibility => !hasBaseline || verdict == ChangeKind.None ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CompareVisibility => hasBaseline ? Visibility.Visible : Visibility.Collapsed;
    /// <summary>Height of a result card: room for the comparison when there is one, and no empty space when there is not.</summary>
    public double CardHeight => hasBaseline ? 168 : 120;
    public Visibility SameSettingsVisibility => hasBaseline && sameSettings ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DifferencesVisibility => hasBaseline && !sameSettings ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PickBaselineVisibility => Baselines.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NoticesVisibility => Notices.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ResultVisibility => Metrics.Count > 0 && !running ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NoResultVisibility => Metrics.Count == 0 && !running ? Visibility.Visible : Visibility.Collapsed;
    public Visibility HistoryVisibility => History.Count > 0 && !running ? Visibility.Visible : Visibility.Collapsed;
    public string HistoryTitle => History.Count == 1 ? "1 earlier check" : $"{History.Count} earlier checks";
    public Visibility HistoryBodyVisibility => historyOpen ? Visibility.Visible : Visibility.Collapsed;
    /// <summary>Chevron down when closed, up when open.</summary>
    public string HistoryChevron => historyOpen ? "" : "";
    public void ToggleHistory() { historyOpen = !historyOpen; Changed(nameof(HistoryBodyVisibility)); Changed(nameof(HistoryChevron)); }

    public string Nudge { get => nudge; private set { if (Set(ref nudge, value)) Changed(nameof(NudgeVisibility)); } }
    public Visibility NudgeVisibility => nudge.Length > 0 && !running && Metrics.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Set by the picker: which earlier check to compare the newest with.</summary>
    public BaselineChoice? SelectedBaseline => selectedBaseline;

    public string CheckIntro => $"Four short tests, about {ExpectedSeconds} seconds. Close other apps first, because anything running changes the numbers.";

    // ---- Power ----

    public string PowerName => state?.Active.Name ?? "";
    public Visibility PowerFastVisibility => state is { ActiveIsFast: true } ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PowerNormalVisibility => state is { ActiveIsFast: false } ? Visibility.Visible : Visibility.Collapsed;
    public string PowerText => state switch
    {
        null => "",
        { ActiveIsFast: true } => "A performance plan is active. The processor stays ready for work instead of slowing down when idle. It uses more power and runs warmer.",
        _ => "Switch to Ultimate Performance so the processor stays ready for work instead of slowing down when idle. On a desktop the gain is small. Run a check before and after to see yours."
    };
    public string PowerWarning => hasBattery ? "This PC has a battery. A performance plan uses more of it and runs warmer, so it suits being plugged in." : "";
    public Visibility PowerWarningVisibility => PowerWarning.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public string PowerButton => powerBusy ? "Working…" : state is { ActiveIsFast: true } ? (state.Note is { } note ? $"Back to {note.PreviousName}" : "Back to Balanced") : "Use Ultimate Performance";
    public bool PowerCanClick => state is not null && !powerBusy && (state.ActiveIsFast ? state.Note is not null || state.Plans.Any(x => !PowerMode.IsFast(x)) : true);
    public bool PowerGoesBack => state is { ActiveIsFast: true };

    // ---- Memory integrity ----

    public string MemoryIntegrityText => memoryIntegrity switch
    {
        "On" => "Windows checks drivers inside a protected area. Turning it off can speed up games by a few percent, but it lowers your protection. To see what it costs on this PC: run a check, turn it off in Tune, restart, then check again.",
        "Off" => "Games run a little faster, and Windows Security shows a warning. You can turn it back on in Tune.",
        _ => "Windows decides. This PC does not say whether it is on."
    };
    public string MemoryIntegrityState => memoryIntegrity;
    public Visibility MemoryOnVisibility => memoryIntegrity == "On" ? Visibility.Visible : Visibility.Collapsed;
    public Visibility MemoryOffVisibility => memoryIntegrity == "On" ? Visibility.Collapsed : Visibility.Visible;

    public void OpenTune() => openTune();

    // ---- Apps ----

    public string AppsNote => Apps.Count == 0 ? "No apps with a window were found." : "Decide who gets the processor first when it is busy. Windows already favours the window you are using, so this rarely matters. It lasts until the app closes.";
    public string AppsCount => Apps.Count == 1 ? "1 app" : $"{Apps.Count} apps";
    public Visibility PutBackVisibility => Apps.Any(x => x.ChangedVisibility == Visibility.Visible) ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Reads the PC. Cheap, so the page calls it every time it is opened.</summary>
    public async Task LoadAsync()
    {
        try
        {
            var read = await Task.Run(() => (State: power.Read(), Memory: MemoryIntegrityNow(), Runs: store.Runs(), Apps: boost.List()));
            state = read.State; memoryIntegrity = read.Memory;
            ShowRuns(read.Runs);
            ShowApps(read.Apps);
            loaded = true;
        }
        // Anything at all: this runs unobserved when the page opens, so an unexpected failure must show on the page instead of leaving it empty.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            toasts.Show("lab-read", ToastKind.Warning, "The Lab could not read this PC", ToastCenter.Flat(ex.Message));
            log.Write(ActivityLevel.Warning, ActivityArea.App, "Lab could not read this PC", ex.ToString());
        }
        foreach (var name in new[] { nameof(PowerName), nameof(PowerFastVisibility), nameof(PowerNormalVisibility), nameof(MemoryOnVisibility), nameof(MemoryOffVisibility), nameof(PowerText), nameof(PowerButton), nameof(PowerCanClick), nameof(PowerGoesBack), nameof(MemoryIntegrityText), nameof(MemoryIntegrityState), nameof(AppsNote), nameof(AppsCount), nameof(PutBackVisibility) }) Changed(name);
        UpdateNudge();
    }

    public bool Loaded => loaded;

    private string MemoryIntegrityNow() => system.Read(TuneScope.Machine, MemoryIntegrityKey, "Enabled")?.Text switch { "1" => "On", "0" => "Off", _ => "Windows decides" };

    // ---- Running a check ----

    public async Task RunCheckAsync()
    {
        if (running) return;
        Running = true; Progress = 0; ProgressTitle = "Starting…"; ProgressDetail = "Keep the PC idle until it finishes."; ProgressLeft = $"about {ExpectedSeconds} seconds";
        checking = new CancellationTokenSource();
        var token = checking.Token;
        try
        {
            var plan = await Task.Run(() => power.Read().Active.Name);
            var memory = MemoryIntegrityNow();
            var clock = Stopwatch.StartNew();
            var step = new Progress<ProbeStep>(s => ShowStep(s, clock));
            var runner = new ProbeRunner(ProbeProfile.Full);
            var metrics = await runner.RunAsync(step, token);
            var run = new ProbeRun(DateTimeOffset.Now, plan, memory, metrics, ProbeRunner.Method, runner.Busy, runner.OnBattery);
            await Task.Run(() => store.AddRun(run));
            pinned = null; // a new check starts over from the one before it
            Running = false;
            ShowRuns(store.Runs());
            log.Write(ActivityLevel.Success, ActivityArea.App, "Performance check finished", $"{plan}; memory integrity {memory.ToLowerInvariant()}; other programs used {run.Busy:0.#}% of the processor. " + string.Join(", ", metrics.Select(x => $"{x.Id} {ProbeRunner.Format(x.Value)}")));
            toasts.Show("lab-check", ToastKind.Success, "Check finished", hasBaseline ? Headline : "Change one setting and run it again to compare.");
        }
        catch (OperationCanceledException) { toasts.Show("lab-check", ToastKind.Info, "Check stopped", "Nothing was saved."); }
        catch (OutOfMemoryException)
        {
            toasts.Show("lab-check", ToastKind.Warning, "Not enough free memory for the check", "It needs about 400 MB free. Close some apps and try again.");
            log.Write(ActivityLevel.Warning, ActivityArea.App, "Performance check could not finish", "Not enough free memory.");
        }
        // The button that starts this cannot catch anything itself, so every other failure ends here rather than closing the app.
        catch (Exception ex)
        {
            toasts.Show("lab-check", ToastKind.Warning, "The check could not finish", ToastCenter.Flat(ex.Message));
            log.Write(ActivityLevel.Warning, ActivityArea.App, "Performance check could not finish", ex.ToString());
        }
        finally
        {
            checking?.Dispose(); checking = null;
            Running = false; Progress = 0;
        }
    }

    private void ShowStep(ProbeStep step, Stopwatch clock)
    {
        Progress = step.Fraction * 100;
        if (step.Round > 0) ProgressTitle = $"Round {step.Round} of {step.Rounds}";
        else if (step.Line.StartsWith("Warm", StringComparison.Ordinal)) ProgressTitle = "Warming up";
        else if (step.Line.StartsWith("Getting", StringComparison.Ordinal)) ProgressTitle = "Getting ready";
        if (step.Line.StartsWith("  ", StringComparison.Ordinal)) ProgressDetail = step.Line.Trim();
        // The first moments say little about the pace, so the guess starts from the plan and takes over from the real pace once a fifth is done.
        var left = step.Fraction >= 0.2 ? clock.Elapsed.TotalSeconds * (1 - step.Fraction) / step.Fraction : ExpectedSeconds * (1 - step.Fraction);
        ProgressLeft = left < 3 ? "almost done" : $"about {Math.Ceiling(left):0} seconds left";
    }

    public void StopCheck() => checking?.Cancel();

    /// <summary>Which earlier check to compare the newest with. Lasts until the next check.</summary>
    public void CompareWith(ProbeRun run)
    {
        if (selectedBaseline?.Run.When == run.When) return;
        pinned = run.When;
        ShowRuns(runs);
    }

    private void ShowRuns(IReadOnlyList<ProbeRun> all)
    {
        runs = all;
        if (all.Count == 0)
        {
            Metrics.Reset([]); History.Reset([]); Baselines.Reset([]); Differences.Reset([]); Notices.Reset([]);
            ContextPlan = ""; ContextMemory = ""; ContextWhen = ""; ContextBusy = ""; Headline = ""; Subline = "";
            hasBaseline = false; verdict = ChangeKind.None; selectedBaseline = null; pinned = null;
        }
        else
        {
            var latest = all[0];
            // Checks made by an older version of the tests measure differently, so they are listed but never compared.
            var comparable = all.Skip(1).Where(x => ProbeCompare.Comparable(x, latest)).ToList();
            var baseline = comparable.FirstOrDefault(x => x.When == pinned) ?? comparable.FirstOrDefault();
            var changes = baseline is null ? [] : ProbeCompare.Between(baseline, latest);

            Metrics.Reset(latest.Metrics.Select(m => ProbeKinds.Find(m.Id) is { } kind ? new MetricRow(kind, m, changes.FirstOrDefault(c => c.Kind.Id == m.Id), baseline?.Metrics.FirstOrDefault(x => x.Id == m.Id)) : null).OfType<MetricRow>().ToList());
            ContextPlan = latest.Plan; ContextMemory = latest.MemoryIntegrity.ToLowerInvariant(); ContextWhen = When(latest);
            ContextBusy = latest.Busy is { } busy ? $"Other programs used {busy:0.#}% of the processor" : "";

            hasBaseline = baseline is not null;
            var choices = comparable.Select(x => new BaselineChoice(x)).ToList();
            // The choice in force is set first: filling the list makes the picker report its own changes, and those must find it already settled.
            selectedBaseline = choices.FirstOrDefault(x => x.Run.When == baseline?.When);
            Baselines.Reset(choices);
            Verdict(latest, baseline, changes);
            Caveats(latest, baseline);
            History.Reset(all.Skip(1).Take(8).Select(x => new CheckHistoryRow(x, ProbeCompare.Comparable(x, latest), x.When == baseline?.When)).ToList());
        }
        foreach (var name in new[] { nameof(ResultVisibility), nameof(NoResultVisibility), nameof(HistoryVisibility), nameof(HistoryTitle), nameof(RunText), nameof(SelectedBaseline), nameof(CompareVisibility), nameof(CardHeight), nameof(VerdictBetterVisibility), nameof(VerdictWorseVisibility), nameof(VerdictNeutralVisibility), nameof(SameSettingsVisibility), nameof(DifferencesVisibility), nameof(PickBaselineVisibility), nameof(NoticesVisibility) }) Changed(name);
        UpdateNudge();
    }

    /// <summary>The headline of the comparison, and which settings differ between the two checks.</summary>
    private void Verdict(ProbeRun latest, ProbeRun? baseline, IReadOnlyList<ProbeChange> changes)
    {
        Differences.Reset([]);
        sameSettings = false; verdict = ChangeKind.None;
        if (baseline is null)
        {
            Headline = "Your starting point is saved";
            Subline = runs.Count > 1 ? "Your earlier checks were made by an older version of the tests, so they cannot be compared. Change one setting below, then check again." : "Change one setting below, then check again to see what it did.";
            return;
        }
        var differences = new List<string>();
        if (baseline.Plan != latest.Plan) differences.Add($"Power plan: {baseline.Plan} → {latest.Plan}");
        if (baseline.MemoryIntegrity != latest.MemoryIntegrity) differences.Add($"Memory integrity: {baseline.MemoryIntegrity.ToLowerInvariant()} → {latest.MemoryIntegrity.ToLowerInvariant()}");
        Differences.Reset(differences);
        sameSettings = differences.Count == 0;

        if (changes.Any(x => x.Disturbed))
        {
            Headline = "Can't compare these fairly";
            Subline = "Other programs were busy during one of the two checks, so the numbers show them as much as your change. Close other apps and check again.";
            return;
        }

        var better = changes.Where(x => x.Verdict == ProbeVerdict.Better).ToList(); var worse = changes.Where(x => x.Verdict == ProbeVerdict.Worse).ToList();
        string Names(List<ProbeChange> list) => string.Join(", ", list.Select(x => $"{x.Kind.Name.ToLowerInvariant()} {Math.Abs(x.Percent):0.#}%"));
        if (better.Count == 0 && worse.Count == 0)
        {
            Headline = "No clear difference";
            Subline = sameSettings ? "Same settings both times, so this shows how much a check varies on its own." : "None of the four tests moved by more than a check normally wobbles. That is a common answer, and a fair one.";
        }
        else if (worse.Count == 0)
        {
            verdict = ChangeKind.Better;
            Headline = better.Count == 1 ? $"{better[0].Kind.Name} is {Math.Abs(better[0].Percent):0.#}% faster" : $"{better.Count} of {changes.Count} tests got faster";
            Subline = better.Count == 1 ? "The other tests show no clear difference." : $"Faster: {Names(better)}. The rest show no clear difference.";
        }
        else if (better.Count == 0)
        {
            verdict = ChangeKind.Worse;
            Headline = worse.Count == 1 ? $"{worse[0].Kind.Name} is {Math.Abs(worse[0].Percent):0.#}% slower" : $"{worse.Count} of {changes.Count} tests got slower";
            Subline = worse.Count == 1 ? "The other tests show no clear difference." : $"Slower: {Names(worse)}. The rest show no clear difference.";
        }
        else
        {
            Headline = $"{better.Count} faster, {worse.Count} slower";
            Subline = $"Faster: {Names(better)}. Slower: {Names(worse)}.";
        }
    }

    /// <summary>Reasons a pair of checks may not be a fair match, said plainly, so a surprising answer is not taken at face value.</summary>
    private void Caveats(ProbeRun latest, ProbeRun? baseline)
    {
        var notes = new List<string>();
        if (latest.Busy is > ProbeCompare.BusyLimit) notes.Add($"Other programs used {latest.Busy:0}% of the processor during the newest check, so its numbers can be lower than they should be. Close other apps and check again.");
        if (baseline?.Busy is > ProbeCompare.BusyLimit) notes.Add($"Other programs used {baseline.Busy:0}% of the processor during the check you are comparing with, so the comparison is less fair.");
        if (baseline is not null && latest.OnBattery != baseline.OnBattery && latest.OnBattery is not null && baseline.OnBattery is not null) notes.Add("One check ran on battery and the other plugged in. Windows slows a PC on battery, so they are not a fair pair.");
        else if (latest.OnBattery == true) notes.Add("This PC ran on battery during the check. Windows slows it down to save power, so plug it in for numbers that mean something.");
        if (baseline is not null && sameSettings && Metrics.Any(x => x.Kind != ChangeKind.None)) notes.Add("The settings are the same, yet a test changed. Something else moved, such as heat or a program in the background. Check again before trusting it.");
        Notices.Reset(notes);
    }

    /// <summary>After a setting changed, the newest check no longer describes this PC. Say so.</summary>
    private void UpdateNudge()
    {
        if (runs.Count == 0 || state is null) { Nudge = ""; return; }
        var latest = runs[0];
        var parts = new List<string>();
        if (latest.Plan != state.Active.Name) parts.Add($"the power plan is now {state.Active.Name}");
        if (latest.MemoryIntegrity != memoryIntegrity) parts.Add($"memory integrity is now {memoryIntegrity.ToLowerInvariant()}");
        Nudge = parts.Count == 0 ? "" : $"Since your last check, {string.Join(" and ", parts)}. Run a check to see what it did.";
    }

    public async Task ClearHistoryAsync()
    {
        await Task.Run(store.ClearRuns);
        pinned = null;
        ShowRuns([]);
    }

    // ---- Power ----

    public async Task SwitchPowerAsync()
    {
        if (powerBusy || state is null) return;
        powerBusy = true; Changed(nameof(PowerButton)); Changed(nameof(PowerCanClick));
        var goBack = state.ActiveIsFast;
        try
        {
            var result = await Task.Run(() => goBack ? power.Restore() : power.Switch());
            toasts.Show("lab-power", result.Ok ? ToastKind.Success : ToastKind.Warning, goBack ? "Power plan changed back" : "Power plan changed", result.Message, goBack ? "" : "Check now", goBack ? null : () => RunCheckAsync(), 12);
            log.Write(result.Ok ? ActivityLevel.Success : ActivityLevel.Warning, ActivityArea.App, goBack ? "Power plan put back" : "Power plan switched", result.Message);
        }
        // Includes the note on disk being unwritable (a read-only Lab folder), which used to escape and close the app.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            toasts.Show("lab-power", ToastKind.Warning, "The power plan could not be changed", ToastCenter.Flat(ex.Message));
            log.Write(ActivityLevel.Warning, ActivityArea.App, "Power plan could not be changed", ex.ToString());
        }
        finally { powerBusy = false; await LoadAsync(); }
    }

    // ---- Apps ----

    public async Task RefreshAppsAsync()
    {
        var apps = await Task.Run(boost.List);
        ShowApps(apps);
        Changed(nameof(AppsNote)); Changed(nameof(AppsCount)); Changed(nameof(PutBackVisibility));
    }

    public async Task SetAppAsync(AppRow row, BoostLevel level)
    {
        var result = await Task.Run(() => boost.Set(row.App, level));
        toasts.Show("lab-app", result.Ok ? ToastKind.Success : ToastKind.Warning, result.Ok ? "Priority changed" : $"{row.Name} was not changed", result.Message, seconds: 8);
        if (result.Ok) log.Write(ActivityLevel.Info, ActivityArea.App, "App priority changed", result.Message);
        await RefreshAppsAsync();
    }

    public async Task PutBackAllAsync()
    {
        var running = await Task.Run(boost.List);
        var done = await Task.Run(() => boost.ResetAll(running));
        toasts.Show("lab-app", ToastKind.Success, done == 0 ? "Nothing to put back" : done == 1 ? "1 app put back" : $"{done} apps put back");
        await RefreshAppsAsync();
    }

    private void ShowApps(IReadOnlyList<AppProcess> apps)
    {
        var rows = apps.Select(a => { var row = new AppRow(a); row.Update(a.Priority, boost.IsChanged(a)); return row; }).ToList();
        // A refresh of the running apps, not a new list: keep the person's place in it.
        Apps.Sync(rows, x => (x.App.Id, x.App.StartTicks));
    }

    public void Dispose() { checking?.Cancel(); checking?.Dispose(); }
}

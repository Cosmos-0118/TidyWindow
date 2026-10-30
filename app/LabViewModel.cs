using System.Diagnostics;
using Microsoft.UI.Xaml;
using TidyWindow.Next.Core;

namespace TidyWindow.Next.Desktop;

public enum ChangeKind { None, Better, Worse }

/// <summary>One line of a check: the number, how steady it was, and how it moved since the check before.</summary>
public sealed class MetricRow(ProbeKind kind, ProbeMetric metric, ProbeChange? change)
{
    public string Name => kind.Name;
    public string What => kind.What;
    public string Value => $"{ProbeRunner.Format(metric.Value)} {kind.Unit}";
    public string Steadiness => $"rounds differed by {metric.Spread:0.#}%";
    public bool HasChange => change is not null;

    public ChangeKind Kind => change?.Verdict switch { ProbeVerdict.Better => ChangeKind.Better, ProbeVerdict.Worse => ChangeKind.Worse, _ => ChangeKind.None };
    public string ChangeText => change is null ? "" : change.Verdict switch
    {
        ProbeVerdict.Better => $"{Math.Abs(change.Percent):0.#}% faster",
        ProbeVerdict.Worse => $"{Math.Abs(change.Percent):0.#}% slower",
        _ => "no clear difference"
    };
    public Visibility BetterVisibility => Kind == ChangeKind.Better ? Visibility.Visible : Visibility.Collapsed;
    public Visibility WorseVisibility => Kind == ChangeKind.Worse ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SameVisibility => HasChange && Kind == ChangeKind.None ? Visibility.Visible : Visibility.Collapsed;
}

public sealed record CheckHistoryRow(string When, string Settings, string Numbers);

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
    private bool running, powerBusy, loaded;
    private string live = "", compareText = "", contextPlan = "", contextMemory = "", contextWhen = "", memoryIntegrity = "";

    public LabViewModel(PowerMode power, LabStore store, ProcessBoost boost, ITuneSystem system, ActivityLog log, ToastCenter toasts, Action openTune, bool hasBattery)
    {
        this.power = power; this.store = store; this.boost = boost; this.system = system; this.log = log; this.toasts = toasts; this.openTune = openTune; this.hasBattery = hasBattery;
    }

    public BulkObservableCollection<string> Lines { get; } = new();
    public BulkObservableCollection<MetricRow> Metrics { get; } = new();
    public BulkObservableCollection<CheckHistoryRow> History { get; } = new();
    public BulkObservableCollection<AppRow> Apps { get; } = new();

    // ---- Check ----

    public bool Running { get => running; private set { if (Set(ref running, value)) { Changed(nameof(CanRun)); Changed(nameof(RunText)); Changed(nameof(RunningVisibility)); Changed(nameof(IdleVisibility)); } } }
    public bool CanRun => !running;
    public string RunText => running ? "Checking…" : Metrics.Count > 0 ? "Check again" : "Run check";
    public Visibility RunningVisibility => running ? Visibility.Visible : Visibility.Collapsed;
    public Visibility IdleVisibility => running ? Visibility.Collapsed : Visibility.Visible;
    public string Live { get => live; private set => Set(ref live, value); }
    public string ContextPlan { get => contextPlan; private set => Set(ref contextPlan, value); }
    public string ContextMemory { get => contextMemory; private set => Set(ref contextMemory, value); }
    public string ContextWhen { get => contextWhen; private set => Set(ref contextWhen, value); }
    public string CompareText { get => compareText; private set { if (Set(ref compareText, value)) Changed(nameof(CompareVisibility)); } }
    public Visibility CompareVisibility => compareText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ResultVisibility => Metrics.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NoResultVisibility => Metrics.Count == 0 && !running ? Visibility.Visible : Visibility.Collapsed;
    public Visibility HistoryVisibility => History.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public string CheckIntro => "Four short tests that take about 15 seconds. Close other apps first, because anything running changes the numbers. To see whether a setting helps, run a check, change one thing, then check again.";

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
        "On" => "On. Windows checks drivers inside a protected area. Turning it off can speed up games by a few percent, but it lowers your protection.",
        "Off" => "Off. Games run a little faster, and Windows Security shows a warning. You can turn it back on in Tune.",
        _ => "Windows decides. This PC does not say whether it is on."
    };
    public string MemoryIntegrityState => memoryIntegrity;
    public Visibility MemoryOnVisibility => memoryIntegrity == "On" ? Visibility.Visible : Visibility.Collapsed;
    public Visibility MemoryOffVisibility => memoryIntegrity == "On" ? Visibility.Collapsed : Visibility.Visible;

    public void OpenTune() => openTune();

    // ---- Apps ----

    public string AppsNote => Apps.Count == 0 ? "No apps with a window were found." : "Decide who gets the processor first when it is busy. Windows already favours the window you are using, so this rarely matters. It lasts until the app closes.";
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
        catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException)
        {
            toasts.Show("lab-read", ToastKind.Warning, "The Lab could not read this PC", ToastCenter.Brief(ex.Message));
            log.Write(ActivityLevel.Warning, ActivityArea.App, "Lab could not read this PC", ex.Message);
        }
        foreach (var name in new[] { nameof(PowerName), nameof(PowerFastVisibility), nameof(PowerNormalVisibility), nameof(MemoryOnVisibility), nameof(MemoryOffVisibility), nameof(PowerText), nameof(PowerButton), nameof(PowerCanClick), nameof(PowerGoesBack), nameof(MemoryIntegrityText), nameof(MemoryIntegrityState), nameof(AppsNote), nameof(PutBackVisibility) }) Changed(name);
    }

    public bool Loaded => loaded;

    private string MemoryIntegrityNow() => system.Read(TuneScope.Machine, MemoryIntegrityKey, "Enabled")?.Text switch { "1" => "On", "0" => "Off", _ => "Windows decides" };

    // ---- Running a check ----

    public async Task RunCheckAsync()
    {
        if (running) return;
        Running = true; Lines.Reset([]); Live = "Starting…";
        checking = new CancellationTokenSource();
        var token = checking.Token;
        try
        {
            var plan = await Task.Run(() => power.Read().Active.Name);
            var memory = MemoryIntegrityNow();
            var lines = new List<string>();
            var progress = new Progress<string>(line => { lines.Add(line); Lines.Reset(lines.TakeLast(10).ToList()); Live = line.Trim(); });
            var metrics = await new ProbeRunner(ProbeProfile.Full).RunAsync(progress, token);
            var run = new ProbeRun(DateTimeOffset.Now, plan, memory, metrics);
            await Task.Run(() => store.AddRun(run));
            ShowRuns(store.Runs());
            log.Write(ActivityLevel.Success, ActivityArea.App, "Performance check finished", $"{plan}; memory integrity {memory.ToLowerInvariant()}. " + string.Join(", ", metrics.Select(x => $"{x.Id} {ProbeRunner.Format(x.Value)}")));
            toasts.Show("lab-check", ToastKind.Success, "Check finished", Metrics.Any(x => x.HasChange) ? "The numbers are below, next to your last check." : "Change one setting and run it again to compare.");
        }
        catch (OperationCanceledException) { toasts.Show("lab-check", ToastKind.Info, "Check stopped", "Nothing was saved."); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            toasts.Show("lab-check", ToastKind.Warning, "The check could not finish", ToastCenter.Brief(ex.Message));
            log.Write(ActivityLevel.Warning, ActivityArea.App, "Performance check could not finish", ex.Message);
        }
        finally
        {
            checking?.Dispose(); checking = null;
            Running = false; Live = ""; Changed(nameof(NoResultVisibility));
        }
    }

    public void StopCheck() => checking?.Cancel();

    private void ShowRuns(IReadOnlyList<ProbeRun> runs)
    {
        if (runs.Count == 0)
        {
            Metrics.Reset([]); History.Reset([]); ContextPlan = ""; ContextMemory = ""; ContextWhen = ""; CompareText = "";
        }
        else
        {
            var latest = runs[0]; var before = runs.Count > 1 ? runs[1] : null;
            var changes = before is null ? [] : ProbeCompare.Between(before, latest);
            Metrics.Reset(latest.Metrics.Select(m => ProbeKinds.Find(m.Id) is { } kind ? new MetricRow(kind, m, changes.FirstOrDefault(c => c.Kind.Id == m.Id)) : null).OfType<MetricRow>().ToList());
            ContextPlan = latest.Plan; ContextMemory = latest.MemoryIntegrity.ToLowerInvariant(); ContextWhen = latest.When.ToLocalTime().ToString("d MMM, h:mm tt");
            CompareText = before is null ? "" : $"Compared with your check on {before.When.ToLocalTime():d MMM, h:mm tt} (power plan {before.Plan}, memory integrity {before.MemoryIntegrity.ToLowerInvariant()})." +
                (before.Plan == latest.Plan && before.MemoryIntegrity == latest.MemoryIntegrity ? " The settings are the same, so this shows how much a check varies by itself." : "") +
                " A change only counts when it is bigger than the checks normally wobble.";
            History.Reset(runs.Skip(1).Take(6).Select(r => new CheckHistoryRow(r.When.ToLocalTime().ToString("d MMM, h:mm tt"), $"{r.Plan} · memory integrity {r.MemoryIntegrity.ToLowerInvariant()}", string.Join("   ", r.Metrics.Select(x => ProbeKinds.Find(x.Id) is { } k ? $"{k.Name.Split(' ')[0]} {ProbeRunner.Format(x.Value)}" : "")))).ToList());
        }
        foreach (var name in new[] { nameof(ResultVisibility), nameof(NoResultVisibility), nameof(HistoryVisibility), nameof(RunText) }) Changed(name);
    }

    public async Task ClearHistoryAsync()
    {
        await Task.Run(store.ClearRuns);
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
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or IOException)
        {
            toasts.Show("lab-power", ToastKind.Warning, "The power plan could not be changed", ToastCenter.Brief(ex.Message));
            log.Write(ActivityLevel.Warning, ActivityArea.App, "Power plan could not be changed", ex.Message);
        }
        finally { powerBusy = false; await LoadAsync(); }
    }

    // ---- Apps ----

    public async Task RefreshAppsAsync()
    {
        var apps = await Task.Run(boost.List);
        ShowApps(apps);
        Changed(nameof(AppsNote)); Changed(nameof(PutBackVisibility));
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
        Apps.Reset(rows);
    }

    public void Dispose() { checking?.Cancel(); checking?.Dispose(); }
}

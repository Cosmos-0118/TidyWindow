using System.Collections.ObjectModel;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using TidyWindow.Next.Core;

namespace TidyWindow.Next.Desktop;

public static class ByteSize
{
    public static string Format(long bytes) => bytes switch
    {
        >= 1073741824 => $"{bytes / 1073741824d:0.##} GB",
        >= 1048576 => $"{bytes / 1048576d:0.#} MB",
        >= 1024 => $"{bytes / 1024d:0.#} KB",
        _ => $"{bytes} B"
    };
}

public sealed class CleanupRow(CleanupFinding finding, Action<CleanupRow> changed) : ObservableState
{
    // A row whose size is unknown until it runs, or that needs administrator permission, is offered but never ticked for the user:
    // ticking it is what asks for permission, so the prompt is always the person's own doing.
    private bool selected = finding.Rule.Level == CleanupLevel.Recommended && !finding.Unmeasured && !finding.NeedsAdmin && !finding.Rule.IsSystemAction;
    private MeasuredItem? exact;
    private bool measuring, declined, allowed;
    public CleanupFinding Finding { get; } = finding;
    public bool IsSelected { get => selected; set { if (Set(ref selected, value)) changed(this); } }
    public string Name => Finding.Rule.Name;
    public string Detail => Finding.AppRunning ? Finding.Rule.Detail + " The app is open, so files it is using will be skipped." : Finding.Rule.Detail;
    /// <summary>What the elevated helper found, once the person has allowed it to look.</summary>
    public MeasuredItem? Exact { get => exact; set { exact = value; NotifySize(); } }
    public bool Measuring { get => measuring; set { if (measuring != value) { measuring = value; NotifySize(); } } }
    /// <summary>Permission has been given, so what is left is the measuring itself.</summary>
    public bool Allowed { get => allowed; set { if (allowed != value) { allowed = value; NotifySize(); } } }
    public bool Declined { get => declined; set { if (declined != value) { declined = value; NotifySize(); } } }
    // A row Windows can only size after cleaning has nothing to measure, so ticking it never asks for permission early.
    public bool NeedsMeasuring => NeedsAdmin && exact is null && !Finding.Unmeasured;
    public long Bytes => exact?.Bytes ?? Finding.Bytes;
    private void NotifySize() { Changed(nameof(SizeText)); Changed(nameof(FilesText)); Changed(nameof(AutomationName)); }
    public string SizeText
    {
        get
        {
            if (measuring) return "Measuring…";
            if (exact is { } found) return found.Unmeasured ? "Size shown after" : found.Bytes > 0 ? ByteSize.Format(found.Bytes) : "Empty";
            if (NeedsAdmin && !Finding.Unmeasured) return declined ? "Needs permission" : Finding.Bytes > 0 ? "At least " + ByteSize.Format(Finding.Bytes) : "Tick to measure";
            return Finding.Unmeasured ? "Size shown after" : Finding.Bytes > 0 || Finding.Rule.Action == CleanupAction.RecycleBin ? (Finding.Unreadable > 0 ? "At least " : "") + ByteSize.Format(Finding.Bytes) : "Empty";
        }
    }
    public string FilesText
    {
        get
        {
            if (measuring) return !allowed ? "asking for permission" : "measuring";
            if (exact is { } found) return found.Unmeasured ? "cleaning" : found.Files == 1 ? "1 file" : $"{found.Files:N0} files";
            if (NeedsAdmin && !Finding.Unmeasured) return declined ? "permission declined" : "needs admin to measure";
            return Finding.Unmeasured ? "cleaning" : Finding.Files == 1 ? "1 file" : $"{Finding.Files:N0} files";
        }
    }
    public bool NeedsAdmin => Finding.NeedsAdmin || Finding.Rule.IsSystemAction;
    public Visibility AdminVisibility => NeedsAdmin ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ReviewVisibility => Finding.Rule.Level == CleanupLevel.Review ? Visibility.Visible : Visibility.Collapsed;
    public string ReviewText => Finding.Rule.Quarantine ? "Review · can be undone" : "Review";
    public string AutomationName => $"{Name}, {SizeText}";
    private string stateText = "";
    /// <summary>What is happening to this row during a clean: Waiting, Working…, Cleaned. Empty otherwise.</summary>
    public string StateText { get => stateText; set { if (Set(ref stateText, value)) Changed(nameof(StateVisibility)); } }
    public Visibility StateVisibility => stateText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
}

public sealed class CleanupGroup(string name) : ObservableState
{
    public string Name { get; } = name;
    public ObservableCollection<CleanupRow> Rows { get; } = [];
}

public sealed class CleanupViewModel : ObservableState, IDisposable
{
    private static readonly string[] GroupOrder = ["Windows", "Browsers", "Apps", "Games", "Developer", "Files"];
    private readonly IReadOnlyList<CleanupRule> rules;
    private readonly CleanupScanner scanner;
    private readonly CleanupCleaner cleaner;
    private readonly CleanupQuarantine quarantine;
    private readonly SystemCleanLauncher system;
    private readonly ActivityLog log;
    private readonly Action<Action> post;
    private readonly ToastCenter toasts;
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? work;
    private long generation;
    private bool scanning, cleaning, scanned, measuringAdmin;
    private long selectedBytes;
    private int selectedCount, selectedAdmin;
    private string status = "Scan to see what can be cleaned";

    public ObservableCollection<CleanupGroup> Groups { get; } = [];
    public CleanupViewModel(IReadOnlyList<CleanupRule> rules, CleanupScanner scanner, CleanupCleaner cleaner, CleanupQuarantine quarantine, SystemCleanLauncher system, ActivityLog log, Action<Action> post, ToastCenter toasts)
    {
        this.rules = rules; this.scanner = scanner; this.cleaner = cleaner; this.quarantine = quarantine; this.system = system; this.log = log; this.post = post; this.toasts = toasts;
    }

    private string cleanStep = "", cleanHint = "", cleanElapsed = "0:00";
    private double cleanValue;
    private bool cleanIndeterminate, progressShown;
    private int stepsDone, stepsTotal;

    public string CleanStep { get => cleanStep; private set => Set(ref cleanStep, value); }
    public string CleanHint { get => cleanHint; private set { Set(ref cleanHint, value); Changed(nameof(HintVisibility)); } }
    public string CleanElapsed { get => cleanElapsed; private set => Set(ref cleanElapsed, value); }
    public double CleanValue { get => cleanValue; private set => Set(ref cleanValue, value); }
    public bool CleanIndeterminate { get => cleanIndeterminate; private set => Set(ref cleanIndeterminate, value); }
    public Visibility HintVisibility => cleanHint.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CleaningVisibility => progressShown ? Visibility.Visible : Visibility.Collapsed;

    public bool Scanned => scanned;
    public bool Busy => scanning || cleaning || measuringAdmin;
    public bool CanScan => !Busy;
    public bool CanClean => !Busy && selectedCount > 0;
    public string ScanLabel => scanning ? "Scanning…" : scanned ? "Scan again" : "Scan";
    public string CleanLabel => cleaning ? "Cleaning…" : selectedBytes > 0 ? $"Clean {ByteSize.Format(selectedBytes)}" : "Clean";
    public string Status { get => status; private set => Set(ref status, value); }
    public bool ScanOverlay => scanning && Groups.Count == 0;
    public Visibility EmptyVisibility => scanned && !scanning && Groups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility UndoVisibility => UndoText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public string UndoText { get; private set; } = "";
    public string AdminNote => selectedAdmin == 0 ? "" : $"{selectedAdmin} selected {(selectedAdmin == 1 ? "item needs" : "items need")} administrator permission. Windows asks when you tick one, to measure it, and once more when you clean.";
    public Visibility AdminVisibility => selectedAdmin > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Names of selected items that cannot be undone and are not plain caches; empty when the clean needs no confirmation.</summary>
    public string ConfirmText
    {
        get
        {
            var risky = SelectedRows().Where(x => x.Finding.Rule.Level == CleanupLevel.Review && !x.Finding.Rule.Quarantine).Select(x => x.Name).ToList();
            return risky.Count == 0 ? "" : $"These are marked Review and cannot be undone: {string.Join(", ", risky)}.";
        }
    }

    private IEnumerable<CleanupRow> SelectedRows() => Groups.SelectMany(x => x.Rows).Where(x => x.IsSelected);

    private void Notify()
    {
        foreach (var name in new[] { nameof(Busy), nameof(CanScan), nameof(CanClean), nameof(ScanLabel), nameof(CleanLabel), nameof(ScanOverlay), nameof(EmptyVisibility), nameof(CleaningVisibility) }) Changed(name);
    }

    private void RowChanged(CleanupRow row)
    {
        Recount();
        // Ticking something that needs administrator permission is the person asking to see its size.
        if (row.IsSelected && row.NeedsMeasuring && !row.Measuring && !Busy) _ = MeasureAdminAsync();
    }

    private void Recount()
    {
        var chosen = SelectedRows().ToList();
        selectedBytes = chosen.Sum(x => x.Bytes); selectedCount = chosen.Count; selectedAdmin = chosen.Count(x => x.NeedsAdmin);
        Changed(nameof(CanClean)); Changed(nameof(CleanLabel)); Changed(nameof(AdminNote)); Changed(nameof(AdminVisibility));
    }

    /// <summary>
    /// Asks Windows for administrator permission once, then sizes every administrator row at once, so ticking the next one asks nothing more.
    /// Declining leaves the rows as they were, unticks what was just ticked, and never blocks the rest of the page.
    /// </summary>
    private async Task MeasureAdminAsync()
    {
        if (Busy || lifetime.IsCancellationRequested) return;
        var rows = Groups.SelectMany(x => x.Rows).Where(x => x.NeedsMeasuring).ToList();
        if (rows.Count == 0) return;
        var asked = rows.Where(x => x.IsSelected).ToList();
        var current = generation;
        var before = Status;
        work?.Dispose(); work = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var token = work.Token;
        measuringAdmin = true;
        foreach (var row in rows) { row.Declined = false; row.Allowed = false; row.Measuring = true; }
        Status = "Waiting for administrator permission to measure…";
        Notify();
        string? problem = null;
        var byId = rows.ToDictionary(x => x.Finding.Rule.Id);
        try
        {
            // Sizes are shown as the helper reports them rather than all at the end.
            var report = await system.MeasureAsync(rows.Select(x => x.Finding.Rule.Id).ToList(),
                onItem: item => post(() =>
                {
                    if (current != generation || !byId.TryGetValue(item.Id, out var row)) return;
                    row.Exact = item; row.Measuring = false;
                    Recount();
                }),
                onAllowed: () => post(() =>
                {
                    if (current != generation) return;
                    foreach (var row in rows.Where(x => x.Exact is null)) row.Allowed = true;
                    Status = "Measuring…";
                    Notify();
                }),
                cancellationToken: token);
            if (current != generation) return;
            if (report.Declined) problem = "Administrator permission was declined, so the size of system areas is not shown. Tick the item again to retry.";
            else if (report.Error is not null) problem = "The sizes could not be measured: " + ToastCenter.Brief(report.Error);
            else
            {
                foreach (var row in rows) row.Exact = report.Items.FirstOrDefault(x => x.Id == row.Finding.Rule.Id);
                var total = report.Items.Where(x => !x.Unmeasured).Sum(x => x.Bytes);
                log.Write(ActivityLevel.Info, ActivityArea.App, "Cleanup measured administrator areas", $"{ByteSize.Format(total)} found in {report.Items.Count(x => x.Bytes > 0)} of {rows.Count} areas.");
            }
        }
        catch (OperationCanceledException) { problem = "Measuring was stopped."; }
        catch (Exception ex)
        {
            problem = "The sizes could not be measured: " + ToastCenter.Brief(ex.Message);
            log.Write(ActivityLevel.Warning, ActivityArea.App, "Cleanup could not measure administrator areas", ex.Message);
        }
        finally
        {
            foreach (var row in rows) { row.Measuring = false; if (problem is not null && row.Exact is null) row.Declined = true; }
            // What was ticked only to be measured goes back to unticked when that failed.
            if (problem is not null) foreach (var row in asked) row.IsSelected = false;
            measuringAdmin = false;
            Status = before;
            Notify(); Recount();
        }
        if (problem is not null) toasts.Show("clean", ToastKind.Info, "Sizes not measured", problem);
    }

    public async Task ScanAsync()
    {
        if (Busy || lifetime.IsCancellationRequested) return;
        var current = ++generation;
        work?.Dispose(); work = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var token = work.Token;
        scanning = true; selectedBytes = 0; selectedCount = 0; selectedAdmin = 0;
        Groups.Clear();
        Status = "Scanning…";
        Notify(); Changed(nameof(AdminNote)); Changed(nameof(AdminVisibility));
        var empty = 0;
        try
        {
            var progress = new Reporter(finding => post(() =>
            {
                if (current != generation) return;
                if (finding.HasContent) Add(finding);
                else empty++;
            }));
            var findings = await scanner.ScanAsync(rules, progress, token);
            if (current != generation) return;
            scanned = true;
            var total = findings.Where(x => x.Skipped is null).Sum(x => x.Bytes);
            Status = total == 0 ? "Nothing to clean right now" : $"{ByteSize.Format(total)} can be cleaned. Items marked Review or Needs admin are not selected until you tick them.";
            log.Write(ActivityLevel.Info, ActivityArea.App, "Cleanup scan finished", $"{ByteSize.Format(total)} found in {findings.Count(x => x.HasContent)} areas; {empty} areas were already clean; {findings.Count(x => x.NeedsAdmin && x.HasContent)} need administrator permission to clean.");
        }
        catch (OperationCanceledException) { Status = "Scan stopped"; }
        catch (Exception ex)
        {
            Status = "The scan could not finish: " + ex.Message;
            log.Write(ActivityLevel.Warning, ActivityArea.App, "Cleanup scan could not finish", ex.Message);
        }
        finally
        {
            if (current == generation)
            {
                scanning = false; RefreshUndo(); Notify();
                Changed(nameof(AdminNote)); Changed(nameof(AdminVisibility));
            }
        }
    }

    private void Add(CleanupFinding finding)
    {
        var group = Groups.FirstOrDefault(x => x.Name == finding.Rule.Group);
        if (group is null)
        {
            group = new CleanupGroup(finding.Rule.Group);
            var position = Groups.TakeWhile(x => Rank(x.Name) < Rank(group.Name)).Count();
            Groups.Insert(position, group);
            Changed(nameof(ScanOverlay)); Changed(nameof(EmptyVisibility));
        }
        var row = new CleanupRow(finding, RowChanged);
        var index = group.Rows.TakeWhile(x => x.Finding.Bytes >= finding.Bytes).Count();
        group.Rows.Insert(index, row);
        if (row.IsSelected) RowChanged(row);
    }

    private static int Rank(string group) { var i = Array.IndexOf(GroupOrder, group); return i < 0 ? GroupOrder.Length : i; }

    public async Task CleanAsync()
    {
        if (Busy || selectedCount == 0 || lifetime.IsCancellationRequested) return;
        var chosen = SelectedRows().Select(x => x.Finding).ToList();
        // Anything that needs administrator rights goes to the elevated helper in one go; everything else stays in this process.
        var elevated = chosen.Where(x => x.NeedsAdmin || x.Rule.IsSystemAction).ToList();
        var normal = chosen.Except(elevated).ToList();
        work?.Dispose(); work = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var token = work.Token;
        var rows = SelectedRows().ToList();
        var timer = Stopwatch.StartNew();
        using var clockStop = new CancellationTokenSource();
        stepsDone = 0; stepsTotal = chosen.Count; progressShown = true; CleanElapsed = "0:00";
        foreach (var row in rows) row.StateText = "Waiting";
        BeginProgress(normal.Count > 0 ? "Cleaning…" : "Waiting for administrator permission…", normal.Count > 0 ? "" : PermissionHint, indeterminate: normal.Count == 0);
        cleaning = true; Notify();
        Status = "Cleaning…";
        _ = RunClockAsync(timer, clockStop.Token);
        CleanupResult? result = null;
        try
        {
            var progress = new Reporter<CleanupProgress>(step => post(() => { if (cleaning) OnStep(rows, step); }));
            result = await cleaner.CleanAsync(normal, progress, token);
            var notes = new List<string>();
            if (elevated.Count > 0)
            {
                // The permission prompt and Windows' own tools give no steady output, so the bar keeps moving on its own meanwhile.
                BeginProgress("Waiting for administrator permission…", PermissionHint, indeterminate: true);
                var report = await system.RunAsync(elevated.Select(x => x.Rule.Id).ToList(), progress, token);
                result = result with { FreedBytes = result.FreedBytes + report.Result.FreedBytes, Removed = result.Removed + report.Result.Removed, InUse = result.InUse + report.Result.InUse, Denied = result.Denied + report.Result.Denied, Failed = result.Failed + report.Result.Failed };
                notes.AddRange(report.Notes);
            }
            result = result with { Elapsed = timer.Elapsed };
            ShowResult(result, notes);
            log.Write(result.Skipped > 0 ? ActivityLevel.Warning : ActivityLevel.Success, ActivityArea.App, $"Cleanup freed {ByteSize.Format(result.FreedBytes)}",
                $"{result.Removed:N0} files removed in {result.Elapsed.TotalSeconds:0.0} s. {result.InUse} in use, {result.Denied} protected, {result.Failed} failed." + (result.HeldBytes > 0 ? $" {ByteSize.Format(result.HeldBytes)} held for undo." : ""));
        }
        catch (OperationCanceledException) { toasts.Show("clean", ToastKind.Info, "Cleaning stopped", "Anything already removed stays removed."); }
        catch (Exception ex)
        {
            toasts.Show("clean", ToastKind.Warning, "Cleaning could not finish", ToastCenter.Brief(ex.Message));
            log.Write(ActivityLevel.Error, ActivityArea.App, "Cleanup could not finish", ex.Message);
        }
        finally
        {
            clockStop.Cancel();
            progressShown = false; cleaning = false; Notify();
            foreach (var row in rows) row.StateText = "";
            Changed(nameof(CleaningVisibility));
        }
        await ScanAsync();
    }

    private const string PermissionHint = "Approve the Windows prompt to continue. It may be waiting behind this window.";

    private void BeginProgress(string step, string hint, bool indeterminate)
    {
        CleanStep = step; CleanHint = hint; CleanIndeterminate = indeterminate;
        CleanValue = stepsTotal == 0 ? 0 : stepsDone * 100d / stepsTotal;
        Changed(nameof(CleaningVisibility));
    }

    private void OnStep(IReadOnlyList<CleanupRow> rows, CleanupProgress step)
    {
        var row = rows.FirstOrDefault(x => x.Name == step.Name);
        if (step.Starting)
        {
            if (row is not null) row.StateText = "Working…";
            BeginProgress($"Cleaning {step.Name}", "Windows is doing this itself and it can take several minutes. Keep TidyWindow open.", indeterminate: true);
            return;
        }
        if (row is not null && row.StateText != "Cleaned") { row.StateText = "Cleaned"; stepsDone = Math.Min(stepsTotal, stepsDone + 1); }
        BeginProgress($"Cleaned {step.Name}", $"{ByteSize.Format(step.FreedBytes)} freed so far", indeterminate: false);
        Status = $"Cleaning · {stepsDone} of {stepsTotal} done";
    }

    private async Task RunClockAsync(Stopwatch timer, CancellationToken stop)
    {
        using var tick = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await tick.WaitForNextTickAsync(stop))
            {
                var elapsed = timer.Elapsed;
                post(() => CleanElapsed = elapsed.TotalHours >= 1 ? $"{(int)elapsed.TotalHours}:{elapsed:mm\\:ss}" : $"{(int)elapsed.TotalMinutes}:{elapsed:ss}");
            }
        }
        catch (OperationCanceledException) { }
    }

    private void ShowResult(CleanupResult result, IReadOnlyList<string> resultNotes)
    {
        var took = result.Elapsed.TotalMinutes >= 1 ? $"{(int)result.Elapsed.TotalMinutes} min {result.Elapsed.Seconds} s" : $"{result.Elapsed.TotalSeconds:0.0} s";
        var parts = new List<string> { $"Finished in {took}" };
        if (result.HeldBytes > 0) parts.Add($"{ByteSize.Format(result.HeldBytes)} held for 14 days");
        if (result.InUse > 0) parts.Add(result.InUse == 1 ? "1 file in use, skipped" : $"{result.InUse:N0} files in use, skipped");
        var missed = result.Denied + result.Failed;
        if (missed > 0) parts.Add($"{missed:N0} could not be removed");
        var detail = string.Join(" · ", parts) + (resultNotes.Count > 0 ? "\n" + string.Join("\n", resultNotes) : "");
        toasts.Show("clean", missed > 0 ? ToastKind.Warning : ToastKind.Success, ByteSize.Format(result.FreedBytes) + " freed", detail, result.HeldBytes > 0 ? "Undo" : "", result.HeldBytes > 0 ? UndoAsync : null, 14);
    }

    public async Task UndoAsync()
    {
        if (Busy || quarantine.Latest() is not { } batch) return;
        cleaning = true; Notify();
        RestoreResult restored;
        try { restored = await Task.Run(() => quarantine.Restore(batch.Id)); }
        finally { cleaning = false; Notify(); }
        var summary = restored.Conflicts > 0 ? $"Restored {restored.Restored:N0} files. {restored.Conflicts:N0} were left held because a file already exists there." : $"Restored {restored.Restored:N0} files.";
        toasts.Show("clean", restored.Conflicts > 0 ? ToastKind.Warning : ToastKind.Success, "Cleanup undone", summary);
        log.Write(ActivityLevel.Info, ActivityArea.App, "Cleanup undone", summary);
        await ScanAsync();
    }

    private void RefreshUndo()
    {
        var batch = quarantine.Latest();
        UndoText = batch is null ? "" : $"Undo · {batch.Files:N0} files ({ByteSize.Format(batch.Bytes)}) held until {(batch.Created + CleanupQuarantine.Retention):d MMM}";
        Changed(nameof(UndoText)); Changed(nameof(UndoVisibility));
    }

    public void Cancel() { work?.Cancel(); }
    public void Dispose() { lifetime.Cancel(); work?.Dispose(); lifetime.Dispose(); }

    private sealed class Reporter(Action<CleanupFinding> action) : IProgress<CleanupFinding> { public void Report(CleanupFinding value) => action(value); }
    private sealed class Reporter<T>(Action<T> action) : IProgress<T> { public void Report(T value) => action(value); }
}

using System.Collections.ObjectModel;
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
    private bool selected = finding.Rule.Level == CleanupLevel.Recommended;
    public CleanupFinding Finding { get; } = finding;
    public bool IsSelected { get => selected; set { if (Set(ref selected, value)) changed(this); } }
    public string Name => Finding.Rule.Name;
    public string Detail => Finding.AppRunning ? Finding.Rule.Detail + " The app is open, so files it is using will be skipped." : Finding.Rule.Detail;
    public string SizeText => Finding.Rule.Action == CleanupAction.RecycleBin || Finding.Bytes > 0 ? ByteSize.Format(Finding.Bytes) : "Empty";
    public string FilesText => Finding.Files == 1 ? "1 file" : $"{Finding.Files:N0} files";
    public Visibility ReviewVisibility => Finding.Rule.Level == CleanupLevel.Review ? Visibility.Visible : Visibility.Collapsed;
    public string ReviewText => Finding.Rule.Quarantine ? "Review · can be undone" : "Review";
    public string AutomationName => $"{Name}, {SizeText}";
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
    private readonly ActivityLog log;
    private readonly Action<Action> post;
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? work;
    private long generation;
    private bool scanning, cleaning, scanned;
    private long selectedBytes;
    private int needAdmin;
    private string status = "Scan to see what can be cleaned";
    private string summary = "";

    public ObservableCollection<CleanupGroup> Groups { get; } = [];
    public CleanupViewModel(IReadOnlyList<CleanupRule> rules, CleanupScanner scanner, CleanupCleaner cleaner, CleanupQuarantine quarantine, ActivityLog log, Action<Action> post)
    {
        this.rules = rules; this.scanner = scanner; this.cleaner = cleaner; this.quarantine = quarantine; this.log = log; this.post = post;
    }

    public bool Scanned => scanned;
    public bool Busy => scanning || cleaning;
    public bool CanScan => !Busy;
    public bool CanClean => !Busy && selectedBytes > 0;
    public string ScanLabel => scanning ? "Scanning…" : scanned ? "Scan again" : "Scan";
    public string CleanLabel => cleaning ? "Cleaning…" : selectedBytes > 0 ? $"Clean {ByteSize.Format(selectedBytes)}" : "Clean";
    public string Status { get => status; private set => Set(ref status, value); }
    public string Summary { get => summary; private set { Set(ref summary, value); Changed(nameof(SummaryVisibility)); } }
    public Visibility SummaryVisibility => Summary.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public bool ScanOverlay => scanning && Groups.Count == 0;
    public Visibility EmptyVisibility => scanned && !scanning && Groups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility UndoVisibility => UndoText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public string UndoText { get; private set; } = "";
    public string AdminNote => needAdmin == 0 ? "" : $"{needAdmin} Windows system {(needAdmin == 1 ? "area needs" : "areas need")} administrator rights and {(needAdmin == 1 ? "was" : "were")} skipped.";
    public Visibility AdminVisibility => needAdmin > 0 ? Visibility.Visible : Visibility.Collapsed;

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
        foreach (var name in new[] { nameof(Busy), nameof(CanScan), nameof(CanClean), nameof(ScanLabel), nameof(CleanLabel), nameof(ScanOverlay), nameof(EmptyVisibility) }) Changed(name);
    }

    private void RowChanged(CleanupRow row)
    {
        selectedBytes = SelectedRows().Sum(x => x.Finding.Bytes);
        Changed(nameof(CanClean)); Changed(nameof(CleanLabel));
    }

    public async Task ScanAsync(bool keepSummary = false)
    {
        if (Busy || lifetime.IsCancellationRequested) return;
        var current = ++generation;
        work?.Dispose(); work = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var token = work.Token;
        scanning = true; selectedBytes = 0; needAdmin = 0;
        Groups.Clear();
        if (!keepSummary) Summary = "";
        Status = "Scanning…";
        Notify(); Changed(nameof(AdminNote)); Changed(nameof(AdminVisibility));
        var empty = 0;
        try
        {
            var progress = new Reporter(finding => post(() =>
            {
                if (current != generation) return;
                if (finding.Skipped is not null) needAdmin++;
                else if (finding.HasContent) Add(finding);
                else empty++;
            }));
            var findings = await scanner.ScanAsync(rules, progress, token);
            if (current != generation) return;
            scanned = true;
            var total = findings.Where(x => x.Skipped is null).Sum(x => x.Bytes);
            Status = total == 0 ? "Nothing to clean right now" : $"{ByteSize.Format(total)} can be cleaned. Items marked Review are not selected.";
            log.Write(ActivityLevel.Info, ActivityArea.App, "Cleanup scan finished", $"{ByteSize.Format(total)} found in {findings.Count(x => x.HasContent)} areas; {empty} areas were already clean; {findings.Count(x => x.Skipped is not null)} need administrator rights.");
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
        if (row.IsSelected) { selectedBytes += finding.Bytes; Changed(nameof(CanClean)); Changed(nameof(CleanLabel)); }
    }

    private static int Rank(string group) { var i = Array.IndexOf(GroupOrder, group); return i < 0 ? GroupOrder.Length : i; }

    public async Task CleanAsync()
    {
        if (Busy || selectedBytes == 0 || lifetime.IsCancellationRequested) return;
        var chosen = SelectedRows().Select(x => x.Finding).ToList();
        work?.Dispose(); work = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var token = work.Token;
        cleaning = true; Notify();
        Status = "Cleaning…";
        CleanupResult? result = null;
        try
        {
            var progress = new Reporter<CleanupProgress>(step => post(() => { if (cleaning) Status = $"Cleaned {step.Name} · {ByteSize.Format(step.FreedBytes)} so far"; }));
            result = await cleaner.CleanAsync(chosen, progress, token);
            Summary = Describe(result);
            log.Write(result.Skipped > 0 ? ActivityLevel.Warning : ActivityLevel.Success, ActivityArea.App, $"Cleanup freed {ByteSize.Format(result.FreedBytes)}",
                $"{result.Removed:N0} files removed in {result.Elapsed.TotalSeconds:0.0} s. {result.InUse} in use, {result.Denied} protected, {result.Failed} failed." + (result.HeldBytes > 0 ? $" {ByteSize.Format(result.HeldBytes)} held for undo." : ""));
        }
        catch (OperationCanceledException) { Summary = "Cleaning stopped. Anything already removed stays removed."; }
        catch (Exception ex)
        {
            Summary = "Cleaning could not finish: " + ex.Message;
            log.Write(ActivityLevel.Error, ActivityArea.App, "Cleanup could not finish", ex.Message);
        }
        finally { cleaning = false; Notify(); }
        await ScanAsync(keepSummary: true);
    }

    private static string Describe(CleanupResult result)
    {
        var parts = new List<string> { $"Freed {ByteSize.Format(result.FreedBytes)}" };
        if (result.HeldBytes > 0) parts.Add($"{ByteSize.Format(result.HeldBytes)} held for 14 days");
        if (result.InUse > 0) parts.Add($"{result.InUse:N0} {(result.InUse == 1 ? "file was" : "files were")} in use and skipped");
        if (result.Denied + result.Failed > 0) parts.Add($"{result.Denied + result.Failed:N0} could not be removed");
        return string.Join(" · ", parts) + $" · {result.Elapsed.TotalSeconds:0.0} s";
    }

    public async Task UndoAsync()
    {
        if (Busy || quarantine.Latest() is not { } batch) return;
        cleaning = true; Notify();
        RestoreResult restored;
        try { restored = await Task.Run(() => quarantine.Restore(batch.Id)); }
        finally { cleaning = false; Notify(); }
        Summary = restored.Conflicts > 0 ? $"Restored {restored.Restored:N0} files. {restored.Conflicts:N0} were left held because a file already exists there." : $"Restored {restored.Restored:N0} files.";
        log.Write(ActivityLevel.Info, ActivityArea.App, "Cleanup undone", Summary);
        await ScanAsync(keepSummary: true);
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

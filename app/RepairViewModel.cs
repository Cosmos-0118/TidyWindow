using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using TidyWindow.Next.Core;

namespace TidyWindow.Next.Desktop;

public sealed class RepairRow(RepairFinding finding, Action changed) : ObservableState
{
    // Only what is clearly leftover is ticked for the person; anything that might still be in use is offered, never assumed.
    private bool selected = finding.Level == RepairLevel.Recommended && finding.CanFix;
    public RepairFinding Finding { get; } = finding;
    public bool IsSelected { get => selected; set { if (Set(ref selected, value)) changed(); } }
    public string Title => Finding.Title;
    public string Detail => Finding.Detail;
    public string Evidence => Finding.Evidence;
    public Visibility CheckVisibility => Finding.CanFix ? Visibility.Visible : Visibility.Collapsed;
    public Visibility InfoVisibility => Finding.CanFix ? Visibility.Collapsed : Visibility.Visible;
    public Visibility AdminVisibility => Finding.NeedsAdmin ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ReviewVisibility => Finding.Level == RepairLevel.Review ? Visibility.Visible : Visibility.Collapsed;
    public string AutomationName => $"{Title}. {Detail}";
}

public sealed class RepairGroup(string name) : ObservableState
{
    public string Name { get; } = name;
    public ObservableCollection<RepairRow> Rows { get; } = [];
}

/// <summary>One program in the full list, for removing an entry by hand.</summary>
public sealed class ProgramRow(UninstallEntry entry, IReadOnlyList<string> keys)
{
    public UninstallEntry Entry { get; } = entry;
    public IReadOnlyList<string> Keys { get; } = keys;
    public string Name => Entry.Name;
    public string Sub => string.Join(" · ", new[] { Entry.Publisher, Entry.Version }.Where(x => x.Length > 0));
    public string Where => Entry.Key.StartsWith("HKCU", StringComparison.OrdinalIgnoreCase) ? "Just for you" : "All users";
    public Visibility AdminVisibility => Keys.Any(InstallRepair.IsMachine) ? Visibility.Visible : Visibility.Collapsed;
    public string AutomationName => $"{Name}, {Where}";
}

/// <summary>One finished repair, kept so it can be undone later.</summary>
public sealed class HistoryRow(RepairBatch batch)
{
    public RepairBatch Batch { get; } = batch;
    public string Title => Batch.Title;
    public string When => Batch.Created.ToLocalTime().ToString("d MMM yyyy, h:mm tt");
    public string Keys
    {
        get
        {
            var names = Batch.Keys.Select(x => x.Path[(x.Path.LastIndexOf('\\') + 1)..]).ToList();
            return names.Count <= 2 ? string.Join(", ", names) : $"{names[0]}, {names[1]} and {names.Count - 2} more";
        }
    }
    public string Count => Batch.Keys.Count == 1 ? "1 registry key" : $"{Batch.Keys.Count} registry keys";
    public Visibility AdminVisibility => Batch.Keys.Any(x => InstallRepair.IsMachine(x.Path)) ? Visibility.Visible : Visibility.Collapsed;
    public string AutomationName => $"Undo {Title}, {When}";
}

public sealed class RepairViewModel(IInstallSystem system, RepairService service, RepairHistory history, ActivityLog log, ToastCenter toasts) : ObservableState, IDisposable
{
    private static readonly string[] GroupOrder = ["Programs Windows still lists but cannot remove", "Windows Installer records", "Installs that got stuck", "Waiting on Windows"];
    private readonly CancellationTokenSource lifetime = new();
    private IReadOnlyList<ProgramRow> allPrograms = [];
    private bool scanning, fixing, scanned;
    private int selectedCount;
    private string query = "";

    public ObservableCollection<RepairGroup> Groups { get; } = [];
    public BulkObservableCollection<ProgramRow> Programs { get; } = new();
    public BulkObservableCollection<HistoryRow> History { get; } = new();

    public bool Scanned => scanned;
    public bool Busy => scanning || fixing;
    public bool CanScan => !Busy;
    public bool CanFix => !Busy && selectedCount > 0;
    public string ScanLabel => scanning ? "Scanning…" : scanned ? "Scan again" : "Scan";
    public string FixLabel => fixing ? "Fixing…" : selectedCount > 0 ? $"Fix {selectedCount} {(selectedCount == 1 ? "issue" : "issues")}" : "Fix";
    public string UndoTabTitle => History.Count > 0 ? $"Undo ({History.Count})" : "Undo";
    public Visibility NoHistoryVisibility => History.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    public bool ScanOverlay => scanning && Groups.Count == 0 && allPrograms.Count == 0;
    public Visibility EmptyVisibility => scanned && !scanning && Groups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public string ProgramCount => allPrograms.Count == 1 ? "1 program" : $"{allPrograms.Count:N0} programs";
    public Visibility NoProgramsVisibility => scanned && Programs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public string NoProgramsText => allPrograms.Count == 0 ? "No programs were found." : "No program matches your search.";

    public string Query { get => query; set { if (Set(ref query, value)) ApplyFilter(); } }

    private void Notify()
    {
        foreach (var name in new[] { nameof(Busy), nameof(CanScan), nameof(CanFix), nameof(ScanLabel), nameof(FixLabel), nameof(ScanOverlay), nameof(EmptyVisibility), nameof(NoProgramsVisibility) }) Changed(name);
    }

    private IEnumerable<RepairRow> SelectedRows() => Groups.SelectMany(x => x.Rows).Where(x => x.IsSelected && x.Finding.CanFix);

    private void RowChanged()
    {
        var chosen = SelectedRows().ToList();
        selectedCount = chosen.Count;
        Changed(nameof(CanFix)); Changed(nameof(FixLabel));
    }

    public async Task ScanAsync()
    {
        if (Busy || lifetime.IsCancellationRequested) return;
        scanning = true; Groups.Clear(); selectedCount = 0;
        Notify(); RowChanged();
        try
        {
            var scan = await Task.Run(() => InstallRepair.Scan(system), lifetime.Token);
            Show(scan);
            scanned = true;
            var fixable = scan.Findings.Count(x => x.CanFix);
            log.Write(ActivityLevel.Info, ActivityArea.App, "Repair scan finished", $"{scan.Entries.Count} programs checked; {fixable} can be repaired, {scan.Findings.Count(x => x.Level == RepairLevel.Recommended)} of them clearly leftover.");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            toasts.Show("repair-scan", ToastKind.Warning, "The scan could not finish", ToastCenter.Brief(ex.Message));
            log.Write(ActivityLevel.Warning, ActivityArea.App, "Repair scan could not finish", ex.Message);
        }
        finally
        {
            scanning = false; RefreshHistory(); Notify(); RowChanged();
        }
    }

    private void Show(RepairScan scan)
    {
        foreach (var finding in scan.Findings.OrderBy(x => x.Level).ThenBy(x => x.Title, StringComparer.CurrentCultureIgnoreCase))
        {
            var title = GroupOrder[finding.Kind switch
            {
                RepairKind.BrokenEntry => 0,
                RepairKind.InstallerRecord => 1,
                RepairKind.InstallLock or RepairKind.RunningInstaller => 2,
                _ => 3
            }];
            var group = Groups.FirstOrDefault(x => x.Name == title);
            if (group is null)
            {
                group = new RepairGroup(title);
                var position = Groups.TakeWhile(x => Array.IndexOf(GroupOrder, x.Name) < Array.IndexOf(GroupOrder, title)).Count();
                Groups.Insert(position, group);
            }
            group.Rows.Add(new RepairRow(finding, RowChanged));
        }
        allPrograms = scan.Entries
            .Where(x => x.Name.Trim().Length > 0 && !x.SystemComponent && !x.IsUpdate)
            .OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(x => new ProgramRow(x, InstallRepair.RemovalKeys(x, scan.Products))).ToList();
        ApplyFilter();
        Changed(nameof(ProgramCount));
    }

    private void ApplyFilter()
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Programs.Reset(words.Length == 0 ? allPrograms : allPrograms.Where(x => words.All(w => x.Name.Contains(w, StringComparison.CurrentCultureIgnoreCase) || x.Sub.Contains(w, StringComparison.CurrentCultureIgnoreCase))).ToList());
        Changed(nameof(NoProgramsVisibility)); Changed(nameof(NoProgramsText));
    }

    /// <summary>Fixes everything ticked on the Problems tab.</summary>
    public async Task FixAsync()
    {
        if (Busy || selectedCount == 0) return;
        var chosen = SelectedRows().Select(x => x.Finding).ToList();
        var title = chosen.Count == 1 ? $"Repaired {chosen[0].Title}" : $"Repaired {chosen.Count} leftovers";
        await RunAsync(chosen, title, rescan: true);
    }

    /// <summary>Takes one program off the list by hand, with Windows Installer's record when it has one.</summary>
    public async Task RemoveProgramAsync(ProgramRow row)
    {
        if (Busy) return;
        var finding = new RepairFinding("manual:" + row.Entry.Key, RepairKind.BrokenEntry, row.Name, "", row.Entry.Key, RepairLevel.Review, row.Keys);
        await RunAsync([finding], $"Removed {row.Name} from the list", rescan: true);
    }

    private async Task RunAsync(IReadOnlyList<RepairFinding> findings, string title, bool rescan)
    {
        fixing = true; Notify();
        try
        {
            var outcome = await service.FixAsync(findings, title, lifetime.Token);
            var batch = outcome.Batch;
            if (outcome.Problems.Count == 0)
                toasts.Show("repair", ToastKind.Success, title, batch is null ? "" : "You can undo this any time from the Undo tab.", batch is null ? "" : "Undo", batch is null ? null : () => UndoAsync(batch), 12);
            else
                toasts.Show("repair", ToastKind.Warning, $"{outcome.Fixed} of {findings.Count} repaired", ToastCenter.Brief(string.Join(" ", outcome.Problems)), batch is null ? "" : "Undo", batch is null ? null : () => UndoAsync(batch), 15);
            log.Write(outcome.Problems.Count == 0 ? ActivityLevel.Success : ActivityLevel.Warning, ActivityArea.App, title,
                $"{outcome.Fixed} of {findings.Count} repaired. " + string.Join(" ", outcome.Problems));
        }
        catch (OperationCanceledException)
        {
            toasts.Show("repair", ToastKind.Info, "Administrator permission was declined", "Nothing was changed.");
            log.Write(ActivityLevel.Info, ActivityArea.App, "Repair stopped", "Administrator permission was declined.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            toasts.Show("repair", ToastKind.Warning, "The repair could not finish", ToastCenter.Brief(ex.Message));
            log.Write(ActivityLevel.Error, ActivityArea.App, "Repair could not finish", ex.Message);
        }
        finally { fixing = false; RefreshHistory(); Notify(); }
        if (rescan) await ScanAsync();
    }

    /// <summary>Puts back everything one earlier repair removed. Works for any repair still in the Undo tab, not only the last.</summary>
    public async Task UndoAsync(RepairBatch batch)
    {
        if (Busy) return;
        fixing = true; Notify();
        try
        {
            var restored = await service.UndoAsync(batch, lifetime.Token);
            toasts.Show("repair", ToastKind.Success, "Undone", $"{batch.Title}. {restored} {(restored == 1 ? "registry key" : "registry keys")} put back.");
            log.Write(ActivityLevel.Info, ActivityArea.App, "Repair undone", batch.Title);
        }
        catch (OperationCanceledException) { toasts.Show("repair", ToastKind.Info, "Administrator permission was declined", "Nothing was restored."); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            toasts.Show("repair", ToastKind.Warning, "The undo could not finish", ToastCenter.Brief(ex.Message));
            log.Write(ActivityLevel.Error, ActivityArea.App, "Repair undo could not finish", ex.Message);
        }
        finally { fixing = false; RefreshHistory(); Notify(); }
        await ScanAsync();
    }

    public Task UndoAsync(HistoryRow row) => UndoAsync(row.Batch);

    /// <summary>Reads the saved repairs again, newest first.</summary>
    public void RefreshHistory()
    {
        History.Reset(history.All().Select(x => new HistoryRow(x)).ToList());
        Changed(nameof(UndoTabTitle)); Changed(nameof(NoHistoryVisibility));
    }

    public void Dispose() { lifetime.Cancel(); lifetime.Dispose(); }
}

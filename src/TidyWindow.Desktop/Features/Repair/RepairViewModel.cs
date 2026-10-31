using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
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

/// <summary>What happened to one thing in the last repair, with every step that was tried on the way.</summary>
public sealed class ResultRow(RepairResult result)
{
    public string Title => result.Title;
    public string Summary => result.Summary;
    public string Status => result.Status switch { RepairStatus.Fixed => "Fixed", RepairStatus.FixedAfterRestart => "Fixed after restart", _ => "Could not fix" };
    public string Glyph => result.Status == RepairStatus.NotFixed ? "\uE7BA" : result.Status == RepairStatus.FixedAfterRestart ? "\uE777" : "\uE73E";
    public string Steps => string.Join("\n", result.Steps);
    public Visibility StepsVisibility => result.Steps.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public string AutomationName => $"{Title}: {Status}. {Summary}";
}

public sealed class RepairGroup(string name) : ObservableState
{
    public string Name { get; } = name;
    public ObservableCollection<RepairRow> Rows { get; } = [];
}

/// <summary>One program in the full list, for removing an entry by hand.</summary>
public sealed class ProgramRow(UninstallEntry entry, IReadOnlyList<string> keys) : ObservableState
{
    private ImageSource? icon;
    public UninstallEntry Entry { get; } = entry;
    public ImageSource? Icon => icon;
    public void SetIcon(ImageSource? image) { icon = image; Changed(nameof(Icon)); }
    // Windows Installer products without a registered icon are found through the shortcut or folder they installed; the uninstaller says where that is.
    public IconQuery IconQuery => new(Entry.Name, "", "", Entry.DisplayIcon, Entry.InstallLocation, Entry.UninstallString.Length > 0 ? Entry.UninstallString : Entry.QuietUninstallString, InstallRepair.ProductCode(Entry) ?? "");
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
    public string Count => Describe(Batch.Keys.Count, Batch.PutBack.Count);

    /// <summary>"3 registry keys", "1 setup program" or both: what a repair changed, or what an undo put back.</summary>
    public static string Describe(int keys, int files)
    {
        var parts = new List<string>();
        if (keys > 0) parts.Add(keys == 1 ? "1 registry key" : $"{keys} registry keys");
        if (files > 0) parts.Add(files == 1 ? "1 setup program" : $"{files} setup programs");
        return parts.Count == 0 ? "nothing" : string.Join(" and ", parts);
    }
    public Visibility AdminVisibility => Batch.Keys.Any(x => InstallRepair.IsMachine(x.Path)) ? Visibility.Visible : Visibility.Collapsed;
    public string AutomationName => $"Undo {Title}, {When}";
}

public sealed class RepairViewModel(IInstallSystem system, RepairService service, RepairHistory history, ActivityLog log, ToastCenter toasts, IconService? iconService = null, Func<Task<string?>>? chooseFolder = null) : ObservableState, IDisposable
{
    private readonly IconService icons = iconService ?? IconService.Shared;
    private static readonly string[] GroupOrder = ["Programs Windows still lists but cannot remove", "Windows Installer records", "Installs that got stuck", "Waiting on Windows"];
    private readonly CancellationTokenSource lifetime = new();
    private IReadOnlyList<ProgramRow> allPrograms = [];
    private IReadOnlyList<MsiProduct> scanProducts = [];
    private bool restartNeeded, allowDownload = true;
    private readonly List<string> installerFolders = [];
    private bool scanning, fixing, scanned, listening;
    private int selectedCount;
    private string query = "";

    public ObservableCollection<RepairGroup> Groups { get; } = [];
    public BulkObservableCollection<ProgramRow> Programs { get; } = new();
    public BulkObservableCollection<HistoryRow> History { get; } = new();
    public BulkObservableCollection<ResultRow> Results { get; } = new();
    /// <summary>Whether a program's original installer may be fetched from WinGet when its own copy is gone.</summary>
    public bool AllowDownload { get => allowDownload; set => Set(ref allowDownload, value); }
    public string InstallerFolderText => installerFolders.Count == 0 ? "No folder chosen" : installerFolders.Count == 1 ? installerFolders[0] : $"{installerFolders.Count} folders chosen";
    public Visibility LocateVisibility => chooseFolder is null ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Lets the person point at a folder that holds the program's installer, such as Downloads; the repair checks each file there against the program before using it.</summary>
    public async Task LocateInstallerAsync()
    {
        if (chooseFolder is null || Busy) return;
        if (await chooseFolder() is { Length: > 0 } folder && !installerFolders.Contains(folder, StringComparer.OrdinalIgnoreCase)) installerFolders.Add(folder);
        Changed(nameof(InstallerFolderText));
    }

    public Visibility ResultsVisibility => Results.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RestartVisibility => restartNeeded ? Visibility.Visible : Visibility.Collapsed;

    public bool Scanned => scanned;
    public bool Busy => scanning || fixing;
    public bool CanScan => !Busy;
    public bool CanFix => !Busy && selectedCount > 0;
    public string ScanLabel => scanning ? "Scanning…" : scanned ? "Scan again" : "Scan";
    public string FixLabel => fixing ? "Fixing…" : selectedCount > 0 ? $"Fix {selectedCount} {(selectedCount == 1 ? "issue" : "issues")}" : "Fix";
    public string UndoTabTitle => History.Count > 0 ? $"Undo ({History.Count})" : "Undo";
    public Visibility NoHistoryVisibility => History.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    public bool ScanOverlay => scanning && Groups.Count == 0 && allPrograms.Count == 0;

    // While a repair or undo runs: the page is blurred and says which item, what is happening now, and what already finished.
    private string progressHeading = "", progressCounter = "", progressText = "Getting started…";
    public bool FixOverlay => fixing;
    public string ProgressHeading => progressHeading;
    public string ProgressCounter => progressCounter;
    public string ProgressText => progressText;
    public BulkObservableCollection<string> ProgressSteps { get; } = new();

    private void ResetProgress()
    {
        progressHeading = ""; progressCounter = ""; progressText = "Getting started…"; ProgressSteps.Reset([]);
        Changed(nameof(ProgressHeading)); Changed(nameof(ProgressCounter)); Changed(nameof(ProgressText));
    }

    private void OnProgress(RepairProgress progress)
    {
        progressHeading = progress.Title;
        progressCounter = progress.Total > 1 && progress.Index > 0 ? $"{progress.Index} of {progress.Total}" : "";
        if (progress.Doing.Length > 0) progressText = progress.Doing;
        ProgressSteps.Reset(progress.Done);
        Changed(nameof(ProgressHeading)); Changed(nameof(ProgressCounter)); Changed(nameof(ProgressText));
    }
    public Visibility EmptyVisibility => scanned && !scanning && Groups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public string ProgramCount => allPrograms.Count == 1 ? "1 program" : $"{allPrograms.Count:N0} programs";
    public Visibility NoProgramsVisibility => scanned && Programs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public string NoProgramsText => allPrograms.Count == 0 ? "No programs were found." : "No program matches your search.";

    public string Query { get => query; set { if (Set(ref query, value)) ApplyFilter(); } }

    private void Notify()
    {
        foreach (var name in new[] { nameof(Busy), nameof(CanScan), nameof(CanFix), nameof(ScanLabel), nameof(FixLabel), nameof(ScanOverlay), nameof(FixOverlay), nameof(EmptyVisibility), nameof(NoProgramsVisibility) }) Changed(name);
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
                RepairKind.InstallerRecord or RepairKind.BundleRecord or RepairKind.DanglingLinks => 1,
                RepairKind.InstallLock or RepairKind.RunningInstaller or RepairKind.InstallerService => 2,
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
        scanProducts = scan.Products;
        allPrograms = scan.Entries
            .Where(x => x.Name.Trim().Length > 0 && !x.SystemComponent && !x.IsUpdate)
            .OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(x => new ProgramRow(x, InstallRepair.RemovalKeys(x, scan.Products))).ToList();
        foreach (var row in allPrograms) if (icons.TryGet(row.IconQuery, out var cached) && cached is not null) row.SetIcon(cached);
        ApplyFilter(keepPosition: true);
        Changed(nameof(ProgramCount));
        if (!listening) { IconService.DarkChanged += ReloadIcons; listening = true; }
        _ = LoadIconsAsync(allPrograms);
    }

    private void ReloadIcons() => _ = LoadIconsAsync(allPrograms, everyRow: true);

    // Icons arrive a few at a time after the list is up, rows in view first; a program with none keeps the plain app glyph.
    private async Task LoadIconsAsync(IReadOnlyList<ProgramRow> rows, bool everyRow = false)
    {
        var shown = Programs.ToHashSet();
        var queue = rows.Where(x => everyRow || x.Icon is null).OrderBy(x => shown.Contains(x) ? 0 : 1).ToList();
        try { await Task.WhenAll(queue.Select(async row => row.SetIcon(await icons.LoadAsync(row.IconQuery, lifetime.Token)))); }
        catch (OperationCanceledException) { }
    }

    /// <param name="keepPosition">The list is being refreshed under the person, not re-filtered by them: change only the rows that differ, so the scroll position stays.</param>
    private void ApplyFilter(bool keepPosition = false)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var shown = words.Length == 0 ? allPrograms : allPrograms.Where(x => words.All(w => x.Name.Contains(w, StringComparison.CurrentCultureIgnoreCase) || x.Sub.Contains(w, StringComparison.CurrentCultureIgnoreCase))).ToList();
        if (keepPosition) Programs.Sync(shown, x => x.Entry.Key); else Programs.Reset(shown);
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
        // A program Windows Installer knows is removed with its whole record, found in every place, not only the few keys the list shows.
        var code = InstallRepair.ProductCode(row.Entry);
        var product = code is null ? null : scanProducts.FirstOrDefault(x => string.Equals(x.Code, code, StringComparison.OrdinalIgnoreCase));
        var finding = product is null
            ? new RepairFinding("manual:" + row.Entry.Key, RepairKind.BrokenEntry, row.Name, "", row.Entry.Key, RepairLevel.Review, row.Keys)
            : new RepairFinding("manual:" + row.Entry.Key, RepairKind.InstallerRecord, row.Name, "", row.Entry.Key, RepairLevel.Review, row.Keys, Footprint: new("product", product.Code, product.Context, product.Sid, [row.Entry.Key]), Product: product);
        await RunAsync([finding], $"Removed {row.Name} from the list", rescan: true);
    }

    private async Task RunAsync(IReadOnlyList<RepairFinding> findings, string title, bool rescan)
    {
        ResetProgress();
        fixing = true; Notify();
        try
        {
            var outcome = await service.FixAsync(findings, title, lifetime.Token, new RepairOptions(allowDownload, installerFolders.ToList()), new Progress<RepairProgress>(OnProgress));
            var batch = outcome.Batch;
            ShowResults(outcome.Results ?? [], outcome.RestartNeeded);
            var undo = batch is null ? null : (Func<Task>)(() => UndoAsync(batch));
            if (outcome.Problems.Count == 0)
                toasts.Show("repair", ToastKind.Success, title, outcome.RestartNeeded ? "Restart Windows to finish." : batch is null ? "" : "You can undo this any time from the Undo tab.", batch is null ? "" : "Undo", undo, 12);
            else
                toasts.Show("repair", ToastKind.Warning, $"{outcome.Fixed} of {findings.Count} repaired", ToastCenter.Brief(string.Join(" ", outcome.Problems)), batch is null ? "" : "Undo", undo, 15);
            log.Write(outcome.Problems.Count == 0 ? ActivityLevel.Success : ActivityLevel.Warning, ActivityArea.App, title,
                $"{outcome.Fixed} of {findings.Count} repaired. " + string.Join(" ", outcome.Problems) + string.Concat((outcome.Results ?? []).Select(x => $"\n{x.Title}: {x.Status}. " + string.Join(" ", x.Steps))));
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

    private void ShowResults(IReadOnlyList<RepairResult> results, bool restart)
    {
        restartNeeded = restart;
        Results.Reset(results.OrderByDescending(x => x.Status == RepairStatus.NotFixed).Select(x => new ResultRow(x)).ToList());
        Changed(nameof(ResultsVisibility)); Changed(nameof(RestartVisibility));
    }

    /// <summary>Puts back everything one earlier repair removed. Works for any repair still in the Undo tab, not only the last.</summary>
    public async Task UndoAsync(RepairBatch batch)
    {
        if (Busy) return;
        ResetProgress();
        fixing = true; Notify();
        try
        {
            var outcome = await service.UndoAsync(batch, lifetime.Token, new Progress<RepairProgress>(OnProgress));
            if (outcome.Complete)
            {
                ShowResults([], false);
                var said = new List<string>();
                if (outcome.Restored > 0) said.Add($"{HistoryRow.Describe(outcome.Restored, 0)} put back");
                if (outcome.FilesRemoved > 0) said.Add($"{HistoryRow.Describe(0, outcome.FilesRemoved)} removed again");
                toasts.Show("repair", ToastKind.Success, "Undone", $"{batch.Title}. {string.Join(" and ", said)}.");
                log.Write(ActivityLevel.Info, ActivityArea.App, "Repair undone", batch.Title);
            }
            else
            {
                // Nothing was put back, and the page says exactly which keys stopped it.
                ShowResults([new($"undo:{batch.Id}", batch.Title, RepairStatus.NotFixed, outcome.Problems[0], outcome.Problems.Skip(1).Take(12).ToList())], false);
                toasts.Show("repair", ToastKind.Warning, "The undo could not finish", ToastCenter.Brief(outcome.Problems[0] + " " + (outcome.Problems.Count > 1 ? outcome.Problems[1] : "")));
                log.Write(ActivityLevel.Error, ActivityArea.App, "Repair undo could not finish", string.Join(" ", outcome.Problems));
            }
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

    public void Dispose() { IconService.DarkChanged -= ReloadIcons; lifetime.Cancel(); lifetime.Dispose(); }
}

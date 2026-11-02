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
    public IReadOnlyList<StepRow> StepItems => result.Steps.Select(x => new StepRow(x)).ToList();
    public string StepsLabel => result.Steps.Count == 1 ? "1 step tried" : $"{result.Steps.Count} steps tried";
    public Visibility StepsVisibility => result.Steps.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public string AutomationName => $"{Title}: {Status}. {Summary}";
}

/// <summary>One step of a repair, marked when it did not work out so the list reads at a glance.</summary>
public sealed class StepRow(string text)
{
    private static readonly string[] Setbacks = ["could not", "not allowed", "none matches", "does not list", "is not this", "still missing", "still names", "still does not", "nothing there", "none is there", "no setup program", "Windows Installer said:"];
    public string Text => text;
    private bool Problem => Setbacks.Any(x => text.Contains(x, StringComparison.OrdinalIgnoreCase));
    public Visibility OkVisibility => Problem ? Visibility.Collapsed : Visibility.Visible;
    public Visibility ProblemVisibility => Problem ? Visibility.Visible : Visibility.Collapsed;
}

public sealed class RepairGroup(string name) : ObservableState
{
    public string Name { get; } = name;
    public ObservableCollection<RepairRow> Rows { get; } = [];
    private IEnumerable<RepairRow> Fixable => Rows.Where(x => x.Finding.CanFix);

    /// <summary>Ticks or clears every row in the group that can be fixed; a broken bundle can bring dozens of them with it.</summary>
    public bool AllSelected
    {
        get => Fixable.Any() && Fixable.All(x => x.IsSelected);
        set { foreach (var row in Fixable) row.IsSelected = value; Changed(nameof(AllSelected)); }
    }
    public Visibility SelectAllVisibility => Fixable.Count() > 1 ? Visibility.Visible : Visibility.Collapsed;
    public void Refresh() => Changed(nameof(AllSelected));
}

/// <summary>One program in the full list, for removing an entry by hand.</summary>
public sealed class ProgramRow(UninstallEntry entry, IReadOnlyList<string> keys, MsiProduct? repairable = null) : ObservableState
{
    private ImageSource? icon;
    public UninstallEntry Entry { get; } = entry;
    public ImageSource? Icon => icon;
    public void SetIcon(ImageSource? image) { icon = image; Changed(nameof(Icon)); }
    // Windows Installer products without a registered icon are found through the shortcut or folder they installed; the uninstaller says where that is.
    public IconQuery IconQuery => new(Entry.Name, "", "", Entry.DisplayIcon, Entry.InstallLocation, Entry.UninstallString.Length > 0 ? Entry.UninstallString : Entry.QuietUninstallString, InstallRepair.ProductCode(Entry) ?? "");
    public IReadOnlyList<string> Keys { get; } = keys;
    public string Name => Entry.Name;
    /// <summary>A package another program installs for itself (a Burn bundle's parts): Windows hides it from the list.</summary>
    public bool Hidden => Entry.SystemComponent;
    /// <summary>The Windows Installer product this row can be repaired as, when it still has its saved installer copy.</summary>
    public MsiProduct? RepairProduct { get; } = repairable;
    /// <summary>Programs installed by Visual Studio refuse to run on their own ("use the Visual Studio Installer and select Modify"), so there is nothing to offer for them here.</summary>
    public bool ManagedByVisualStudio => Entry.Name.Contains(" from Visual Studio", StringComparison.OrdinalIgnoreCase);
    public Visibility RepairVisibility => RepairProduct is null || ManagedByVisualStudio ? Visibility.Collapsed : Visibility.Visible;
    public string Sub => string.Join(" · ", new[] { Entry.Publisher, Entry.Version, Hidden ? "Hidden sub-package" : "" }.Where(x => x.Length > 0));
    public string Where => Entry.Key.StartsWith("HKCU", StringComparison.OrdinalIgnoreCase) ? "Just for you" : "All users";
    public Visibility AdminVisibility => Keys.Any(InstallRepair.IsMachine) ? Visibility.Visible : Visibility.Collapsed;
    public string AutomationName => $"{Name}, {Where}";
    public string RepairAutomationName => $"Repair {Name}, {Where}";
}

public sealed class RepairViewModel(IInstallSystem system, RepairService service, RepairHistory history, ActivityLog log, ToastCenter toasts, IconService? iconService = null, Func<Task<string?>>? chooseFolder = null) : ObservableState, IDisposable
{
    private readonly IconService icons = iconService ?? IconService.Shared;
    private static readonly string[] GroupOrder = ["Programs Windows still lists but cannot remove", "Windows Installer records", "Installs that got stuck", "Shortcuts and Start menu leftovers", "Leftover installer files", "Waiting on Windows"];
    private readonly CancellationTokenSource lifetime = new();
    private IReadOnlyList<ProgramRow> allPrograms = [], everyProgram = [];
    private bool showHidden;
    private IReadOnlyList<MsiProduct> scanProducts = [];
    private bool restartNeeded, allowDownload = true, removeProgramFolders, optionsOpen;
    private readonly List<string> installerFolders = [];
    private bool scanning, fixing, scanned, listening;
    private int selectedCount;
    private string query = "";

    public ObservableCollection<RepairGroup> Groups { get; } = [];
    public BulkObservableCollection<ProgramRow> Programs { get; } = new();
    // The Undo and Quarantine lists: each has its own search, filters and sort, and is rebuilt from what is saved on disk.
    private readonly ArchiveState undoState = new("repairs", ["Newest first", "Oldest first", "Most changes", "Name"]);
    private readonly ArchiveState quarantineState = new("items", ["Newest first", "Oldest first", "Largest", "Name"]);
    private IReadOnlyList<HistoryRow> allHistory = [];
    private IReadOnlyList<QuarantineRow> allQuarantine = [];
    private List<QuarantineRow> shownQuarantine = [];
    private readonly SemaphoreSlim measuring = new(4);
    private bool wired, bulkSelecting;
    private int refreshes;

    public ArchiveState UndoState { get { Wire(); return undoState; } }
    public ArchiveState QuarantineState { get { Wire(); return quarantineState; } }
    public BulkObservableCollection<object> UndoList { get; } = new();
    public BulkObservableCollection<object> QuarantineList { get; } = new();

    private void Wire()
    {
        if (wired) return;
        wired = true;
        undoState.Refiltered += ApplyUndo;
        quarantineState.Refiltered += ApplyQuarantine;
    }
    public BulkObservableCollection<ResultRow> Results { get; } = new();
    /// <summary>Whether a program's original installer may be fetched from WinGet when its own copy is gone.</summary>
    public bool AllowDownload { get => allowDownload; set { if (Set(ref allowDownload, value)) Changed(nameof(OptionsSummary)); } }
    /// <summary>When a program's record is removed, also set aside the program's own folder under Program Files. Off by default: the folder may hold the person's own files. It goes to the Quarantine tab, not the bin.</summary>
    public bool RemoveProgramFolders { get => removeProgramFolders; set { if (Set(ref removeProgramFolders, value)) Changed(nameof(OptionsSummary)); } }

    /// <summary>Whether the options card is open. Folded away it leaves one line saying how the two switches are set.</summary>
    public bool OptionsOpen { get => optionsOpen; set { if (Set(ref optionsOpen, value)) { Changed(nameof(OptionsVisibility)); Changed(nameof(OptionsSummaryVisibility)); Changed(nameof(OptionsChevron)); Changed(nameof(OptionsToggleName)); } } }
    public Visibility OptionsVisibility => optionsOpen ? Visibility.Visible : Visibility.Collapsed;
    public Visibility OptionsSummaryVisibility => optionsOpen ? Visibility.Collapsed : Visibility.Visible;
    public string OptionsChevron => optionsOpen ? DetailLine.Up : DetailLine.Down;
    public string OptionsToggleName => optionsOpen ? "Hide repair options" : "Show repair options";
    public string OptionsSummary => $"Download installers {(allowDownload ? "on" : "off")}  ·  Move program folders to Quarantine {(removeProgramFolders ? "on" : "off")}";
    /// <summary>Also list the packages other programs install for themselves, which Windows hides: one of them can block an install with "another version is already installed".</summary>
    public bool ShowHidden { get => showHidden; set { if (Set(ref showHidden, value)) Relist(); } }
    public Visibility ClearFoldersVisibility => installerFolders.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public string InstallerFolderText => installerFolders.Count == 0 ? "No folder chosen" : installerFolders.Count == 1 ? installerFolders[0] : $"{installerFolders.Count} folders chosen";
    public Visibility LocateVisibility => chooseFolder is null ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Lets the person point at a folder that holds the program's installer, such as Downloads; the repair checks each file there against the program before using it.</summary>
    public async Task LocateInstallerAsync()
    {
        if (chooseFolder is null || Busy) return;
        if (await chooseFolder() is { Length: > 0 } folder && !installerFolders.Contains(folder, StringComparer.OrdinalIgnoreCase)) installerFolders.Add(folder);
        Changed(nameof(InstallerFolderText)); Changed(nameof(ClearFoldersVisibility));
    }

    /// <summary>Forgets the folders chosen for the installer.</summary>
    public void ClearInstallerFolders()
    {
        if (Busy) return;
        installerFolders.Clear();
        Changed(nameof(InstallerFolderText)); Changed(nameof(ClearFoldersVisibility));
    }

    public Visibility ResultsVisibility => Results.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RestartVisibility => restartNeeded ? Visibility.Visible : Visibility.Collapsed;

    public bool Scanned => scanned;
    public bool Busy => scanning || fixing;
    public bool CanScan => !Busy;
    public bool CanFix => !Busy && selectedCount > 0;
    public string ScanLabel => scanning ? "Scanning…" : scanned ? "Scan again" : "Scan";
    public string FixLabel => fixing ? "Fixing…" : selectedCount > 0 ? $"Fix {selectedCount} {(selectedCount == 1 ? "issue" : "issues")}" : "Fix";
    public string QuarantineTabTitle => allQuarantine.Count > 0 ? $"Quarantine ({allQuarantine.Count:N0})" : "Quarantine";
    public string UndoTabTitle => allHistory.Count > 0 ? $"Undo ({allHistory.Count:N0})" : "Undo";

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
        foreach (var group in Groups) group.Refresh();
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
                RepairKind.InstallerRecord or RepairKind.BundleRecord or RepairKind.DanglingLinks or RepairKind.MissingPatch => 1,
                RepairKind.InstallLock or RepairKind.RunningInstaller or RepairKind.InstallerService => 2,
                RepairKind.BrokenShortcut or RepairKind.EmptyFolder => 3,
                RepairKind.InstallerFiles => 4,
                _ => 5
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
        everyProgram = scan.Entries
            .Where(x => x.Name.Trim().Length > 0 && !x.IsUpdate)
            .OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(x => new ProgramRow(x, InstallRepair.RemovalKeys(x, scan.Products), Repairable(x, scan.Products))).ToList();
        if (!listening) { IconService.DarkChanged += ReloadIcons; listening = true; }
        Relist();
    }

    /// <summary>The product a list entry stands for, when Windows Installer still has the installer copy it needs to repair it.</summary>
    private MsiProduct? Repairable(UninstallEntry entry, IReadOnlyList<MsiProduct> products)
    {
        var code = InstallRepair.ProductCode(entry);
        return code is null ? null : products.FirstOrDefault(x => string.Equals(x.Code, code, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(x.LocalPackage) && system.FileExists(x.LocalPackage));
    }

    /// <summary>Lists the programs again: the hidden sub-packages only when asked for.</summary>
    private void Relist()
    {
        allPrograms = showHidden ? everyProgram : everyProgram.Where(x => !x.Hidden).ToList();
        foreach (var row in allPrograms) if (row.Icon is null && icons.TryGet(row.IconQuery, out var cached) && cached is not null) row.SetIcon(cached);
        ApplyFilter(keepPosition: true);
        Changed(nameof(ProgramCount));
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
        var id = "manual:" + row.Entry.Key;
        var code = InstallRepair.ProductCode(row.Entry);
        var products = code is null ? [] : scanProducts.Where(x => string.Equals(x.Code, code, StringComparison.OrdinalIgnoreCase)).ToList();
        var findings = new List<RepairFinding>();
        if (products.Count > 0)
        {
            // A program Windows Installer knows is removed with its whole record, found in every place, and in every context it is registered in.
            foreach (var product in products)
                findings.Add(new(findings.Count == 0 ? id : $"{id}:{product.Context}", RepairKind.InstallerRecord, row.Name, "", row.Entry.Key, RepairLevel.Review, row.Keys, Footprint: new("product", product.Code, product.Context, product.Sid, [row.Entry.Key]), Product: product, Entry: row.Entry));
        }
        else if (row.Entry.BundleProvider.Length > 0)
            // A Burn bundle (Python, Visual C++ and many more) keeps dependency records that name it, and they go with its entry.
            findings.Add(new(id, RepairKind.BundleRecord, row.Name, "", row.Entry.Key, RepairLevel.Review, row.Keys, Footprint: new("bundle", Keys: [row.Entry.Key]), Entry: row.Entry));
        else
            findings.Add(new(id, RepairKind.BrokenEntry, row.Name, "", row.Entry.Key, RepairLevel.Review, row.Keys, Entry: row.Entry));
        await RunAsync(findings, $"Removed {row.Name} from the list", rescan: true);
    }

    /// <summary>Repairs a working program from the saved copy of its own installer, the way Windows' Repair does. Nothing is removed.</summary>
    public async Task RepairProgramAsync(ProgramRow row)
    {
        if (Busy || row.RepairProduct is not { } product) return;
        ResetProgress();
        fixing = true; Notify();
        try
        {
            var result = await service.RepairProgramAsync(product, row.Name, lifetime.Token, new Progress<RepairProgress>(OnProgress), new RepairOptions(allowDownload, installerFolders.ToList(), RemoveProgramFolders: removeProgramFolders));
            ShowResults([result], result.Status == RepairStatus.FixedAfterRestart);
            if (result.Status == RepairStatus.NotFixed) toasts.Show("repair", ToastKind.Warning, $"{row.Name} could not be repaired", ToastCenter.Brief(result.Summary));
            else toasts.Show("repair", ToastKind.Success, $"Repaired {row.Name}", result.Status == RepairStatus.FixedAfterRestart ? "Restart Windows to finish." : "See Last repair on the Problems tab for what was done.");
            log.Write(result.Status == RepairStatus.NotFixed ? ActivityLevel.Warning : ActivityLevel.Success, ActivityArea.App, $"Repaired {row.Name}", result.Summary + " " + string.Join(" ", result.Steps));
        }
        catch (OperationCanceledException) { toasts.Show("repair", ToastKind.Info, "Administrator permission was declined", "Nothing was changed."); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            toasts.Show("repair", ToastKind.Warning, "The repair could not finish", ToastCenter.Brief(ex.Message));
            log.Write(ActivityLevel.Error, ActivityArea.App, "Repair could not finish", ex.Message);
        }
        finally { fixing = false; Notify(); }
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
                if (outcome.FoldersRestored > 0) said.Add($"{HistoryRow.Describe(0, 0, 0, 0, outcome.FoldersRestored)} moved back");
                if (outcome.ShortcutsRestored > 0) said.Add($"{HistoryRow.Describe(0, 0, outcome.ShortcutsRestored)} put back");
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

    /// <summary>Reads the saved repairs again and builds both lists. This happens off the screen thread: a long history is thousands of files.</summary>
    public void RefreshHistory() => _ = RefreshHistoryAsync();

    private static string KeyOf(QuarantineItem item) => $"{item.BatchId}|{item.Kind}|{item.Original}";

    private async Task RefreshHistoryAsync()
    {
        if (lifetime.IsCancellationRequested) return;
        var mine = ++refreshes;
        try
        {
            var (repairs, items) = await Task.Run(() =>
            {
                history.Expire();
                return (history.All().Select(x => new HistoryRow(x)).ToList(), service.Quarantine());
            }, lifetime.Token);
            if (mine != refreshes) return;
            allHistory = repairs;
            // A row that is already on screen keeps its selection and its measured size.
            var before = new Dictionary<string, QuarantineRow>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in allQuarantine) before.TryAdd(KeyOf(row.Item), row);
            allQuarantine = items.Select(x => before.TryGetValue(KeyOf(x), out var kept) ? kept : new QuarantineRow(x, SelectionChanged)).ToList();
            ApplyUndo();
            ApplyQuarantine();
            _ = MeasureAsync(allQuarantine.Where(x => x.Bytes < 0).ToList());
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Write(ActivityLevel.Warning, ActivityArea.App, "The saved repairs could not be read", ex.Message);
        }
    }

    private void ApplyUndo()
    {
        var shown = allHistory.Where(x => undoState.Matches(x.Entry)).ToList();
        UndoList.Sync(ArchiveList.Build(shown, undoState.Sort, DateTimeOffset.Now, x => x.Batch.Created, x => x.Weight, x => x.Title, _ => ""), RowKey);
        undoState.Report(allHistory.Count, shown.Count, "");
        Changed(nameof(UndoTabTitle));
    }

    private void ApplyQuarantine()
    {
        var shown = allQuarantine.Where(x => quarantineState.Matches(x.Entry)).ToList();
        // Something that is selected but filtered out is no longer in front of the person, so it is not part of what they act on.
        foreach (var hidden in allQuarantine.Where(x => x.IsSelected && !shown.Contains(x)).ToList()) hidden.IsSelected = false;
        shownQuarantine = shown;
        QuarantineList.Sync(ArchiveList.Build(shown, quarantineState.Sort, DateTimeOffset.Now, x => x.Item.Created, x => Math.Max(x.Bytes, 0), x => x.Name,
            group => group.Sum(x => Math.Max(x.Bytes, 0)) is var total and > 0 ? QuarantineRow.Format(total) : ""), RowKey);
        var size = shown.Sum(x => Math.Max(x.Bytes, 0));
        quarantineState.Report(allQuarantine.Count, shown.Count, size > 0 ? QuarantineRow.Format(size) + " on disk" : "");
        Changed(nameof(QuarantineTabTitle)); SelectionChanged();
    }

    private static object RowKey(object row) => row is ArchiveHeader header ? "header:" + header.Label : row;

    private async Task MeasureAsync(IReadOnlyList<QuarantineRow> rows)
    {
        if (rows.Count == 0) return;
        try
        {
            await Task.WhenAll(rows.Select(async row =>
            {
                await measuring.WaitAsync(lifetime.Token);
                try
                {
                    var size = row.Item.Folder is { } folder ? await Task.Run(() => FolderSize.Of(folder.Stored), lifetime.Token) : row.Item.Link?.Data is { } data ? data.Length / 4 * 3 : 0;
                    row.SetBytes(Math.Max(size, 0));
                }
                finally { measuring.Release(); }
            }));
            ApplyQuarantine();
        }
        catch (OperationCanceledException) { }
    }

    // ---- Choosing things in the quarantine ----

    public int SelectedCount => shownQuarantine.Count(x => x.IsSelected);
    public Visibility SelectionVisibility => SelectedCount > 0 ? Visibility.Visible : Visibility.Collapsed;
    public string SelectionText => SelectedCount == 1 ? "1 selected" : $"{SelectedCount:N0} selected";
    public Visibility SelectAllShownVisibility => shownQuarantine.Count > 1 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Ticks or clears every item that is shown, however many a search has narrowed the list to.</summary>
    public bool AllShownSelected
    {
        get => shownQuarantine.Count > 0 && shownQuarantine.All(x => x.IsSelected);
        set
        {
            bulkSelecting = true;
            try { foreach (var row in shownQuarantine) row.IsSelected = value; }
            finally { bulkSelecting = false; }
            SelectionChanged();
        }
    }

    private void SelectionChanged()
    {
        if (bulkSelecting) return;
        Changed(nameof(SelectedCount)); Changed(nameof(SelectionVisibility)); Changed(nameof(SelectionText)); Changed(nameof(AllShownSelected)); Changed(nameof(SelectAllShownVisibility));
    }

    public Task ResolveSelectedAsync(bool keep) => ResolveAsync(shownQuarantine.Where(x => x.IsSelected).ToList(), keep);

    /// <summary>Keeps (puts back where it was) or removes for good the chosen things from the quarantine.</summary>
    public async Task ResolveAsync(IReadOnlyList<QuarantineRow> rows, bool keep)
    {
        if (Busy || rows.Count == 0) return;
        ResetProgress();
        fixing = true; Notify();
        var verb = keep ? "put back" : "removed";
        try
        {
            var outcome = await service.ResolveAsync(rows.Select(x => x.Item).ToList(), keep, lifetime.Token, new Progress<RepairProgress>(OnProgress));
            if (outcome.Problems.Count == 0)
                toasts.Show("repair", ToastKind.Success, keep ? "Put back" : "Removed for good", rows.Count == 1 ? $"{rows[0].Name} was {verb}." : $"{outcome.Done} items were {verb}.");
            else
                toasts.Show("repair", ToastKind.Warning, $"{outcome.Done} of {rows.Count} {verb}", ToastCenter.Brief(string.Join(" ", outcome.Problems)));
            log.Write(outcome.Problems.Count == 0 ? ActivityLevel.Info : ActivityLevel.Warning, ActivityArea.App, keep ? "Quarantine: put back" : "Quarantine: removed", $"{outcome.Done} of {rows.Count} {verb}. " + string.Join(" ", outcome.Problems));
        }
        catch (OperationCanceledException) { toasts.Show("repair", ToastKind.Info, "Administrator permission was declined", "Nothing was changed."); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            toasts.Show("repair", ToastKind.Warning, "That could not finish", ToastCenter.Brief(ex.Message));
            log.Write(ActivityLevel.Error, ActivityArea.App, "Quarantine could not finish", ex.Message);
        }
        finally { fixing = false; RefreshHistory(); Notify(); }
    }

    public Task ResolveAsync(QuarantineRow row, bool keep) => ResolveAsync([row], keep);

    public void Dispose() { IconService.DarkChanged -= ReloadIcons; lifetime.Cancel(); lifetime.Dispose(); }
}

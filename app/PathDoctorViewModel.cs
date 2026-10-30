using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using TidyWindow.Next.Core;

namespace TidyWindow.Next.Desktop;

public sealed class PathRow(PathEntry entry, int count, bool editable) : ObservableState
{
    public PathEntry Entry { get; } = entry;
    public string Number => (Entry.Index + 1).ToString();
    public string Folder => Entry.Raw;
    public string Detail => Entry.Missing ? "This folder no longer exists." : Entry.Duplicate ? $"Already in {Entry.DuplicateOf}, so this copy does nothing." : Entry.Raw != Entry.Folder ? Entry.Folder : "";
    public Visibility DetailVisibility => Detail.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public string Badge => Entry.Missing ? "Missing" : Entry.Duplicate ? "Listed twice" : Entry.StoreApps ? "Store app shortcuts" : "";
    public Visibility WarnVisibility => Entry.NeedsFixing ? Visibility.Visible : Visibility.Collapsed;
    public Visibility InfoBadgeVisibility => Badge.Length > 0 && !Entry.NeedsFixing ? Visibility.Visible : Visibility.Collapsed;
    public Visibility EditVisibility => editable ? Visibility.Visible : Visibility.Collapsed;
    public bool CanUp => Entry.Index > 0;
    public bool CanDown => Entry.Index < count - 1;
    public string AutomationName => $"{Entry.Raw}, position {Entry.Index + 1}";
}

public sealed class CandidateRow(CommandCandidate candidate, bool wins, bool canMakeFirst, PathEntry? entry) : ObservableState
{
    private string version = "Checking…";
    private bool stub;
    public CommandCandidate Candidate { get; } = candidate;
    public PathEntry? Entry { get; } = entry;
    public string File => Candidate.File;
    public bool Wins => wins;
    public bool Stub => stub;
        public double Emphasis => wins ? 1 : 0.7;
    public Visibility RunsVisibility => wins && !stub ? Visibility.Visible : Visibility.Collapsed;
    public Visibility HiddenVisibility => !wins && !stub ? Visibility.Visible : Visibility.Collapsed;
    public Visibility StoreLinkVisibility => stub ? Visibility.Visible : Visibility.Collapsed;
    public string Version { get => version; private set => Set(ref version, value); }
    // A Store placeholder is never worth moving to the front.
    public Visibility MakeFirstVisibility => canMakeFirst && !wins && !stub ? Visibility.Visible : Visibility.Collapsed;

    public void Report(ProbeResult result)
    {
        stub = result.StoreStub;
        foreach (var name in new[] { nameof(Stub), nameof(MakeFirstVisibility), nameof(RunsVisibility), nameof(HiddenVisibility), nameof(StoreLinkVisibility) }) Changed(name);
        Version = result.StoreStub ? "Opens the Microsoft Store" : result.Version.Length > 0 ? result.Version : "Version unknown";
    }
}

public sealed class CommandRow : ObservableState
{
    private readonly Action<string, bool> remember;
    private bool open;

    public CommandRow(CommandLookup lookup, IReadOnlyList<CandidateRow> candidates, bool open, Action<string, bool> remember)
    {
        Lookup = lookup;
        Candidates = candidates;
        this.open = open && candidates.Count > 1;
        this.remember = remember;
        // The winner's version and Store-link state arrive after the row is shown.
        if (Winner is { } winner)
            winner.PropertyChanged += (_, _) =>
            {
                foreach (var name in new[] { nameof(VersionText), nameof(RunsVisibility), nameof(StoreLinkVisibility) }) Changed(name);
            };
    }

    public CommandLookup Lookup { get; }
    public string Name => Lookup.Command;
    public IReadOnlyList<CandidateRow> Candidates { get; }
    public CandidateRow? Winner => Candidates.Count > 0 ? Candidates[0] : null;
    public string Headline => Winner is null ? "Not found in PATH" : Candidates.Count == 1 ? "1 copy" : $"{Candidates.Count} copies";
    public bool Expandable => Candidates.Count > 1;
    public bool IsOpen => open;
    public string PathText => Winner?.File ?? "Not found in PATH";
    public string VersionText => Winner?.Version ?? "";
    public Visibility RunsVisibility => Winner is { Stub: false } ? Visibility.Visible : Visibility.Collapsed;
    public Visibility StoreLinkVisibility => Winner is { Stub: true } ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ExpandableVisibility => Expandable ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SingleVisibility => Expandable ? Visibility.Collapsed : Visibility.Visible;
    public Visibility ChoicesVisibility => open ? Visibility.Visible : Visibility.Collapsed;
    public string Chevron => open ? "" : "";

    public void Toggle()
    {
        if (!Expandable) return;
        open = !open;
        remember(Name, open);
        Changed(nameof(IsOpen)); Changed(nameof(ChoicesVisibility)); Changed(nameof(Chevron));
    }
}

public sealed class ToolRow(ToolSetup setup) : ObservableState
{
    public ToolSetup Setup { get; } = setup;
    public string Name => Setup.Name;
    public string Command => Setup.Command;
    public string Folder => Setup.Folder;
    public string Detail => Setup.Detail;
    public string StatusText => Setup.State switch { ToolState.NotInPath => "Not in PATH", ToolState.Working => "Working", _ => "Not installed" };
    public Visibility FolderVisibility => Setup.Folder.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility WarnVisibility => Setup.State == ToolState.NotInPath ? Visibility.Visible : Visibility.Collapsed;
    public Visibility GoodVisibility => Setup.State == ToolState.Working ? Visibility.Visible : Visibility.Collapsed;
    public Visibility AddVisibility => WarnVisibility;
    public Visibility NotInstalledVisibility => Setup.State == ToolState.NotInstalled ? Visibility.Visible : Visibility.Collapsed;
    public double Emphasis => Setup.State == ToolState.NotInstalled ? 0.55 : 1;
}

public sealed class PathDoctorViewModel(PathDoctor doctor, PathDoctor machineDoctor, MachinePathSession session, CommandResolver resolver, PathSetup setup, ActivityLog log, Action<Action> post, Func<PathSnapshot, Task<string?>> chooseFolder, Action openAliasSettings) : ObservableState, IDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly List<string> custom = [];
    private readonly HashSet<string> opened = new(StringComparer.OrdinalIgnoreCase);
    private PathSnapshot? snapshot;
    private long generation;
    private bool busy, loaded, unlocked;
    private readonly HashSet<string> told = [];
    private string undoText = "", machineUndoText = "", missingCommands = "", lookupText = "";
    private int machineFixable;

    public ObservableCollection<ToastItem> Toasts { get; } = [];
    public ObservableCollection<CommandRow> Commands { get; } = [];
    public ObservableCollection<PathRow> UserFolders { get; } = [];
    public ObservableCollection<PathRow> MachineFolders { get; } = [];
    public ObservableCollection<ToolRow> Tools { get; } = [];

    public int ReadyCount => Tools.Count(x => x.Setup.State == ToolState.NotInPath);
    public Visibility AddAllVisibility => ReadyCount > 1 ? Visibility.Visible : Visibility.Collapsed;
    public string AddAllLabel => $"Add all {ReadyCount} to PATH";
    public string ToolsTabTitle => ReadyCount > 0 ? $"Add tools ({ReadyCount})" : "Add tools";
    public string ToolsIntro => !loaded ? "" : ReadyCount > 0
        ? (ReadyCount == 1 ? "1 installed tool is missing from your PATH, so its command is not recognized in a terminal." : $"{ReadyCount} installed tools are missing from your PATH, so their commands are not recognized in a terminal.")
        : "Every tool Path Doctor knows about that is installed on this PC already works from a terminal.";

    public bool Loaded => loaded;
    public bool Busy => busy;
    public bool CanChange => !busy;
    public bool ScanOverlay => busy && !loaded;
    public string UndoText { get => undoText; private set { Set(ref undoText, value); Changed(nameof(UndoVisibility)); } }
    public Visibility UndoVisibility => undoText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public string MissingCommands { get => missingCommands; private set { Set(ref missingCommands, value); Changed(nameof(MissingVisibility)); } }
    public Visibility MissingVisibility => missingCommands.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public string LookupText { get => lookupText; set => Set(ref lookupText, value); }
    public bool MachineUnlocked => unlocked;
    public Visibility MachineLockedVisibility => unlocked ? Visibility.Collapsed : Visibility.Visible;
    public Visibility MachineUnlockedVisibility => unlocked ? Visibility.Visible : Visibility.Collapsed;
    public string MachineCountText => MachineFolders.Count == 1 ? "1 folder" : $"{MachineFolders.Count} folders";
    public string MachineFixLabel => machineFixable == 1 ? "Remove 1 broken entry" : $"Remove {machineFixable} broken entries";
    public Visibility MachineFixVisibility => unlocked && machineFixable > 0 ? Visibility.Visible : Visibility.Collapsed;
    public string MachineUndoText { get => machineUndoText; private set { Set(ref machineUndoText, value); Changed(nameof(MachineUndoVisibility)); } }
    public Visibility MachineUndoVisibility => unlocked && machineUndoText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    private void Notify()
    {
        foreach (var name in new[] { nameof(Busy), nameof(CanChange), nameof(ScanOverlay), nameof(Loaded), nameof(ToolsIntro), nameof(MachineUnlocked), nameof(MachineLockedVisibility), nameof(MachineUnlockedVisibility), nameof(MachineFixVisibility), nameof(MachineUndoVisibility) }) Changed(name);
    }

    public async Task RefreshAsync()
    {
        if (lifetime.IsCancellationRequested) return;
        var current = ++generation;
        busy = true; Notify();
        try
        {
            var (state, lookups, javaHome, tools) = await Task.Run(() =>
            {
                var s = doctor.Load();
                var names = CommandResolver.Common.Concat(custom).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                return (s, names.Select(n => resolver.Find(n, s)).ToList(), doctor.Variable("JAVA_HOME"), setup.Find(s, n => resolver.Find(n, s)));
            }, lifetime.Token);
            if (current != generation) return;
            snapshot = state;
            SyncLock();
            machineFixable = state.Machine.Count(x => x.NeedsFixing);
            Fill(UserFolders, state.User, true); Fill(MachineFolders, state.Machine, unlocked);
            foreach (var name in new[] { nameof(MachineCountText), nameof(MachineFixLabel), nameof(MachineFixVisibility) }) Changed(name);

            Tools.Clear();
            foreach (var tool in tools) Tools.Add(new ToolRow(tool));
            foreach (var name in new[] { nameof(ReadyCount), nameof(AddAllVisibility), nameof(AddAllLabel), nameof(ToolsTabTitle), nameof(ToolsIntro) }) Changed(name);

            Commands.Clear();
            var missing = new List<string>();
            var all = new List<CandidateRow>();
            foreach (var lookup in lookups)
            {
                if (lookup.Winner is null)
                {
                    // Names the person typed themselves always get an answer; the built-in list stays quiet about tools that are not installed.
                    if (custom.Contains(lookup.Command, StringComparer.OrdinalIgnoreCase)) Commands.Add(new CommandRow(lookup, [], false, Remember));
                    else missing.Add(lookup.Command);
                    continue;
                }
                var winnerMovable = lookup.Winner.Scope == PathScope.User;
                var rows = lookup.Candidates.Take(6).Select((c, i) => new CandidateRow(c, i == 0, winnerMovable && c.Scope == PathScope.User, c.Scope == PathScope.User && c.Index < state.User.Count ? state.User[c.Index] : null)).ToList();
                all.AddRange(rows);
                Commands.Add(new CommandRow(lookup, rows, opened.Contains(lookup.Command), Remember));
            }
            MissingCommands = missing.Count == 0 ? "" : "Not installed or not in PATH: " + string.Join(", ", missing) + ". The Add tools tab can fix this when they are installed.";

            var notes = PathAdvice.JavaHome(javaHome, lookups.First(x => x.Command == "java"), Directory.Exists);
            Advise(state, notes);
            var backup = doctor.LastBackup;
            UndoText = backup is null ? "" : "Undo: " + backup.Why;
            MachineUndoText = machineDoctor.LastBackup is { } machineBackup ? "Undo: " + machineBackup.Why : "";
            loaded = true; busy = false; Notify();

            await ProbeAsync(all, state, notes, current);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Toast("read-failed", ToastKind.Warning, "Path Doctor could not read your PATH", ex.Message, "Try again", () => RefreshAsync());
            log.Write(ActivityLevel.Warning, ActivityArea.App, "Path Doctor could not read PATH", ex.Message);
        }
        finally { if (current == generation) { busy = false; Notify(); } }
    }

    private void Remember(string name, bool open) { if (open) opened.Add(name); else opened.Remove(name); }

    private static void Fill(ObservableCollection<PathRow> target, IReadOnlyList<PathEntry> entries, bool editable)
    {
        target.Clear();
        foreach (var entry in entries) target.Add(new PathRow(entry, entries.Count, editable));
    }

    /// <summary>The helper leaves on its own after sitting idle; when it has, editing is locked again.</summary>
    private void SyncLock() { if (unlocked && !session.IsOpen) unlocked = false; }

    private PathDoctor For(PathRow row) => row.Entry.Scope == PathScope.Machine ? machineDoctor : doctor;
    private Func<Task>? UndoFor(PathRow row) => row.Entry.Scope == PathScope.Machine ? MachineUndoAsync : null;

    private async Task ProbeAsync(List<CandidateRow> rows, PathSnapshot state, IReadOnlyList<string> notes, long current)
    {
        using var gate = new SemaphoreSlim(6);
        await Task.WhenAll(rows.Select(async row =>
        {
            await gate.WaitAsync(lifetime.Token).ConfigureAwait(false);
            try
            {
                var command = Path.GetFileNameWithoutExtension(row.File);
                var result = await CommandProbe.RunAsync(command, row.File, lifetime.Token).ConfigureAwait(false);
                post(() => { if (current == generation) row.Report(result); });
            }
            catch (OperationCanceledException) { }
            finally { gate.Release(); }
        })).ConfigureAwait(true);
        if (current == generation && !lifetime.IsCancellationRequested) Advise(state, notes);
    }

    /// <summary>Tells the person about what is worth fixing, once each, and takes back a warning that no longer applies.</summary>
    private void Advise(PathSnapshot state, IReadOnlyList<string> notes)
    {
        var advice = new List<(string Key, string Title, string Detail, string Action, Func<Task>? Run)>();

        var broken = state.User.Where(x => x.NeedsFixing).ToList();
        if (broken.Count > 0)
        {
            var missing = broken.Count(x => x.Missing);
            var repeated = broken.Count - missing;
            var parts = new List<string>();
            if (missing > 0) parts.Add(missing == 1 ? "1 no longer exists" : $"{missing} no longer exist");
            if (repeated > 0) parts.Add(repeated == 1 ? "1 is listed twice" : $"{repeated} are listed twice");
            advice.Add(("fix:" + string.Join('|', broken.Select(x => x.Raw)),
                broken.Count == 1 ? "1 PATH folder needs fixing" : $"{broken.Count} PATH folders need fixing",
                string.Join(", ", parts) + ". Removing them is safe.",
                broken.Count == 1 ? "Fix it" : $"Fix all {broken.Count}", FixAllAsync));
        }

        var stubs = Commands.Where(x => x.Winner is { Stub: true }).Select(x => x.Name).ToList();
        if (stubs.Count > 0)
            advice.Add(("store:" + string.Join('|', stubs),
                $"Typing {string.Join(" or ", stubs)} opens the Microsoft Store",
                "A Store shortcut comes before the real program. Turn the shortcut off in Windows settings, or pick the working copy under Programs.",
                "Open settings", () => { openAliasSettings(); return Task.CompletedTask; }));

        foreach (var note in notes) advice.Add(("java:" + note, "Java is set up two different ways", note, "", null));

        foreach (var stale in Toasts.Where(x => IsAdvice(x.Key) && advice.All(a => a.Key != x.Key)).ToList()) Toasts.Remove(stale);
        foreach (var item in advice)
            if (told.Add(item.Key)) Toast(item.Key, ToastKind.Warning, item.Title, item.Detail, item.Action, item.Run);
    }

    private static bool IsAdvice(string key) => key.StartsWith("fix:") || key.StartsWith("store:") || key.StartsWith("java:");

    private void Toast(string key, ToastKind kind, string title, string detail = "", string action = "", Func<Task>? run = null)
    {
        // A newer message about the same thing replaces the older one instead of stacking up.
        foreach (var old in Toasts.Where(x => x.Key == key).ToList()) Toasts.Remove(old);
        // Things the person still has to deal with stay until dismissed; confirmations and one-off errors fade.
        var seconds = IsAdvice(key) || key == "read-failed" ? 0 : kind == ToastKind.Success ? 8 : 10;
        var item = new ToastItem(key, kind, title, detail, action, run, t => post(() => Toasts.Remove(t)), seconds);
        Toasts.Add(item);
        while (Toasts.Count > 4) Toasts.RemoveAt(0);
        if (seconds == 0) return;
        _ = Task.Delay(TimeSpan.FromSeconds(seconds), lifetime.Token)
            .ContinueWith(_ => post(() => Toasts.Remove(item)), TaskContinuationOptions.OnlyOnRanToCompletion);
    }

    public async Task CheckAgainAsync()
    {
        told.Clear();
        foreach (var advice in Toasts.Where(x => IsAdvice(x.Key)).ToList()) Toasts.Remove(advice);
        await RefreshAsync();
    }

    public async Task FixAllAsync() => await ApplyAsync(() => doctor.FixProblems());
    public async Task RemoveAsync(PathRow row) => await ApplyAsync(() => For(row).Remove(row.Entry), undo: UndoFor(row));
    public async Task MoveAsync(PathRow row, int delta) => await ApplyAsync(() => For(row).Move(row.Entry, row.Entry.Index + delta), undo: UndoFor(row));
    public async Task MakeFirstAsync(CandidateRow row) { if (row.Entry is { } entry) await ApplyAsync(() => doctor.Move(entry, 0)); }
    public async Task AddToolAsync(ToolRow row) => await ApplyAsync(() => doctor.AddMany([row.Folder]));
    public async Task AddAllToolsAsync()
    {
        var folders = Tools.Where(x => x.Setup.State == ToolState.NotInPath).Select(x => x.Folder).ToList();
        await ApplyAsync(() => doctor.AddMany(folders));
    }
    public async Task UndoAsync() => await ApplyAsync(() => doctor.Undo(), offerUndo: false);
    public void OpenSettings() => openAliasSettings();

    /// <summary>Asks Windows for administrator permission once, then shows the edit buttons on the Windows-wide folders.</summary>
    public async Task UnlockMachineAsync()
    {
        if (busy || unlocked || lifetime.IsCancellationRequested) return;
        busy = true; Notify();
        try
        {
            await session.OpenAsync(lifetime.Token);
            unlocked = true;
            log.Write(ActivityLevel.Info, ActivityArea.App, "Path Doctor unlocked Windows-wide PATH editing", "Administrator permission was granted.");
        }
        catch (OperationCanceledException) { Toast("unlock", ToastKind.Info, "Administrator permission was declined", "The Windows-wide folders stay read-only."); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Toast("unlock", ToastKind.Warning, "Could not unlock editing", ex.Message);
            log.Write(ActivityLevel.Warning, ActivityArea.App, "Path Doctor could not get administrator permission", ex.Message);
        }
        busy = false;
        if (snapshot is not null) Fill(MachineFolders, snapshot.Machine, unlocked);
        Notify();
    }

    public void LockMachine()
    {
        session.Close();
        unlocked = false;
        if (snapshot is not null) Fill(MachineFolders, snapshot.Machine, false);
        Notify();
    }

    public async Task MachineFixAsync() => await ApplyAsync(() => machineDoctor.FixProblems(), undo: MachineUndoAsync);
    public async Task MachineUndoAsync() => await ApplyAsync(() => machineDoctor.Undo(), offerUndo: false);

    public async Task AddMachineFolderAsync()
    {
        var folder = await chooseFolder(snapshot ?? await Task.Run(doctor.Load));
        if (!string.IsNullOrWhiteSpace(folder)) await ApplyAsync(() => machineDoctor.Add(folder), undo: MachineUndoAsync);
    }

    public async Task AddFolderAsync()
    {
        var folder = await chooseFolder(snapshot ?? await Task.Run(doctor.Load));
        if (!string.IsNullOrWhiteSpace(folder)) await ApplyAsync(() => doctor.Add(folder));
    }

    public async Task LookUpAsync()
    {
        var name = LookupText.Trim();
        if (name.Length == 0 || name.Any(c => Path.GetInvalidFileNameChars().Contains(c))) return;
        if (!custom.Contains(name, StringComparer.OrdinalIgnoreCase)) custom.Add(name);
        LookupText = "";
        await RefreshAsync();
    }

    private async Task ApplyAsync(Func<PathChange> change, bool offerUndo = true, Func<Task>? undo = null)
    {
        if (busy || lifetime.IsCancellationRequested) return;
        busy = true; Notify();
        PathChange result;
        try { result = await Task.Run(change); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException)
        {
            result = new(false, "Windows would not let this change be saved: " + ex.Message);
            log.Write(ActivityLevel.Warning, ActivityArea.App, "Path Doctor change failed", ex.Message);
        }
        if (!result.Changed) Toast("result", ToastKind.Warning, result.Message);
        else if (offerUndo) Toast("result", ToastKind.Success, result.Message, "Open a new terminal to see it. Windows that are already open keep the old PATH.", "Undo", undo ?? UndoAsync);
        else Toast("result", ToastKind.Success, result.Message);
        if (result.Changed) log.Write(ActivityLevel.Success, ActivityArea.App, "Path Doctor changed your PATH", result.Message);
        busy = false; Notify();
        await RefreshAsync();
    }

    public void Dispose() { lifetime.Cancel(); session.Close(); lifetime.Dispose(); }
}

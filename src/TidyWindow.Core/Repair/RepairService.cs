namespace TidyWindow.Next.Core;

public enum RepairStatus { Fixed, FixedAfterRestart, NotFixed }

/// <summary>What happened to one finding: the outcome, a plain sentence about it, and every step that was tried in order.</summary>
public sealed record RepairResult(string Id, string Title, RepairStatus Status, string Summary, IReadOnlyList<string> Steps);

public sealed record RepairOutcome(int Fixed, IReadOnlyList<string> Problems, RepairBatch? Batch, IReadOnlyList<RepairResult>? Results = null)
{
    public bool RestartNeeded => Results?.Any(x => x.Status == RepairStatus.FixedAfterRestart) ?? false;
}

public enum QuarantineKind { Folder, Shortcut }

/// <summary>One thing a repair removed and kept, with where it came from. Exactly one of <see cref="Folder"/> and <see cref="Link"/> is set.</summary>
public sealed record QuarantineItem(string BatchId, string Title, DateTimeOffset Created, string Name, string Original, QuarantineKind Kind, FolderBackup? Folder, ShortcutBackup? Link);

public sealed record QuarantineOutcome(int Done, IReadOnlyList<string> Problems);

/// <summary>What a running repair or undo is doing, for a progress display: which item (Index of Total), what is happening this moment, and the last few things that finished for it.</summary>
public sealed record RepairProgress(int Index, int Total, string Title, string Doing, IReadOnlyList<string> Done);

/// <summary>An undo either put everything back or put nothing back; Problems says exactly what stopped it.</summary>
public sealed record UndoOutcome(int Restored, IReadOnlyList<string> Problems, int FilesRemoved = 0, int ShortcutsRestored = 0, int FoldersRestored = 0)
{
    public bool Complete => Problems.Count == 0;
}

/// <summary>
/// Applies repairs, safest first: a program that is still installed gets its saved installer copy restored from its own installer files;
/// only when that cannot be done is its whole Windows Installer record removed, in every place, as one step that is undone again if any part fails.
/// Every removed key is copied first and kept, so the whole repair can be undone.
/// </summary>
public sealed partial class RepairService(IInstallSystem system, IKeyEditor user, IKeyEditor machine, Func<CancellationToken, Task> unlock, Action relock, RepairHistory history,
    IInstallerOps? ops = null, IElevatedOps? elevated = null, IInstallerFetcher? fetcher = null, IShortcutEditor? userLinks = null, IShortcutEditor? machineLinks = null, IFolderEditor? userFolders = null, IFolderEditor? machineFolders = null)
{
    private const int Unknown = InstallRepair.Unknown;

    /// <summary>The state of one run: whether the helper is already approved, and what has been read so far.</summary>
    private sealed class Run(CancellationToken token, RepairOptions options, IProgress<RepairProgress>? sink = null)
    {
        public readonly RepairOptions Options = options;
        public bool Elevated;
        public bool Indexed;
        public readonly List<KeyBackup> Backups = [];
        public readonly List<string> Files = [];
        public readonly List<ShortcutBackup> Links = [];
        public readonly List<FolderBackup> Moved = [];
        /// <summary>The program's record was actually removed from the registry by the item being repaired (it can also be repaired in place, which removes nothing).</summary>
        public bool Removed;
        public readonly List<RepairResult> Results = [];
        public CancellationToken Token = token;

        // What the person sees while it runs: which item, what is being done this moment, and what already finished for this item.
        public int Total;
        private int index;
        private string title = "", doing = "";
        private readonly List<string> done = [];

        public void Begin(string itemTitle) { index++; title = itemTitle; done.Clear(); doing = ""; Removed = false; Send(); }
        public void Doing(string text) { doing = text; Send(); }
        public void Finished(string line) { done.Add(line); Send(); }
        private void Send() => sink?.Report(new(index, Total, title, doing, done.TakeLast(4).ToList()));
    }

    /// <summary>The steps tried for one item. Each one is also shown, as it is added, in the progress display.</summary>
    private sealed class Steps(Run run) : List<string>
    {
        public new void Add(string step) { base.Add(step); run.Finished(step); }
        public new void AddRange(IEnumerable<string> steps) { foreach (var step in steps) Add(step); }
    }

    public async Task<RepairOutcome> FixAsync(IReadOnlyList<RepairFinding> findings, string title, CancellationToken cancellationToken = default, RepairOptions? options = null, IProgress<RepairProgress>? progress = null)
    {
        var run = new Run(cancellationToken, options ?? new(), progress) { Total = findings.Count };
        if (findings.Any(x => x.NeedsAdmin))
        {
            run.Doing("Asking Windows for administrator permission. Approve the prompt when it appears.");
            await unlock(cancellationToken).ConfigureAwait(false); run.Elevated = true;
        }
        try
        {
            // The registry work waits on the helper for every key, so it runs off the caller's thread.
            await Task.Run(() => { foreach (var finding in findings) { cancellationToken.ThrowIfCancellationRequested(); run.Results.Add(FixOne(finding, run)); } }, CancellationToken.None).ConfigureAwait(false);
        }
        finally { relock(); }
        // A repair that changed nothing leaves nothing to undo.
        var batch = run.Backups.Count > 0 || run.Files.Count > 0 || run.Links.Count > 0 || run.Moved.Count > 0 ? history.Save(title, run.Backups, files: run.Files, shortcuts: run.Links, folders: run.Moved) : null;
        var problems = run.Results.Where(x => x.Status == RepairStatus.NotFixed).Select(x => $"{x.Title}: {x.Summary}").ToList();
        return new(run.Results.Count(x => x.Status != RepairStatus.NotFixed), problems, batch, run.Results);
    }

    /// <summary>
    /// Repairs a program that works as far as Windows Installer is concerned, from the saved copy of its own installer, the way Windows' own Repair does (<c>msiexec /fvomus</c>).
    /// Nothing is removed from the registry, so there is nothing to undo.
    /// </summary>
    public async Task<RepairResult> RepairProgramAsync(MsiProduct product, string title, CancellationToken cancellationToken = default, IProgress<RepairProgress>? progress = null, RepairOptions? options = null)
    {
        var run = new Run(cancellationToken, options ?? new(), progress) { Total = 1 };
        run.Begin(title);
        var steps = new Steps(run);
        var finding = new RepairFinding($"repair:{product.Code}", RepairKind.InstallerRecord, title, "", product.Code, RepairLevel.Review, []);
        var package = product.LocalPackage;
        if (ops is null || string.IsNullOrWhiteSpace(package) || !system.FileExists(package))
            return Failed(finding, "Windows Installer has no saved copy of this program's installer, so it cannot be repaired from here.", steps);
        var elevate = product.Context != "User";
        if (elevate && elevated is null) return Failed(finding, "Repairing a Windows-wide program needs the administrator helper, which is not available.", steps);
        if (elevate)
        {
            run.Doing("Asking Windows for administrator permission. Approve the prompt when it appears.");
            await unlock(cancellationToken).ConfigureAwait(false); run.Elevated = true;
        }
        try
        {
            run.Doing("Repairing the program with Windows Installer, from its saved installer file…");
            var result = await Task.Run(() => elevate ? elevated!.Reinstall(package, product) : ops.Reinstall(package, product), CancellationToken.None).ConfigureAwait(false);
            steps.Add($"Repaired it with Windows Installer; it returned {result.ExitCode}. {result.Output}".TrimEnd());
            if (result.ExitCode is 0 or 3010 or 1641)
                return Done(finding, "Windows Installer checked the program and put back what was missing or changed.", steps, restart: result.ExitCode != 0);
            var said = WhyItFailed(result.Output);
            if (said is not null) steps.Add($"Windows Installer said: {said}");
            // The saved copy was not enough: Windows Installer wanted files from the program's original installer. Look for that, the way a broken record is repaired.
            if (said is not null && NeedsOriginal().IsMatch(said))
            {
                if (Recover(finding, product, run, steps) is { } restored) return restored;
                if (steps.Any(x => x.StartsWith("Found the program's own installer file at ", StringComparison.Ordinal)))
                    return Failed(finding, steps[^1].TrimEnd('.') + ".", steps);
            }
            var hint = said is not null && NeedsOriginal().IsMatch(said) ? " Use “Locate installer folder…” to point at the program's original installer, or allow downloading it from WinGet, then try again." : "";
            return Failed(finding, $"Windows Installer could not repair it ({(said is null ? Describe(result.ExitCode) : said.TrimEnd('.'))}).{hint}", steps);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return Failed(finding, ex.Message, steps);
        }
        finally { if (elevate) relock(); }
    }

    /// <summary>The first error Windows Installer wrote to the log a repair left (its "Log: path" line), in its own words; null when there is no log to read.</summary>
    internal static string? WhyItFailed(string output)
    {
        if (LogLine().Match(output) is not { Success: true } line) return null;
        try
        {
            var bytes = File.ReadAllBytes(line.Groups[1].Value.Trim());
            // msiexec writes its log as UTF-16, or as ANSI on older Windows.
            var text = bytes.Length > 1 && (bytes[1] == 0 || (bytes[0] == 0xFF && bytes[1] == 0xFE)) ? System.Text.Encoding.Unicode.GetString(bytes) : System.Text.Encoding.UTF8.GetString(bytes);
            if (ErrorLine().Match(text) is { Success: true } error) return error.Groups[1].Value.Trim();
            if ((text.Contains("SOURCEMGMT: Failed to resolve source", StringComparison.Ordinal) || text.Contains("Repair: Could not resolve source", StringComparison.Ordinal))
                && MissingPackageNote().Match(text) is { Success: true } missing)
                return $"Error 1706. An installation package for the product cannot be found ('{missing.Groups[1].Value.Trim()}').";
            if (text.Contains("SECUREREPAIR: SecureRepair Failed", StringComparison.Ordinal) && SecureRepairNote().Match(text) is { Success: true } secure)
                return $"Error 1316. Windows Installer expected the installer file to be named '{Path.GetFileName(secure.Groups[1].Value.Trim())}'.";
            if (text.Contains("Backup folder is created by user", StringComparison.Ordinal) && text.Contains("Error in rollback skipped", StringComparison.Ordinal))
                return "Windows' rollback folder (C:\\Config.Msi) had locked permissions from an earlier interrupted install.";
            // A launch condition refused to run before anything was changed: the package says in its own words why (often "use the Visual Studio Installer").
            if (LaunchConditionNote().Match(text) is { Success: true } refused)
                return $"the program's own installer refused to run: {refused.Groups[1].Value.Trim()}";
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"Log: (.+)$", System.Text.RegularExpressions.RegexOptions.Multiline)]
    private static partial System.Text.RegularExpressions.Regex LogLine();
    // The first "Error 1706. An installation package ..." line a product reports about itself.
    [System.Text.RegularExpressions.GeneratedRegex(@"-- (Error \d{4}\.[^\r\n]+)")]
    private static partial System.Text.RegularExpressions.Regex ErrorLine();
    [System.Text.RegularExpressions.GeneratedRegex(@"Note: 1: 1706 2: [^\r\n]*?3: ([^\r\n]+)")]
    private static partial System.Text.RegularExpressions.Regex MissingPackageNote();
    [System.Text.RegularExpressions.GeneratedRegex(@"Note: 1: 1316 2: ([^\r\n]+)")]
    private static partial System.Text.RegularExpressions.Regex SecureRepairNote();
    // The message a package's launch condition gives when it stops the run (action LaunchConditions, return value 3).
    [System.Text.RegularExpressions.GeneratedRegex(@"Doing action: LaunchConditions[\s\S]*?Product: [^\r\n]*? -- ([^\r\n]+)[\s\S]*?Action ended [^\r\n]*?LaunchConditions\. Return value 3")]
    private static partial System.Text.RegularExpressions.Regex LaunchConditionNote();
    // 1706 and 1707 cannot find the installation package, 1708 and 1612 its source files, 1714 an update's saved file.
    [System.Text.RegularExpressions.GeneratedRegex(@"^Error (?:1706|1707|1612|1714)\b")]
    private static partial System.Text.RegularExpressions.Regex NeedsOriginal();

    private void Elevate(Run run)
    {
        if (run.Elevated) return;
        run.Doing("Asking Windows for administrator permission. Approve the prompt when it appears.");
        unlock(run.Token).GetAwaiter().GetResult();
        run.Elevated = true;
    }

    private RepairResult FixOne(RepairFinding finding, Run run)
    {
        run.Begin(finding.Title);
        var steps = new Steps(run);
        try
        {
            if (finding.ProcessId is { } id)
            {
                run.Doing("Ending the stuck Windows Installer process…");
                steps.Add("Ended the stuck Windows Installer process.");
                return system.EndProcess(id) ? Done(finding, "It is no longer running.", steps) : Failed(finding, "Windows would not let TidyWindow end it.", steps);
            }
            if (finding.ServiceStuck) return StopService(finding, run, steps);
            if (finding.Shortcuts is { Count: > 0 } stale) return RemoveStale(finding, stale, run, steps);
            if (finding.Folders is { Count: > 0 } leftover) return SetAsideFound(finding, leftover, run, steps);
            if (finding.Entry is not null && finding.Alive && Revive(finding, run, steps) is { } revived) return revived;
            if (finding.Entry is not null && finding.Alive && run.Options.RestoreOnly) return LeftAlone(finding, steps);
            if (finding.Footprint is { Kind: "bundle" } bundle) return Swept(finding, Purge(finding, bundle, run, steps), run, steps);
            if (finding.Footprint is { } request) return Swept(finding, FixProduct(finding, request, run, steps), run, steps);
            if (finding.Links is { Count: > 0 } links) return RemoveLinks(finding, links, run, steps);
            return Swept(finding, RemoveKeys(finding, run, steps), run, steps);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException)
        {
            return Failed(finding, ex.Message, steps);
        }
    }

    private static RepairResult Done(RepairFinding finding, string summary, Steps steps, bool restart = false) =>
        new(finding.Id, finding.Title, restart ? RepairStatus.FixedAfterRestart : RepairStatus.Fixed, summary, steps);

    private static RepairResult Failed(RepairFinding finding, string reason, Steps steps) =>
        new(finding.Id, finding.Title, RepairStatus.NotFixed, reason, steps);

    private static RepairResult LeftAlone(RepairFinding finding, Steps steps)
    {
        steps.Add("The program is still on this PC and its record could not be restored, so the record was left as it was.");
        return Failed(finding, "It could not be restored, and the program is still on this PC, so nothing was removed.", steps);
    }

    // ---- A program that is still installed, with something missing that it needs ----

    /// <summary>
    /// A list entry whose uninstaller is gone, for a program that is still installed: point it at an uninstaller that is still there, and failing that
    /// (when the person allowed downloading) have WinGet's copy of the same version put the program's own uninstaller back. Null means neither worked.
    /// </summary>
    private RepairResult? Revive(RepairFinding finding, Run run, Steps steps)
    {
        var entry = finding.Entry!;
        if (InstallRepair.ProductCode(entry) is not null) return null;

        run.Doing("Looking for the program's own uninstaller…");
        if (FindUninstaller(entry) is { } found)
        {
            var editor = InstallRepair.IsMachine(entry.Key) ? machine : user;
            var done = new List<KeyBackup>();
            try
            {
                foreach (var (name, old) in new[] { ("UninstallString", entry.UninstallString), ("QuietUninstallString", entry.QuietUninstallString) })
                {
                    if (name == "QuietUninstallString" && old.Length == 0) continue;
                    if (old.Length > 0 && InstallRepair.Candidates(old).Any(x => system.FileExists(system.Expand(x.Trim().Trim('"'))))) continue;
                    KeyBackup? copy;
                    try { copy = editor.SetText(entry.Key, name, Repoint(old, found)); }
                    catch (UnauthorizedAccessException) when (!InstallRepair.IsMachine(entry.Key)) { Elevate(run); editor = machine; copy = machine.SetText(entry.Key, name, Repoint(old, found)); }
                    if (copy is not null) done.Add(copy);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException)
            {
                steps.Add($"Could not point the entry at {found}: {ex.Message}");
                RollBack(done, editor, steps);
                return null;
            }
            if (done.Count > 0 && Uninstallable(entry.Key))
            {
                steps.Add($"Found the uninstaller that is still on the PC, {found}, and pointed the entry at it.");
                run.Backups.AddRange(done);
                return Done(finding, $"It could not be uninstalled because the entry named a missing uninstaller. It now points at {found}, so Windows can uninstall the program again.", steps);
            }
            steps.Add("Pointed the entry at the uninstaller it found, but Windows still does not see it there.");
            RollBack(done, editor, steps);
        }
        else steps.Add("Looked in the program's folder for an uninstaller; none is there.");

        // A bundle whose cached setup program is gone gets one back: from a folder the person chose first, then from WinGet.
        var bundleTarget = finding.Kind == RepairKind.BundleRecord && ops is not null ? CachedSetup(entry) : null;
        if (bundleTarget is not null && run.Options.Folders is { Count: > 0 } chosen)
        {
            run.Doing($"Checking {chosen.Count} folder{(chosen.Count == 1 ? "" : "s")} you chose for the program's setup program…");
            var setups = chosen.SelectMany(ops!.FilesIn).Where(x => x.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)).ToList();
            if (PutBackSetup(finding, entry, setups, bundleTarget, run, steps, quiet: true) is { } restored) return restored;
            steps.Add(setups.Count == 0 ? $"Looked in {chosen.Count} folder{(chosen.Count == 1 ? "" : "s")} the person chose; no setup program is there." : $"Looked in {chosen.Count} folder{(chosen.Count == 1 ? "" : "s")} the person chose; none of the {setups.Count} setup programs there is this program's.");
        }

        if (!run.Options.Download || fetcher is null) { steps.Add("Getting the original installer was not allowed, so nothing was downloaded."); return null; }
        run.Doing("Asking WinGet for this program's original installer…");
        if (fetcher.Match(entry.Name, entry.Version, run.Token) is not { } match) { steps.Add($"WinGet does not list {entry.Name} {entry.Version} as a package, so there is no original installer to get."); return null; }
        steps.Add($"WinGet knows it as {match.Id} {match.Version}.");
        var work = Path.Combine(Path.GetTempPath(), "TidyWindow-repair-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            if (finding.Kind == RepairKind.BundleRecord && ops is not null)
            {
                if (bundleTarget is null) steps.Add("The entry does not name a setup program in Windows' package cache, so there is nowhere to put one back.");
                else
                {
                    run.Doing($"Downloading {match.Id} {match.Version} from WinGet…");
                    var downloads = fetcher.Download(match, work, ArchitectureOf(entry), run.Token).Where(x => x.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)).ToList();
                    if (PutBackSetup(finding, entry, downloads, bundleTarget, run, steps, quiet: false) is { } downloaded) return downloaded;
                }
            }
            else
            {
                run.Doing($"Reinstalling {match.Id} {match.Version} over the program with WinGet. This can take a couple of minutes…");
                var result = fetcher.Reinstall(match, run.Token);
                steps.Add($"Ran the original installer over the program with WinGet; it returned {result.ExitCode}.");
                if (result.ExitCode is 0 or 3010 or 1641 && Uninstallable(entry.Key))
                    return Done(finding, "The original installer was run over the program, which put its uninstaller back, so Windows can uninstall it normally.", steps, restart: result.ExitCode != 0);
            }
        }
        finally { Cleanup(work); }
        steps.Add("The entry still names an uninstaller that is not there.");
        return null;
    }

    /// <summary>
    /// Puts the first of these setup programs that is this program's back in the package cache. A Windows-wide bundle's cache needs administrator rights, so the helper does it;
    /// <paramref name="quiet"/> skips the ones that are simply another program's, as a folder full of downloads is mostly.
    /// </summary>
    private RepairResult? PutBackSetup(RepairFinding finding, UninstallEntry entry, IEnumerable<string> files, string target, Run run, Steps steps, bool quiet)
    {
        foreach (var file in files)
        {
            run.Doing("Checking the setup program's signature and putting it back in Windows' package cache…");
            RunResult result;
            if (InstallRepair.IsMachine(entry.Key) && elevated is not null) { Elevate(run); result = elevated.RestoreBundleCopy(file, entry.Name, target); }
            else result = ops!.RestoreBundleCopy(file, entry.Name, target);
            // 87 is "not this program's"; a folder of downloads is full of those.
            if (quiet && result.ExitCode == 87) continue;
            steps.Add(result.ExitCode == 0 ? $"Put the setup program back as {target}." : $"Could not put the setup program back as {target}: {result.Output}".TrimEnd());
            if (result.ExitCode != 0) continue;
            run.Files.Add(target);
            if (Uninstallable(entry.Key))
                return Done(finding, "The program's own setup program was put back in Windows' package cache, so it can be uninstalled and repaired from the Windows list again.", steps);
        }
        return null;
    }

    /// <summary>The architecture a program was built for, from what its entry says about itself, so WinGet is asked for that build and not the one for another processor.</summary>
    internal static string? ArchitectureOf(UninstallEntry entry)
    {
        var setup = InstallRepair.Candidates(string.IsNullOrWhiteSpace(entry.UninstallString) ? entry.QuietUninstallString : entry.UninstallString).Select(x => Path.GetFileName(x.Trim().Trim('"'))).FirstOrDefault() ?? "";
        var text = entry.Name + " " + setup;
        if (Arm64().IsMatch(text)) return "arm64";
        if (X64().IsMatch(text)) return "x64";
        if (X86().IsMatch(text)) return "x86";
        return entry.Key.Contains(@"\WOW6432Node\", StringComparison.OrdinalIgnoreCase) ? "x86" : null;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"(?<![A-Za-z0-9])(?:arm64|aarch64)(?![A-Za-z0-9])", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex Arm64();
    [System.Text.RegularExpressions.GeneratedRegex(@"(?<![A-Za-z0-9])(?:x64|amd64|x86[-_]64|64-bit)(?![A-Za-z0-9])", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex X64();
    [System.Text.RegularExpressions.GeneratedRegex(@"(?<![A-Za-z0-9])(?:x86|win32|32-bit)(?![A-Za-z0-9])", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex X86();

    /// <summary>A file under C:\ProgramData is only reachable with administrator rights.</summary>
    private static bool NeedsAdminFile(string path) => path.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    /// <summary>The setup program in Windows' package cache that the entry's uninstall command names.</summary>
    private string? CachedSetup(UninstallEntry entry)
    {
        foreach (var command in new[] { entry.UninstallString, entry.QuietUninstallString })
            foreach (var candidate in InstallRepair.Candidates(command))
                if (system.Expand(candidate.Trim().Trim('"')) is { } path && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && path.Contains(@"\Package Cache\", StringComparison.OrdinalIgnoreCase)) return path;
        return null;
    }

    private bool Uninstallable(string key) => system.Entries().FirstOrDefault(x => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase)) is { } now && InstallRepair.HasUninstaller(system, now);

    /// <summary>An uninstaller that Inno Setup, NSIS or another installer left in the program's own folder, next to where the entry says it should be.</summary>
    private string? FindUninstaller(UninstallEntry entry)
    {
        var folders = new List<string>();
        foreach (var command in new[] { entry.UninstallString, entry.QuietUninstallString })
            foreach (var candidate in InstallRepair.Candidates(command))
                if (system.Expand(candidate.Trim().Trim('"')) is { Length: > 0 } path && Path.GetDirectoryName(path) is { Length: > 0 } folder) folders.Add(folder);
        if (system.Expand(entry.InstallLocation.Trim().Trim('"')) is { Length: > 0 } location) folders.Add(location);
        var icon = entry.DisplayIcon.Trim().Trim('"');
        if (icon.LastIndexOf(',') is > 0 and var comma && int.TryParse(icon[(comma + 1)..], out _)) icon = icon[..comma];
        if (system.Expand(icon.Trim('"')) is { Length: > 0 } iconPath && Path.GetDirectoryName(iconPath) is { Length: > 0 } iconFolder) folders.Add(iconFolder);

        var wanted = InstallRepair.Candidates(string.IsNullOrWhiteSpace(entry.UninstallString) ? entry.QuietUninstallString : entry.UninstallString).Select(x => Path.GetFileName(system.Expand(x.Trim().Trim('"')))).FirstOrDefault() ?? "";
        foreach (var folder in folders.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!system.FolderExists(folder)) continue;
            var files = system.FilesIn(folder).Where(x => UninstallerName().IsMatch(Path.GetFileName(x))).OrderBy(x => string.Equals(Path.GetFileName(x), wanted, StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
            if (files.Count > 0) return files[0];
        }
        return null;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^(?:unins\d*|uninst(?:all)?[\w\-]*|uninstall[\w\-]*)\.exe$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex UninstallerName();

    /// <summary>The old command with its program swapped for the new one, so its switches (such as /SILENT) stay.</summary>
    private static string Repoint(string old, string file)
    {
        var text = old.Trim();
        if (text.Length == 0) return $"\"{file}\"";
        if (text[0] == '"' && text.IndexOf('"', 1) is > 0 and var end) return $"\"{file}\"{text[(end + 1)..]}";
        var program = InstallRepair.Candidates(text).FirstOrDefault() ?? text;
        return $"\"{file}\"{text[Math.Min(program.Length, text.Length)..]}";
    }

    private static void Cleanup(string folder) { try { if (Directory.Exists(folder)) Directory.Delete(folder, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }

    // ---- A program Windows Installer has a record of ----

    private RepairResult FixProduct(RepairFinding finding, FootprintRequest request, Run run, Steps steps)
    {
        // A program that is still installed is repaired, not unregistered, whenever its own installer file can be found.
        if (finding.Alive && finding.Product is { } product && ops is not null)
        {
            var recovered = Recover(finding, product, run, steps);
            if (recovered is not null) return recovered;
            if (run.Options.RestoreOnly) return LeftAlone(finding, steps);
        }
        return Purge(finding, request, run, steps);
    }

    private RepairResult? Recover(RepairFinding finding, MsiProduct product, Run run, Steps steps)
    {
        var registered = ops!.RegisteredPackageCode(product);
        var work = Path.Combine(Path.GetTempPath(), "TidyWindow-repair-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            string? Matching(IEnumerable<string> files)
            {
                var count = 0;
                foreach (var file in files)
                {
                    var candidates = file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? ops.ExpandBundle(file, Path.Combine(work, "layout" + count++), product.Name) : [file];
                    foreach (var candidate in candidates)
                    {
                        var info = ops.ReadPackage(candidate);
                        if (info is null || !string.Equals(info.ProductCode, product.Code, StringComparison.OrdinalIgnoreCase)) continue;
                        if (registered is not null && info.PackageCode.Length > 0 && !string.Equals(info.PackageCode, registered, StringComparison.OrdinalIgnoreCase)) continue;
                        return candidate;
                    }
                }
                return null;
            }

            run.Doing("Looking for the program's own installer file in its source list and the installer caches…");
            var match = Matching(ops.CandidatePackages(product));
            if (match is null) steps.Add("Looked for the program's own installer file in its source list and the installer caches; none matches this exact build.");
            if (match is null && run.Options.Folders is { Count: > 0 } folders)
            {
                run.Doing($"Checking {folders.Count} folder{(folders.Count == 1 ? "" : "s")} you chose for the installer…");
                match = Matching(folders.SelectMany(ops.FilesIn));
                steps.Add(match is null ? $"Looked in {folders.Count} folder{(folders.Count == 1 ? "" : "s")} the person chose; nothing there is this exact build." : "Found it in a folder the person chose.");
            }
            if (match is null && run.Options.Download && fetcher is not null)
            {
                match = Download(product, run, work, Matching, steps);
            }
            else if (match is null && !run.Options.Download) steps.Add("Getting the original installer was not allowed, so nothing was downloaded.");
            if (match is null) return null;
            steps.Add($"Found the program's own installer file at {match}.");
            return Reinstall(finding, product, match, run, steps);
        }
        finally { Cleanup(work); }
    }

    /// <summary>Asks WinGet for the exact version of the program (or of the setup program that installed it) and checks what it gives back against the program.</summary>
    private string? Download(MsiProduct product, Run run, string work, Func<IEnumerable<string>, string?> matching, Steps steps)
    {
        var entries = system.Entries();
        var first = product.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        var queries = new List<UninstallEntry>();
        queries.AddRange(entries.Where(x => string.Equals(x.KeyName, product.Code, StringComparison.OrdinalIgnoreCase)));
        // Burn programs (Python, Visual C++) list the setup program as the entry, and its packages as separate products.
        queries.AddRange(entries.Where(x => x.BundleProvider.Length > 0 && first.Length >= 3 && x.Name.StartsWith(first, StringComparison.OrdinalIgnoreCase)));
        var tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var architecture = product.Name.Contains("(64-bit)", StringComparison.OrdinalIgnoreCase) ? "x64" : product.Name.Contains("(32-bit)", StringComparison.OrdinalIgnoreCase) ? "x86" : null;
        foreach (var query in queries.Take(4))
        {
            run.Doing($"Asking WinGet for {query.Name}…");
            if (fetcher!.Match(query.Name, query.Version, run.Token) is not { } package || !tried.Add(package.Id + " " + package.Version)) continue;
            run.Doing($"Downloading {package.Id} {package.Version} from WinGet…");
            steps.Add($"WinGet knows {query.Name} as {package.Id} {package.Version}; downloaded that version.");
            var found = matching(fetcher.Download(package, Path.Combine(work, "download" + tried.Count), architecture, run.Token));
            if (found is not null) return found;
            steps.Add("What it downloaded is not this exact build of the program, so it was not used.");
        }
        if (tried.Count == 0) steps.Add("WinGet does not list this program at its installed version, so there is no original installer to get.");
        return null;
    }

    private RepairResult? Reinstall(RepairFinding finding, MsiProduct product, string match, Run run, Steps steps)
    {
        RunResult result;
        run.Doing("Repairing the program with Windows Installer, from its own installer file…");
        if (product.Context == "User") result = ops!.Reinstall(match, product);
        else if (elevated is not null) { Elevate(run); result = elevated.Reinstall(match, product); }
        else { steps.Add("Repairing a Windows-wide program needs the administrator helper, which is not available."); return null; }
        steps.Add($"Repaired it with Windows Installer; it returned {result.ExitCode}. {result.Output}".TrimEnd());

        if (result.ExitCode is 0 or 3010 or 1641)
        {
            var now = system.Products().FirstOrDefault(x => string.Equals(x.Code, product.Code, StringComparison.OrdinalIgnoreCase));
            if (now is not null && !string.IsNullOrWhiteSpace(now.LocalPackage) && system.FileExists(now.LocalPackage))
                return Done(finding, "Windows Installer has its saved copy again, so the program can be repaired, removed and updated normally.", steps, restart: result.ExitCode != 0);
            steps.Add("Windows Installer reported success, but the saved copy is still missing.");
        }
        else
        {
            var said = WhyItFailed(result.Output);
            steps.Add(said is null ? $"Windows Installer could not repair it ({Describe(result.ExitCode)})." : $"Windows Installer could not repair it ({said.TrimEnd('.')}).");
        }
        return null;
    }

    private static string Describe(int code) => code switch
    {
        1618 => "another install is running", 1612 => "the installer's source files are not all there", 1603 => "the install failed",
        1605 => "Windows Installer does not know the program", 1925 => "it needs administrator rights", 87 => "that file does not match the program", _ => "error " + code
    };

    // ---- Removing a record completely ----

    private RepairResult Purge(RepairFinding finding, FootprintRequest request, Run run, Steps steps)
    {
        Elevate(run);
        run.Doing("Finding every place Windows Installer keeps this program's record…");
        var plan = machine.Footprint(request with { Fresh = !run.Indexed });
        run.Indexed = true;
        if (plan.Unreadable.Count > 0)
        {
            steps.Add("Looked for every place Windows Installer keeps this record.");
            return Failed(finding, $"Windows would not let TidyWindow read {plan.Unreadable[0]}, so it removed nothing rather than leave a half-removed record.", steps);
        }
        steps.AddRange(plan.Notes);
        var code = request.Code;
        if (plan.IsEmpty && (code.Length == 0 || system.ProductState(code) == Unknown || plan.Elsewhere))
            return Done(finding, "No record of it was left to remove.", steps);
        steps.Add($"Found {plan.Keys.Count} registry {(plan.Keys.Count == 1 ? "key" : "keys")} and {plan.Values.Count} shared {(plan.Values.Count == 1 ? "link" : "links")} that make up its record.");

        var done = new List<KeyBackup>();
        run.Doing($"Removing {plan.Keys.Count} registry {(plan.Keys.Count == 1 ? "key" : "keys")} and saving a copy of each for Undo…");
        try
        {
            foreach (var key in plan.Keys) if (machine.Delete(key) is { } copy) done.Add(copy);
            foreach (var link in plan.Values) if (machine.DeleteValue(link.Path, link.Name) is { } copy) done.Add(copy);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException)
        {
            steps.Add("Removing the record failed part of the way.");
            return Failed(finding, ex.Message + RollBack(done, machine, steps), steps);
        }

        run.Doing("Checking that Windows no longer sees the program and nothing is left behind…");
        var left = Leftovers(plan);
        if (left.Count == 0 && code.Length > 0 && !plan.Elsewhere && system.ProductState(code) != Unknown)
            left.Add($"Windows Installer still reports the program (state {system.ProductState(code)})");
        if (left.Count > 0)
        {
            steps.Add("Checked afterwards that nothing was left.");
            return Failed(finding, left[0] + ", so the removal was undone." + RollBack(done, machine, steps), steps);
        }
        steps.Add("Checked afterwards: Windows Installer no longer knows the program and none of its keys are left.");
        run.Backups.AddRange(done);
        run.Removed = true;
        return Done(finding, $"Removed the whole record ({plan.Keys.Count} keys, {plan.Values.Count} shared links). The program can be installed again.", steps);
    }

    private List<string> Leftovers(FootprintPlan plan)
    {
        var left = new List<string>();
        foreach (var key in plan.Keys) if (machine.Exists(key)) { left.Add($"{key} is still there"); break; }
        foreach (var link in plan.Values)
            if (machine.Snapshot(link.Path) is { } snapshot && snapshot.Values.Any(x => string.Equals(x.Name, link.Name, StringComparison.OrdinalIgnoreCase))) { left.Add($"The link in {link.Path} is still there"); break; }
        return left;
    }

    /// <summary>Puts back what a failed step had already removed. Returns a sentence to add to the reason, empty when everything went back.</summary>
    private static string RollBack(List<KeyBackup> done, IKeyEditor editor, Steps steps)
    {
        var failed = new List<string>();
        foreach (var copy in Enumerable.Reverse(done))
        {
            try { editor.Restore(copy); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException) { failed.Add(Leaf(copy.Path)); }
        }
        done.Clear();
        if (failed.Count == 0) { steps.Add("Put back everything it had removed, so nothing is half removed."); return " Everything it had removed was put back."; }
        steps.Add($"Could not put back {failed.Count} of the keys it had removed: {string.Join(", ", failed.Take(5))}.");
        return $" {failed.Count} removed keys could not be put back ({string.Join(", ", failed.Take(3))}); use Undo.";
    }

    private static string Leaf(string path) => path[(path.LastIndexOf('\\') + 1)..];

    // ---- Leftover links, plain keys, services ----

    private RepairResult RemoveLinks(RepairFinding finding, IReadOnlyList<ValueRef> links, Run run, Steps steps)
    {
        Elevate(run);
        run.Doing($"Removing {links.Count} leftover {(links.Count == 1 ? "link" : "links")}…");
        var done = new List<KeyBackup>();
        try { foreach (var link in links) if (machine.DeleteValue(link.Path, link.Name) is { } copy) done.Add(copy); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException)
        {
            return Failed(finding, ex.Message + RollBack(done, machine, steps), steps);
        }
        steps.Add($"Removed {done.Count} {(done.Count == 1 ? "link" : "links")}.");
        run.Backups.AddRange(done);
        return Done(finding, $"Removed {done.Count} {(done.Count == 1 ? "link" : "links")}.", steps);
    }

    private RepairResult RemoveKeys(RepairFinding finding, Run run, Steps steps)
    {
        var done = new List<(KeyBackup Copy, IKeyEditor Editor)>();
        run.Doing($"Removing {finding.Keys.Count} registry {(finding.Keys.Count == 1 ? "key" : "keys")} and saving a copy for Undo…");
        try
        {
            foreach (var key in finding.Keys)
            {
                var editor = InstallRepair.IsMachine(key) ? machine : user;
                KeyBackup? copy;
                try { copy = editor.Delete(key); }
                // A key in the user's own hive can carry permissions only an administrator can change; the helper reaches the same hive with more rights.
                catch (UnauthorizedAccessException) when (!InstallRepair.IsMachine(key))
                {
                    steps.Add("The key's permissions kept this account out, so the administrator helper took over.");
                    Elevate(run); editor = machine; copy = machine.Delete(key);
                }
                if (copy is not null) done.Add((copy, editor));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException)
        {
            var failed = new List<string>();
            foreach (var (copy, editor) in Enumerable.Reverse(done))
            {
                try { editor.Restore(copy); } catch (Exception back) when (back is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException) { failed.Add(Leaf(copy.Path)); }
            }
            return Failed(finding, ex.Message + (done.Count > 0 && failed.Count == 0 ? " The keys already removed were put back." : failed.Count > 0 ? $" {failed.Count} removed keys could not be put back; use Undo." : ""), steps);
        }
        run.Backups.AddRange(done.Select(x => x.Copy));
        run.Removed = done.Count > 0;
        var left =finding.Alive && finding.Entry is { InstallLocation.Length: > 0 } entry ? $" The program's own files were left in {entry.InstallLocation}; reinstall it to get them under Windows' control again, or delete that folder." : "";
        return Done(finding, done.Count == 0 ? "It was already gone." : $"Removed {done.Count} registry {(done.Count == 1 ? "key" : "keys")}.{left}", steps);
    }

    private RepairResult StopService(RepairFinding finding, Run run, Steps steps)
    {
        if (elevated is null) return Failed(finding, "The administrator helper is not available.", steps);
        Elevate(run);
        run.Doing("Stopping the Windows Installer service…");
        steps.Add("Stopped the Windows Installer service so Windows starts a fresh one.");
        return elevated.StopInstallerService() ? Done(finding, "The service was stopped. Windows starts it again the next time an install needs it.", steps) : Failed(finding, "The service would not stop. Restarting Windows clears it.", steps);
    }

    // ---- Shortcuts and Start menu folders ----

    private bool IsMachineLink(string path) =>
        system.ShortcutRoots().Where(x => ShortcutPlaces.Under(path, x.Path)).OrderByDescending(x => x.Path.Length).FirstOrDefault()?.Machine ?? false;

    private IShortcutEditor Editor(string path) =>
        (IsMachineLink(path) ? machineLinks : userLinks) ?? throw new InvalidOperationException("Removing shortcuts is not available here.");

    /// <summary>Removes each shortcut or empty folder, keeping a copy of every one in <paramref name="done"/> as it goes so a failure can be taken back.</summary>
    private void Delete(IReadOnlyList<string> paths, Run run, List<ShortcutBackup> done)
    {
        foreach (var path in paths)
        {
            var editor = Editor(path);
            if (IsMachineLink(path)) Elevate(run);
            done.AddRange(editor.RemoveShortcut(path));
        }
    }

    /// <summary>Puts back what a failed step had already removed. Returns what could not be put back, which stays in the Undo tab.</summary>
    private List<ShortcutBackup> PutBack(List<ShortcutBackup> done)
    {
        var stuck = new List<ShortcutBackup>();
        foreach (var copy in Enumerable.Reverse(done))
        {
            try { Editor(copy.Path).RestoreShortcut(copy); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException) { stuck.Add(copy); }
        }
        done.Clear();
        return stuck;
    }

    private static string Count(int number, string one, string many) => number == 1 ? $"1 {one}" : $"{number} {many}";

    private static bool IsShortcutFile(string path) => path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".url", StringComparison.OrdinalIgnoreCase);

    private static string Describe(IReadOnlyList<string> paths)
    {
        var files = paths.Count(IsShortcutFile);
        var parts = new List<string>();
        if (files > 0) parts.Add(Count(files, "shortcut", "shortcuts"));
        if (paths.Count - files > 0) parts.Add(Count(paths.Count - files, "empty folder", "empty folders"));
        return string.Join(" and ", parts);
    }

    private RepairResult RemoveStale(RepairFinding finding, IReadOnlyList<string> paths, Run run, Steps steps)
    {
        run.Doing($"Removing {Describe(paths)} and saving a copy of each for Undo…");
        var done = new List<ShortcutBackup>();
        try { Delete(paths, run, done); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException)
        {
            steps.Add("Removing the shortcuts failed part of the way.");
            var stuck = PutBack(done);
            run.Links.AddRange(stuck);
            return Failed(finding, ex.Message + (stuck.Count == 0 ? " What was already removed was put back." : $" {stuck.Count} removed items could not be put back; use Undo."), steps);
        }
        system.ShellChanged(paths);
        run.Links.AddRange(done);
        var unsaved = paths.Count(IsShortcutFile) - done.Count(x => x.Data is not null && IsShortcutFile(x.Path));
        steps.Add($"Removed {Describe(paths)}; told Windows, so the Start menu and search drop {(paths.Count == 1 ? "it" : "them")} now.");
        if (unsaved > 0) steps.Add($"{Count(unsaved, "file was", "files were")} OneDrive placeholders that could not be read, so there is no copy of {(unsaved == 1 ? "it" : "them")} to put back.");
        return Done(finding, $"Removed {Describe(paths)}.", steps);
    }

    /// <summary>
    /// After a program's registry record is removed, takes away the shortcuts that started it and that are broken now, so Windows Search and the Start menu stop listing it.
    /// Shortcuts that still work are never touched, and the all-users Start menu is left for the next scan unless the helper is already approved.
    /// </summary>
    private RepairResult Swept(RepairFinding finding, RepairResult result, Run run, Steps steps) =>
        SweptFolders(finding, SweptShortcuts(finding, result, run, steps), run, steps);

    private RepairResult SweptShortcuts(RepairFinding finding, RepairResult result, Run run, Steps steps)
    {
        if (result.Status == RepairStatus.NotFixed || (finding.Entry is null && finding.Product is null)) return result;
        try
        {
            var picked = InstallRepair.SweepOf(system, InstallRepair.MarksOf(system, finding.Entry, finding.Product));
            if (picked.Count == 0) return result;
            var paths = picked.Where(x => run.Elevated || !x.Machine).Select(x => x.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var skipped = picked.Count(x => !run.Elevated && x.Machine);
            if (skipped > 0) steps.Add($"{Count(skipped, "leftover shortcut", "leftover shortcuts")} in the all-users Start menu were left alone, because removing them needs administrator permission. Scan again to clean them up.");
            if (paths.Count == 0) return result;

            run.Doing("Removing the shortcuts the program left behind…");
            var done = new List<ShortcutBackup>();
            try { Delete(paths, run, done); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException)
            {
                run.Links.AddRange(PutBack(done));
                steps.Add($"Could not remove the program's leftover shortcuts ({ex.Message}); they were left as they were.");
                return result;
            }
            system.ShellChanged(paths);
            run.Links.AddRange(done);
            steps.Add($"Also removed {Describe(paths)} that started the removed program, so Windows Search stops listing it.");
            return result with { Summary = result.Summary + $" Its leftover {Describe(paths)} were removed too." };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException)
        {
            steps.Add($"Could not look for the program's leftover shortcuts: {ex.Message}");
            return result;
        }
    }

    // ---- Installer files and program folders ----

    private IFolderEditor FolderEditor(string path) =>
        (RepairFolders.IsMachine(path) ? machineFolders : userFolders) ?? throw new InvalidOperationException("Setting folders aside is not available here.");

    /// <summary>Sets each folder aside, keeping where it went in <paramref name="done"/> as it goes so a failure can be taken back.</summary>
    private void SetAsideAll(IReadOnlyList<string> paths, Run run, List<FolderBackup> done)
    {
        foreach (var path in paths)
        {
            var editor = FolderEditor(path);
            if (RepairFolders.IsMachine(path)) Elevate(run);
            if (editor.SetAside(path, history.SetAsideFolder) is { } kept) done.Add(kept);
        }
    }

    /// <summary>Puts back what a failed step had already set aside. Returns what could not be put back, which stays in the Undo tab.</summary>
    private List<FolderBackup> PutBackAll(List<FolderBackup> done)
    {
        var stuck = new List<FolderBackup>();
        foreach (var kept in Enumerable.Reverse(done))
        {
            try { FolderEditor(kept.Original).PutBack(kept); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException) { stuck.Add(kept); }
        }
        done.Clear();
        return stuck;
    }

    private RepairResult SetAsideFound(RepairFinding finding, IReadOnlyList<string> paths, Run run, Steps steps)
    {
        run.Doing($"Setting {Count(paths.Count, "folder", "folders")} aside and keeping them for Undo…");
        var done = new List<FolderBackup>();
        try { SetAsideAll(paths, run, done); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException)
        {
            steps.Add("Setting the folders aside failed part of the way.");
            var stuck = PutBackAll(done);
            run.Moved.AddRange(stuck);
            return Failed(finding, ex.Message + (stuck.Count == 0 ? " What was already set aside was put back." : $" {stuck.Count} folders could not be put back; use Undo."), steps);
        }
        run.Moved.AddRange(done);
        steps.Add($"Set {Count(done.Count, "folder", "folders")} aside. They stay in the Undo tab until the repair is undone or ten newer repairs push it out.");
        return Done(finding, $"Set {Count(done.Count, "folder", "folders")} aside.", steps);
    }

    /// <summary>
    /// After a program's record is removed: sets aside the installer files its record left (icon folders, and the Package Cache of a program that is gone), the program's own folder when the person asked for that,
    /// and the rollback files of an interrupted install whose marker was just removed. Everything is kept for Undo.
    /// </summary>
    private RepairResult SweptFolders(RepairFinding finding, RepairResult result, Run run, Steps steps)
    {
        var removed = run.Removed;
        run.Removed = false;
        if (!removed || result.Status == RepairStatus.NotFixed) return result;
        try
        {
            var found = new List<string>();
            if (finding.Entry is not null || finding.Product is not null)
            {
                var bundle = finding.Footprint is { Kind: "bundle" };
                // A program that is still on this PC keeps its installer files, which are what repairs and updates it, unless the person said to remove the program for good.
                if (!finding.Alive || run.Options.RemoveProgramFolders)
                    foreach (var code in InstallRepair.CodesOf(finding.Entry, finding.Product, bundle))
                    {
                        if (!bundle && system.ProductState(code) != Unknown) continue;
                        // The cached packages of a hidden sub-package belong to the setup program that installed it, which still uses them.
                        found.AddRange(system.InstallerLeftovers(code, packageCache: bundle || finding.Entry is { SystemComponent: false }));
                    }
                if (run.Options.RemoveProgramFolders) found.AddRange(InstallRepair.ProgramFolders(system, finding.Entry, finding.Product));
            }
            if (finding.Kind == RepairKind.InstallLock && !system.InstallerBusy() && system.RollbackFolder() is { } rollback) found.Add(rollback);

            // The person's own choice to remove a program's folder may ask for the helper; the installer files are only taken when it is already approved.
            var wanted = found.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var paths = wanted.Where(x => run.Elevated || run.Options.RemoveProgramFolders || !RepairFolders.IsMachine(x)).ToList();
            if (wanted.Count > paths.Count) steps.Add($"{Count(wanted.Count - paths.Count, "leftover installer folder", "leftover installer folders")} were left alone, because setting them aside needs administrator permission. Scan again to clean them up.");
            if (paths.Count == 0) return result;

            run.Doing("Setting aside what the program left behind…");
            var done = new List<FolderBackup>();
            try { SetAsideAll(paths, run, done); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException)
            {
                run.Moved.AddRange(PutBackAll(done));
                steps.Add($"Could not set the program's leftover folders aside ({ex.Message}); they were left as they were.");
                return result;
            }
            run.Moved.AddRange(done);
            steps.Add($"Also set {Count(done.Count, "leftover folder", "leftover folders")} aside ({string.Join(", ", done.Take(3).Select(x => Path.GetFileName(x.Original)))}{(done.Count > 3 ? "…" : "")}). Undo puts them back.");
            return result with { Summary = result.Summary + $" Its {Count(done.Count, "leftover folder was", "leftover folders were")} set aside too." };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException)
        {
            steps.Add($"Could not look for the program's leftover folders: {ex.Message}");
            return result;
        }
    }

    // ---- Quarantine: what repairs removed, one thing at a time ----

    /// <summary>Everything the saved repairs removed that can be put back by itself: the folders they set aside and the shortcut files they removed, newest first.</summary>
    public IReadOnlyList<QuarantineItem> Quarantine()
    {
        var items = new List<QuarantineItem>();
        foreach (var batch in history.All())
        {
            foreach (var folder in batch.SetAside)
                items.Add(new(batch.Id, batch.Title, batch.Created, Path.GetFileName(folder.Original), folder.Original, QuarantineKind.Folder, folder, null));
            foreach (var link in batch.Shortcuts.Where(x => x.Data is not null))
                items.Add(new(batch.Id, batch.Title, batch.Created, Path.GetFileNameWithoutExtension(link.Path), link.Path, QuarantineKind.Shortcut, null, link));
        }
        return items;
    }

    /// <summary>
    /// Keeps (puts back) or removes for good each of these. Each one stands alone: one that cannot be done is reported and the rest carry on.
    /// A kept item leaves the repair it came from, so undoing that repair later does not put it back a second time.
    /// </summary>
    public async Task<QuarantineOutcome> ResolveAsync(IReadOnlyList<QuarantineItem> items, bool keep, CancellationToken cancellationToken = default, IProgress<RepairProgress>? progress = null)
    {
        var run = new Run(cancellationToken, new(), progress) { Total = items.Count };
        if (items.Any(IsMachineItem))
        {
            run.Doing("Asking Windows for administrator permission. Approve the prompt when it appears.");
            await unlock(cancellationToken).ConfigureAwait(false); run.Elevated = true;
        }
        var problems = new List<string>();
        var done = 0;
        try
        {
            await Task.Run(() =>
            {
                foreach (var item in items)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    run.Begin(item.Name);
                    run.Doing(keep ? "Putting it back…" : "Removing it for good…");
                    try { Resolve(item, keep, run); done++; }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException) { problems.Add($"{item.Name}: {ex.Message}"); }
                }
            }, CancellationToken.None).ConfigureAwait(false);
        }
        finally { relock(); }
        return new(done, problems);
    }

    private bool IsMachineItem(QuarantineItem item) => item.Folder is not null ? RepairFolders.IsMachine(item.Original) : IsMachineLink(item.Original);

    private void Resolve(QuarantineItem item, bool keep, Run run)
    {
        if (IsMachineItem(item)) Elevate(run);
        if (item.Folder is { } folder)
        {
            if (keep) FolderEditor(folder.Original).PutBack(folder); else FolderEditor(folder.Original).Discard(folder);
            history.Update(item.BatchId, batch => batch with { Moved = batch.SetAside.Where(x => x != folder).ToList() });
            return;
        }
        if (item.Link is not { } link) return;
        if (keep)
        {
            Editor(link.Path).RestoreShortcut(link);
            system.ShellChanged([link.Path]);
        }
        history.Update(item.BatchId, batch => batch with { Links = batch.Shortcuts.Where(x => x != link).ToList() });
    }

    // ---- Undo ----

    /// <summary>
    /// Puts back everything one repair removed, with the original permissions. It is all or nothing: if any key cannot be put back,
    /// the keys that were put back in this attempt are removed again, the repair stays in the Undo tab, and the reasons are returned.
    /// </summary>
    public async Task<UndoOutcome> UndoAsync(RepairBatch batch, CancellationToken cancellationToken = default, IProgress<RepairProgress>? progress = null)
    {
        var run = new Run(cancellationToken, new(), progress) { Total = 1 };
        run.Begin(batch.Title);
        if (batch.Keys.Any(x => InstallRepair.IsMachine(x.Path)) || batch.PutBack.Any(NeedsAdminFile) || batch.Shortcuts.Any(x => IsMachineLink(x.Path)) || batch.SetAside.Any(x => RepairFolders.IsMachine(x.Original)))
        {
            run.Doing("Asking Windows for administrator permission. Approve the prompt when it appears.");
            await unlock(cancellationToken).ConfigureAwait(false); run.Elevated = true;
        }
        run.Doing(batch.Keys.Count > 0 ? $"Putting back {batch.Keys.Count} registry {(batch.Keys.Count == 1 ? "key" : "keys")} with their original permissions…" : "Taking back what the repair put in place…");
        UndoOutcome outcome;
        try { outcome = await Task.Run(() => Undo(batch, run), CancellationToken.None).ConfigureAwait(false); }
        finally { relock(); }
        if (outcome.Complete) history.Remove(batch.Id);
        return outcome;
    }

    private UndoOutcome Undo(RepairBatch batch, Run run)
    {
        var applied = new List<(KeyBackup Copy, KeyBackup? Before, IKeyEditor Editor)>();
        var problems = new List<string>();
        foreach (var copy in Enumerable.Reverse(batch.Keys))
        {
            var editor = InstallRepair.IsMachine(copy.Path) ? machine : user;
            try
            {
                KeyBackup? before;
                try { before = editor.Snapshot(copy.Path); editor.Restore(copy); }
                catch (UnauthorizedAccessException) when (!InstallRepair.IsMachine(copy.Path))
                {
                    Elevate(run); editor = machine; before = editor.Snapshot(copy.Path); editor.Restore(copy);
                }
                applied.Add((copy, before, editor));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException)
            {
                problems.Add($"{Leaf(copy.Path)}: {ex.Message}");
            }
        }
        var keyProblems = problems.Count;
        // The shortcuts come back once the registry has; the last one first removed is the first put back, so a folder exists before the file in it.
        var restored = new List<ShortcutBackup>();
        if (problems.Count == 0 && batch.Shortcuts.Count > 0)
        {
            run.Doing($"Putting back {Count(batch.Shortcuts.Count(x => x.Data is not null), "shortcut", "shortcuts")}…");
            foreach (var copy in Enumerable.Reverse(batch.Shortcuts))
            {
                try { Editor(copy.Path).RestoreShortcut(copy); restored.Add(copy); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException)
                {
                    problems.Add($"{Path.GetFileName(copy.Path)}: {ex.Message}");
                    break;
                }
            }
        }
        // The folders that were set aside come back last of the things a repair removed, the last one set aside first.
        var movedBack = new List<FolderBackup>();
        if (problems.Count == 0 && batch.SetAside.Count > 0)
        {
            run.Doing($"Moving {Count(batch.SetAside.Count, "folder", "folders")} back…");
            foreach (var kept in Enumerable.Reverse(batch.SetAside))
            {
                try { FolderEditor(kept.Original).PutBack(kept); movedBack.Add(kept); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException)
                {
                    problems.Add($"{Path.GetFileName(kept.Original)}: {ex.Message}");
                    break;
                }
            }
        }
        if (problems.Count == 0)
        {
            if (restored.Count > 0) system.ShellChanged(restored.Select(x => x.Path).ToList());
            // The files a repair put back go last, once everything else is back; one that cannot be removed leaves the repair in the Undo tab to try again.
            var removed = 0;
            foreach (var file in batch.PutBack)
            {
                run.Doing($"Removing the setup program the repair put back ({Path.GetFileName(file)})…");
                var remover = NeedsAdminFile(file) && elevated is not null ? elevated : ops;
                if (remover is null || remover.RemoveCopy(file)) removed++;
                else problems.Add($"{Path.GetFileName(file)}: Windows would not let TidyWindow remove the setup program it put back. The registry keys were put back.");
            }
            return new(applied.Count, problems, removed, restored.Count(x => x.Data is not null), movedBack.Count);
        }

        // Something could not be put back: take back what was, so the PC is exactly as it was before the undo.
        var stuck = new List<string>();
        foreach (var kept in Enumerable.Reverse(movedBack))
        {
            try { FolderEditor(kept.Original).TakeAgain(kept); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException) { stuck.Add($"{Path.GetFileName(kept.Original)}: {ex.Message}"); }
        }
        foreach (var copy in Enumerable.Reverse(restored))
        {
            try { Editor(copy.Path).RemoveShortcut(copy.Path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException) { stuck.Add($"{Path.GetFileName(copy.Path)}: {ex.Message}"); }
        }
        foreach (var (copy, before, editor) in Enumerable.Reverse(applied))
        {
            try { editor.Delete(copy.Path); if (before is not null) editor.Restore(before); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException) { stuck.Add($"{Leaf(copy.Path)}: {ex.Message}"); }
        }
        var head = keyProblems > 0 ? $"{keyProblems} of {batch.Keys.Count} registry keys could not be put back, so nothing was restored." : "A shortcut or folder could not be put back, so nothing was restored.";
        if (stuck.Count > 0) problems.Add($"{stuck.Count} keys restored before the failure could not be taken out again and may still be there ({stuck[0]}).");
        problems.Insert(0, head);
        return new(0, problems);
    }
}

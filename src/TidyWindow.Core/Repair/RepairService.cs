namespace TidyWindow.Next.Core;

public enum RepairStatus { Fixed, FixedAfterRestart, NotFixed }

/// <summary>What happened to one finding: the outcome, a plain sentence about it, and every step that was tried in order.</summary>
public sealed record RepairResult(string Id, string Title, RepairStatus Status, string Summary, IReadOnlyList<string> Steps);

public sealed record RepairOutcome(int Fixed, IReadOnlyList<string> Problems, RepairBatch? Batch, IReadOnlyList<RepairResult>? Results = null)
{
    public bool RestartNeeded => Results?.Any(x => x.Status == RepairStatus.FixedAfterRestart) ?? false;
}

/// <summary>What a running repair or undo is doing, for a progress display: which item (Index of Total), what is happening this moment, and the last few things that finished for it.</summary>
public sealed record RepairProgress(int Index, int Total, string Title, string Doing, IReadOnlyList<string> Done);

/// <summary>An undo either put everything back or put nothing back; Problems says exactly what stopped it.</summary>
public sealed record UndoOutcome(int Restored, IReadOnlyList<string> Problems, int FilesRemoved = 0)
{
    public bool Complete => Problems.Count == 0;
}

/// <summary>
/// Applies repairs, safest first: a program that is still installed gets its saved installer copy restored from its own installer files;
/// only when that cannot be done is its whole Windows Installer record removed, in every place, as one step that is undone again if any part fails.
/// Every removed key is copied first and kept, so the whole repair can be undone.
/// </summary>
public sealed partial class RepairService(IInstallSystem system, IKeyEditor user, IKeyEditor machine, Func<CancellationToken, Task> unlock, Action relock, RepairHistory history,
    IInstallerOps? ops = null, IElevatedOps? elevated = null, IInstallerFetcher? fetcher = null)
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
        public readonly List<RepairResult> Results = [];
        public CancellationToken Token = token;

        // What the person sees while it runs: which item, what is being done this moment, and what already finished for this item.
        public int Total;
        private int index;
        private string title = "", doing = "";
        private readonly List<string> done = [];

        public void Begin(string itemTitle) { index++; title = itemTitle; done.Clear(); doing = ""; Send(); }
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
        var batch = run.Backups.Count > 0 || run.Files.Count > 0 ? history.Save(title, run.Backups, files: run.Files) : null;
        var problems = run.Results.Where(x => x.Status == RepairStatus.NotFixed).Select(x => $"{x.Title}: {x.Summary}").ToList();
        return new(run.Results.Count(x => x.Status != RepairStatus.NotFixed), problems, batch, run.Results);
    }

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
            if (finding.Entry is not null && finding.Alive && Revive(finding, run, steps) is { } revived) return revived;
            if (finding.Footprint is { Kind: "bundle" } bundle) return Purge(finding, bundle, run, steps);
            if (finding.Footprint is { } request) return FixProduct(finding, request, run, steps);
            if (finding.Links is { Count: > 0 } links) return RemoveLinks(finding, links, run, steps);
            return RemoveKeys(finding, run, steps);
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

        if (!run.Options.Download || fetcher is null) { steps.Add("Getting the original installer was not allowed, so nothing was downloaded."); return null; }
        run.Doing("Asking WinGet for this program's original installer…");
        if (fetcher.Match(entry.Name, entry.Version) is not { } match) { steps.Add($"WinGet does not list {entry.Name} {entry.Version} as a package, so there is no original installer to get."); return null; }
        steps.Add($"WinGet knows it as {match.Id} {match.Version}.");
        var work = Path.Combine(Path.GetTempPath(), "TidyWindow-repair-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            if (finding.Kind == RepairKind.BundleRecord && ops is not null)
            {
                var target = CachedSetup(entry);
                if (target is null) steps.Add("The entry does not name a setup program in Windows' package cache, so there is nowhere to put one back.");
                else
                {
                    run.Doing($"Downloading {match.Id} {match.Version} from WinGet…");
                    var downloads = fetcher.Download(match, work, null, run.Token).Where(x => x.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)).ToList();
                    foreach (var file in downloads)
                    {
                        run.Doing("Checking the download's signature and putting it back in Windows' package cache…");
                        var result = ops.RestoreBundleCopy(file, entry.Name, target);
                        steps.Add(result.ExitCode == 0 ? $"Put the downloaded setup program back as {target}." : $"Could not put the downloaded setup program back as {target}: {result.Output}".TrimEnd());
                        if (result.ExitCode == 0) run.Files.Add(target);
                        if (result.ExitCode == 0 && Uninstallable(entry.Key))
                            return Done(finding, "The program's own setup program was downloaded and put back in Windows' package cache, so it can be uninstalled and repaired from the Windows list again.", steps);
                    }
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
            if (fetcher!.Match(query.Name, query.Version) is not { } package || !tried.Add(package.Id + " " + package.Version)) continue;
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
        else steps.Add($"Windows Installer could not repair it ({Describe(result.ExitCode)}).");
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
        var left = finding.Alive && finding.Entry is { InstallLocation.Length: > 0 } entry ? $" The program's own files were left in {entry.InstallLocation}; reinstall it to get them under Windows' control again, or delete that folder." : "";
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

    // ---- Undo ----

    /// <summary>
    /// Puts back everything one repair removed, with the original permissions. It is all or nothing: if any key cannot be put back,
    /// the keys that were put back in this attempt are removed again, the repair stays in the Undo tab, and the reasons are returned.
    /// </summary>
    public async Task<UndoOutcome> UndoAsync(RepairBatch batch, CancellationToken cancellationToken = default, IProgress<RepairProgress>? progress = null)
    {
        var run = new Run(cancellationToken, new(), progress) { Total = 1 };
        run.Begin(batch.Title);
        if (batch.Keys.Any(x => InstallRepair.IsMachine(x.Path)))
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
        if (problems.Count == 0)
        {
            // The files a repair put back go last, once everything else is back; one that cannot be removed leaves the repair in the Undo tab to try again.
            var removed = 0;
            foreach (var file in batch.PutBack)
            {
                run.Doing($"Removing the setup program the repair put back ({Path.GetFileName(file)})…");
                if (ops is null || ops.RemoveCopy(file)) removed++;
                else problems.Add($"{Path.GetFileName(file)}: Windows would not let TidyWindow remove the setup program it put back. The registry keys were put back.");
            }
            return new(applied.Count, problems, removed);
        }

        // Something could not be put back: take back what was, so the PC is exactly as it was before the undo.
        var stuck = new List<string>();
        foreach (var (copy, before, editor) in Enumerable.Reverse(applied))
        {
            try { editor.Delete(copy.Path); if (before is not null) editor.Restore(before); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException) { stuck.Add($"{Leaf(copy.Path)}: {ex.Message}"); }
        }
        var head = $"{problems.Count} of {batch.Keys.Count} registry keys could not be put back, so nothing was restored.";
        if (stuck.Count > 0) problems.Add($"{stuck.Count} keys restored before the failure could not be taken out again and may still be there ({stuck[0]}).");
        problems.Insert(0, head);
        return new(0, problems);
    }
}

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;

namespace TidyWindow.Next.Core;

/// <summary>Programs that were running from an app's folder: the ones that were closed, and the ones left open because a person may be working in them.</summary>
public sealed record ClosedPrograms(IReadOnlyList<string> Closed, IReadOnlyList<string> StillOpen)
{
    public static ClosedPrograms None { get; } = new([], []);
}

/// <summary>What the uninstall ladder needs from the running PC besides the installed-programs list (<see cref="IInstallSystem"/>).</summary>
public interface IUninstallHost
{
    bool IsAdministrator { get; }
    /// <summary>Closes the programs running from <paramref name="folder"/>: windowed ones are asked to close and left alone if they stay open, background ones are ended.</summary>
    Task<ClosedPrograms> CloseProgramsInAsync(string folder, CancellationToken token);
    /// <summary>The first bytes of a file as text, to tell which installer built an uninstaller. Empty when the file cannot be read.</summary>
    string ReadHead(string path, int bytes);
}

/// <summary>A program's own uninstaller, run directly with the switches that make it silent.</summary>
public sealed partial record NativeUninstall(string FileName, IReadOnlyList<string> Arguments, string Engine, bool Machine)
{
    private static readonly string[] InnoSwitches = ["/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART"];

    /// <summary>
    /// How to run the uninstaller a list entry names, silently. An engine that is not recognized gets null: its silent switches are not guessed.
    /// A Windows Installer product is removed by its code, whatever the entry says (many entries say <c>MsiExec /I</c>, which would open the installer, not remove it).
    /// </summary>
    public static NativeUninstall? Plan(UninstallEntry entry, IInstallSystem system, Func<string, int, string> readHead)
    {
        var machine = InstallRepair.IsMachine(entry.Key);
        if (InstallRepair.ProductCode(entry) is { } code)
            return system.ProductState(code) == InstallRepair.Unknown ? null
                : new(Path.Combine(Environment.SystemDirectory, "msiexec.exe"), ["/x", code, "/qn", "/norestart", "REBOOT=ReallySuppress"], "Windows Installer", machine);

        var quiet = entry.QuietUninstallString.Trim();
        var command = quiet.Length > 0 ? quiet : entry.UninstallString.Trim();
        if (command.Length == 0 || !TryParse(system.Expand(command), system, out var program, out var arguments)) return null;
        if (quiet.Length > 0) return new(program, arguments, "its quiet uninstall command", machine);

        var name = Path.GetFileName(program);
        bool Has(string value) => arguments.Any(x => x.Equals(value, StringComparison.OrdinalIgnoreCase));
        if (InnoUninstaller().IsMatch(name))
            return new(program, Has("/SILENT") || Has("/VERYSILENT") ? arguments : [.. arguments, .. InnoSwitches], "Inno Setup", machine);
        if (entry.BundleProvider.Length > 0 || program.Contains(@"\Package Cache\", StringComparison.OrdinalIgnoreCase))
            return new(program, [.. arguments, .. new[] { "/uninstall", "/quiet", "/norestart" }.Where(x => !Has(x))], "WiX Burn", machine);
        if (name.Equals("Update.exe", StringComparison.OrdinalIgnoreCase) && Has("--uninstall"))
            return new(program, Has("-s") ? arguments : [.. arguments, "-s"], "Squirrel", machine);
        if (NsisUninstaller().IsMatch(name) && readHead(program, 2_000_000).Contains("Nullsoft", StringComparison.Ordinal))
            return new(program, Has("/S") ? arguments : [.. arguments, "/S"], "NSIS", machine);
        return null;
    }

    /// <summary>Splits an uninstall command into the program it runs (which must exist) and its arguments.</summary>
    internal static bool TryParse(string command, IInstallSystem system, out string program, out List<string> arguments)
    {
        program = ""; arguments = [];
        var text = command.Trim();
        var quoted = text.StartsWith('"');
        foreach (var candidate in InstallRepair.Candidates(text))
        {
            var path = candidate.Trim().Trim('"');
            if (!Path.IsPathRooted(path) || !system.FileExists(path)) continue;
            program = path;
            var rest = quoted ? text[(text.IndexOf('"', 1) + 1)..] : text[Math.Min(candidate.Length, text.Length)..];
            arguments = Split(rest);
            return true;
        }
        return false;
    }

    private static List<string> Split(string text)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var inQuote = false; var started = false;
        foreach (var c in text)
        {
            if (c == '"') { inQuote = !inQuote; started = true; }
            else if (char.IsWhiteSpace(c) && !inQuote) { if (started) result.Add(current.ToString()); current.Clear(); started = false; }
            else { current.Append(c); started = true; }
        }
        if (started) result.Add(current.ToString());
        return result;
    }

    [GeneratedRegex(@"^unins\d{3}\.exe$", RegexOptions.IgnoreCase)]
    private static partial Regex InnoUninstaller();
    [GeneratedRegex(@"^(?:uninst|uninstall|unins)[\w\- ]*\.exe$", RegexOptions.IgnoreCase)]
    private static partial Regex NsisUninstaller();
}

/// <param name="Result">The last command that ran, which decides whether the uninstall worked.</param>
/// <param name="Trail">Each thing that was tried, in order, as a sentence a person can read.</param>
/// <param name="EntryRemoved">The app's leftover list entry was removed by the repair engine instead of an uninstaller running.</param>
public sealed record UninstallLadderResult(CommandResult Result, IReadOnlyList<string> Trail, bool EntryRemoved = false);

/// <summary>
/// What happens after WinGet fails to uninstall an app. One command is not enough: an uninstaller can fail because Windows Installer is busy, because the app is
/// still running, because it needs administrator rights, because WinGet matched the wrong source, or because the app's own record is broken. Each of those has
/// its own remedy, tried at most once and only when the failure points to it, then the app's own uninstaller is run directly, and last the repair engine
/// restores or (for a program that is gone) removes the broken record. Nothing here runs before the first failure, so a working uninstall behaves as it always did.
/// </summary>
public sealed class UninstallRecovery(IInstallSystem system, IUninstallHost host, ICommandRunner runner, RepairService? repair = null)
{
    /// <summary>How a retried WinGet command differs from the first one.</summary>
    public sealed record Variant(bool Elevated = false, bool WithoutSource = false);

    public TimeSpan BusyWait { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan BusyPoll { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan NativeTimeout { get; init; } = TimeSpan.FromMinutes(20);
    /// <summary>Whether restoring a broken record may fetch the program's original installer from WinGet (checked against the exact program before use), as the Repair page does by default.</summary>
    public bool AllowDownload { get; init; }
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = (time, token) => Task.Delay(time, token);
    /// <summary>Where a package manager keeps an app's own files, for managers that do not register the app with Windows (Scoop). Null when the app has no such folder.</summary>
    public Func<ManagedPackage, string?> ManagedFolder { get; init; } = ScoopFolder;
    /// <summary>Remove-AppxPackage only works in Windows PowerShell 5.1; PowerShell 7 loads the module through a layer that fails.</summary>
    public string WindowsPowerShell { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");

    private static string? ScoopFolder(ManagedPackage app)
    {
        if (app.Manager != ManagerKind.Scoop || !PackageOutputParser.IsSafeId(app.Id)) return null;
        var root = app.Global
            ? Environment.GetEnvironmentVariable("SCOOP_GLOBAL") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "scoop")
            : new ManagerLocations().Get(ManagerKind.Scoop).Root;
        return Path.Combine(root, "apps", app.Id.Split('/')[^1]);
    }

    /// <summary>The package full name a Store app's WinGet id carries (<c>MSIX\Name_1.0_x64__publisher</c>), only when it is made of the characters a package full name has.</summary>
    internal static string? StoreFullName(ManagedPackage app) =>
        app.Id.StartsWith(@"MSIX\", StringComparison.OrdinalIgnoreCase) && Regex.IsMatch(app.Id[5..], @"^[A-Za-z0-9._\-]+$") ? app.Id[5..] : null;

    private static bool Succeeded(CommandResult result) => result.ExitCode is 0 or 1641 or 3010;

    /// <summary>
    /// A command run through the Windows permission prompt cannot have its output captured. When it fails, keep what the run before it said, so the reason and
    /// the error code are not lost (the same thing <see cref="InstallFailure.RunAsync"/> does for its own retry).
    /// </summary>
    private static CommandResult Keep(CommandResult before, CommandResult after) =>
        Succeeded(after) || !string.IsNullOrWhiteSpace(after.Output) || !string.IsNullOrWhiteSpace(after.Error) ? after : after with { Output = before.Output, Error = before.Error };

    public async Task<UninstallLadderResult> RecoverAsync(ManagedPackage app, CommandResult first, Func<Variant, Task<CommandResult>> rerun, IProgress<CommandOutput>? progress, CancellationToken token)
    {
        var trail = new List<string>();
        var result = first;
        var variant = new Variant();
        // Only WinGet lists apps by their Windows registry entry; Chocolatey ids and Scoop names are not display names, so they are never matched to one.
        var winget = app.Manager == ManagerKind.Winget;
        var entry = winget ? Safe(() => FindEntry(app)) : null;
        var tried = new HashSet<string>();
        var removed = false;

        async Task<CommandResult> Again(string what)
        {
            Say(progress, what);
            var next = await rerun(variant).ConfigureAwait(false);
            trail.Add($"Ran the uninstall again{(variant.Elevated ? " as administrator" : "")}{(variant.WithoutSource ? " without naming a WinGet source" : "")}; it returned {next.ExitCode}.");
            return variant.Elevated ? Keep(result, next) : next;
        }

        while (!Succeeded(result))
        {
            token.ThrowIfCancellationRequested();
            var inner = InstallFailure.InnerExitCode(result) ?? InstallFailure.ManagerExitCode(result);
            var code = winget ? unchecked((uint)result.ExitCode) : 0u;
            if (inner is 1602 or 1223 || code == 0x8A15010C || result.Error.Contains("declined", StringComparison.OrdinalIgnoreCase)) break;
            var folder = entry is not null ? Safe(() => ProgramFolder(entry)) : Safe(() => ManagedFolder(app) is { } own && system.FolderExists(own) ? own : null);
            var machine = entry is not null ? InstallRepair.IsMachine(entry.Key) : app.Id.StartsWith(@"ARP\User\", StringComparison.OrdinalIgnoreCase) ? false : (bool?)null;

            // 1. Windows Installer is busy with something else: wait for it instead of failing the same way again.
            if ((inner == 1618 || code == 0x8A150102) && tried.Add("wait"))
            {
                Say(progress, "Waiting for another Windows Installer task to finish…");
                var idle = await WaitForInstallerAsync(token).ConfigureAwait(false);
                trail.Add(idle ? "Waited for the other Windows Installer task to finish." : $"Another Windows Installer task was still running after {BusyWait.TotalSeconds:0} seconds.");
                if (idle) result = await Again("Trying the uninstall again…").ConfigureAwait(false);
                continue;
            }

            // 2. The uninstaller needs administrator rights, which it cannot ask for when it runs without a window. This comes before closing programs: it ends nothing.
            if (winget && !host.IsAdministrator && !variant.Elevated && machine != false && NeedsAdministrator(result, inner, machine) && tried.Add("admin"))
            {
                variant = variant with { Elevated = true };
                result = await Again("The uninstaller needs administrator permission. Asking Windows for it…").ConfigureAwait(false);
                continue;
            }

            // 3. The app is still running (or a helper of it is), which locks its files. WinGet only says so for some installers: Inno Setup, NSIS and Squirrel
            // uninstallers report a plain 1 or 2, and Scoop deletes the folder itself, so any failure with a known folder is worth looking there. Nothing running means nothing happens.
            if (folder is not null && inner is not (1602 or 1223 or 1618 or 1605 or 1612 or 1614 or 1714) && code != 0x8A150102 && tried.Add("close"))
            {
                Say(progress, $"Closing {app.Name} if it is running…");
                var closed = await host.CloseProgramsInAsync(folder, token).ConfigureAwait(false);
                if (closed.Closed.Count > 0) trail.Add($"Closed {string.Join(", ", closed.Closed)}, which {(closed.Closed.Count == 1 ? "was" : "were")} running from {folder}.");
                // A window that stayed open belongs to a person; every other remedy would run the uninstaller into it again.
                if (closed.StillOpen.Count > 0) { trail.Add($"{string.Join(", ", closed.StillOpen)} is still open. Close it yourself, then uninstall again."); break; }
                if (closed.Closed.Count > 0) result = await Again("Trying the uninstall again…").ConfigureAwait(false);
                continue;
            }

            // 3b. A Store app WinGet cannot remove is removed the way Windows removes one for the current user.
            if (winget && app.Origin == AppOrigin.StoreApp && StoreFullName(app) is { } fullName && tried.Add("appx"))
            {
                Say(progress, $"Removing {app.Name} with Windows PowerShell…");
                var script = $"$ErrorActionPreference='Stop';try{{Remove-AppxPackage -Package '{fullName}'}}catch{{[Console]::Error.WriteLine($_.Exception.Message);exit 1}}";
                result = await runner.RunAsync(new CommandSpec(WindowsPowerShell, ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script))], NativeTimeout) { Progress = progress }, token).ConfigureAwait(false);
                trail.Add($"Removed it with Windows PowerShell (Remove-AppxPackage); it returned {result.ExitCode}.");
                continue;
            }

            // 4. Windows' record of the app is broken: an entry for a program Windows Installer no longer has, a missing saved installer, a missing uninstaller.
            if (entry is not null && repair is not null && IsBrokenRecord(entry, inner) && tried.Add("repair"))
            {
                var (gone, restored) = await RepairAsync(entry, app, inner is 1612 or 1714, trail, progress, token).ConfigureAwait(false);
                if (gone) { removed = true; result = new(0, "", ""); break; }
                if (restored) result = await Again("The record is restored. Trying the uninstall again…").ConfigureAwait(false);
                continue;
            }

            // 5. WinGet was asked for its own catalog's copy of an app it only matched by name.
            if (winget && app.Origin == AppOrigin.Package && !app.Source.Equals("msstore", StringComparison.OrdinalIgnoreCase) && !variant.WithoutSource && tried.Add("source"))
            {
                variant = variant with { WithoutSource = true };
                result = await Again("Trying again against what is installed on this PC, not WinGet’s catalog…").ConfigureAwait(false);
                continue;
            }

            // 6. Skip WinGet and run the app's own uninstaller.
            if (entry is not null && tried.Add("native"))
            {
                var plan = Safe(() => NativeUninstall.Plan(entry, system, host.ReadHead));
                if (plan is null) { trail.Add("TidyWindow does not know a silent way to run this app’s own uninstaller, so it did not guess one."); continue; }
                Say(progress, $"Running {app.Name}’s own uninstaller ({plan.Engine}) directly…");
                var direct = await runner.RunAsync(new CommandSpec(plan.FileName, plan.Arguments, NativeTimeout, plan.Machine && !host.IsAdministrator) { Progress = progress, MayShowInstaller = true }, token).ConfigureAwait(false);
                trail.Add($"Ran its own uninstaller directly ({plan.Engine}); it returned {direct.ExitCode}.");
                // The uninstaller's own exit code is the installer code WinGet would have printed, so say it in the same words and the rest of the ladder and the explanation read it.
                if (!Succeeded(direct) && !direct.Error.Contains("declined", StringComparison.OrdinalIgnoreCase))
                    direct = direct with { Output = (direct.Output + $"\nUninstaller failed with exit code: {direct.ExitCode}").Trim() };
                result = direct;
                continue;
            }
            break;
        }
        return new(result, trail, removed);
    }

    private static bool NeedsAdministrator(CommandResult result, int? inner, bool? machine) =>
        InstallFailure.NeedsAdministrator(result) || inner is 1925 or 5 or unchecked((int)0x80070005)
        // A generic installer failure of a program for every user, run without a window, is almost always a missing permission.
        || (inner is 1603 && machine != false);

    /// <summary>Whether the failure is Windows' own record of the app being wrong, which no amount of retrying fixes.</summary>
    private bool IsBrokenRecord(UninstallEntry entry, int? inner) =>
        inner is 1605 or 1612 or 1614 or 1714 || InstallRepair.ProductCode(entry) is null && Safe(() => !InstallRepair.HasUninstaller(system, entry));

    private async Task<(bool Removed, bool Restored)> RepairAsync(UninstallEntry entry, ManagedPackage app, bool patches, List<string> trail, IProgress<CommandOutput>? progress, CancellationToken token)
    {
        try
        {
            Say(progress, "Checking Windows’ install records for this app…");
            var scan = await Task.Run(() => InstallRepair.Scan(system, shortcuts: false), token).ConfigureAwait(false);
            var code = InstallRepair.ProductCode(entry);
            var related = scan.Findings.Where(x => Related(x, entry, code, patches)).ToList();
            if (related.Count == 0) { trail.Add("Looked at Windows’ install records for it; none of them needs repairing."); return (false, false); }
            var outcome = await repair!.FixAsync(related, $"Uninstall {app.Name}", token, new RepairOptions(Download: AllowDownload, RestoreOnly: true),
                new Inline<RepairProgress>(p => { if (p.Doing.Length > 0) Say(progress, p.Doing); })).ConfigureAwait(false);
            foreach (var result in outcome.Results ?? [])
                trail.Add(result.Status == RepairStatus.NotFixed ? $"Could not repair “{result.Title}”: {result.Summary}" : $"Repaired “{result.Title}”: {result.Summary}");
            var gone = !system.Entries().Any(x => string.Equals(x.Key, entry.Key, StringComparison.OrdinalIgnoreCase));
            if (gone) trail.Add("Its list entry is gone. Repair › Undo can put it back.");
            return (gone, !gone && (outcome.Results?.Any(x => x.Status != RepairStatus.NotFixed) ?? false));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
        {
            trail.Add("Repairing the record did not run: " + (ex is OperationCanceledException ? "administrator permission was declined." : ex.Message));
            return (false, false);
        }
    }

    private static bool Related(RepairFinding finding, UninstallEntry entry, string? code, bool patches)
    {
        bool Same(string? a, string? b) => a is not null && b is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        return finding.Kind switch
        {
            RepairKind.MissingPatch => patches && entry.Name.Length > 0 && finding.Detail.Contains(entry.Name, StringComparison.OrdinalIgnoreCase),
            RepairKind.BrokenEntry or RepairKind.BundleRecord or RepairKind.InstallerRecord =>
                Same(finding.Id, "entry:" + entry.Key) || Same(finding.Entry?.Key, entry.Key) || code is not null && (Same(finding.Product?.Code, code) || Same(finding.Evidence, code)),
            _ => false
        };
    }

    private async Task<bool> WaitForInstallerAsync(CancellationToken token)
    {
        var waited = TimeSpan.Zero;
        while (Safe(() => system.InstallerBusy() || system.Installers().Count > 0))
        {
            if (waited >= BusyWait) return false;
            await Delay(BusyPoll, token).ConfigureAwait(false);
            waited += BusyPoll;
        }
        return true;
    }

    /// <summary>The list entry an installed app is, found by the registry key WinGet named it by, or for an app WinGet matched to its catalog, by its display name.</summary>
    internal UninstallEntry? FindEntry(ManagedPackage app)
    {
        var entries = system.Entries();
        if (app.Id.StartsWith(@"MSIX\", StringComparison.OrdinalIgnoreCase)) return null;
        if (app.Id.StartsWith(@"ARP\", StringComparison.OrdinalIgnoreCase))
        {
            var parts = app.Id.Split('\\', 4);
            if (parts.Length < 4) return null;
            var hive = parts[1].Equals("Machine", StringComparison.OrdinalIgnoreCase) ? @"HKLM\" : @"HKCU\";
            var matches = entries.Where(x => x.KeyName.Equals(parts[3], StringComparison.OrdinalIgnoreCase) && x.Key.StartsWith(hive, StringComparison.OrdinalIgnoreCase)).ToList();
            var wow = parts[2].Equals("X86", StringComparison.OrdinalIgnoreCase);
            return matches.FirstOrDefault(x => x.Key.Contains(@"\WOW6432Node\", StringComparison.OrdinalIgnoreCase) == wow) ?? matches.FirstOrDefault();
        }
        // Running the wrong program's uninstaller is worse than running none, so each step needs exactly one match, with the version breaking a tie.
        var candidates = entries.Where(x => !x.IsUpdate && x.Name.Trim().Length > 0).ToList();
        UninstallEntry? Single(IReadOnlyList<UninstallEntry> found) =>
            found.Count == 1 ? found[0] : found.Where(x => PackageOutputParser.SameVersion(x.Version, app.Version)).ToList() is [var only] ? only : null;

        if (Single(candidates.Where(x => x.Name.Trim().Equals(app.Name.Trim(), StringComparison.OrdinalIgnoreCase)).ToList()) is { } exact) return exact;
        // The same name apart from punctuation and a trailing "Installer" or "Setup" ("MSYS2 Installer" is listed as "MSYS2").
        var key = InstalledInventory.NameKey(app.Name);
        if (key.Length == 0) return null;
        if (Single(candidates.Where(x => InstalledInventory.NameKey(x.Name) == key).ToList()) is { } alike) return alike;
        // A catalog name that is the start of the entry's ("Python 3.13" and "Python 3.13.9 (64-bit)") is accepted only when the version agrees too.
        return key.Length >= 6 ? Single(candidates.Where(x => PackageOutputParser.SameVersion(x.Version, app.Version) && InstalledInventory.NameKey(x.Name).StartsWith(key, StringComparison.Ordinal)).ToList()) : null;
    }

    /// <summary>The folder an app runs from, when its entry says where that is and it exists.</summary>
    private string? ProgramFolder(UninstallEntry entry)
    {
        var folders = new List<string> { system.Expand(entry.InstallLocation.Trim().Trim('"')) };
        var icon = entry.DisplayIcon.Trim();
        icon = icon.StartsWith('"') && icon.IndexOf('"', 1) is > 0 and var end ? icon[1..end] : icon;
        if (icon.LastIndexOf(',') is > 0 and var comma && int.TryParse(icon[(comma + 1)..], out _)) icon = icon[..comma];
        if (Path.GetDirectoryName(system.Expand(icon.Trim().Trim('"'))) is { Length: > 0 } iconFolder) folders.Add(iconFolder);
        return folders.FirstOrDefault(x => x.Length > 0 && Path.IsPathRooted(x) && system.FolderExists(x)
            && !x.Contains(@"\Installer", StringComparison.OrdinalIgnoreCase) && !x.Contains(@"\Package Cache", StringComparison.OrdinalIgnoreCase));
    }

    private static T? Safe<T>(Func<T> read)
    {
        try { return read(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException or Win32Exception) { return default; }
    }

    private static void Say(IProgress<CommandOutput>? progress, string text)
    {
        try { progress?.Report(new(text, CommandOutputKind.Notice)); }
        catch { /* A display failure must not stop the uninstall. */ }
    }

    private sealed class Inline<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) { try { report(value); } catch { /* A display failure must not stop the repair. */ } }
    }
}

[SupportedOSPlatform("windows")]
public sealed class WindowsUninstallHost : IUninstallHost
{
    public TimeSpan Grace { get; init; } = TimeSpan.FromSeconds(8);
    public bool IsAdministrator => FixService.IsAdministrator();

    public string ReadHead(string path, int bytes)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[(int)Math.Min(bytes, stream.Length)];
            var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            return Encoding.Latin1.GetString(buffer, 0, read);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ""; }
    }

    public async Task<ClosedPrograms> CloseProgramsInAsync(string folder, CancellationToken token)
    {
        if (Normalize(folder) is not { } root || IsProtected(root)) return ClosedPrograms.None;
        var prefix = root + "\\";
        var found = new List<Process>(); var names = new Dictionary<Process, string>();
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (process.Id != Environment.ProcessId && process.MainModule?.FileName is { } path && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { found.Add(process); names[process] = Path.GetFileName(path); continue; }
            }
            // Processes of other users and protected processes cannot be inspected, and are not an app's to close.
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException) { }
            process.Dispose();
        }
        try
        {
            foreach (var process in found.Where(HasWindow)) { try { process.CloseMainWindow(); } catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { } }
            var deadline = DateTime.UtcNow + Grace;
            while (DateTime.UtcNow < deadline && found.Any(x => HasWindow(x) && !Exited(x))) await Task.Delay(250, token).ConfigureAwait(false);

            var closed = new List<string>(); var open = new List<string>();
            foreach (var process in found)
            {
                var name = names[process];
                if (Exited(process)) closed.Add(name);
                // A window that stayed open may be asking whether to save; a person decides that, not TidyWindow.
                else if (HasWindow(process)) open.Add(name);
                else if (End(process)) closed.Add(name);
                else open.Add(name);
            }
            return new(closed.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), open.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
        }
        finally { foreach (var process in found) process.Dispose(); }
    }

    private static bool HasWindow(Process process) { try { return process.MainWindowHandle != IntPtr.Zero; } catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { return false; } }
    private static bool Exited(Process process) { try { return process.HasExited; } catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { return true; } }
    private static bool End(Process process)
    {
        try { process.Kill(); return process.WaitForExit(3000); }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { return Exited(process); }
    }

    private static string? Normalize(string folder)
    {
        try { return Path.IsPathRooted(folder) ? Path.GetFullPath(folder).TrimEnd('\\') : null; }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    /// <summary>A folder that holds Windows, other programs or the person's files is never an app's own folder, so nothing running from inside it is ended.</summary>
    internal static bool IsProtected(string root)
    {
        if (Path.GetPathRoot(root + "\\")?.TrimEnd('\\').Equals(root, StringComparison.OrdinalIgnoreCase) ?? true) return true;
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var places = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), Environment.SystemDirectory,
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            local, Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), Path.Combine(local, "Programs"), Path.Combine(local, "Microsoft", "WindowsApps"),
            AppContext.BaseDirectory.TrimEnd('\\')
        };
        // Protected when the folder is one of these or contains one (so a folder above Program Files, or above TidyWindow itself, is refused too).
        return places.Where(x => x.Length > 0).Any(x => x.Equals(root, StringComparison.OrdinalIgnoreCase) || x.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase));
    }
}

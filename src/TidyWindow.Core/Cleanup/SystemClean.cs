using System.Diagnostics;

namespace TidyWindow.Next.Core;

/// <summary>What an elevated clean did: the totals, plus plain-language notes about anything unusual. <paramref name="Declined"/> is set when the person said no to the permission prompt, so nothing ran.</summary>
public sealed record SystemCleanReport(CleanupResult Result, string[] Notes, bool Declined = false);

/// <summary>The size the elevated helper found for one rule. <see cref="Unmeasured"/> means Windows reports the size only after it has cleaned; <see cref="Estimated"/> means Windows' own guess, not a count.</summary>
public sealed record MeasuredItem(string Id, long Bytes, int Files, int Unreadable, bool Unmeasured, string? Note = null, bool Estimated = false);

/// <summary>The few machine operations system cleanup needs, replaceable in tests.</summary>
public interface ISystemTools
{
    /// <summary>Runs a program to completion and returns its exit code.</summary>
    Task<int> RunAsync(string fileName, string[] arguments, TimeSpan timeout, CancellationToken cancellationToken);
    /// <summary>Like <see cref="RunAsync"/>, but also returns what the program printed.</summary>
    Task<(int ExitCode, string Output)> RunForOutputAsync(string fileName, string[] arguments, TimeSpan timeout, CancellationToken cancellationToken);
    /// <summary>Turns one Disk Cleanup handler on or off for the unattended run that follows.</summary>
    void SetCleanupHandler(string handler, int slot, bool enabled);
    /// <summary>Turns the slot off for every handler, so an interrupted earlier run or another script cannot make the unattended run clean more than was asked for.</summary>
    void ClearCleanupHandlers(int slot);
    long FreeBytes(string drive);
}

public sealed class WindowsSystemTools : ISystemTools
{
    private const string Handlers = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VolumeCaches";

    public async Task<int> RunAsync(string fileName, string[] arguments, TimeSpan timeout, CancellationToken cancellationToken) =>
        (await RunForOutputAsync(fileName, arguments, timeout, cancellationToken).ConfigureAwait(false)).ExitCode;

    public async Task<(int ExitCode, string Output)> RunForOutputAsync(string fileName, string[] arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(fileName) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"{fileName} could not be started.");
        // Output is always drained so a chatty tool never blocks on a full pipe; the exit code is what decides success.
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errors = process.StandardError.ReadToEndAsync(cancellationToken);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        try { await process.WaitForExitAsync(limit.Token).ConfigureAwait(false); await Task.WhenAll(output, errors).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            if (cancellationToken.IsCancellationRequested) throw;
            throw new TimeoutException($"{Path.GetFileName(fileName)} did not finish in time.");
        }
        return (process.ExitCode, output.Result);
    }

    public void SetCleanupHandler(string handler, int slot, bool enabled)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey($@"{Handlers}\{handler}", writable: true) ?? throw new InvalidOperationException($"Windows has no \"{handler}\" cleanup handler.");
        var name = $"StateFlags{slot:D4}";
        if (enabled) key.SetValue(name, 2, Microsoft.Win32.RegistryValueKind.DWord);
        else key.DeleteValue(name, throwOnMissingValue: false);
    }

    public void ClearCleanupHandlers(int slot)
    {
        if (!OperatingSystem.IsWindows()) return;
        var name = $"StateFlags{slot:D4}";
        using var handlers = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(Handlers);
        foreach (var handler in handlers?.GetSubKeyNames() ?? [])
        {
            try
            {
                using var key = handlers!.OpenSubKey(handler, writable: true);
                if (key?.GetValue(name) is not null) key.DeleteValue(name, throwOnMissingValue: false);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
        }
    }

    public long FreeBytes(string drive) => new DriveInfo(drive).AvailableFreeSpace;
}

/// <summary>
/// Runs the administrator-only part of a clean: files in protected Windows folders go through the normal cleaner, and the
/// areas Windows must manage itself go through Windows' own tools. Nothing here takes ownership of files or edits permissions.
/// A stop request (the person, or the app going away) ends the run between steps and returns what was done by then.
/// </summary>
public sealed class SystemCleanJob(CleanupContext context, ISystemTools tools)
{
    private const int HandlerSlot = 5099;
    private static readonly TimeSpan ComponentStoreLimit = TimeSpan.FromMinutes(45);
    private static readonly TimeSpan CleanmgrLimit = TimeSpan.FromMinutes(60);
    private static readonly TimeSpan ComponentStoreAnalysisLimit = TimeSpan.FromMinutes(10);
    internal const string StoppedNote = "Cleaning was stopped before it finished. Anything already removed stays removed.";
    private const string FreeSpaceNote = "The space freed here is approximate: it is measured from the change in free space on the system drive while Windows worked.";

    public async Task<SystemCleanReport> RunAsync(IReadOnlyList<CleanupRule> catalog, IReadOnlyCollection<string> ruleIds, IProgress<CleanupProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var timer = Stopwatch.StartNew();
        var notes = new List<string>();
        // Only rules marked for administrators run here: this is the elevated surface, so it never accepts anything else by name.
        var rules = catalog.Where(rule => rule.Admin && ruleIds.Contains(rule.Id)).ToList();
        if (!context.IsAdministrator)
            return new(new CleanupResult(0, 0, 0, 0, rules.Count, null, 0, timer.Elapsed), ["Administrator rights were not available, so nothing was cleaned."]);

        IReadOnlyList<CleanupFinding> findings;
        try { findings = await new CleanupScanner(context).ScanAsync(rules, null, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return new(new CleanupResult(0, 0, 0, 0, 0, null, 0, timer.Elapsed, Stopped: true), [StoppedNote]); }
        var files = await new CleanupCleaner(context).CleanAsync(findings.Where(x => !x.Rule.IsSystemAction && x.HasContent).ToList(), progress, cancellationToken).ConfigureAwait(false);
        long freed = files.FreedBytes; int removed = files.Removed, failed = files.Failed;
        var stopped = files.Stopped;

        foreach (var finding in findings.Where(x => x.Rule.IsSystemAction && x.HasContent))
        {
            if (stopped || cancellationToken.IsCancellationRequested) { stopped = true; break; }
            // Windows' own tools can run for minutes without output, so say which one has started.
            progress?.Report(new CleanupProgress(finding.Rule.Name, freed, removed, Starting: true));
            try
            {
                var (bytes, count, note) = await RunActionAsync(finding, cancellationToken).ConfigureAwait(false);
                freed += bytes; removed += count;
                if (note is not null) notes.Add(note);
                progress?.Report(new CleanupProgress(finding.Rule.Name, freed, removed));
            }
            catch (OperationCanceledException) { stopped = true; break; }
            catch (Exception ex)
            {
                failed++;
                notes.Add($"{finding.Rule.Name} could not be cleaned: {ex.Message}");
            }
        }
        if (stopped) notes.Add(StoppedNote);
        return new(new CleanupResult(freed, removed, files.InUse, files.Denied, failed, null, 0, timer.Elapsed, stopped), notes.ToArray());
    }

    /// <summary>
    /// Measures what the given administrator rules could free, without cleaning anything. Files areas are scanned with full access, so
    /// nothing is left unreadable. Areas only Windows can size (the component store, driver packages) are reported as sized-after-cleaning,
    /// which keeps the measure a plain folder walk. With <paramref name="estimate"/> the component store is also sized by Windows' own
    /// analysis, which can take minutes, so it goes last and every other size is reported through <paramref name="progress"/> as soon as it is known.
    /// </summary>
    public async Task<IReadOnlyList<MeasuredItem>> MeasureAsync(IReadOnlyList<CleanupRule> catalog, IReadOnlyCollection<string> ruleIds, IProgress<MeasuredItem>? progress = null, bool estimate = false, CancellationToken cancellationToken = default)
    {
        // Same guard as cleaning: only rules marked for administrators are ever looked at here.
        var rules = catalog.Where(rule => rule.Admin && ruleIds.Contains(rule.Id)).ToList();
        if (!context.IsAdministrator) throw new InvalidOperationException("Administrator rights were not available, so nothing was measured.");
        var findings = await new CleanupScanner(context).ScanAsync(rules, null, cancellationToken).ConfigureAwait(false);
        var measured = new Dictionary<string, MeasuredItem>();
        foreach (var finding in findings.OrderBy(x => estimate && x.Rule.Action == CleanupAction.ComponentStore))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rule = finding.Rule;
            MeasuredItem item;
            if (estimate && rule.Action == CleanupAction.ComponentStore) item = await AnalyzeComponentStoreAsync(rule, cancellationToken).ConfigureAwait(false);
            else
            {
                var note = finding.Unmeasured ? "Windows reports the size only after it has cleaned." : null;
                item = new MeasuredItem(rule.Id, finding.Bytes, finding.Files, finding.Unreadable, finding.Unmeasured, note);
            }
            measured[rule.Id] = item;
            progress?.Report(item);
        }
        return findings.Select(x => measured[x.Rule.Id]).ToList();
    }

    private async Task<MeasuredItem> AnalyzeComponentStoreAsync(CleanupRule rule, CancellationToken token)
    {
        var windows = context.Variable("WINDIR");
        MeasuredItem Unknown(string note) => new(rule.Id, 0, 0, 0, true, note);
        if (string.IsNullOrWhiteSpace(windows)) return Unknown("The Windows folder is unknown.");
        try
        {
            var (exit, output) = await tools.RunForOutputAsync(Path.Combine(windows, "System32", "Dism.exe"), ["/Online", "/Cleanup-Image", "/AnalyzeComponentStore"], ComponentStoreAnalysisLimit, token).ConfigureAwait(false);
            if (exit != 0) return Unknown($"Windows could not estimate this (error code {exit}).");
            return ComponentStoreAnalysis.Reclaimable(output) is { } bytes
                ? new MeasuredItem(rule.Id, bytes, bytes > 0 ? 1 : 0, 0, false, "Estimated by Windows; the space actually freed is shown afterwards.", Estimated: true)
                : Unknown("Windows did not give a size for this.");
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or System.ComponentModel.Win32Exception) { return Unknown(ex.Message); }
    }

    private async Task<(long Freed, int Removed, string? Note)> RunActionAsync(CleanupFinding finding, CancellationToken token)
    {
        var windows = context.Variable("WINDIR") ?? throw new InvalidOperationException("The Windows folder is unknown.");
        var drive = context.Variable("SYSTEMDRIVE") ?? throw new InvalidOperationException("The system drive is unknown.");
        var system32 = Path.Combine(windows, "System32");
        switch (finding.Rule.Action)
        {
            case CleanupAction.ComponentStore:
            {
                // The component store is mostly hard links, so its size cannot be added up; free space before and after is the honest measure.
                var before = tools.FreeBytes(drive);
                // DISM stopped part-way through its cleanup can leave the component store with a pending operation that only a restart resolves, so a stop
                // request lets it finish and takes effect before the next action. Only the time limit ends it early, and then the note says what to do.
                int exit;
                try { exit = await tools.RunAsync(Path.Combine(system32, "Dism.exe"), ["/Online", "/Cleanup-Image", "/StartComponentCleanup"], ComponentStoreLimit, CancellationToken.None).ConfigureAwait(false); }
                catch (TimeoutException) { throw new InvalidOperationException($"Windows was still cleaning its update files after {ComponentStoreLimit.TotalMinutes:0} minutes and was stopped. If Windows Update reports a pending operation, restart the PC."); }
                if (exit is not (0 or 3010)) throw new InvalidOperationException($"Windows reported error code {exit}.");
                var gained = Math.Max(0, tools.FreeBytes(drive) - before);
                return (gained, gained > 0 ? 1 : 0, (exit == 3010 ? "Windows needs a restart to finish cleaning its update files. " : "") + FreeSpaceNote);
            }
            case CleanupAction.PreviousWindows:
            {
                var folder = finding.Roots.FirstOrDefault() ?? Path.Combine(drive + Path.DirectorySeparatorChar, "Windows.old");
                tools.ClearCleanupHandlers(HandlerSlot);
                tools.SetCleanupHandler("Previous Installations", HandlerSlot, true);
                int exit;
                try { exit = await tools.RunAsync(Path.Combine(system32, "cleanmgr.exe"), [$"/sagerun:{HandlerSlot}"], CleanmgrLimit, token).ConfigureAwait(false); }
                finally { tools.SetCleanupHandler("Previous Installations", HandlerSlot, false); }
                if (exit != 0) throw new InvalidOperationException($"Windows Disk Cleanup reported error code {exit}.");
                return Directory.Exists(folder)
                    ? (0, 0, "Windows did not remove the previous Windows installation. It may need a restart first.")
                    : (finding.Bytes, finding.Files, null);
            }
            case CleanupAction.DiskCleanup:
            {
                // Same honest measure as the component store: these handlers report no sizes, so free space before and after is the answer.
                var handlers = finding.Rule.Handlers.Where(handler => context.HandlerFolders(handler) is not null).ToList();
                var before = tools.FreeBytes(drive);
                var armed = new List<string>();
                int exit;
                try
                {
                    tools.ClearCleanupHandlers(HandlerSlot);
                    foreach (var handler in handlers) { tools.SetCleanupHandler(handler, HandlerSlot, true); armed.Add(handler); }
                    exit = await tools.RunAsync(Path.Combine(system32, "cleanmgr.exe"), [$"/sagerun:{HandlerSlot}"], CleanmgrLimit, token).ConfigureAwait(false);
                }
                finally
                {
                    foreach (var handler in armed)
                        try { tools.SetCleanupHandler(handler, HandlerSlot, false); } catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.Security.SecurityException) { }
                }
                if (exit != 0) throw new InvalidOperationException($"Windows Disk Cleanup reported error code {exit}.");
                var gained = Math.Max(0, tools.FreeBytes(drive) - before);
                return (gained, gained > 0 ? 1 : 0, FreeSpaceNote);
            }
            case CleanupAction.Hibernation:
            {
                var exit = await tools.RunAsync(Path.Combine(system32, "powercfg.exe"), ["/hibernate", "off"], TimeSpan.FromMinutes(2), token).ConfigureAwait(false);
                if (exit != 0) throw new InvalidOperationException($"powercfg reported error code {exit}.");
                // Windows removes hiberfil.sys when hibernate goes off. The file is looked at again rather than the size from the scan being taken on trust.
                var file = Path.Combine(drive + Path.DirectorySeparatorChar, "hiberfil.sys");
                return File.Exists(file)
                    ? (0, 0, "Hibernate is off, but Windows has not removed the hibernation file yet; it may go after a restart. Turn hibernate back on any time with: powercfg /hibernate on")
                    : (finding.Bytes, 1, "Hibernate and Fast Startup are now off. Turn them back on with: powercfg /hibernate on");
            }
            default: throw new InvalidOperationException("This is not a system action.");
        }
    }
}

/// <summary>Reads the report of <c>Dism /AnalyzeComponentStore</c>.</summary>
public static partial class ComponentStoreAnalysis
{
    [System.Text.RegularExpressions.GeneratedRegex(@":\s*(?<n>\d+(?:[.,]\d+)?)\s*(?<u>[KMGT]B)\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex Size();

    /// <summary>
    /// Space Windows can give back: "Backups and Disabled Features" plus "Cache and Temporary Data", the 4th and 5th sizes in the report.
    /// The labels are translated, so the sizes are taken by position. Null when the report does not look as expected: too few sizes, or parts that add up
    /// to more than the store itself (the 2nd size), which means the lines are not laid out the way this expects.
    /// </summary>
    public static long? Reclaimable(string output)
    {
        var sizes = new List<long>();
        foreach (var line in output.Split('\n'))
        {
            var match = Size().Match(line.TrimEnd());
            if (!match.Success) continue;
            var number = double.Parse(match.Groups["n"].Value.Replace(',', '.'), System.Globalization.CultureInfo.InvariantCulture);
            var scale = char.ToUpperInvariant(match.Groups["u"].Value[0]) switch { 'K' => 1024d, 'M' => 1048576d, 'G' => 1073741824d, _ => 1099511627776d };
            sizes.Add((long)(number * scale));
        }
        if (sizes.Count < 5) return null;
        var reclaimable = sizes[3] + sizes[4];
        return reclaimable <= sizes[1] ? reclaimable : null;
    }
}

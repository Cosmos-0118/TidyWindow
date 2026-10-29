using System.Diagnostics;

namespace TidyWindow.Next.Core;

/// <summary>What an elevated clean did: the totals, plus plain-language notes about anything unusual.</summary>
public sealed record SystemCleanReport(CleanupResult Result, string[] Notes);

/// <summary>The few machine operations system cleanup needs, replaceable in tests.</summary>
public interface ISystemTools
{
    /// <summary>Runs a program to completion and returns its exit code.</summary>
    Task<int> RunAsync(string fileName, string[] arguments, TimeSpan timeout, CancellationToken cancellationToken);
    /// <summary>Turns one Disk Cleanup handler on or off for the unattended run that follows.</summary>
    void SetCleanupHandler(string handler, int slot, bool enabled);
    long FreeBytes(string drive);
}

public sealed class WindowsSystemTools : ISystemTools
{
    private const string Handlers = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VolumeCaches";

    public async Task<int> RunAsync(string fileName, string[] arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(fileName) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"{fileName} could not be started.");
        // Output is drained so a chatty tool never blocks on a full pipe; the exit code is what decides success.
        var drain = Task.WhenAll(process.StandardOutput.ReadToEndAsync(cancellationToken), process.StandardError.ReadToEndAsync(cancellationToken));
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        try { await process.WaitForExitAsync(limit.Token).ConfigureAwait(false); await drain.ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            if (cancellationToken.IsCancellationRequested) throw;
            throw new TimeoutException($"{Path.GetFileName(fileName)} did not finish in time.");
        }
        return process.ExitCode;
    }

    public void SetCleanupHandler(string handler, int slot, bool enabled)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey($@"{Handlers}\{handler}", writable: true) ?? throw new InvalidOperationException($"Windows has no \"{handler}\" cleanup handler.");
        var name = $"StateFlags{slot:D4}";
        if (enabled) key.SetValue(name, 2, Microsoft.Win32.RegistryValueKind.DWord);
        else key.DeleteValue(name, throwOnMissingValue: false);
    }

    public long FreeBytes(string drive) => new DriveInfo(drive).AvailableFreeSpace;
}

/// <summary>
/// Runs the administrator-only part of a clean: files in protected Windows folders go through the normal cleaner, and the
/// areas Windows must manage itself go through Windows' own tools. Nothing here takes ownership of files or edits permissions.
/// </summary>
public sealed class SystemCleanJob(CleanupContext context, ISystemTools tools)
{
    private const int HandlerSlot = 5099;
    private static readonly TimeSpan ComponentStoreLimit = TimeSpan.FromMinutes(45);
    private static readonly TimeSpan CleanmgrLimit = TimeSpan.FromMinutes(60);

    public async Task<SystemCleanReport> RunAsync(IReadOnlyList<CleanupRule> catalog, IReadOnlyCollection<string> ruleIds, IProgress<CleanupProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var timer = Stopwatch.StartNew();
        var notes = new List<string>();
        // Only rules marked for administrators run here: this is the elevated surface, so it never accepts anything else by name.
        var rules = catalog.Where(rule => rule.Admin && ruleIds.Contains(rule.Id)).ToList();
        if (!context.IsAdministrator)
            return new(new CleanupResult(0, 0, 0, 0, rules.Count, null, 0, timer.Elapsed), ["Administrator rights were not available, so nothing was cleaned."]);

        var findings = await new CleanupScanner(context).ScanAsync(rules, null, cancellationToken).ConfigureAwait(false);
        var files = await new CleanupCleaner(context).CleanAsync(findings.Where(x => !x.Rule.IsSystemAction && x.HasContent).ToList(), progress, cancellationToken).ConfigureAwait(false);
        long freed = files.FreedBytes; int removed = files.Removed, failed = files.Failed;

        foreach (var finding in findings.Where(x => x.Rule.IsSystemAction && x.HasContent))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var (bytes, count, note) = await RunActionAsync(finding, cancellationToken).ConfigureAwait(false);
                freed += bytes; removed += count;
                if (note is not null) notes.Add(note);
                progress?.Report(new CleanupProgress(finding.Rule.Name, freed, removed));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                failed++;
                notes.Add($"{finding.Rule.Name} could not be cleaned: {ex.Message}");
            }
        }
        return new(new CleanupResult(freed, removed, files.InUse, files.Denied, failed, null, 0, timer.Elapsed), notes.ToArray());
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
                var exit = await tools.RunAsync(Path.Combine(system32, "Dism.exe"), ["/Online", "/Cleanup-Image", "/StartComponentCleanup"], ComponentStoreLimit, token).ConfigureAwait(false);
                if (exit is not (0 or 3010)) throw new InvalidOperationException($"Windows reported error code {exit}.");
                var gained = Math.Max(0, tools.FreeBytes(drive) - before);
                return (gained, gained > 0 ? 1 : 0, (exit == 3010 ? "Windows needs a restart to finish cleaning its update files. " : "") + "Space freed is measured from the change in free space on the system drive.");
            }
            case CleanupAction.PreviousWindows:
            {
                var folder = finding.Roots.FirstOrDefault() ?? Path.Combine(drive + Path.DirectorySeparatorChar, "Windows.old");
                tools.SetCleanupHandler("Previous Installations", HandlerSlot, true);
                int exit;
                try { exit = await tools.RunAsync(Path.Combine(system32, "cleanmgr.exe"), [$"/sagerun:{HandlerSlot}"], CleanmgrLimit, token).ConfigureAwait(false); }
                finally { tools.SetCleanupHandler("Previous Installations", HandlerSlot, false); }
                if (exit != 0) throw new InvalidOperationException($"Windows Disk Cleanup reported error code {exit}.");
                return Directory.Exists(folder)
                    ? (0, 0, "Windows did not remove the previous Windows installation. It may need a restart first.")
                    : (finding.Bytes, finding.Files, null);
            }
            case CleanupAction.Hibernation:
            {
                var exit = await tools.RunAsync(Path.Combine(system32, "powercfg.exe"), ["/hibernate", "off"], TimeSpan.FromMinutes(2), token).ConfigureAwait(false);
                if (exit != 0) throw new InvalidOperationException($"powercfg reported error code {exit}.");
                return (finding.Bytes, 1, "Hibernate and Fast Startup are now off. Turn them back on with: powercfg /hibernate on");
            }
            default: throw new InvalidOperationException("This is not a system action.");
        }
    }
}

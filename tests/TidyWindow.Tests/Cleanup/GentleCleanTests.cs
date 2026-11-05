using System.Diagnostics;
using TidyWindow.Next.Core;
using Xunit;

namespace TidyWindow.Next.Tests;

/// <summary>The clean must stay usable on slow disks and small machines, and Windows' own tools must be tracked to their real end.</summary>
public sealed class GentleCleanTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);
    private readonly string root = Path.Combine(Path.GetTempPath(), "tw-gentle-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string temp;

    public GentleCleanTests()
    {
        temp = Path.Combine(root, "Users", "me", "AppData", "Local", "Temp");
        Directory.CreateDirectory(temp);
    }

    public void Dispose() { try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    private CleanupContext Context(Func<DateTime>? now = null, int parallelism = 4, bool lowImpact = true) => new()
    {
        Variable = name => name switch
        {
            "USERPROFILE" => Path.Combine(root, "Users", "me"), "LOCALAPPDATA" => Path.Combine(root, "Users", "me", "AppData", "Local"),
            "APPDATA" => Path.Combine(root, "Users", "me", "AppData", "Roaming"), "TEMP" => temp, "WINDIR" => Path.Combine(root, "Windows"),
            "PROGRAMDATA" => Path.Combine(root, "ProgramData"), "ProgramFiles" => Path.Combine(root, "Program Files"), "SYSTEMDRIVE" => root, _ => null
        },
        IsAdministrator = false, Now = now ?? (() => Now), RunningProcesses = () => new HashSet<string>(), Parallelism = parallelism, LowImpact = lowImpact
    };

    private static CleanupRule TempRule() => new() { Id = "temp", Group = "Test", Name = "temp", Paths = ["%TEMP%"], MinAgeDays = 1 };

    private void Make(string relative, bool readOnly = false)
    {
        var path = Path.Combine(temp, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[10]);
        File.SetLastWriteTimeUtc(path, Now.AddDays(-5));
        if (readOnly) File.SetAttributes(path, FileAttributes.ReadOnly);
    }

    private async Task<CleanupFinding> Scan(CleanupContext context) => (await new CleanupScanner(context).ScanAsync([TempRule()])).Single();

    private sealed class Collect : IProgress<CleanupProgress>
    {
        public readonly List<CleanupProgress> Seen = [];
        public void Report(CleanupProgress value) { lock (Seen) Seen.Add(value); }
    }

    [Fact]
    public async Task ManyFilesInManyFoldersAreRemovedAndTheFoldersGoWithThem()
    {
        for (var folder = 0; folder < 20; folder++)
            for (var file = 0; file < 150; file++) Make($"cache{folder}/sub{file % 3}/f{file}.tmp");
        var context = Context();
        var finding = await Scan(context);

        var result = await new CleanupCleaner(context).CleanAsync([finding]);

        Assert.Equal(3000, result.Removed);
        Assert.Equal(30_000, result.FreedBytes);
        Assert.Equal(0, result.Skipped);
        Assert.Empty(Directory.EnumerateFileSystemEntries(temp));
    }

    [Fact]
    public async Task ReadOnlyFilesAreRemovedAndAFileInUseIsSkippedWithoutAnException()
    {
        for (var i = 0; i < 40; i++) Make($"ro/r{i}.tmp", readOnly: true);
        Make("busy/open.tmp");
        var context = Context();
        var finding = await Scan(context);

        using var held = new FileStream(Path.Combine(temp, "busy", "open.tmp"), FileMode.Open, FileAccess.Read, FileShare.None);
        var result = await new CleanupCleaner(context).CleanAsync([finding]);

        Assert.Equal(40, result.Removed);
        Assert.Equal(1, result.InUse);
        Assert.Equal(0, result.Failed + result.Denied);
        Assert.True(File.Exists(Path.Combine(temp, "busy", "open.tmp")));
    }

    [Fact]
    public async Task ALongCleanReportsRunningTalliesAndOnlyTheLastReportOfARuleIsFinal()
    {
        for (var i = 0; i < 500; i++) Make($"a/f{i}.tmp");
        var context = Context();
        var finding = await Scan(context);
        var progress = new Collect();

        await new CleanupCleaner(context).CleanAsync([finding], progress);

        Assert.Contains(progress.Seen, x => x.Partial);
        var final = progress.Seen.Where(x => !x.Partial).ToList();
        Assert.Single(final);
        Assert.Equal(500, final[0].Removed);
        // The tallies never run ahead of the final figure.
        Assert.All(progress.Seen.Where(x => x.Partial), x => Assert.True(x.Removed <= 500));
    }

    [Fact]
    public async Task CleaningHandsBackControlAtOnceInsteadOfWorkingOnTheCallersThread()
    {
        Make("a/f.tmp");
        var context = Context();
        var finding = await Scan(context);
        using var inside = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        // The clock is the first thing the clean reads. If it ran on the caller's thread, the call below would not return until it was released.
        var slow = Context(now: () => { inside.Set(); release.Wait(5000); return Now; });

        var started = Stopwatch.StartNew();
        var task = new CleanupCleaner(slow).CleanAsync([finding]);
        var returned = started.Elapsed;

        Assert.True(inside.Wait(5000), "The clean never started.");
        Assert.False(task.IsCompleted);
        Assert.True(returned < TimeSpan.FromSeconds(2), $"CleanAsync held the caller for {returned.TotalSeconds:0.0} s.");
        release.Set();
        Assert.Equal(1, (await task).Removed);
    }

    [Fact]
    public async Task TheSameCleanWorksWithLowImpactOffAndWithOneWorker()
    {
        for (var i = 0; i < 200; i++) Make($"x/f{i}.tmp");
        foreach (var (parallelism, lowImpact) in new[] { (1, true), (8, false) })
        {
            var context = Context(parallelism: parallelism, lowImpact: lowImpact);
            var finding = await Scan(context);
            if (!finding.HasContent) continue;
            var result = await new CleanupCleaner(context).CleanAsync([finding]);
            Assert.Equal(200, result.Removed);
            for (var i = 0; i < 200; i++) Make($"x/f{i}.tmp");
        }
    }

    [Fact]
    public void BackgroundModeCanBeEnteredLeftAndEnteredAgainOnTheSameThread()
    {
        // Entering while already in the mode fails quietly, so a second scope working at all proves the first one was left.
        for (var round = 0; round < 3; round++)
        {
            using (CleanupIo.Background.Begin(true))
            {
                var path = Path.Combine(temp, $"bg{round}.tmp");
                File.WriteAllBytes(path, new byte[10]);
                Assert.Equal(DeleteResult.Removed, CleanupIo.DeleteFile(path));
                Assert.Equal(DeleteResult.Gone, CleanupIo.DeleteFile(path));
            }
        }
    }

    [Fact]
    public void WorkersMatchTheMachineAndNeverExceedTheCeiling()
    {
        Assert.Equal(1, CleanupLoad.ForClean(Context(parallelism: 1), [root]).Workers);
        var gentle = CleanupLoad.ForClean(Context(parallelism: 32), [root]);
        Assert.InRange(gentle.Workers, 2, 8);
        // Whatever the machine is, the load is decided by the disk under the folders: a spinning or unknown one is always treated gently.
        Assert.Equal(CleanupLoad.IsLowEnd(CleanupIo.KindOf(root)), gentle.Background);
        Assert.True(CleanupLoad.IsLowEnd(StorageKind.Rotational));
        Assert.True(CleanupLoad.IsLowEnd(StorageKind.Unknown));
        var plain = CleanupLoad.ForClean(Context(parallelism: 32, lowImpact: false), [root]);
        Assert.Equal(32, plain.Workers);
        Assert.False(plain.Background);
    }

    // ---- Windows' own tools, tracked to their real end ----

    private static readonly string Cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
    private static readonly string PowerShell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    [Fact]
    public async Task ARunIsNotOverUntilTheProgramsItStartedAreOverToo()
    {
        if (!OperatingSystem.IsWindows()) return;
        // cmd starts a detached ping and exits at once, as Disk Cleanup does with its helper: the parent is gone, the work is not.
        var timer = Stopwatch.StartNew();
        var outcome = await SupervisedTool.RunAsync(Cmd, ["/c", "start", "/b", "ping", "-n", "4", "127.0.0.1", ">nul"], TimeSpan.FromSeconds(60), Timeout.InfiniteTimeSpan, hideWindows: true, endWithUs: true, CancellationToken.None);

        Assert.Equal(ToolEnd.Finished, outcome.End);
        Assert.True(timer.Elapsed > TimeSpan.FromSeconds(2), $"The run ended after {timer.Elapsed.TotalSeconds:0.0} s, while its helper was still working.");
    }

    [Fact]
    public async Task AProgramThatDoesNothingIsEndedAsStalled()
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(PowerShell)) return;
        var timer = Stopwatch.StartNew();
        var outcome = await SupervisedTool.RunAsync(PowerShell, ["-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 120"], TimeSpan.FromSeconds(100), TimeSpan.FromSeconds(3), hideWindows: false, endWithUs: true, CancellationToken.None);

        Assert.Equal(ToolEnd.Stalled, outcome.End);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(60), $"The stalled program was only ended after {timer.Elapsed.TotalSeconds:0} s.");
    }

    [Fact]
    public async Task AProgramThatRunsPastItsTimeIsEnded()
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(PowerShell)) return;
        var outcome = await SupervisedTool.RunAsync(PowerShell, ["-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 120"], TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan, hideWindows: false, endWithUs: true, CancellationToken.None);

        Assert.Equal(ToolEnd.TimedOut, outcome.End);
    }

    [Fact]
    public async Task AStopRequestEndsTheWholeProgramAndSurfacesAsCancellation()
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(PowerShell)) return;
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var timer = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            SupervisedTool.RunAsync(PowerShell, ["-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 120"], TimeSpan.FromSeconds(100), Timeout.InfiniteTimeSpan, hideWindows: false, endWithUs: true, cancel.Token));

        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task AProgramThatFinishesNormallyKeepsItsExitCode()
    {
        if (!OperatingSystem.IsWindows()) return;
        var outcome = await SupervisedTool.RunAsync(Cmd, ["/c", "exit", "7"], TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(10), hideWindows: true, endWithUs: true, CancellationToken.None);

        Assert.Equal(ToolEnd.Finished, outcome.End);
        Assert.Equal(7, outcome.ExitCode);
    }

    private delegate bool WindowCallback(IntPtr window, IntPtr parameter);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback, IntPtr parameter);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);

    /// <summary>Every top-level window of the named programs, shown or not, and whether any is showing.</summary>
    private static (int Windows, bool AnyVisible) WindowsOf(string program)
    {
        var pids = Process.GetProcessesByName(program).Select(x => (uint)x.Id).ToHashSet();
        int count = 0; var visible = false;
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var owner);
            if (pids.Contains(owner)) { count++; visible |= IsWindowVisible(window); }
            return true;
        }, IntPtr.Zero);
        return (count, visible);
    }

    private static async Task<(bool Existed, bool StillVisible)> WatchCharmap(bool hide)
    {
        var charmap = Path.Combine(Environment.SystemDirectory, "charmap.exe");
        using var cancel = new CancellationTokenSource();
        var run = SupervisedTool.RunAsync(charmap, [], TimeSpan.FromSeconds(60), Timeout.InfiniteTimeSpan, hideWindows: hide, endWithUs: true, cancel.Token, startHidden: false);
        bool existed = false, visible = false;
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(5) && !run.IsCompleted)
        {
            await Task.Delay(400);
            var (count, any) = WindowsOf("charmap");
            existed |= count > 0;
            // The watch needs a moment to catch a window that has only just appeared.
            if (watch.Elapsed > TimeSpan.FromSeconds(2)) visible |= any;
        }
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        // Ending a job is asynchronous: give the processes a moment to go.
        for (var i = 0; i < 50 && Process.GetProcessesByName("charmap").Length > 0; i++) await Task.Delay(100);
        Assert.Empty(Process.GetProcessesByName("charmap"));
        return (existed, visible);
    }

    [Fact]
    public async Task AToolsWindowIsHiddenSoNobodyCanCloseItHalfWay()
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(Path.Combine(Environment.SystemDirectory, "charmap.exe"))) return;
        var (shown, shownVisible) = await WatchCharmap(hide: false);
        // Started visibly and left alone, the program's window shows, so the check below means something. A machine with no desktop to show it on proves nothing either way.
        if (!shown) return;
        Assert.True(shownVisible, "Control run: the program's window should be showing.");

        var (existed, visible) = await WatchCharmap(hide: true);
        Assert.True(existed);
        Assert.False(visible, "The program's window was left showing.");
    }
}

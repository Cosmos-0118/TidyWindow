using System.Diagnostics;
using System.Text;
using TidyWindow.Next.Core;
using Xunit;

namespace TidyWindow.Next.Tests;

/// <summary>A command or an installer that stops answering must never leave the queue, and so the whole app, waiting for it forever.</summary>
[Collection("Processes")]
public sealed class HangResilienceTests
{
    private static PackageOperation Install(string id) => PackageOperation.Install(new(ManagerKind.Scoop, id, id, "1", "main"));
    private static PackageActionResult Done => new(ActionOutcome.Completed, "Done", "");
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static CommandSpec PowerShell(string script, TimeSpan timeout) => new(new ExecutableLocator().PowerShell,
        ["-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script))], timeout);

    // The command prints its result and ends, but leaves a helper running that still holds the same output pipes (as an installer's helper can).
    private const string LeavesAHelperBehind = "Start-Process -FilePath (Get-Process -Id $PID).Path -ArgumentList '-NoProfile','-Command','Start-Sleep -Seconds 40' -NoNewWindow; [Console]::Out.Write('finished')";

    [Fact]
    public async Task ACommandThatLeavesAHelperHoldingItsOutputStillReturnsOnceItHasEnded()
    {
        var clock = Stopwatch.StartNew();
        var notices = new List<string>();
        var result = await new ProcessRunner(TimeSpan.FromSeconds(1)).RunAsync(PowerShell(LeavesAHelperBehind, TimeSpan.FromMinutes(5)) with
        { Progress = new Progress(value => { if (value.Kind == CommandOutputKind.Notice) lock (notices) notices.Add(value.Text); }) }).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30));
        Assert.Equal(0, result.ExitCode); Assert.Equal("finished", result.Output);
        Assert.Contains(notices, x => x.Contains("stopped waiting", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ACommandThatRunsOutOfTimeIsEndedWithItsHelpersAndReportsTheTimeout()
    {
        var clock = Stopwatch.StartNew();
        await Assert.ThrowsAsync<TimeoutException>(() => new ProcessRunner(TimeSpan.FromSeconds(1)).RunAsync(
            PowerShell(LeavesAHelperBehind.Replace("[Console]::Out.Write('finished')", "Start-Sleep -Seconds 40"), TimeSpan.FromSeconds(3))).WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task CancellingARunningCommandEndsItAndReturnsControl()
    {
        using var source = new CancellationTokenSource();
        var running = new ProcessRunner().RunAsync(PowerShell("Start-Sleep -Seconds 40", TimeSpan.FromMinutes(5)), source.Token);
        await Task.Delay(1000);
        source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public async Task StoppingTheRunningOperationEndsItAndTheQueueMovesOn()
    {
        var started = Signal();
        var queue = new PackageOperationQueue(async (operation, token) =>
        {
            if (operation.Id != "stuck") return Done;
            started.SetResult(); await Task.Delay(Timeout.Infinite, token); return Done;
        });
        var entries = queue.Enqueue([Install("stuck"), Install("next")]);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(queue.StopRunning(entries[0].Id));
        await queue.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var results = queue.Snapshot().Entries;
        Assert.Equal(ActionOutcome.Failed, results[0].Result!.Outcome); Assert.StartsWith("Stopped", results[0].Result!.Message);
        Assert.Equal(ActionOutcome.Completed, results[1].Result!.Outcome);
    }

    [Fact]
    public async Task AnOperationThatIgnoresBeingStoppedCannotHoldTheQueue()
    {
        var started = Signal(); var never = Signal();
        var queue = new PackageOperationQueue(async (operation, _) =>
        {
            if (operation.Id != "stuck") return Done;
            started.SetResult(); await never.Task; return Done;
        }) { StopGrace = TimeSpan.FromMilliseconds(200) };
        queue.Enqueue([Install("stuck"), Install("next")]);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(queue.StopRunning());
        await queue.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var results = queue.Snapshot().Entries;
        Assert.Contains("did not respond", results[0].Result!.Detail);
        Assert.Equal(ActionOutcome.Completed, results[1].Result!.Outcome);
        never.SetResult();
    }

    [Fact]
    public async Task AFixThatIsFinishingItsCurrentStepIsWaitedForAndNotGivenUpOn()
    {
        var started = Signal(); var release = Signal();
        var fix = new PackageOperation(PackageOperationKind.Fix, default, "sfc", "System file check", Fix: new FixRun("sfc", []));
        var queue = new PackageOperationQueue(async (operation, _) =>
        {
            if (operation.Fix is null) return Done;
            // A step that must not be ended half way ignores the stop; the steps that put things back run after it.
            started.SetResult(); await release.Task; return new(ActionOutcome.Failed, "Stopped System file check", "It was stopped before it finished.");
        }) { StopGrace = TimeSpan.FromMilliseconds(100) };
        queue.Enqueue([fix]);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(queue.StopRunning());
        var idle = queue.WhenIdleAsync();
        // Well past the grace an ordinary operation gets: the queue is still busy, so a window waiting on it has not been told it may close.
        await Task.Delay(600);
        Assert.False(idle.IsCompleted);
        Assert.Equal(QueueItemState.Running, queue.Snapshot().Entries[0].State);
        release.SetResult();
        await idle.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("Stopped System file check", queue.Snapshot().Entries[0].Result!.Message);
    }

    [Fact]
    public async Task APersonCanStopWaitingForAHungFixAndTheQueueMovesOnWithoutEndingIt()
    {
        var started = Signal(); var hung = Signal();
        var fix = new PackageOperation(PackageOperationKind.Fix, default, "sfc", "System file check", Fix: new FixRun("sfc", []));
        var queue = new PackageOperationQueue(async (operation, _) =>
        {
            if (operation.Fix is null) return Done;
            started.SetResult(); await hung.Task; return new(ActionOutcome.Failed, "Stopped System file check", "It was stopped before it finished.");
        }) { StopGrace = TimeSpan.FromMilliseconds(100) };
        var settled = new List<QueueEntry>();
        queue.Settled += entry => { lock (settled) settled.Add(entry); };
        queue.Enqueue([fix, Install("next")]);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var id = queue.Snapshot().Entries[0].Id;

        // Giving up is a choice made after asking it to stop, never the first thing offered.
        Assert.False(queue.Snapshot().Entries[0].StopRequested);
        Assert.False(queue.StopWaitingForFix(id));
        Assert.True(queue.StopRunning());
        Assert.True(queue.Snapshot().Entries[0].StopRequested);
        Assert.False(queue.StopWaitingForFix(Guid.NewGuid()));
        Assert.True(queue.StopWaitingForFix(id));

        await queue.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var entries = queue.Snapshot().Entries;
        Assert.Equal("Stopped waiting for System file check", entries[0].Result!.Message);
        Assert.Contains("not ended", entries[0].Result!.Detail);
        Assert.Equal(ActionOutcome.Completed, entries[1].Result!.Outcome);
        Assert.Equal(["System file check"], queue.Lingering);
        Assert.Empty(settled);

        // The fix is still there to end, and how it really ended is reported then.
        hung.SetResult();
        for (var i = 0; i < 100 && settled.Count == 0; i++) await Task.Delay(50);
        var report = Assert.Single(settled);
        Assert.Equal("Stopped System file check", report.Result!.Message);
        Assert.Empty(queue.Lingering);
    }

    [Fact]
    public async Task OnlyAFixCanBeLetGoOfAndAFixThatEndsOnItsOwnIsNotReportedAsGivenUp()
    {
        var started = Signal(); var finish = Signal();
        var queue = new PackageOperationQueue(async (operation, token) =>
        {
            started.SetResult();
            if (operation.Fix is null) { await Task.Delay(Timeout.Infinite, token); return Done; }
            await finish.Task; return new(ActionOutcome.Failed, "Stopped System file check", "It was stopped before it finished.");
        }) { StopGrace = TimeSpan.FromMilliseconds(100) };
        queue.Enqueue([Install("pkg")]);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var id = queue.Snapshot().Entries[0].Id;
        Assert.True(queue.StopRunning());
        Assert.False(queue.StopWaitingForFix(id));
        await queue.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(queue.Lingering);

        var fixStarted = Signal();
        var fixes = new PackageOperationQueue(async (_, _) => { fixStarted.SetResult(); await finish.Task; return new(ActionOutcome.Failed, "Stopped System file check", "It was stopped before it finished."); });
        fixes.Enqueue([new PackageOperation(PackageOperationKind.Fix, default, "sfc", "System file check", Fix: new FixRun("sfc", []))]);
        await fixStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(fixes.StopRunning());
        finish.SetResult();
        await fixes.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("Stopped System file check", fixes.Snapshot().Entries[0].Result!.Message);
        Assert.Empty(fixes.Lingering);
    }

    [Fact]
    public async Task AFixStartedWhileAnotherIsStillFinishingSaysSoAndCanBeStopped()
    {
        var gate = Signal(); var firstRunning = Signal();
        var item = new FixItem { Id = "slow", Name = "Slow fix", Group = FixGroup.Windows, Scope = FixScope.User, Summary = "s", Effect = "e", Steps = [new FixStep { Title = "wait", File = "cmd.exe" }] };
        var first = new BlockingExecutor(firstRunning, gate);
        var service = new FixService([item], first, null, () => false, null);
        var firstRun = service.RunAsync(new("slow", []), new Progress(_ => { }), CancellationToken.None);
        await firstRunning.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var notices = new List<string>();
        using var stop = new CancellationTokenSource();
        var second = service.RunAsync(new("slow", []), new Progress(o => { if (o.Kind == CommandOutputKind.Notice) lock (notices) notices.Add(o.Text); }), stop.Token);
        await Task.Delay(100);
        Assert.Contains(notices, x => x.Contains("still finishing"));
        Assert.False(second.IsCompleted);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second.WaitAsync(TimeSpan.FromSeconds(5)));
        gate.SetResult();
        await firstRun.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class BlockingExecutor(TaskCompletionSource entered, TaskCompletionSource gate) : IFixStepExecutor
    {
        public async Task<int> ExecuteAsync(FixStepContext context, IProgress<CommandOutput> progress, CancellationToken cancellationToken)
        { entered.TrySetResult(); await gate.Task; return 0; }
    }

    [Fact]
    public async Task StoppingIsRefusedForAnOperationThatIsNotTheRunningOne()
    {
        var started = Signal(); var release = Signal();
        var queue = new PackageOperationQueue(async (_, _) => { started.TrySetResult(); await release.Task; return Done; });
        var entries = queue.Enqueue([Install("one"), Install("two")]);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(queue.StopRunning(entries[1].Id)); Assert.False(queue.StopRunning(Guid.NewGuid()));
        release.SetResult(); await queue.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.All(queue.Snapshot().Entries, x => Assert.Equal(ActionOutcome.Completed, x.Result!.Outcome));
        Assert.False(queue.StopRunning());
    }

    [Fact]
    public async Task StartedIsRaisedBeforeEachOperationRunsAndFinishedAfter()
    {
        var order = new List<string>();
        var queue = new PackageOperationQueue((operation, _) => { lock (order) order.Add("run:" + operation.Id); return Task.FromResult(Done); });
        queue.Started += entry => { lock (order) order.Add("start:" + entry.Operation.Id); };
        queue.Finished += entry => { lock (order) order.Add("end:" + entry.Operation.Id); };
        queue.Enqueue([Install("a"), Install("b")]);
        await queue.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(["start:a", "run:a", "end:a", "start:b", "run:b", "end:b"], order);
    }

    [Fact]
    public void AnOperationThatWasNeverFinishedIsReportedOnceAtTheNextLaunch()
    {
        var path = Path.Combine(Path.GetTempPath(), "tidywindow-interrupted-" + Guid.NewGuid().ToString("N"), "note.json");
        try
        {
            var work = new InterruptedWork(path);
            Assert.Null(work.Take());
            work.Begin(Install("openjdk26"));
            var last = new InterruptedWork(path).Take();
            Assert.NotNull(last); Assert.Equal("openjdk26", last.Name); Assert.Equal("installing", last.Kind); Assert.Equal("Scoop", last.Source);
            Assert.Null(work.Take());
        }
        finally { try { Directory.Delete(Path.GetDirectoryName(path)!, true); } catch (DirectoryNotFoundException) { } }
    }

    [Fact]
    public void AFinishedOperationLeavesNothingToReportAndADamagedNoteIsIgnored()
    {
        var folder = Path.Combine(Path.GetTempPath(), "tidywindow-interrupted-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(folder, "note.json");
        try
        {
            var work = new InterruptedWork(path);
            work.Begin(Install("app")); work.End();
            Assert.Null(work.Take());
            File.WriteAllText(path, "{ not json");
            Assert.Null(work.Take()); Assert.False(File.Exists(path));
        }
        finally { try { Directory.Delete(folder, true); } catch (DirectoryNotFoundException) { } }
    }

    private sealed class Progress(Action<CommandOutput> report) : IProgress<CommandOutput> { public void Report(CommandOutput value) => report(value); }
}

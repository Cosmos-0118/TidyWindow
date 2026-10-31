using System.Diagnostics;
using System.Text;
using TidyWindow.Next.Core;
using Xunit;

namespace TidyWindow.Next.Tests;

/// <summary>A command or an installer that stops answering must never leave the queue, and so the whole app, waiting for it forever.</summary>
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

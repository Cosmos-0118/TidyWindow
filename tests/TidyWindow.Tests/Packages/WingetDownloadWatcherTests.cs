using System.Collections.Concurrent;
using TidyWindow.Next.Core;
using Xunit;

namespace TidyWindow.Next.Tests;

public sealed class WingetDownloadWatcherTests
{
    private const string Url = "Downloading https://github.com/ip7z/7zip/releases/download/26.05/7z2605-x64.msi\r\n";

    [Fact]
    public async Task ReportsThePercentageWhileWingetIsDownloadingAndLeavesItsOutputAlone()
    {
        var runner = new PollingRunner();
        var seen = new Recorder();
        using var watcher = new WingetDownloadWatcher(seen, runner);
        watcher.Report(new("Found 7-Zip [7zip.7zip] Version 26.05\r\n"));
        watcher.Report(new(Url));
        await runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        runner.Print("0/2000\n1000/2000\n");
        runner.Print("1000/2000\n1990/2000\n");
        await seen.WaitForAsync(x => x is { Kind: CommandOutputKind.Transfer, Text: "Downloading 99%" });
        Assert.Equal(["Downloading 0%", "Downloading 50%", "Downloading 99%"], seen.Transfers);
        Assert.Equal(["Found 7-Zip [7zip.7zip] Version 26.05\r\n", Url], seen.Items.Where(x => x.Kind == CommandOutputKind.Output).Select(x => x.Text));
    }

    [Fact]
    public async Task NumbersSplitAcrossChunksAreStillRead()
    {
        var runner = new PollingRunner();
        var seen = new Recorder();
        using var watcher = new WingetDownloadWatcher(seen, runner);
        watcher.Report(new(Url));
        await runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        runner.Print("250/10"); runner.Print("00\n");
        await seen.WaitForAsync(x => x.Text == "Downloading 25%");
    }

    [Fact]
    public async Task TheNextLineFromWingetEndsTheDownloadAndStopsThePoller()
    {
        var runner = new PollingRunner();
        var seen = new Recorder();
        using var watcher = new WingetDownloadWatcher(seen, runner);
        watcher.Report(new(Url));
        await runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        runner.Print("500/1000\n");
        await seen.WaitForAsync(x => x.Text == "Downloading 50%");
        watcher.Report(new("Successfully verified installer hash\r\n"));
        await runner.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("", seen.Transfers[^1]);
        runner.Print("900/1000\n");
        await Task.Delay(100);
        Assert.Equal(["Downloading 50%", ""], seen.Transfers);
    }

    [Fact]
    public void ADownloadThatAlreadyEndedInTheSameChunkIsNeverWatched()
    {
        var runner = new PollingRunner();
        using var watcher = new WingetDownloadWatcher(new Recorder(), runner);
        watcher.Report(new(Url + "Successfully verified installer hash\r\n"));
        Assert.Equal(0, runner.Runs);
    }

    [Theory]
    [InlineData("Found 7-Zip [7zip.7zip] Version 26.05\r\n")]
    [InlineData("Downloading 50%\r\n")]
    [InlineData("")]
    public void OutputThatIsNotADownloadStartsNothing(string text)
    {
        var runner = new PollingRunner();
        using var watcher = new WingetDownloadWatcher(new Recorder(), runner);
        watcher.Report(new(text));
        Assert.Equal(0, runner.Runs);
    }

    [Fact]
    public async Task ADownloadIsOnlyWatchedOncePerLine()
    {
        var runner = new PollingRunner();
        using var watcher = new WingetDownloadWatcher(new Recorder(), runner);
        watcher.Report(new(Url));
        watcher.Report(new(Url));
        await runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, runner.Runs);
    }

    [Fact]
    public async Task APollerThatCannotRunLeavesTheInstallUntouched()
    {
        var seen = new Recorder();
        using var watcher = new WingetDownloadWatcher(seen, new ThrowingRunner());
        watcher.Report(new(Url));
        watcher.Report(new("Successfully verified installer hash\r\n"));
        await Task.Delay(100);
        Assert.All(seen.Transfers, text => Assert.Equal("", text));
        Assert.Equal(["Downloading https://github.com/ip7z/7zip/releases/download/26.05/7z2605-x64.msi\r\n", "Successfully verified installer hash\r\n"],
            seen.Items.Where(x => x.Kind == CommandOutputKind.Output).Select(x => x.Text));
    }

    [Fact]
    public async Task DisposingStopsThePollerAndClearsTheNumber()
    {
        var runner = new PollingRunner();
        var seen = new Recorder();
        var watcher = new WingetDownloadWatcher(seen, runner);
        watcher.Report(new(Url));
        await runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        watcher.Dispose();
        await runner.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal([""], seen.Transfers);
        watcher.Report(new(Url));
        Assert.Equal(1, runner.Runs);
    }

    [Fact]
    public async Task TheQueueShowsTransfersBesideTheOperationAndNotInItsOutput()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IProgress<CommandOutput>? captured = null;
        var queue = PackageOperationQueue.WithLiveOutput(async (operation, progress, _) =>
        {
            captured = progress; started.SetResult(); await release.Task;
            return new(ActionOutcome.Completed, operation.Id, "");
        });
        queue.Enqueue([PackageOperation.Install(new(ManagerKind.Winget, "7zip.7zip", "7-Zip", "26.05", "winget"))]);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        captured!.Report(new("Downloading https://example.com/a.msi\r\n"));
        var stateVersion = queue.Snapshot().StateVersion;
        captured.Report(new("Downloading 42%", CommandOutputKind.Transfer));
        var running = Assert.Single(queue.Snapshot().Entries);
        Assert.Equal(new CommandProgress("Downloading", 42), running.Progress);
        Assert.Equal("Downloading https://example.com/a.msi\r\n", running.Output);
        Assert.Equal(stateVersion, queue.Snapshot().StateVersion);
        var revision = queue.Snapshot().Revision;
        captured.Report(new("Downloading 42%", CommandOutputKind.Transfer));
        Assert.Equal(revision, queue.Snapshot().Revision);
        captured.Report(new("", CommandOutputKind.Transfer));
        Assert.Null(Assert.Single(queue.Snapshot().Entries).Progress);
        release.SetResult(); await queue.WhenIdleAsync();
    }

    private sealed class Recorder : IProgress<CommandOutput>
    {
        private readonly ConcurrentQueue<CommandOutput> items = new();
        public IReadOnlyList<CommandOutput> Items => items.ToArray();
        public IReadOnlyList<string> Transfers => items.Where(x => x.Kind == CommandOutputKind.Transfer).Select(x => x.Text).ToArray();
        public void Report(CommandOutput value) => items.Enqueue(value);
        public async Task WaitForAsync(Func<CommandOutput, bool> match)
        {
            var end = DateTime.UtcNow.AddSeconds(5);
            while (!items.Any(match)) { if (DateTime.UtcNow > end) throw new TimeoutException("The expected output never arrived."); await Task.Delay(10); }
        }
    }

    /// <summary>Stands in for the PowerShell poller: prints what the test tells it to until it is cancelled.</summary>
    private sealed class PollingRunner : ICommandRunner
    {
        private volatile IProgress<CommandOutput>? progress;
        private int runs;
        public int Runs => runs;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Print(string text) => progress?.Report(new(text));
        public async Task<CommandResult> RunAsync(CommandSpec command, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref runs);
            progress = command.Progress; Started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, cancellationToken); }
            catch (OperationCanceledException) { Stopped.TrySetResult(); throw; }
            return new(0, "", "");
        }
    }

    private sealed class ThrowingRunner : ICommandRunner
    {
        public Task<CommandResult> RunAsync(CommandSpec command, CancellationToken cancellationToken = default) => throw new InvalidOperationException("PowerShell is not available.");
    }
}

using System.Collections.Concurrent;
using System.Text;
using TidyWindow.Next.Core;
using Xunit;

namespace TidyWindow.Next.Tests;

public sealed class LiveOutputTests
{
    [Fact]
    public async Task ProcessOutputArrivesBeforeCommandExitsWithoutWaitingForANewline()
    {
        var releasePath = Path.Combine(Path.GetTempPath(), "tidywindow-output-" + Guid.NewGuid().ToString("N"));
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var chunks = new ConcurrentQueue<CommandOutput>();
        var script = "[Console]::Out.Write('first'); [Console]::Out.Flush(); [Console]::Error.Write('warning'); [Console]::Error.Flush(); while (!(Test-Path -LiteralPath '" + releasePath.Replace("'", "''") + "')) { Start-Sleep -Milliseconds 50 }; [Console]::Out.Write('last')";
        var command = PowerShell(script) with { Progress = new CallbackProgress(value => { chunks.Enqueue(value); if (value.Kind == CommandOutputKind.Output && value.Text.Contains("first")) first.TrySetResult(); }) };
        var running = new ProcessRunner().RunAsync(command);
        try
        {
            await first.Task.WaitAsync(TimeSpan.FromSeconds(8));
            Assert.False(running.IsCompleted);
            File.WriteAllText(releasePath, "release");
            var result = await running;
            Assert.Equal("firstlast", result.Output); Assert.Equal("warning", result.Error);
            Assert.Contains(chunks, x => x.Kind == CommandOutputKind.Error && x.Text.Contains("warning"));
            Assert.Equal("firstlast", string.Concat(chunks.Where(x => x.Kind == CommandOutputKind.Output).Select(x => x.Text)));
        }
        finally
        {
            File.WriteAllText(releasePath, "release");
            try { await running; } finally { File.Delete(releasePath); }
        }
    }

    [Fact]
    public async Task SplitUtf8CharactersAreDecodedAcrossChunks()
    {
        const string expected = "漢字🙂";
        var collected = new StringBuilder();
        var script = "$bytes=[Text.Encoding]::UTF8.GetBytes('" + expected + "'); $stream=[Console]::OpenStandardOutput(); foreach ($b in $bytes) { $stream.WriteByte($b); $stream.Flush(); Start-Sleep -Milliseconds 15 }";
        var result = await new ProcessRunner().RunAsync(PowerShell(script) with { Progress = new CallbackProgress(value => { if (value.Kind == CommandOutputKind.Output) collected.Append(value.Text); }) });
        Assert.Equal(expected, result.Output); Assert.Equal(expected, collected.ToString());
    }

    [Fact]
    public async Task AFailedOutputObserverCannotBlockTheCommandOrItsPipes()
    {
        var result = await new ProcessRunner().RunAsync(PowerShell("[Console]::Out.Write(('x' * 150000)); [Console]::Error.Write(('y' * 150000))") with
        { Progress = new CallbackProgress(_ => throw new IOException("The output view failed")) });
        Assert.Equal(0, result.ExitCode); Assert.Equal(131072, result.Output.Length); Assert.Equal(131072, result.Error.Length);
    }

    [Fact]
    public async Task QueueKeepsBoundedLiveOutputAndNoticeAfterCompletion()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IProgress<CommandOutput>? captured = null;
        var queue = PackageOperationQueue.WithLiveOutput(async (_, progress, _) =>
        {
            captured = progress;
            progress.Report(new("Approve the administrator prompt", CommandOutputKind.Notice));
            progress.Report(new(new string('x', 100000)));
            progress.Report(new("failure text", CommandOutputKind.Error));
            started.SetResult(); await release.Task;
            return new(ActionOutcome.Completed, "Finished", "Done");
        });
        queue.Enqueue([PackageOperation.Install(new(ManagerKind.Winget, "Vendor.App", "App", "1", "winget"))]);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var live = Assert.Single(queue.Snapshot().Entries);
        Assert.Equal(QueueItemState.Running, live.State);
        Assert.Equal(65536, live.Output.Length); Assert.Contains("failure text", live.Output);
        Assert.Contains(live.OutputSegments!, x => x.Kind == CommandOutputKind.Error && x.Text == "failure text");
        Assert.Equal("Approve the administrator prompt", live.Notice);
        release.SetResult(); await queue.WhenIdleAsync();
        var finished = Assert.Single(queue.Snapshot().Entries);
        Assert.Equal(live.Output, finished.Output);
        var revision = queue.Snapshot().Revision;
        captured!.Report(new("Late output from an old callback"));
        Assert.Equal(revision, queue.Snapshot().Revision);
        queue.ClearFinished(); Assert.Empty(queue.Snapshot().Entries);
    }

    [Fact]
    public async Task StreamingDoesNotChangeTheQueueStateVersionOrInterruptTheNextItem()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IProgress<CommandOutput>? captured = null;
        var queue = PackageOperationQueue.WithLiveOutput(async (operation, progress, _) =>
        {
            if (operation.Id == "first") { captured = progress; started.SetResult(); await release.Task; }
            return new(ActionOutcome.Completed, operation.Id, "");
        });
        var first = PackageOperation.Install(new(ManagerKind.Winget, "first", "First", "1", "winget"));
        queue.Enqueue([first, first with { Id = "second", Name = "Second" }]);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stateVersion = queue.Snapshot().StateVersion;
        captured!.Report(new("Downloading…\r50%\n"));
        Assert.Equal(stateVersion, queue.Snapshot().StateVersion);
        queue.ClearFinished(); Assert.Equal(2, queue.Snapshot().Entries.Count);
        release.SetResult(); await queue.WhenIdleAsync();
        Assert.Equal(2, queue.Snapshot().Completions);
    }

    [Theory]
    [InlineData(ManagerKind.Winget, "--interactive")]
    [InlineData(ManagerKind.Chocolatey, "--not-silent")]
    public void InstallerOptionsUseTheManagersSupportedInteractiveFlags(ManagerKind kind, string flag)
    {
        var locator = new Locator(); var runner = new Runner(); var packages = new PackageService(runner, locator);
        var status = new ManagerStatus(ManagerDefinition.All.Single(x => x.Kind == kind), ManagerHealth.Ready, "1", "manager.exe", "Ready");
        var install = packages.InstallCommand(status, new(kind, "Vendor.App", "App", "1", "source"), interactive: true);
        var update = new InstallationService(packages, runner, locator).ActionCommand(status, new(kind, "Vendor.App", "App", "1", "2"), PackageAction.Update, interactive: true);
        Assert.Contains(flag, install.Arguments); Assert.Contains(flag, update.Arguments);
        Assert.True(install.MayShowInstaller); Assert.True(update.MayShowInstaller);
        Assert.Equal(TimeSpan.FromHours(2), install.Timeout);
    }

    [Fact]
    public async Task ExecutorForwardsLiveOutputIntoTheQueueForTheActualPackageCommand()
    {
        var runner = new Runner(); var locator = new Locator(); var packages = new PackageService(runner, locator);
        var executor = new PackageOperationExecutor(packages, new(packages, runner, locator), locator);
        var queue = PackageOperationQueue.WithLiveOutput(executor.ExecuteAsync);
        queue.Enqueue([PackageOperation.Install(new(ManagerKind.Chocolatey, "test-app", "App", "1", "chocolatey")) with { Interactive = true }]);
        await queue.WhenIdleAsync();
        var entry = Assert.Single(queue.Snapshot().Entries);
        Assert.Contains("live install output", entry.Output);
        Assert.Equal(ActionOutcome.Completed, entry.Result!.Outcome);
        Assert.Contains("--not-silent", runner.Install!.Arguments);
    }

    private static CommandSpec PowerShell(string script) => new(new ExecutableLocator().PowerShell,
        ["-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script))], TimeSpan.FromSeconds(15));
    private sealed class CallbackProgress(Action<CommandOutput> report) : IProgress<CommandOutput> { public void Report(CommandOutput value) => report(value); }
    private sealed class Locator : IExecutableLocator { public string? Find(ManagerKind manager) => "manager.exe"; public string PowerShell => "powershell.exe"; }
    private sealed class Runner : ICommandRunner
    {
        public CommandSpec? Install { get; private set; }
        public Task<CommandResult> RunAsync(CommandSpec command, CancellationToken cancellationToken = default)
        {
            if (command.Arguments[0] == "list") return Task.FromResult(new CommandResult(0, Install is null ? "0 packages installed." : "test-app|1", ""));
            Install = command; command.Progress?.Report(new("live install output\n"));
            return Task.FromResult(new CommandResult(0, "live install output\n", ""));
        }
    }
}

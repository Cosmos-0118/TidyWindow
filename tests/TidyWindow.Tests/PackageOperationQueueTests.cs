using System.Collections.Concurrent;
using TidyWindow.Next.Core;
using Xunit;

namespace TidyWindow.Next.Tests;

public sealed class PackageOperationQueueTests
{
    private static PackageOperation Install(string id) => PackageOperation.Install(new(ManagerKind.Chocolatey, id, id, "1", "source"));
    private static PackageOperation Update(string id) => PackageOperation.Manage(new(ManagerKind.Chocolatey, id, id, "1", "2"), PackageAction.Update);
    private static PackageActionResult Done => new(ActionOutcome.Completed, "Done", "");
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Fact]
    public async Task MixedInstallsUpdatesAndUninstallsRunInOneFifoWithNoOverlap()
    {
        var firstStarted = Signal(); var release = Signal();
        var order = new ConcurrentQueue<PackageOperation>(); var active = 0; var maximum = 0;
        var queue = new PackageOperationQueue(async (operation, _) =>
        {
            var running = Interlocked.Increment(ref active); maximum = Math.Max(maximum, running);
            order.Enqueue(operation);
            if (operation.Id == "first") { firstStarted.SetResult(); await release.Task; }
            Interlocked.Decrement(ref active); return Done;
        });
        queue.Enqueue([Install("first"), Update("second")]);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // A different page can keep adding work while the first installer is running.
        queue.Enqueue([PackageOperation.Manage(new(ManagerKind.Scoop, "third", "Third", "1"), PackageAction.Uninstall)]);
        Assert.Equal(3, queue.Snapshot().Entries.Count);
        release.SetResult(); await queue.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(["first", "second", "third"], order.Select(x => x.Id));
        Assert.Equal(1, maximum); Assert.False(queue.Snapshot().HasWork);
        Assert.Equal(3, queue.Snapshot().Completions);
    }

    [Fact]
    public async Task DuplicateClicksAndConflictingActionsForTheSamePackageAreRejected()
    {
        var started = Signal(); var release = Signal();
        var queue = new PackageOperationQueue(async (_, _) => { started.TrySetResult(); await release.Task; return Done; });
        Assert.Single(queue.Enqueue([Install("app"), Install("APP"), Update("app")]));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(queue.Enqueue([Update("APP")]));
        release.SetResult(); await queue.WhenIdleAsync();
        Assert.Single(queue.Snapshot().Entries);
    }

    [Fact]
    public async Task ScoopBucketAliasesDeduplicateButGlobalAndUserInstallsStaySeparate()
    {
        Assert.Equal(PackageOperation.Resource(ManagerKind.Scoop, "main/git"), PackageOperation.Resource(ManagerKind.Scoop, "GIT"));
        Assert.NotEqual(PackageOperation.Resource(ManagerKind.Scoop, "git"), PackageOperation.Resource(ManagerKind.Scoop, "git", true));
        var queue = new PackageOperationQueue((_, _) => Task.FromResult(Done));
        queue.Enqueue([Install("app")]); await queue.WhenIdleAsync();
        queue.Enqueue([Update("app")]); await queue.WhenIdleAsync();
        Assert.Equal(2, queue.Snapshot().Completions); // Finished work does not permanently block a new action.
    }

    [Fact]
    public async Task CancelWaitingNeverCancelsTheRunningInstaller()
    {
        var started = Signal(); var release = Signal(); var executed = new ConcurrentQueue<string>();
        var queue = new PackageOperationQueue(async (operation, _) =>
        { executed.Enqueue(operation.Id); started.TrySetResult(); await release.Task; return Done; });
        var entries = queue.Enqueue([Install("running"), Update("waiting"), Install("remove")]);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(queue.Cancel(entries[0].Id));
        Assert.True(queue.Cancel(entries[1].Id));
        queue.CancelWaiting();
        Assert.True(queue.Snapshot().HasWork);
        release.SetResult(); await queue.WhenIdleAsync();
        Assert.Equal(["running"], executed);
        Assert.Equal(2, queue.Snapshot().Entries.Count(x => x.State == QueueItemState.Cancelled));
    }

    [Fact]
    public async Task FailureAndThrowingObserversDoNotStopRemainingWork()
    {
        var executed = new ConcurrentQueue<string>();
        var queue = new PackageOperationQueue((operation, _) =>
        {
            executed.Enqueue(operation.Id);
            if (operation.Id == "throws") throw new IOException("Network failed");
            return Task.FromResult(operation.Id == "declined" ? new(ActionOutcome.Failed, "Permission declined", "") : Done);
        });
        queue.Changed += () => throw new InvalidOperationException("UI observer failed");
        queue.Finished += _ => throw new InvalidOperationException("Log observer failed");
        queue.Enqueue([Install("throws"), Update("declined"), Install("succeeds")]);
        await queue.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(["throws", "declined", "succeeds"], executed);
        Assert.Equal([ActionOutcome.Failed, ActionOutcome.Failed, ActionOutcome.Completed], queue.Snapshot().Entries.Select(x => x.Result!.Outcome));
    }

    [Fact]
    public async Task ConcurrentEnqueuesAreNotLostAndRecentHistoryIsBounded()
    {
        var count = 0;
        var queue = new PackageOperationQueue((_, _) => { Interlocked.Increment(ref count); return Task.FromResult(Done); });
        await Task.WhenAll(Enumerable.Range(0, 250).Select(i => Task.Run(() => queue.Enqueue([Install("app" + i)]))));
        await queue.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(250, count); Assert.Equal(250, queue.Snapshot().Completions);
        Assert.Equal(100, queue.Snapshot().Entries.Count);
    }

    [Fact]
    public async Task AnEntireSelectedBatchIsEnqueuedBeforeTheWorkerStarts()
    {
        var queue = new PackageOperationQueue((_, _) => Task.FromResult(Done));
        var batch = Enumerable.Range(0, 150).Select(i => Update("selected" + i));
        Assert.Equal(150, queue.Enqueue(batch).Count);
        await queue.WhenIdleAsync();
        Assert.Equal(150, queue.Snapshot().Completions);
    }

    [Fact]
    public void AThrowingBatchProducerCannotLeaveUnstartedWork()
    {
        static IEnumerable<PackageOperation> Broken() { yield return Install("app"); throw new IOException("Broken selection"); }
        var queue = new PackageOperationQueue((_, _) => Task.FromResult(Done));
        Assert.Throws<IOException>(() => queue.Enqueue(Broken()));
        Assert.False(queue.Snapshot().HasWork); Assert.True(queue.WhenIdleAsync().IsCompleted);
    }

    [Fact]
    public async Task QueueRunsRealAdaptersForInstallUpdateAndUninstallAndVerifiesEachResult()
    {
        var runner = new InventoryRunner(); var locator = new Locator();
        var packages = new PackageService(runner, locator);
        var executor = new PackageOperationExecutor(packages, new(packages, runner, locator), locator);
        var queue = new PackageOperationQueue(executor.ExecuteAsync);
        queue.Enqueue([Install("new-app"), Update("update-app"),
            PackageOperation.Manage(new(ManagerKind.Chocolatey, "remove-app", "Remove", "1"), PackageAction.Uninstall)]);
        await queue.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(["install:new-app", "upgrade:update-app", "uninstall:remove-app"], runner.Mutations);
        Assert.Equal("1", runner.Installed["new-app"]); Assert.Equal("2", runner.Installed["update-app"]);
        Assert.False(runner.Installed.ContainsKey("remove-app"));
        Assert.All(queue.Snapshot().Entries, x => Assert.Equal(ActionOutcome.Completed, x.Result!.Outcome));
    }

    [Fact]
    public async Task ExecutionRechecksPackagesAlreadyInstalledOrUpdatedWhileWaiting()
    {
        var runner = new InventoryRunner(); runner.Installed["update-app"] = "2";
        var locator = new Locator(); var packages = new PackageService(runner, locator);
        var executor = new PackageOperationExecutor(packages, new(packages, runner, locator), locator);
        var queue = new PackageOperationQueue(executor.ExecuteAsync);
        queue.Enqueue([Install("remove-app"), Update("update-app")]);
        await queue.WhenIdleAsync();
        Assert.Empty(runner.Mutations);
        Assert.All(queue.Snapshot().Entries, x => Assert.Equal(ActionOutcome.Completed, x.Result!.Outcome));
    }

    private sealed class Locator : IExecutableLocator
    {
        public string? Find(ManagerKind manager) => "manager.exe";
        public string PowerShell => "powershell.exe";
    }
    private sealed class InventoryRunner : ICommandRunner
    {
        public Dictionary<string, string> Installed { get; } = new() { ["update-app"] = "1", ["remove-app"] = "1" };
        public List<string> Mutations { get; } = [];
        public Task<CommandResult> RunAsync(CommandSpec command, CancellationToken cancellationToken = default)
        {
            var verb = command.Arguments[0];
            if (verb == "list") return Task.FromResult(new CommandResult(0, string.Join("\n", Installed.Select(x => $"{x.Key}|{x.Value}")), ""));
            var id = command.Arguments[1]; Mutations.Add($"{verb}:{id}");
            switch (verb)
            {
                case "install": Installed[id] = "1"; break;
                case "upgrade": Installed[id] = "2"; break;
                case "uninstall": Installed.Remove(id); break;
                default: throw new InvalidOperationException("Unexpected command: " + verb);
            }
            return Task.FromResult(new CommandResult(0, "Completed", ""));
        }
    }
}

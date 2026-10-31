namespace TidyWindow.Next.Core;

public enum PackageOperationKind { Install, Update, Uninstall, Fix }
public enum QueueItemState { Waiting, Running, Finished, Cancelled }
/// <param name="Fix">Set for a fix from the Fixes page, which is not a package operation but shares the queue and its live output.</param>
public sealed record PackageOperation(PackageOperationKind Kind, ManagerKind Manager, string Id, string Name,
    Package? InstallPackage = null, ManagedPackage? InstalledPackage = null, bool Interactive = false, FixRun? Fix = null)
{
    public const string FixPrefix = "fix:";
    public string ResourceKey => Fix is not null ? FixResource(Fix.Id) : Resource(Manager, Id, InstalledPackage?.Global ?? false);
    public static string FixResource(string id) => FixPrefix + id.ToUpperInvariant();
    public static PackageOperation ForFix(FixItem item, IReadOnlyList<string> options) => new(PackageOperationKind.Fix, default, item.Id, item.Name, Fix: new(item.Id, options));
    public static string Resource(ManagerKind manager, string id, bool global = false) =>
        $"{manager}:{(manager == ManagerKind.Scoop ? id.Split('/')[^1] : id).ToUpperInvariant()}:{global}";
    public static PackageOperation Install(Package package) => new(PackageOperationKind.Install, package.Manager, package.Id, package.Name, InstallPackage: package);
    public static PackageOperation Manage(ManagedPackage package, PackageAction action) =>
        new(action == PackageAction.Update ? PackageOperationKind.Update : PackageOperationKind.Uninstall, package.Manager, package.Id, package.Name, InstalledPackage: package);
}
public sealed record QueueEntry(Guid Id, PackageOperation Operation, QueueItemState State, PackageActionResult? Result = null, string Output = "", string Notice = "", IReadOnlyList<CommandOutput>? OutputSegments = null);
public sealed record QueueSnapshot(long Revision, IReadOnlyList<QueueEntry> Entries, long Completions = 0, long StateVersion = 0)
{
    public bool HasWork => Entries.Any(x => x.State is QueueItemState.Waiting or QueueItemState.Running);
    public QueueEntry? Find(string resource) => Entries.LastOrDefault(x => x.Operation.ResourceKey.Equals(resource, StringComparison.OrdinalIgnoreCase));
}

/// <summary>One FIFO worker across all managers and pages. Waiting work can be removed; the running operation is only stopped on request, and a stop that is ignored cannot hold the queue.</summary>
public sealed class PackageOperationQueue
{
    private readonly Func<PackageOperation, IProgress<CommandOutput>, CancellationToken, Task<PackageActionResult>> execute;
    public PackageOperationQueue(Func<PackageOperation, CancellationToken, Task<PackageActionResult>> execute)
        : this((operation, _, token) => execute(operation, token)) { }
    private PackageOperationQueue(Func<PackageOperation, IProgress<CommandOutput>, CancellationToken, Task<PackageActionResult>> execute) => this.execute = execute;
    public static PackageOperationQueue WithLiveOutput(Func<PackageOperation, IProgress<CommandOutput>, CancellationToken, Task<PackageActionResult>> execute) => new(execute);
    private readonly object gate = new();
    private readonly List<QueueEntry> entries = [];
    private bool draining;
    private long revision;
    private long completions;
    private long stateVersion;
    private TaskCompletionSource idle = Completed();
    private CancellationTokenSource? stopSource;
    private Guid runningId;
    /// <summary>How long a stopped operation gets to wind down before the queue moves on without it.</summary>
    public TimeSpan StopGrace { get; init; } = TimeSpan.FromSeconds(15);
    public event Action? Changed;
    /// <summary>Raised on the worker just before an operation runs.</summary>
    public event Action<QueueEntry>? Started;
    public event Action<QueueEntry>? Finished;
    public QueueSnapshot Snapshot() { lock (gate) return new(revision, entries.ToArray(), completions, stateVersion); }
    public Task WhenIdleAsync() { lock (gate) return idle.Task; }

    public IReadOnlyList<QueueEntry> Enqueue(IEnumerable<PackageOperation> operations)
    {
        // Materialize before mutating so a failing producer cannot leave an unstarted batch.
        var batch = operations.ToArray();
        var accepted = new List<QueueEntry>();
        var start = false;
        lock (gate)
        {
            foreach (var operation in batch)
            {
                if (entries.Any(x => x.State is QueueItemState.Waiting or QueueItemState.Running &&
                    x.Operation.ResourceKey.Equals(operation.ResourceKey, StringComparison.OrdinalIgnoreCase))) continue;
                var entry = new QueueEntry(Guid.NewGuid(), operation, QueueItemState.Waiting);
                entries.Add(entry); accepted.Add(entry);
            }
            if (accepted.Count == 0) return accepted;
            revision++; stateVersion++;
            if (!draining) { draining = true; start = true; idle = new(TaskCreationOptions.RunContinuationsAsynchronously); }
        }
        Notify(Changed);
        if (start) _ = Task.Run(DrainAsync);
        return accepted;
    }

    public bool Cancel(Guid id)
    {
        lock (gate)
        {
            var index = entries.FindIndex(x => x.Id == id && x.State == QueueItemState.Waiting);
            if (index < 0) return false;
            entries[index] = entries[index] with { State = QueueItemState.Cancelled }; revision++; stateVersion++;
        }
        Notify(Changed); return true;
    }
    public void CancelWaiting()
    {
        lock (gate)
        {
            for (var i = 0; i < entries.Count; i++)
                if (entries[i].State == QueueItemState.Waiting) entries[i] = entries[i] with { State = QueueItemState.Cancelled };
            revision++; stateVersion++;
        }
        Notify(Changed);
    }
    /// <summary>Asks the running operation to stop (its command is ended). With an id, only if that operation is still the one running.</summary>
    public bool StopRunning(Guid? id = null)
    {
        CancellationTokenSource? source; Guid target;
        lock (gate) { source = id is null || id == runningId ? stopSource : null; target = runningId; }
        if (source is null) return false;
        try { source.Cancel(); } catch (ObjectDisposedException) { return false; }
        AppendOutput(target, new("Stopping…", CommandOutputKind.Notice));
        return true;
    }
    public void ClearFinished()
    {
        lock (gate) { entries.RemoveAll(x => x.State is QueueItemState.Finished or QueueItemState.Cancelled); revision++; stateVersion++; }
        Notify(Changed);
    }
    private void AppendOutput(Guid id, CommandOutput output)
    {
        lock (gate)
        {
            var index = entries.FindIndex(x => x.Id == id && x.State == QueueItemState.Running);
            if (index < 0) return;
            var entry = entries[index];
            if (output.Kind == CommandOutputKind.Notice) entry = entry with { Notice = output.Text };
            else
            {
                var segments = entry.OutputSegments?.ToList() ?? [];
                if (segments.Count > 0 && segments[^1].Kind == output.Kind)
                    segments[^1] = segments[^1] with { Text = segments[^1].Text + output.Text };
                else segments.Add(output);
                var length = segments.Sum(x => x.Text.Length);
                while (length > 65536 && segments.Count > 0)
                {
                    var excess = length - 65536;
                    if (segments[0].Text.Length <= excess) { length -= segments[0].Text.Length; segments.RemoveAt(0); }
                    else { segments[0] = segments[0] with { Text = segments[0].Text[excess..] }; length -= excess; }
                }
                while (segments.Count > 512) segments.RemoveAt(0);
                entry = entry with { Output = string.Concat(segments.Select(x => x.Text)), OutputSegments = segments.ToArray() };
            }
            entries[index] = entry; revision++;
        }
        Notify(Changed);
    }
    private async Task DrainAsync()
    {
        try { await DrainLoopAsync().ConfigureAwait(false); }
        catch (Exception ex)
        {
            // Whatever went wrong, the queue must be able to run again: close out the operation it was on and carry on with what is waiting.
            bool again;
            lock (gate)
            {
                var closed = false;
                for (var i = 0; i < entries.Count; i++)
                    if (entries[i].State == QueueItemState.Running)
                    {
                        entries[i] = entries[i] with { State = QueueItemState.Finished, Result = new(ActionOutcome.Failed, $"Couldn’t finish {entries[i].Operation.Name}", "The queue hit an unexpected error: " + ex.Message) };
                        completions++; closed = true;
                    }
                // With nothing to blame, waiting work is dropped rather than retried in a loop.
                if (!closed)
                    for (var i = 0; i < entries.Count; i++)
                        if (entries[i].State == QueueItemState.Waiting) entries[i] = entries[i] with { State = QueueItemState.Cancelled };
                stopSource = null; runningId = default;
                again = entries.Any(x => x.State == QueueItemState.Waiting);
                if (!again) { draining = false; idle.TrySetResult(); }
                revision++; stateVersion++;
            }
            Notify(Changed);
            if (again) _ = Task.Run(DrainAsync);
        }
    }
    private async Task DrainLoopAsync()
    {
        while (true)
        {
            QueueEntry? current;
            CancellationTokenSource? stop = null;
            lock (gate)
            {
                current = entries.FirstOrDefault(x => x.State == QueueItemState.Waiting);
                if (current is null) { draining = false; idle.TrySetResult(); revision++; stateVersion++; }
                else
                {
                    current = current with { State = QueueItemState.Running }; entries[entries.FindIndex(x => x.Id == current.Id)] = current;
                    stopSource = stop = new(); runningId = current.Id; revision++; stateVersion++;
                }
            }
            Notify(Changed);
            if (current is null) return;
            Notify(Started, current);
            var result = await RunOneAsync(current, stop!).ConfigureAwait(false);
            QueueEntry finished;
            lock (gate)
            {
                stopSource = null; runningId = default;
                var index = entries.FindIndex(x => x.Id == current.Id);
                finished = entries[index] with { State = QueueItemState.Finished, Result = result };
                entries[index] = finished;
                completions++;
                // Keep a bounded recent outcome for inline state; durable outcomes live in Logs.
                while (entries.Count(x => x.State is QueueItemState.Finished or QueueItemState.Cancelled) > 100)
                    entries.RemoveAt(entries.FindIndex(x => x.State is QueueItemState.Finished or QueueItemState.Cancelled));
                revision++; stateVersion++;
            }
            stop!.Dispose();
            Notify(Finished, finished);
            Notify(Changed);
        }
    }
    /// <summary>Runs one operation and always comes back with a result: a failure, a stop, or an operation that ignored the stop is an outcome, never a hang.</summary>
    private async Task<PackageActionResult> RunOneAsync(QueueEntry entry, CancellationTokenSource stop)
    {
        Task<PackageActionResult> work;
        try { work = execute(entry.Operation, new OutputProgress(output => AppendOutput(entry.Id, output)), stop.Token); }
        catch (Exception ex) { return Failure(entry.Operation, ex, stop); }
        var gaveUp = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watch = stop.Token.Register(() => _ = Task.Delay(StopGrace).ContinueWith(_ => gaveUp.TrySetResult(), TaskScheduler.Default));
        if (await Task.WhenAny(work, gaveUp.Task).ConfigureAwait(false) != work)
        {
            _ = work.ContinueWith(task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
            return new(ActionOutcome.Failed, $"Stopped {entry.Operation.Name}", "It did not respond to being stopped, so the queue moved on without it. It may still be running; check its window or Task Manager, and check the app before trying again.");
        }
        try { return await work.ConfigureAwait(false); }
        catch (Exception ex) { return Failure(entry.Operation, ex, stop); }
    }
    private static PackageActionResult Failure(PackageOperation operation, Exception ex, CancellationTokenSource stop) =>
        ex is OperationCanceledException && stop.IsCancellationRequested
            ? new(ActionOutcome.Failed, $"Stopped {operation.Name}", operation.Fix is not null ? "This fix was stopped before it finished." : "It was stopped before it finished. An install or update that is stopped partway can leave the app incomplete; check Installed apps before trying again.")
            : new(ActionOutcome.Failed, $"Couldn’t {operation.Kind.ToString().ToLowerInvariant()} {operation.Name}", ex.Message);
    private static void Notify(Action<QueueEntry>? handlers, QueueEntry entry)
    { if (handlers is not null) foreach (Action<QueueEntry> handler in handlers.GetInvocationList()) { try { handler(entry); } catch { /* Observers cannot stall the queue. */ } } }
    private static void Notify(Action? handlers)
    { if (handlers is not null) foreach (Action handler in handlers.GetInvocationList()) { try { handler(); } catch { /* Keep the worker independent of UI observers. */ } } }
    private static TaskCompletionSource Completed() { var value = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); value.SetResult(); return value; }
    private sealed class OutputProgress(Action<CommandOutput> report) : IProgress<CommandOutput>
    { public void Report(CommandOutput value) => report(value); }
}

/// <param name="windowsApps">Reads what Windows lists as installed. Apps installed outside a package manager are invisible to
/// the manager's own list, so this stops an installer being run over an existing installation. Null skips the check.</param>
public sealed class PackageOperationExecutor(PackageService packages, InstallationService installations, IExecutableLocator locator, Func<InstalledInventory>? windowsApps = null)
{
    private async Task<InstalledApp?> FindOnWindowsAsync(Package package)
    {
        if (windowsApps is null) return null;
        try { return await Task.Run(() => windowsApps().Find(package)).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return null; }
    }

    public async Task<PackageActionResult> ExecuteAsync(PackageOperation operation, CancellationToken token)
        => await ExecuteAsync(operation, null, token).ConfigureAwait(false);

    public async Task<PackageActionResult> ExecuteAsync(PackageOperation operation, IProgress<CommandOutput>? progress, CancellationToken token)
    {
        var path = locator.Find(operation.Manager);
        if (path is null) return new(ActionOutcome.Failed, $"{operation.Name} could not be processed", "Its package manager is no longer installed. Check Overview.");
        var manager = new ManagerStatus(ManagerDefinition.All.Single(x => x.Kind == operation.Manager), ManagerHealth.Ready, "", path, "");
        if (operation.Kind != PackageOperationKind.Install)
            return await installations.RunAsync(manager, operation.InstalledPackage ?? throw new ArgumentException("Missing installed identity."),
                operation.Kind == PackageOperationKind.Update ? PackageAction.Update : PackageAction.Uninstall, token, progress, operation.Interactive).ConfigureAwait(false);
        var package = operation.InstallPackage ?? throw new ArgumentException("Missing install identity.");
        var before = await installations.ReadAsync(manager, checkUpdates: false, token).ConfigureAwait(false);
        if (before.Error is not null) return new(ActionOutcome.Failed, $"Couldn’t check {package.Name} before installing", before.Error);
        if (before.Packages.Any(x => PackageOperation.Resource(x.Manager, x.Id, x.Global) == operation.ResourceKey))
            return new(ActionOutcome.Completed, $"{package.Name} is already installed", "No installation was started.");
        if (await FindOnWindowsAsync(package).ConfigureAwait(false) is { } present)
            return new(ActionOutcome.Completed, $"{package.Name} is already installed",
                $"Windows lists it as “{present.Name}”{(present.Version.Length > 0 ? " " + present.Version : "")}, so it was installed outside {package.ManagerName}. No installation was started.");
        var result = await packages.InstallAsync(manager, package, token, progress, operation.Interactive).ConfigureAwait(false);
        var reason = InstallFailure.Explain(result);
        var detail = $"{package.ManagerName} · {package.Id}\nExit code: {result.ExitCode}\n" + (reason is null ? "" : reason + "\n") + PackageOutputParser.Clean(result.Output + "\n" + result.Error).Trim();
        if (result.ExitCode is not (0 or 1641 or 3010))
        {
            if (InstallFailure.NotAFailure(result.ExitCode) is { } soft)
                return soft == ActionOutcome.RestartRequired ? new(soft, $"{package.Name} installed; restart required", detail) : new(soft, $"{package.Name} is already installed", detail);
            // Some installers report an error code even though they finished. Trust what is on the computer, but say so.
            if (await FindOnWindowsAsync(package).ConfigureAwait(false) is not null)
            {
                packages.ClearSearchCache();
                return new(ActionOutcome.Unverified, $"{package.Name} is installed, but the installer reported an error", detail);
            }
            return new(ActionOutcome.Failed, $"Couldn’t install {package.Name}", detail);
        }
        packages.ClearSearchCache();
        if (result.ExitCode is 1641 or 3010) return new(ActionOutcome.RestartRequired, $"{package.Name} installed; restart required", detail);
        var after = await installations.ReadSettledAsync(manager, x => x.Packages.Any(p => PackageOperation.Resource(p.Manager, p.Id, p.Global) == operation.ResourceKey), token).ConfigureAwait(false);
        return after.Error is null && after.Packages.Any(x => PackageOperation.Resource(x.Manager, x.Id, x.Global) == operation.ResourceKey)
            ? new(ActionOutcome.Completed, $"{package.Name} installed", detail)
            : new(ActionOutcome.Unverified, $"{package.Name}: installation could not be verified", detail + "\n" + after.Error);
    }
}

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

/// <summary>One FIFO worker across all managers and pages. Only waiting work can be cancelled.</summary>
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
    public event Action? Changed;
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
        while (true)
        {
            QueueEntry? current;
            lock (gate)
            {
                current = entries.FirstOrDefault(x => x.State == QueueItemState.Waiting);
                if (current is null) { draining = false; idle.TrySetResult(); revision++; stateVersion++; }
                else { current = current with { State = QueueItemState.Running }; entries[entries.FindIndex(x => x.Id == current.Id)] = current; revision++; stateVersion++; }
            }
            Notify(Changed);
            if (current is null) return;
            PackageActionResult result;
            try { result = await execute(current.Operation, new OutputProgress(output => AppendOutput(current.Id, output)), CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { result = new(ActionOutcome.Failed, $"Couldn’t {current.Operation.Kind.ToString().ToLowerInvariant()} {current.Operation.Name}", ex.Message); }
            QueueEntry finished;
            lock (gate)
            {
                var index = entries.FindIndex(x => x.Id == current.Id);
                finished = entries[index] with { State = QueueItemState.Finished, Result = result };
                entries[index] = finished;
                completions++;
                // Keep a bounded recent outcome for inline state; durable outcomes live in Logs.
                while (entries.Count(x => x.State is QueueItemState.Finished or QueueItemState.Cancelled) > 100)
                    entries.RemoveAt(entries.FindIndex(x => x.State is QueueItemState.Finished or QueueItemState.Cancelled));
                revision++; stateVersion++;
            }
            if (Finished is { } handlers)
                foreach (Action<QueueEntry> handler in handlers.GetInvocationList()) { try { handler(finished); } catch { /* Observers cannot stall the queue. */ } }
            Notify(Changed);
        }
    }
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

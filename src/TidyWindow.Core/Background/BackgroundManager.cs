using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;

namespace TidyWindow.Next.Core;

/// <summary>
/// Makes one change on this PC, after checking again that the item may be changed, and reads it back to be sure it took.
/// A change that fails part-way is undone, so an item is never left half switched. Running it twice does the same as running it once.
/// With a <paramref name="vault"/>, as in the administrator helper, how each target looked before it was turned off is kept there and turning
/// it back on uses only that copy: the app's own record could have been edited by any program running as the ordinary account, and it must
/// never choose what the helper writes with administrator rights.
/// </summary>
public sealed class LocalBackgroundChanger(IBackgroundSystem system, IBackgroundWriter writer, IReadOnlyList<BackgroundItem> catalog, ProtectedStore? vault = null) : IBackgroundChanger
{
    /// <exception cref="UnauthorizedAccessException">The change needs administrator rights this process does not have.</exception>
    public BackgroundOutcome Change(BackgroundRequest request)
    {
        var entry = BackgroundScan.Find(system, catalog, request.Id);
        if (entry is null) return new(false, "That item is not on this PC.");
        if (!request.Enable && !entry.On) return new(true);
        // Already on everywhere: nothing to write, and a copy kept for it says nothing useful any more.
        if (request.Enable && entry.FullyOn) { Forget(request.Id); return new(true); }
        if (!request.Enable && !entry.CanChange) return new(false, entry.Lock);
        var before = Capture(entry);
        List<BackgroundSaved>? previous = null;
        // The copy is kept before the first write: without it there is no exact way back.
        if (!request.Enable && !Keep(request.Id, before, out var problem, out previous)) return new(false, problem);
        // Only the helper's own copy decides what comes back with administrator rights, never the one that arrived with the request.
        var saved = !request.Enable ? null : vault is null ? request.Saved : vault.Load<List<BackgroundSaved>>(request.Id);
        var notes = new List<string>();
        var done = new List<BackgroundTarget>();
        try
        {
            if (request.Enable) TurnOn(entry, saved, notes, done); else TurnOff(entry, notes);
        }
        catch (UnauthorizedAccessException)
        {
            // A service can allow a start-type change yet deny stopping. Put it back before the administrator helper retries,
            // or the retry sees it already off, records nothing to restore, and leaves it running.
            if (!request.Enable) { Undo(entry, before); Unkeep(request.Id, previous); }
            throw;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Security.SecurityException or COMException or Win32Exception or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            if (!request.Enable) { Undo(entry, before); Unkeep(request.Id, previous); }
            return new(false, ex.Message);
        }
        var after = BackgroundScan.Find(system, catalog, request.Id);
        // Turning off is checked as a whole; turning on is checked for every target that was switched, so one that did not take is not hidden by the others.
        var took = after is not null && (request.Enable ? done.All(t => after.Targets.First(s => s.Target == t).On) : !after.On);
        if (!took)
        {
            if (!request.Enable) { Undo(entry, before); Unkeep(request.Id, previous); }
            return new(false, "Windows did not accept the change.");
        }
        if (request.Enable) Forget(request.Id);
        return new(true, null, notes.Count == 0 ? null : string.Join(" ", notes), before);
    }

    /// <summary>Keeps the copy the item will be turned back on from. A target found already off keeps what an earlier copy saved for it when it was still on.</summary>
    private bool Keep(string id, List<BackgroundSaved> before, out string problem, out List<BackgroundSaved>? previous)
    {
        problem = ""; previous = null;
        if (vault is null) return true;
        try
        {
            previous = vault.Load<List<BackgroundSaved>>(id);
            vault.Save(id, Merge(before, previous));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problem = "The copy needed to turn this item back on could not be kept, so it was not turned off. " + ex.Message;
            return false;
        }
    }

    /// <summary>What is saved now, except that a target found already off keeps what an earlier copy saved for it when it was still on.</summary>
    internal static List<BackgroundSaved> Merge(List<BackgroundSaved> now, List<BackgroundSaved>? earlier) =>
        now.Select(s => !s.WasOn && earlier?.FirstOrDefault(o => o.Kind == s.Kind && string.Equals(o.Name, s.Name, StringComparison.OrdinalIgnoreCase) && o.WasOn) is { } kept ? kept : s).ToList();

    /// <summary>Puts the copy back the way it was before a change that did not go through.</summary>
    private void Unkeep(string id, List<BackgroundSaved>? previous)
    {
        if (vault is null) return;
        try { if (previous is null) vault.Delete(id); else vault.Save(id, previous); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* A copy that lingers only ever puts things back the way they were. */ }
    }

    private void Forget(string id)
    {
        try { vault?.Delete(id); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* The item is on; a copy that lingers does no harm. */ }
    }

    private List<BackgroundSaved> Capture(BackgroundEntry entry)
    {
        var saved = new List<BackgroundSaved>();
        foreach (var state in entry.Targets.Where(x => x.Present))
        {
            var target = state.Target;
            switch (target.Kind)
            {
                case BackgroundTargetKind.Service:
                    var service = system.Service(target.Name);
                    saved.Add(new(target.Kind, target.Name, state.On, service?.Start ?? target.DefaultStart, service?.Delayed ?? false));
                    break;
                case BackgroundTargetKind.Task:
                    saved.Add(new(target.Kind, target.Name, state.On));
                    break;
                default:
                    saved.Add(new(target.Kind, target.Name, state.On, Value: system.Policy(target.Key, target.Name)));
                    break;
            }
        }
        return saved;
    }

    private void TurnOff(BackgroundEntry entry, List<string> notes)
    {
        foreach (var state in entry.Targets.Where(x => x.Present && x.On))
        {
            var target = state.Target;
            switch (target.Kind)
            {
                case BackgroundTargetKind.Service:
                    // "Manual" keeps a service available for the things that ask for it; "disabled" is only for services nothing should start.
                    writer.SetServiceStart(target.Name, target.Off == "manual" ? 3 : 4, false);
                    if (state.Running && !writer.StopService(target.Name)) notes.Add($"{target.Name} is still running; it stops the next time you restart Windows.");
                    break;
                case BackgroundTargetKind.Task:
                    writer.SetTask(target.Name, false);
                    break;
                default:
                    writer.SetPolicy(target.Key, target.Name, target.Block);
                    notes.Add("Windows applies this the next time you sign in or restart.");
                    break;
            }
        }
    }

    /// <param name="done">The targets that were switched, for the read-back. A target that is already on is left alone, whatever a copy says about it.</param>
    private void TurnOn(BackgroundEntry entry, IReadOnlyList<BackgroundSaved>? saved, List<string> notes, List<BackgroundTarget> done)
    {
        foreach (var state in entry.Targets.Where(x => x.Present && !x.On))
        {
            var target = state.Target;
            var was = saved?.FirstOrDefault(x => x.Kind == target.Kind && string.Equals(x.Name, target.Name, StringComparison.OrdinalIgnoreCase));
            // Something that was already off before TidyWindow touched it stays off.
            if (was is { WasOn: false }) continue;
            switch (target.Kind)
            {
                case BackgroundTargetKind.Service:
                    // Only a start type this page itself sets; anything else in a copy falls back to how Windows ships the service.
                    var start = was is { Start: 2 or 3 } ? was.Start : target.DefaultStart;
                    var delayed = was?.Delayed ?? target.DefaultDelayed;
                    writer.SetServiceStart(target.Name, start, start == 2 && delayed);
                    if (start == 2 && !state.Running) { try { writer.StartService(target.Name); } catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { notes.Add($"{target.Name} starts the next time you restart Windows."); } }
                    break;
                case BackgroundTargetKind.Task:
                    writer.SetTask(target.Name, true);
                    break;
                default:
                    // A copy may only bring back a value Windows documents for the setting; anything else is treated as "was not set".
                    writer.SetPolicy(target.Key, target.Name, was?.Value is { } value && target.Values.Contains(value) ? value : null);
                    break;
            }
            done.Add(target);
        }
    }

    /// <summary>Puts every target back the way it was, ignoring anything that will not go.</summary>
    private void Undo(BackgroundEntry entry, IReadOnlyList<BackgroundSaved> before)
    {
        try { TurnOn(BackgroundScan.Find(system, catalog, entry.Id) ?? entry, before, [], []); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or System.Security.SecurityException or COMException or Win32Exception or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
    }
}

/// <summary>What TidyWindow has turned off, kept on disk so it can be turned back on later.</summary>
public sealed class BackgroundHistory(string folder)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly object gate = new();
    private string FilePath => Path.Combine(folder, "turned-off.json");

    public IReadOnlyList<BackgroundChange> All()
    {
        lock (gate) return Load().OrderByDescending(x => x.When).ToList();
    }

    public BackgroundChange? Get(string id)
    {
        lock (gate) return Load().FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Records a change. If the item was already recorded, what was saved earlier for a target that was already on is kept.</summary>
    public void Add(BackgroundChange change)
    {
        lock (gate)
        {
            var list = Load();
            var old = list.FirstOrDefault(x => string.Equals(x.Id, change.Id, StringComparison.OrdinalIgnoreCase));
            list.RemoveAll(x => string.Equals(x.Id, change.Id, StringComparison.OrdinalIgnoreCase));
            var saved = change.Saved.Select(s => !s.WasOn && old?.Saved.FirstOrDefault(o => o.Kind == s.Kind && o.Name == s.Name && o.WasOn) is { } earlier ? earlier : s).ToList();
            list.Add(change with { Saved = saved });
            Save(list);
        }
    }

    public void Remove(string id)
    {
        lock (gate)
        {
            var list = Load();
            if (list.RemoveAll(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase)) > 0) Save(list);
        }
    }

    private List<BackgroundChange> Load()
    {
        try { return JsonSerializer.Deserialize<List<BackgroundChange>>(System.IO.File.ReadAllText(FilePath), Json) ?? []; }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return []; }
    }

    private void Save(List<BackgroundChange> list)
    {
        Directory.CreateDirectory(folder);
        var temp = FilePath + ".tmp";
        System.IO.File.WriteAllText(temp, JsonSerializer.Serialize(list, Json));
        System.IO.File.Move(temp, FilePath, overwrite: true);
    }
}

/// <param name="Declined">The person said no to the administrator prompt part-way, so the items after the last result were not tried.</param>
public sealed record BackgroundBatch(IReadOnlyList<(BackgroundEntry Entry, BackgroundOutcome Outcome)> Results, bool Declined);

/// <summary>Chooses who makes a change: this process when it can, the administrator helper (after one permission prompt) when it must.</summary>
public sealed class BackgroundManager(IBackgroundChanger user, IBackgroundChanger machine, Func<Task> unlock, BackgroundHistory history, Func<DateTimeOffset>? clock = null)
{
    private readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.Now);

    public BackgroundHistory History => history;

    /// <exception cref="OperationCanceledException">The person declined the administrator prompt.</exception>
    public async Task<BackgroundOutcome> SetAsync(BackgroundEntry entry, bool enable)
    {
        if (!entry.CanChange) return new(false, entry.Lock ?? "That item cannot be changed.");
        var request = new BackgroundRequest(entry.Id, enable, enable ? history.Get(entry.Id)?.Saved : null);
        BackgroundOutcome outcome;
        try { outcome = await Task.Run(() => user.Change(request)).ConfigureAwait(false); }
        catch (UnauthorizedAccessException)
        {
            await unlock().ConfigureAwait(false);
            try { outcome = await Task.Run(() => machine.Change(request)).ConfigureAwait(false); }
            catch (IOException)
            {
                // The helper leaves by itself after five idle minutes, and the app only finds out when it next uses it. Ask again, once.
                await unlock().ConfigureAwait(false);
                outcome = await Task.Run(() => machine.Change(request)).ConfigureAwait(false);
            }
        }
        if (outcome.Ok)
        {
            if (enable) history.Remove(entry.Id);
            else if (outcome.Before is { Count: > 0 } before) history.Add(new(entry.Id, entry.Item.Name, now(), before));
        }
        return outcome;
    }

    /// <summary>
    /// Changes several items one after another. The administrator prompt comes at most once, because the helper stays open for the rest.
    /// An item that cannot be changed is reported and skipped; if the person declines the prompt, the rest are not attempted.
    /// </summary>
    public async Task<BackgroundBatch> SetManyAsync(IEnumerable<BackgroundEntry> entries, bool enable)
    {
        var results = new List<(BackgroundEntry, BackgroundOutcome)>();
        foreach (var entry in entries)
        {
            try { results.Add((entry, await SetAsync(entry, enable).ConfigureAwait(false))); }
            catch (OperationCanceledException) { return new(results, true); }
            // Whatever went wrong with one item is that item's failure; the batch carries on and the page stays up.
            catch (Exception ex) { results.Add((entry, new(false, ex.Message))); }
        }
        return new(results, false);
    }

    /// <summary>Turns back on everything TidyWindow turned off. Items that are gone from this PC, or that something else already turned back on, are forgotten.</summary>
    public async Task<IReadOnlyList<(BackgroundChange Change, BackgroundOutcome Outcome)>> RestoreAllAsync(IBackgroundSystem system, IReadOnlyList<BackgroundItem> catalog)
    {
        var results = new List<(BackgroundChange, BackgroundOutcome)>();
        foreach (var change in history.All())
        {
            var entry = await Task.Run(() => BackgroundScan.Find(system, catalog, change.Id)).ConfigureAwait(false);
            if (entry is null || entry.FullyOn) { history.Remove(change.Id); continue; }
            results.Add((change, await SetAsync(entry, true).ConfigureAwait(false)));
        }
        return results;
    }
}

public sealed record BackgroundMessage(string Op, BackgroundRequest? Request = null);

/// <summary>
/// The elevated side of the background page. It rebuilds the item from the catalog and from the PC itself and applies the same rules as the app,
/// so it only ever switches things the app would also switch. It leaves by itself when idle.
/// </summary>
public static class BackgroundHost
{
    public const string Command = "--background";
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public static async Task<int> RunAsync(string[] args, IBackgroundChanger? changer = null, Func<bool>? isAdministrator = null, TimeSpan? idle = null)
    {
        if (args.Length < 2 || args[0] != Command) return 64;
        if (changer is null)
        {
            if (!OperatingSystem.IsWindows()) return 66;
            changer = new LocalBackgroundChanger(new WindowsBackgroundSystem(), new WindowsBackgroundWriter(), BackgroundCatalog.Load(), new ProtectedStore("quiet"));
        }
        isAdministrator ??= () => OperatingSystem.IsWindows() && new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        using var pipe = await HelperPipe.TryConnectAsync(args[1]).ConfigureAwait(false);
        if (pipe is null) return 65;
        var reader = new PipeLineReader(pipe);
        var writer = new StreamWriter(pipe) { AutoFlush = true };

        async Task Reply(BackgroundOutcome reply)
        {
            try { await writer.WriteLineAsync(JsonSerializer.Serialize(reply, Json)).ConfigureAwait(false); }
            catch (IOException) { }
        }

        var admin = false;
        while (true)
        {
            string? line;
            using (var timeout = new CancellationTokenSource(idle ?? TimeSpan.FromMinutes(5)))
            {
                try { line = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false); }
                catch (Exception ex) when (ex is OperationCanceledException or IOException) { return 0; }
            }
            if (line is null) return 0;
            BackgroundMessage? message;
            try { message = JsonSerializer.Deserialize<BackgroundMessage>(line, Json); }
            catch (JsonException) { message = null; }
            if (message is null) { await Reply(new(false, "That request was not understood.")).ConfigureAwait(false); continue; }

            switch (message.Op)
            {
                case "bye": return 0;
                case "hello": admin = isAdministrator(); await Reply(new(admin)).ConfigureAwait(false); break;
                case "set" when admin && message.Request is { } request:
                    BackgroundOutcome outcome;
                    try { outcome = changer.Change(request); }
                    catch (UnauthorizedAccessException) { outcome = new(false, "Windows refused the change even with administrator permission. This may be a service that belongs to an app package."); }
                    await Reply(outcome).ConfigureAwait(false);
                    break;
                default: await Reply(new(false, "That request was not understood.")).ConfigureAwait(false); break;
            }
        }
    }
}

/// <summary>The app side: starts the helper through the Windows permission prompt and sends it each change over a private pipe.</summary>
/// <param name="replyTimeout">How long one change may take in the helper. Stopping a service waits ten seconds at most and an item has a handful of targets, so a helper that is quiet for the default two minutes is stuck and is let go.</param>
public sealed class BackgroundSession(Func<string, Task> launch, TimeSpan? replyTimeout = null) : IBackgroundChanger, IDisposable
{
    public static readonly TimeSpan DefaultReplyTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ByeTimeout = TimeSpan.FromMilliseconds(500);
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private readonly TimeSpan timeout = replyTimeout ?? DefaultReplyTimeout;
    // One change at a time goes through the gate. The pipe itself sits behind "state", which is never held while waiting on the pipe, so
    // closing the session never waits behind a change that is still in flight: it ends that change instead.
    private readonly object gate = new(), state = new();
    private NamedPipeServerStream? pipe;
    private PipeLineReader? reader;
    private StreamWriter? writer;
    private CancellationTokenSource? waiting;
    private bool interrupted;

    public bool IsOpen { get { lock (state) return pipe is { IsConnected: true }; } }

    public static BackgroundSession Elevated(string helperPath) => new(name => HelperPipe.LaunchElevated(helperPath, BackgroundHost.Command, name));

    /// <summary>Asks for permission and connects. Throws <see cref="OperationCanceledException"/> if the person says no.</summary>
    public async Task OpenAsync(CancellationToken cancellationToken = default)
    {
        if (IsOpen) return;
        Close();
        var name = HelperPipe.NewName("background");
        var server = HelperPipe.CreateServer(name);
        try
        {
            var launched = Task.Run(() => launch(name), CancellationToken.None);
            var connecting = server.WaitForConnectionAsync(cancellationToken);
            if (await Task.WhenAny(connecting, launched).ConfigureAwait(false) != connecting)
            {
                await launched.ConfigureAwait(false);
                throw new IOException("The administrator helper stopped before it could connect.");
            }
            await connecting.ConfigureAwait(false);
            var input = new PipeLineReader(server);
            var output = new StreamWriter(server) { AutoFlush = true };
            await output.WriteLineAsync(JsonSerializer.Serialize(new BackgroundMessage("hello"), Json)).ConfigureAwait(false);
            var reply = JsonSerializer.Deserialize<BackgroundOutcome>(await input.ReadLineAsync().ConfigureAwait(false) ?? "", Json);
            if (reply is not { Ok: true }) throw new UnauthorizedAccessException("The helper did not get administrator permission.");
            lock (state) { pipe = server; reader = input; writer = output; interrupted = false; }
        }
        catch
        {
            server.Dispose();
            throw;
        }
    }

    /// <exception cref="InvalidOperationException">The helper is not running.</exception>
    /// <exception cref="IOException">The helper stopped, or did not answer in time and was let go.</exception>
    public BackgroundOutcome Change(BackgroundRequest request)
    {
        lock (gate)
        {
            NamedPipeServerStream current; PipeLineReader input; StreamWriter output;
            using var limit = new CancellationTokenSource(timeout);
            lock (state)
            {
                if (pipe is not { IsConnected: true } || reader is null || writer is null) throw new InvalidOperationException("The administrator helper is not running.");
                (current, input, output) = (pipe, reader, writer);
                waiting = limit; interrupted = false;
            }
            BackgroundOutcome? reply;
            try
            {
                // The pipe only takes a line once the helper reads it, so the write is bounded by the same clock as the reply.
                output.WriteLineAsync(JsonSerializer.Serialize(new BackgroundMessage("set", request), Json)).WaitAsync(limit.Token).GetAwaiter().GetResult();
                reply = JsonSerializer.Deserialize<BackgroundOutcome>(input.ReadLineAsync(limit.Token).GetAwaiter().GetResult() ?? "", Json);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or JsonException or ObjectDisposedException)
            {
                bool timedOut;
                lock (state) timedOut = limit.IsCancellationRequested && !interrupted;
                Drop(current);
                throw new IOException(timedOut ? "The administrator helper did not answer in time, so it was closed. Try again." : "The administrator helper stopped. Try again.");
            }
            finally { lock (state) if (waiting == limit) waiting = null; }
            if (reply is null) { Drop(current); throw new IOException("The administrator helper stopped. Try again."); }
            return reply;
        }
    }

    /// <summary>Lets go of a pipe whose helper stopped, or stopped answering. Closing the pipe is what ends the helper: its next read finds the pipe gone.</summary>
    private void Drop(NamedPipeServerStream current)
    {
        lock (state) if (ReferenceEquals(pipe, current)) { pipe = null; reader = null; writer = null; }
        try { current.Dispose(); } catch (IOException) { }
    }

    /// <summary>Ends the session without waiting for the helper. A change still in flight fails with "the helper stopped".</summary>
    public void Close()
    {
        NamedPipeServerStream? current; StreamWriter? output; bool busy;
        lock (state)
        {
            current = pipe; output = writer;
            pipe = null; reader = null; writer = null;
            busy = waiting is not null;
            if (waiting is { } pending) { interrupted = true; pending.Cancel(); }
        }
        if (current is null) return;
        // A helper that is between changes is told to leave. One busy with a change is not reading, so it is simply cut off.
        if (!busy && output is not null) SayBye(output);
        try { current.Dispose(); } catch (IOException) { }
    }

    private static void SayBye(StreamWriter output)
    {
        try
        {
            var bye = output.WriteLineAsync(JsonSerializer.Serialize(new BackgroundMessage("bye"), Json));
            // Bounded: a helper that has stopped reading is let go either way, and closing the pipe ends the write.
            if (!bye.Wait(ByeTimeout)) bye.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        }
        catch (Exception ex) when (ex is AggregateException or IOException or ObjectDisposedException or InvalidOperationException) { }
    }

    public void Dispose() => Close();
}

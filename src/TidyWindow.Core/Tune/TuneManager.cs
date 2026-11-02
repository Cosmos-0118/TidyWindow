using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;

namespace TidyWindow.Next.Core;

/// <summary>
/// Makes one change on this PC, after checking again that the item may be changed, and reads it back to be sure it took.
/// A change that fails part-way is undone, so an item is never left half changed. Running it twice does the same as running it once.
/// When <paramref name="only"/> is set, items of the other scope are refused: the administrator helper never writes to a user's registry.
/// With a <paramref name="vault"/>, as in the administrator helper, the values an item replaces are kept there and putting it back uses only
/// that copy: the app's own record could have been edited by any program running as the ordinary account, and it must never choose what
/// the helper writes with administrator rights.
/// </summary>
public sealed class LocalTuneChanger(ITuneSystem system, ITuneWriter writer, IReadOnlyList<TuneItem> catalog, TuneScope? only = null, ProtectedStore? vault = null) : ITuneChanger
{
    /// <exception cref="UnauthorizedAccessException">The change needs administrator rights this process does not have.</exception>
    public TuneOutcome Change(TuneRequest request)
    {
        var entry = TuneScan.Find(system, catalog, request.Id);
        if (entry is null) return new(false, "That setting is not on this PC.");
        if (only is { } scope && entry.Item.Scope != scope) return new(false, "That setting cannot be changed from here.");
        // Nothing to write: already applied, or nothing of it is applied (then a copy kept for it says nothing useful any more).
        if (request.Apply ? entry.Applied : !entry.Targets.Any(x => x.Applied)) { if (!request.Apply) Forget(request.Id); return new(true); }
        if (!entry.CanChange) return new(false, entry.Lock);
        var before = Capture(entry);
        List<TuneSaved>? previous = null;
        // The copy is kept before the first write: without it there is no exact way back.
        if (request.Apply && !Keep(request.Id, before, out var problem, out previous)) return new(false, problem);
        // Only the helper's own copy decides what goes back with administrator rights, never the one that arrived with the request.
        var saved = request.Apply ? null : vault is null ? request.Saved : vault.Load<List<TuneSaved>>(request.Id);
        var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var done = new List<TuneTarget>();
        try
        {
            if (request.Apply) Apply(entry, written); else Restore(entry, saved, written, done);
        }
        catch (UnauthorizedAccessException) { Undo(entry, before, written); if (request.Apply) Unkeep(request.Id, previous); throw; }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Security.SecurityException)
        {
            Undo(entry, before, written);
            if (request.Apply) Unkeep(request.Id, previous);
            return new(false, ex.Message);
        }
        var after = TuneScan.Find(system, catalog, request.Id);
        var took = after is not null && (request.Apply ? after.Applied : done.All(t => !after.Targets.First(s => s.Target == t).Applied));
        if (!took)
        {
            Undo(entry, before, written);
            if (request.Apply) Unkeep(request.Id, previous);
            return new(false, "Windows did not accept the change.");
        }
        // Put back in full: the copy has done its work. Whatever stays applied was already so before TidyWindow touched it.
        if (!request.Apply) Forget(request.Id);
        return new(true, null, Note(entry.Item), before);
    }

    /// <summary>Keeps the copy the item will be put back from. A setting found already applied keeps the original an earlier copy saved for it.</summary>
    private bool Keep(string id, List<TuneSaved> before, out string problem, out List<TuneSaved>? previous)
    {
        problem = ""; previous = null;
        if (vault is null) return true;
        try
        {
            previous = vault.Load<List<TuneSaved>>(id);
            vault.Save(id, Merge(before, previous));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problem = "The copy needed to put this setting back could not be kept, so it was not changed. " + ex.Message;
            return false;
        }
    }

    /// <summary>What is saved now, except that a setting found already applied keeps the original an earlier copy saved for it.</summary>
    internal static List<TuneSaved> Merge(List<TuneSaved> now, List<TuneSaved>? earlier) =>
        now.Select(s => s.WasApplied && earlier?.FirstOrDefault(o => string.Equals(o.Key, s.Key, StringComparison.OrdinalIgnoreCase) && string.Equals(o.Name, s.Name, StringComparison.OrdinalIgnoreCase) && !o.WasApplied) is { } kept ? kept : s).ToList();

    /// <summary>Puts the copy back the way it was before a change that did not go through.</summary>
    private void Unkeep(string id, List<TuneSaved>? previous)
    {
        if (vault is null) return;
        try { if (previous is null) vault.Delete(id); else vault.Save(id, previous); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* A copy that lingers only ever puts things back the way they were. */ }
    }

    private void Forget(string id)
    {
        try { vault?.Delete(id); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* The setting is back; a copy that lingers does no harm. */ }
    }

    private static string? Note(TuneItem item) => item.Takes switch
    {
        TuneEffect.Explorer => "File Explorer picks this up when it restarts.",
        TuneEffect.SignOut => "Windows applies this the next time you sign in.",
        TuneEffect.Restart => "Windows applies this the next time you restart.",
        _ => null
    };

    private static List<TuneSaved> Capture(TuneEntry entry) =>
        entry.Targets.Select(s => new TuneSaved(s.Target.Key, s.Target.Name, s.Current is not null, s.Current?.Kind ?? s.Target.Kind, s.Current?.Text ?? "", s.Applied)).ToList();

    private static string Mark(TuneTarget target) => target.Key + "|" + target.Name;

    private void Apply(TuneEntry entry, HashSet<string> written)
    {
        foreach (var state in entry.Targets.Where(x => !x.Applied))
        {
            // A value of a kind this page does not write is never replaced, whatever the rest of the item looks like. The scan locks such an item; this is the second check.
            if (state.Current?.Kind == TuneKind.Other) throw new InvalidOperationException("This setting holds a kind of value this page does not change.");
            writer.Set(entry.Item.Scope, state.Target.Key, state.Target.Name, state.Target.Kind, TuneRules.Wanted(state.Target, state.Current));
            written.Add(Mark(state.Target));
        }
    }

    private void Restore(TuneEntry entry, IReadOnlyList<TuneSaved>? saved, HashSet<string> written, List<TuneTarget> done)
    {
        foreach (var state in entry.Targets.Where(x => x.Applied))
        {
            var target = state.Target;
            var was = saved?.FirstOrDefault(x => string.Equals(x.Key, target.Key, StringComparison.OrdinalIgnoreCase) && string.Equals(x.Name, target.Name, StringComparison.OrdinalIgnoreCase));
            // A value that already had the tweaked setting before TidyWindow touched it stays as it is.
            if (was is { WasApplied: true }) continue;
            if (was is not null) Put(entry.Item.Scope, target, was.WasSet, was.Kind, was.Text);
            else Put(entry.Item.Scope, target, target.Normal is not null, target.Kind, target.Normal ?? "");
            written.Add(Mark(target));
            done.Add(target);
        }
    }

    private void Put(TuneScope scope, TuneTarget target, bool set, TuneKind kind, string text)
    {
        if (set)
        {
            var actual = kind == TuneKind.Other ? target.Kind : kind;
            // A saved copy names the kind of value it held. One of another kind is not something this page wrote, so it is not written back:
            // the copy came from a file any program running as the user can edit. A number has to be one the registry can hold, too.
            if (actual != target.Kind) throw new InvalidOperationException("The saved value is not the kind this setting takes, so it was not written.");
            if (actual == TuneKind.Dword && !uint.TryParse(text, out _)) throw new InvalidOperationException("The saved value is not a number this setting can hold, so it was not written.");
            writer.Set(scope, target.Key, target.Name, actual, text);
            return;
        }
        writer.Delete(scope, target.Key, target.Name);
        if (target.RemoveKey.Length > 0) writer.DeleteKey(scope, target.RemoveKey);
    }

    /// <summary>Puts every value this change wrote back the way it was, ignoring anything that will not go.</summary>
    private void Undo(TuneEntry entry, IReadOnlyList<TuneSaved> before, HashSet<string> written)
    {
        foreach (var state in entry.Targets.Where(x => written.Contains(Mark(x.Target))))
        {
            var was = before.First(x => string.Equals(x.Key, state.Target.Key, StringComparison.OrdinalIgnoreCase) && string.Equals(x.Name, state.Target.Name, StringComparison.OrdinalIgnoreCase));
            try { Put(entry.Item.Scope, state.Target, was.WasSet, was.Kind, was.Text); }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or System.Security.SecurityException) { }
        }
    }
}

/// <summary>What TidyWindow has changed, kept on disk so it can be put back later.</summary>
public sealed class TuneHistory(string folder)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly object gate = new();
    private string FilePath => Path.Combine(folder, "changed.json");

    public IReadOnlyList<TuneChange> All()
    {
        lock (gate) return Load().OrderByDescending(x => x.When).ToList();
    }

    public TuneChange? Get(string id)
    {
        lock (gate) return Load().FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Records a change. If the item was already recorded, the original value saved earlier is kept for a setting that this change found already applied.</summary>
    public void Add(TuneChange change)
    {
        lock (gate)
        {
            var list = Load();
            var old = list.FirstOrDefault(x => string.Equals(x.Id, change.Id, StringComparison.OrdinalIgnoreCase));
            list.RemoveAll(x => string.Equals(x.Id, change.Id, StringComparison.OrdinalIgnoreCase));
            var saved = change.Saved.Select(s => s.WasApplied && old?.Saved.FirstOrDefault(o => o.Key == s.Key && o.Name == s.Name && !o.WasApplied) is { } earlier ? earlier : s).ToList();
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

    private List<TuneChange> Load()
    {
        try { return JsonSerializer.Deserialize<List<TuneChange>>(System.IO.File.ReadAllText(FilePath), Json) ?? []; }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return []; }
    }

    private void Save(List<TuneChange> list)
    {
        Directory.CreateDirectory(folder);
        var temp = FilePath + ".tmp";
        System.IO.File.WriteAllText(temp, JsonSerializer.Serialize(list, Json));
        System.IO.File.Move(temp, FilePath, overwrite: true);
    }
}

/// <summary>Chooses who makes a change: this process for the user's own settings, and for machine settings this process when it can or the administrator helper (after one permission prompt) when it must.</summary>
public sealed class TuneManager(ITuneChanger user, ITuneChanger machine, Func<Task> unlock, TuneHistory history, Func<DateTimeOffset>? clock = null)
{
    private readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.Now);

    public TuneHistory History => history;

    /// <exception cref="OperationCanceledException">The person declined the administrator prompt.</exception>
    public async Task<TuneOutcome> SetAsync(TuneEntry entry, bool apply)
    {
        if (!entry.CanChange) return new(false, entry.Lock ?? "That setting cannot be changed.");
        var request = new TuneRequest(entry.Id, apply, apply ? null : history.Get(entry.Id)?.Saved);
        TuneOutcome outcome;
        try { outcome = await Task.Run(() => user.Change(request)).ConfigureAwait(false); }
        catch (UnauthorizedAccessException) when (entry.Item.Scope == TuneScope.Machine)
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
        catch (UnauthorizedAccessException) { outcome = new(false, "Windows does not let this app change that setting."); }
        if (outcome.Ok)
        {
            if (!apply) history.Remove(entry.Id);
            else if (outcome.Before is { Count: > 0 } before) history.Add(new(entry.Id, entry.Item.Name, now(), before));
        }
        return outcome;
    }

    /// <summary>Puts back everything TidyWindow changed. Items that are gone from this PC, or that something else already put back, are forgotten.</summary>
    public async Task<IReadOnlyList<(TuneChange Change, TuneOutcome Outcome)>> RestoreAllAsync(ITuneSystem system, IReadOnlyList<TuneItem> catalog)
    {
        var results = new List<(TuneChange, TuneOutcome)>();
        foreach (var change in history.All())
        {
            var entry = await Task.Run(() => TuneScan.Find(system, catalog, change.Id)).ConfigureAwait(false);
            if (entry is null || !entry.Targets.Any(x => x.Applied)) { history.Remove(change.Id); continue; }
            results.Add((change, await SetAsync(entry, false).ConfigureAwait(false)));
        }
        return results;
    }
}

public sealed record TuneMessage(string Op, TuneRequest? Request = null);

/// <summary>
/// The elevated side of the Tune page. It rebuilds the item from the catalog and from the PC itself and applies the same rules as the app,
/// so it only ever changes machine settings the app would also change. It leaves by itself when idle.
/// </summary>
public static class TuneHost
{
    public const string Command = "--tune";
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public static async Task<int> RunAsync(string[] args, ITuneChanger? changer = null, Func<bool>? isAdministrator = null, TimeSpan? idle = null)
    {
        if (args.Length < 2 || args[0] != Command) return 64;
        if (changer is null)
        {
            if (!OperatingSystem.IsWindows()) return 66;
            changer = new LocalTuneChanger(new WindowsTuneSystem(), new WindowsTuneWriter(), TuneCatalog.Load(), TuneScope.Machine, new ProtectedStore("tune"));
        }
        isAdministrator ??= () => OperatingSystem.IsWindows() && new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        using var pipe = await HelperPipe.TryConnectAsync(args[1]).ConfigureAwait(false);
        if (pipe is null) return 65;
        var reader = new PipeLineReader(pipe);
        var writer = new StreamWriter(pipe) { AutoFlush = true };

        async Task Reply(TuneOutcome reply)
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
            TuneMessage? message;
            try { message = JsonSerializer.Deserialize<TuneMessage>(line, Json); }
            catch (JsonException) { message = null; }
            if (message is null) { await Reply(new(false, "That request was not understood.")).ConfigureAwait(false); continue; }

            switch (message.Op)
            {
                case "bye": return 0;
                case "hello": admin = isAdministrator(); await Reply(new(admin)).ConfigureAwait(false); break;
                case "set" when admin && message.Request is { } request:
                    TuneOutcome outcome;
                    try { outcome = changer.Change(request); }
                    catch (UnauthorizedAccessException ex) { outcome = new(false, ex.Message); }
                    await Reply(outcome).ConfigureAwait(false);
                    break;
                default: await Reply(new(false, "That request was not understood.")).ConfigureAwait(false); break;
            }
        }
    }
}

/// <summary>The app side: starts the helper through the Windows permission prompt and sends it each change over a private pipe.</summary>
/// <param name="replyTimeout">How long one change may take in the helper. A registry write is immediate, so a helper that is quiet for the default two minutes is stuck and is let go.</param>
public sealed class TuneSession(Func<string, Task> launch, TimeSpan? replyTimeout = null) : ITuneChanger, IDisposable
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

    public static TuneSession Elevated(string helperPath) => new(name => HelperPipe.LaunchElevated(helperPath, TuneHost.Command, name));

    /// <summary>Asks for permission and connects. Throws <see cref="OperationCanceledException"/> if the person says no.</summary>
    public async Task OpenAsync(CancellationToken cancellationToken = default)
    {
        if (IsOpen) return;
        Close();
        var name = HelperPipe.NewName("tune");
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
            await output.WriteLineAsync(JsonSerializer.Serialize(new TuneMessage("hello"), Json)).ConfigureAwait(false);
            var reply = JsonSerializer.Deserialize<TuneOutcome>(await input.ReadLineAsync().ConfigureAwait(false) ?? "", Json);
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
    public TuneOutcome Change(TuneRequest request)
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
            TuneOutcome? reply;
            try
            {
                // The pipe only takes a line once the helper reads it, so the write is bounded by the same clock as the reply.
                output.WriteLineAsync(JsonSerializer.Serialize(new TuneMessage("set", request), Json)).WaitAsync(limit.Token).GetAwaiter().GetResult();
                reply = JsonSerializer.Deserialize<TuneOutcome>(input.ReadLineAsync(limit.Token).GetAwaiter().GetResult() ?? "", Json);
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
            var bye = output.WriteLineAsync(JsonSerializer.Serialize(new TuneMessage("bye"), Json));
            // Bounded: a helper that has stopped reading is let go either way, and closing the pipe ends the write.
            if (!bye.Wait(ByeTimeout)) bye.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        }
        catch (Exception ex) when (ex is AggregateException or IOException or ObjectDisposedException or InvalidOperationException) { }
    }

    public void Dispose() => Close();
}

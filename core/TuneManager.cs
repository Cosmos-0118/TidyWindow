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
/// </summary>
public sealed class LocalTuneChanger(ITuneSystem system, ITuneWriter writer, IReadOnlyList<TuneItem> catalog, TuneScope? only = null) : ITuneChanger
{
    /// <exception cref="UnauthorizedAccessException">The change needs administrator rights this process does not have.</exception>
    public TuneOutcome Change(TuneRequest request)
    {
        var entry = TuneScan.Find(system, catalog, request.Id);
        if (entry is null) return new(false, "That setting is not on this PC.");
        if (only is { } scope && entry.Item.Scope != scope) return new(false, "That setting cannot be changed from here.");
        if (request.Apply ? entry.Applied : !entry.Targets.Any(x => x.Applied)) return new(true);
        if (!entry.CanChange) return new(false, entry.Lock);
        var before = Capture(entry);
        var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var done = new List<TuneTarget>();
        try
        {
            if (request.Apply) Apply(entry, written); else Restore(entry, request.Saved, written, done);
        }
        catch (UnauthorizedAccessException) { Undo(entry, before, written); throw; }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Security.SecurityException)
        {
            Undo(entry, before, written);
            return new(false, ex.Message);
        }
        var after = TuneScan.Find(system, catalog, request.Id);
        var took = after is not null && (request.Apply ? after.Applied : done.All(t => !after.Targets.First(s => s.Target == t).Applied));
        if (!took)
        {
            Undo(entry, before, written);
            return new(false, "Windows did not accept the change.");
        }
        return new(true, null, Note(entry.Item), before);
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
        if (set) { writer.Set(scope, target.Key, target.Name, kind == TuneKind.Other ? target.Kind : kind, text); return; }
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
            changer = new LocalTuneChanger(new WindowsTuneSystem(), new WindowsTuneWriter(), TuneCatalog.Load(), TuneScope.Machine);
        }
        isAdministrator ??= () => OperatingSystem.IsWindows() && new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        using var pipe = new NamedPipeClientStream(".", args[1], PipeDirection.InOut, PipeOptions.Asynchronous);
        try { await pipe.ConnectAsync(15000).ConfigureAwait(false); }
        catch (Exception ex) when (ex is TimeoutException or IOException) { return 65; }
        using var reader = new StreamReader(pipe);
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
public sealed class TuneSession(Func<string, Task> launch) : ITuneChanger, IDisposable
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private readonly object gate = new();
    private NamedPipeServerStream? pipe;
    private StreamReader? reader;
    private StreamWriter? writer;

    public bool IsOpen => pipe is { IsConnected: true };

    public static TuneSession Elevated(string helperPath) => new(name =>
    {
        if (!File.Exists(helperPath)) throw new FileNotFoundException("The administrator helper is missing from this installation.", helperPath);
        var info = new ProcessStartInfo(helperPath) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        info.ArgumentList.Add(TuneHost.Command); info.ArgumentList.Add(name);
        Process process;
        try { process = Process.Start(info) ?? throw new InvalidOperationException("The administrator helper could not be started."); }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { throw new OperationCanceledException("Administrator permission was declined."); }
        return process.WaitForExitAsync().ContinueWith(_ => process.Dispose(), TaskScheduler.Default);
    });

    /// <summary>Asks for permission and connects. Throws <see cref="OperationCanceledException"/> if the person says no.</summary>
    public async Task OpenAsync(CancellationToken cancellationToken = default)
    {
        if (IsOpen) return;
        Close();
        var name = "tidywindow-tune-" + Guid.NewGuid().ToString("N");
        var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
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
            var input = new StreamReader(server);
            var output = new StreamWriter(server) { AutoFlush = true };
            await output.WriteLineAsync(JsonSerializer.Serialize(new TuneMessage("hello"), Json)).ConfigureAwait(false);
            var reply = JsonSerializer.Deserialize<TuneOutcome>(await input.ReadLineAsync().ConfigureAwait(false) ?? "", Json);
            if (reply is not { Ok: true }) throw new UnauthorizedAccessException("The helper did not get administrator permission.");
            lock (gate) { pipe = server; reader = input; writer = output; }
        }
        catch
        {
            server.Dispose();
            throw;
        }
    }

    public TuneOutcome Change(TuneRequest request)
    {
        lock (gate)
        {
            if (!IsOpen || reader is null || writer is null) throw new InvalidOperationException("The administrator helper is not running.");
            TuneOutcome? reply;
            try
            {
                writer.WriteLine(JsonSerializer.Serialize(new TuneMessage("set", request), Json));
                reply = JsonSerializer.Deserialize<TuneOutcome>(reader.ReadLine() ?? "", Json);
            }
            catch (Exception ex) when (ex is IOException or JsonException or ObjectDisposedException)
            {
                CloseCore();
                throw new IOException("The administrator helper stopped. Try again.");
            }
            if (reply is null) { CloseCore(); throw new IOException("The administrator helper stopped. Try again."); }
            return reply;
        }
    }

    public void Close() { lock (gate) CloseCore(); }

    private void CloseCore()
    {
        try { writer?.WriteLine(JsonSerializer.Serialize(new TuneMessage("bye"), Json)); } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        try { pipe?.Dispose(); } catch (IOException) { }
        pipe = null; reader = null; writer = null;
    }

    public void Dispose() => Close();
}

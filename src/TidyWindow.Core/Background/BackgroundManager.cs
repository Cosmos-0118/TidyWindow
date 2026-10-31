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
/// </summary>
public sealed class LocalBackgroundChanger(IBackgroundSystem system, IBackgroundWriter writer, IReadOnlyList<BackgroundItem> catalog) : IBackgroundChanger
{
    /// <exception cref="UnauthorizedAccessException">The change needs administrator rights this process does not have.</exception>
    public BackgroundOutcome Change(BackgroundRequest request)
    {
        var entry = BackgroundScan.Find(system, catalog, request.Id);
        if (entry is null) return new(false, "That item is not on this PC.");
        if (!request.Enable && !entry.On) return new(true);
        if (!request.Enable && !entry.CanChange) return new(false, entry.Lock);
        var before = Capture(entry);
        var notes = new List<string>();
        try
        {
            if (request.Enable) TurnOn(entry, request.Saved, notes); else TurnOff(entry, notes);
        }
        catch (UnauthorizedAccessException) { throw; }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Security.SecurityException or COMException or Win32Exception or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            if (!request.Enable) Undo(entry, before);
            return new(false, ex.Message);
        }
        var after = BackgroundScan.Find(system, catalog, request.Id);
        var took = after is not null && (request.Enable ? after.On : !after.On);
        if (!took)
        {
            if (!request.Enable) Undo(entry, before);
            return new(false, "Windows did not accept the change.");
        }
        return new(true, null, notes.Count == 0 ? null : string.Join(" ", notes), before);
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

    private void TurnOn(BackgroundEntry entry, IReadOnlyList<BackgroundSaved>? saved, List<string> notes)
    {
        foreach (var state in entry.Targets.Where(x => x.Present))
        {
            var target = state.Target;
            var was = saved?.FirstOrDefault(x => x.Kind == target.Kind && string.Equals(x.Name, target.Name, StringComparison.OrdinalIgnoreCase));
            // Something that was already off before TidyWindow touched it stays off.
            if (was is { WasOn: false } || state.On && was is null) continue;
            switch (target.Kind)
            {
                case BackgroundTargetKind.Service:
                    var start = was is { Start: 2 or 3 } ? was.Start : target.DefaultStart;
                    var delayed = was?.Delayed ?? target.DefaultDelayed;
                    writer.SetServiceStart(target.Name, start, start == 2 && delayed);
                    if (start == 2 && !state.Running) { try { writer.StartService(target.Name); } catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { notes.Add($"{target.Name} starts the next time you restart Windows."); } }
                    break;
                case BackgroundTargetKind.Task:
                    writer.SetTask(target.Name, true);
                    break;
                default:
                    writer.SetPolicy(target.Key, target.Name, was?.Value);
                    break;
            }
        }
    }

    /// <summary>Puts every target back the way it was, ignoring anything that will not go.</summary>
    private void Undo(BackgroundEntry entry, IReadOnlyList<BackgroundSaved> before)
    {
        try { TurnOn(BackgroundScan.Find(system, catalog, entry.Id) ?? entry, before, []); }
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
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { results.Add((entry, new(false, ex.Message))); }
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
            changer = new LocalBackgroundChanger(new WindowsBackgroundSystem(), new WindowsBackgroundWriter(), BackgroundCatalog.Load());
        }
        isAdministrator ??= () => OperatingSystem.IsWindows() && new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        using var pipe = new NamedPipeClientStream(".", args[1], PipeDirection.InOut, PipeOptions.Asynchronous);
        try { await pipe.ConnectAsync(15000).ConfigureAwait(false); }
        catch (Exception ex) when (ex is TimeoutException or IOException) { return 65; }
        using var reader = new StreamReader(pipe);
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
public sealed class BackgroundSession(Func<string, Task> launch) : IBackgroundChanger, IDisposable
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private readonly object gate = new();
    private NamedPipeServerStream? pipe;
    private StreamReader? reader;
    private StreamWriter? writer;

    public bool IsOpen => pipe is { IsConnected: true };

    public static BackgroundSession Elevated(string helperPath) => new(name =>
    {
        if (!File.Exists(helperPath)) throw new FileNotFoundException("The administrator helper is missing from this installation.", helperPath);
        var info = new ProcessStartInfo(helperPath) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        info.ArgumentList.Add(BackgroundHost.Command); info.ArgumentList.Add(name);
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
        var name = "tidywindow-background-" + Guid.NewGuid().ToString("N");
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
            await output.WriteLineAsync(JsonSerializer.Serialize(new BackgroundMessage("hello"), Json)).ConfigureAwait(false);
            var reply = JsonSerializer.Deserialize<BackgroundOutcome>(await input.ReadLineAsync().ConfigureAwait(false) ?? "", Json);
            if (reply is not { Ok: true }) throw new UnauthorizedAccessException("The helper did not get administrator permission.");
            lock (gate) { pipe = server; reader = input; writer = output; }
        }
        catch
        {
            server.Dispose();
            throw;
        }
    }

    public BackgroundOutcome Change(BackgroundRequest request)
    {
        lock (gate)
        {
            if (!IsOpen || reader is null || writer is null) throw new InvalidOperationException("The administrator helper is not running.");
            BackgroundOutcome? reply;
            try
            {
                writer.WriteLine(JsonSerializer.Serialize(new BackgroundMessage("set", request), Json));
                reply = JsonSerializer.Deserialize<BackgroundOutcome>(reader.ReadLine() ?? "", Json);
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
        try { writer?.WriteLine(JsonSerializer.Serialize(new BackgroundMessage("bye"), Json)); } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        try { pipe?.Dispose(); } catch (IOException) { }
        pipe = null; reader = null; writer = null;
    }

    public void Dispose() => Close();
}

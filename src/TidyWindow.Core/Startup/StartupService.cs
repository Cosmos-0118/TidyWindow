using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;

namespace TidyWindow.Next.Core;

/// <summary>
/// Makes one change on this PC, after checking again that the item may be changed, and reads it back to be sure it took.
/// With a <paramref name="vault"/>, as in the administrator helper, the copy of a removed item is kept there and a put-back uses only that
/// copy: the app's own record could have been edited by any program running as the ordinary account, and it must never choose what the
/// helper writes with administrator rights.
/// </summary>
public sealed class LocalStartupChanger(IStartupSystem system, IStartupWriter writer, ProtectedStore? vault = null) : IStartupChanger
{
    /// <exception cref="UnauthorizedAccessException">The change needs administrator rights this process does not have.</exception>
    public StartupOutcome Change(StartupRequest request)
    {
        try
        {
            return request.Action switch
            {
                StartupAction.Remove => Remove(request),
                StartupAction.Restore => Restore(request),
                _ => Toggle(request)
            };
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Security.SecurityException or COMException or Win32Exception or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or FormatException)
        { return new(false, ex.Message); }
    }

    private StartupOutcome Toggle(StartupRequest request)
    {
        var entry = StartupScan.Find(system, request.Id);
        if (entry is null) return new(false, "That item is no longer on this PC.");
        if (!entry.CanChange) return new(false, entry.Reason);
        if (!entry.CanToggle) return new(false, "Windows has no on/off switch for this item. It can only be removed.", entry);
        try { Apply(entry, request); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Security.SecurityException or COMException or Win32Exception or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        { return new(false, ex.Message, entry); }
        var after = StartupScan.Find(system, request.Id);
        return after is null || after.Enabled != request.Enable
            ? new(false, "Windows did not accept the change.", after ?? entry)
            : new(true, null, after);
    }

    // Taking an item out is the one change that deletes, so the copy needed to put it back is made first and handed back with the outcome.
    private StartupOutcome Remove(StartupRequest request)
    {
        var entry = StartupScan.Find(system, request.Id);
        if (entry is null) return new(false, "That item is no longer on this PC.");
        if (!entry.CanChange) return new(false, entry.Reason);
        if (!entry.CanRemove) return new(false, "That kind of item cannot be removed from startup.", entry);
        StartupVault copy;
        string problem;
        switch (entry.Kind)
        {
            case StartupKind.RunKey:
                var value = system.RunValues().FirstOrDefault(x => x.Scope == entry.Scope && x.Where == entry.Source && string.Equals(x.Name, entry.Key, StringComparison.Ordinal));
                if (value is null) return new(false, "That item is no longer on this PC.");
                copy = new(entry.Kind, entry.Scope, entry.Source, entry.Key, value.Command, value.Expandable);
                // The copy is kept before the value goes: without it there is no way back.
                if (!Keep(request.Id, copy, out problem)) return new(false, problem, entry);
                writer.RemoveRun(entry.Scope, entry.Source, entry.Key);
                break;
            case StartupKind.StartupFolder:
                var contents = writer.RemoveFolderItem(entry.Scope, entry.Key);
                copy = new(entry.Kind, entry.Scope, entry.Source, entry.Key, entry.Command, Data: Convert.ToBase64String(contents));
                // A copy that could never be put back is no copy at all, so the file goes straight back and the item stays.
                if (StartupGuard.FolderItemProblem(entry.Key, contents) is { } badFile) { writer.RestoreFolderItem(copy.Scope, copy.Key, contents); return new(false, badFile + " It was left alone.", entry); }
                if (!Keep(request.Id, copy, out problem)) { writer.RestoreFolderItem(copy.Scope, copy.Key, contents); return new(false, problem, entry); }
                break;
            default:
                var xml = writer.RemoveTask(entry.Key);
                copy = new(entry.Kind, entry.Scope, entry.Source, entry.Key, entry.Command, Data: xml);
                if (StartupGuard.TaskDefinitionProblem(xml, entry.Scope) is { } badTask) { writer.RestoreTask(copy.Key, xml); return new(false, badTask + " It was left alone.", entry); }
                if (!Keep(request.Id, copy, out problem)) { writer.RestoreTask(copy.Key, copy.Data); return new(false, problem, entry); }
                break;
        }
        return StartupScan.Find(system, request.Id) is null
            ? new(true, null, null, copy)
            : new(false, "Windows did not accept the change.", entry, copy);
    }

    private bool Keep(string id, StartupVault copy, out string problem)
    {
        problem = "";
        if (vault is null) return true;
        try { vault.Save(id, copy); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problem = "The copy needed to put this item back could not be kept, so it was not removed. " + ex.Message;
            return false;
        }
    }

    // The helper may be asked to put back something it never removed, so it checks the copy is well formed and belongs to the item it was asked for.
    private StartupOutcome Restore(StartupRequest request)
    {
        StartupVault? copy;
        if (vault is not null)
        {
            // Only the helper's own copy is put back with administrator rights, never the one that arrived with the request.
            copy = vault.Load<StartupVault>(request.Id);
            if (copy is null) return new(false, "There is no protected copy of this item to put back. It may have been removed by an earlier version of TidyWindow; add it to startup again by hand.");
        }
        else copy = request.Vault;
        if (copy is null) return new(false, "There is nothing to put back.");
        if (StartupScan.IdFor(copy) != request.Id || copy.Key.Length == 0) return new(false, "That copy does not belong to this item.");
        if (StartupScan.Find(system, request.Id) is { } already)
        {
            // Something with this name is back. The same item (reinstalled, or put back by hand) means the copy is no longer needed; a
            // different one is pointed out and the copy stays, so a newcomer is never reported as the item put back.
            if (copy.Command.Length > 0 && !string.Equals(already.Command.Trim(), copy.Command.Trim(), StringComparison.OrdinalIgnoreCase))
                return new(false, $"Something else has taken this item's place in startup since it was removed. It now runs: {already.Command}. The saved copy was kept; check the new item before you decide what to do with it.", already);
            Forget(request.Id);
            return new(true, null, already);
        }
        // The copy is checked once more here, whoever kept it: it is the one thing the writes below take on trust.
        switch (copy.Kind)
        {
            case StartupKind.RunKey:
                writer.RestoreRun(copy.Scope, copy.Source, copy.Key, copy.Command, copy.Expandable);
                break;
            case StartupKind.StartupFolder:
                var contents = Convert.FromBase64String(copy.Data);
                if (StartupGuard.FolderItemProblem(copy.Key, contents) is { } badFile) return new(false, badFile + " It was not put back.");
                writer.RestoreFolderItem(copy.Scope, copy.Key, contents);
                break;
            case StartupKind.ScheduledTask:
                if (StartupGuard.TaskDefinitionProblem(copy.Data, copy.Scope) is { } badTask) return new(false, badTask + " It was not put back.");
                writer.RestoreTask(copy.Key, copy.Data);
                break;

            default:
                return new(false, "That kind of item cannot be put back.");
        }
        if (StartupScan.Find(system, request.Id) is { } after) { Forget(request.Id); return new(true, null, after); }
        return new(false, "Windows did not accept the change.");
    }

    private void Forget(string id)
    {
        try { vault?.Delete(id); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* The item is back; a copy that lingers does no harm. */ }
    }

    private void Apply(StartupEntry entry, StartupRequest request)
    {
        switch (entry.Kind)
        {
            case StartupKind.RunKey or StartupKind.StartupFolder:
                writer.SetApproved(entry.Scope, entry.Source, entry.Key, request.Enable);
                break;
            case StartupKind.ScheduledTask:
                writer.SetTask(entry.Key, request.Enable);
                break;
            case StartupKind.Service:
                // Never "Disabled": Manual lets Windows still start the service when something needs it, so nothing that relies on it breaks.
                if (request.Enable) writer.SetService(entry.Key, 2, request.RestoreDelayed);
                else writer.SetService(entry.Key, 3, false);
                break;
            case StartupKind.StoreApp:
                var parts = entry.Key.Split('|');
                writer.SetStoreTask(parts[0], parts[1], request.Enable);
                break;
            default:
                throw new InvalidOperationException("That item cannot be changed.");
        }
    }
}

/// <summary>What TidyWindow has turned off, kept on disk so it can be turned back on later.</summary>
public sealed class StartupHistory(string folder)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly object gate = new();
    private string FilePath => Path.Combine(folder, "turned-off.json");

    public IReadOnlyList<StartupChange> All()
    {
        lock (gate) return Load().OrderByDescending(x => x.When).ToList();
    }

    public StartupChange? Get(string id)
    {
        lock (gate) return Load().FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    public void Add(StartupChange change)
    {
        lock (gate)
        {
            var list = Load().Where(x => !string.Equals(x.Id, change.Id, StringComparison.OrdinalIgnoreCase)).ToList();
            list.Add(change);
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

    private List<StartupChange> Load()
    {
        try { return JsonSerializer.Deserialize<List<StartupChange>>(System.IO.File.ReadAllText(FilePath), Json) ?? []; }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return []; }
    }

    private void Save(List<StartupChange> list)
    {
        Directory.CreateDirectory(folder);
        var temp = FilePath + ".tmp";
        System.IO.File.WriteAllText(temp, JsonSerializer.Serialize(list, Json));
        System.IO.File.Move(temp, FilePath, overwrite: true);
    }
}

/// <summary>Chooses who makes a change: this process when it can, the administrator helper (after one permission prompt) when it must.</summary>
public sealed class StartupService(IStartupChanger user, IStartupChanger machine, Func<Task> unlock, StartupHistory history, Func<DateTimeOffset>? clock = null)
{
    private readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.Now);

    public StartupHistory History => history;

    /// <exception cref="OperationCanceledException">The person declined the administrator prompt.</exception>
    public async Task<StartupOutcome> SetAsync(StartupEntry entry, bool enable)
    {
        if (!entry.CanChange) return new(false, entry.Reason);
        var remembered = history.Get(entry.Id);
        var request = new StartupRequest(entry.Id, enable, remembered?.ServiceStart ?? 2, remembered?.ServiceDelayed ?? false);
        var outcome = await SendAsync(request).ConfigureAwait(false);
        if (outcome.Ok)
        {
            if (enable) history.Remove(entry.Id);
            else history.Add(new(entry.Id, entry.Name, entry.Kind, entry.Scope, now(), entry.ServiceStart, entry.ServiceDelayed));
        }
        return outcome;
    }

    /// <summary>Takes an item out of startup, keeping a copy so <see cref="RestoreAsync"/> can put it back exactly as it was.</summary>
    /// <exception cref="OperationCanceledException">The person declined the administrator prompt.</exception>
    public async Task<StartupOutcome> RemoveAsync(StartupEntry entry)
    {
        if (!entry.CanRemove) return new(false, entry.CanChange ? "That kind of item cannot be removed from startup." : entry.Reason);
        var outcome = await SendAsync(new(entry.Id, false, Action: StartupAction.Remove)).ConfigureAwait(false);
        // The copy is saved before anything else can go wrong: it is the only way back.
        if (outcome.Ok && outcome.Vault is { } vault) history.Add(new(entry.Id, entry.Name, entry.Kind, entry.Scope, now(), Removed: true, Vault: vault));
        return outcome;
    }

    /// <summary>Puts back an item <see cref="RemoveAsync"/> took out.</summary>
    /// <exception cref="OperationCanceledException">The person declined the administrator prompt.</exception>
    public async Task<StartupOutcome> RestoreAsync(StartupChange change)
    {
        if (change.Vault is not { } vault) { history.Remove(change.Id); return new(false, "There is no saved copy of this item."); }
        var outcome = await SendAsync(new(change.Id, true, Action: StartupAction.Restore, Vault: vault)).ConfigureAwait(false);
        if (outcome.Ok) history.Remove(change.Id);
        return outcome;
    }

    private async Task<StartupOutcome> SendAsync(StartupRequest request)
    {
        try { return await Task.Run(() => user.Change(request)).ConfigureAwait(false); }
        catch (UnauthorizedAccessException)
        {
            await unlock().ConfigureAwait(false);
            try { return await Task.Run(() => machine.Change(request)).ConfigureAwait(false); }
            catch (IOException)
            {
                // The helper leaves by itself after five idle minutes, and the app only finds out when it next uses it. Ask again, once.
                await unlock().ConfigureAwait(false);
                return await Task.Run(() => machine.Change(request)).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Turns back on everything TidyWindow turned off or removed. Items that are gone from this PC are forgotten.</summary>
    public async Task<IReadOnlyList<(StartupChange Change, StartupOutcome Outcome)>> RestoreAllAsync(IStartupSystem system)
    {
        var results = new List<(StartupChange, StartupOutcome)>();
        foreach (var change in history.All())
        {
            if (change.Removed) { results.Add((change, await RestoreAsync(change).ConfigureAwait(false))); continue; }
            var entry = await Task.Run(() => StartupScan.Find(system, change.Id)).ConfigureAwait(false);
            if (entry is null) { history.Remove(change.Id); continue; }
            if (entry.Enabled) { history.Remove(change.Id); continue; }
            results.Add((change, await SetAsync(entry, true).ConfigureAwait(false)));
        }
        return results;
    }
}

public sealed record StartupMessage(string Op, StartupRequest? Request = null);

/// <summary>
/// The elevated side of the startup manager. It rebuilds the item from the PC itself and applies the same rules as the app, so it only ever
/// turns off things the app would also turn off. It leaves by itself when idle.
/// </summary>
public static class StartupHost
{
    public const string Command = "--startup";
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public static async Task<int> RunAsync(string[] args, IStartupChanger? changer = null, Func<bool>? isAdministrator = null, TimeSpan? idle = null)
    {
        if (args.Length < 2 || args[0] != Command) return 64;
        if (changer is null)
        {
            if (!OperatingSystem.IsWindows()) return 66;
            changer = new LocalStartupChanger(new WindowsStartupSystem(), new WindowsStartupWriter(), new ProtectedStore("startup"));
        }
        isAdministrator ??= () => OperatingSystem.IsWindows() && new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        using var pipe = await HelperPipe.TryConnectAsync(args[1]).ConfigureAwait(false);
        if (pipe is null) return 65;
        var reader = new PipeLineReader(pipe);
        var writer = new StreamWriter(pipe) { AutoFlush = true };

        async Task Reply(StartupOutcome reply)
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
            StartupMessage? message;
            try { message = JsonSerializer.Deserialize<StartupMessage>(line, Json); }
            catch (JsonException) { message = null; }
            if (message is null) { await Reply(new(false, "That request was not understood.")).ConfigureAwait(false); continue; }

            switch (message.Op)
            {
                case "bye": return 0;
                case "hello": admin = isAdministrator(); await Reply(new(admin)).ConfigureAwait(false); break;
                case "set" when admin && message.Request is { } request:
                    StartupOutcome outcome;
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
public sealed class StartupSession(Func<string, Task> launch) : IStartupChanger, IDisposable
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private readonly object gate = new();
    private NamedPipeServerStream? pipe;
    private PipeLineReader? reader;
    private StreamWriter? writer;

    public bool IsOpen => pipe is { IsConnected: true };

    public static StartupSession Elevated(string helperPath) => new(name => HelperPipe.LaunchElevated(helperPath, StartupHost.Command, name));

    /// <summary>Asks for permission and connects. Throws <see cref="OperationCanceledException"/> if the person says no.</summary>
    public async Task OpenAsync(CancellationToken cancellationToken = default)
    {
        if (IsOpen) return;
        Close();
        var name = HelperPipe.NewName("startup");
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
            await output.WriteLineAsync(JsonSerializer.Serialize(new StartupMessage("hello"), Json)).ConfigureAwait(false);
            var reply = JsonSerializer.Deserialize<StartupOutcome>(await input.ReadLineAsync().ConfigureAwait(false) ?? "", Json);
            if (reply is not { Ok: true }) throw new UnauthorizedAccessException("The helper did not get administrator permission.");
            lock (gate) { pipe = server; reader = input; writer = output; }
        }
        catch
        {
            server.Dispose();
            throw;
        }
    }

    public StartupOutcome Change(StartupRequest request)
    {
        lock (gate)
        {
            if (!IsOpen || reader is null || writer is null) throw new InvalidOperationException("The administrator helper is not running.");
            StartupOutcome? reply;
            try
            {
                writer.WriteLine(JsonSerializer.Serialize(new StartupMessage("set", request), Json));
                reply = JsonSerializer.Deserialize<StartupOutcome>(reader.ReadLine() ?? "", Json);
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
        try { writer?.WriteLine(JsonSerializer.Serialize(new StartupMessage("bye"), Json)); } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        try { pipe?.Dispose(); } catch (IOException) { }
        pipe = null; reader = null; writer = null;
    }

    public void Dispose() => Close();
}

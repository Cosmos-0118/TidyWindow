using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;

namespace TidyWindow.Next.Core;

/// <summary>Makes one change on this PC, after checking again that the item may be changed, and reads it back to be sure it took.</summary>
public sealed class LocalStartupChanger(IStartupSystem system, IStartupWriter writer) : IStartupChanger
{
    /// <exception cref="UnauthorizedAccessException">The change needs administrator rights this process does not have.</exception>
    public StartupOutcome Change(StartupRequest request)
    {
        var entry = StartupScan.Find(system, request.Id);
        if (entry is null) return new(false, "That item is no longer on this PC.");
        if (!entry.CanChange) return new(false, entry.Reason);
        try { Apply(entry, request); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Security.SecurityException or COMException or Win32Exception or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        { return new(false, ex.Message, entry); }
        var after = StartupScan.Find(system, request.Id);
        return after is null || after.Enabled != request.Enable
            ? new(false, "Windows did not accept the change.", after ?? entry)
            : new(true, null, after);
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
        StartupOutcome outcome;
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
            else history.Add(new(entry.Id, entry.Name, entry.Kind, entry.Scope, now(), entry.ServiceStart, entry.ServiceDelayed));
        }
        return outcome;
    }

    /// <summary>Turns back on everything TidyWindow turned off. Items that are gone from this PC are forgotten.</summary>
    public async Task<IReadOnlyList<(StartupChange Change, StartupOutcome Outcome)>> RestoreAllAsync(IStartupSystem system)
    {
        var results = new List<(StartupChange, StartupOutcome)>();
        foreach (var change in history.All())
        {
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
            changer = new LocalStartupChanger(new WindowsStartupSystem(), new WindowsStartupWriter());
        }
        isAdministrator ??= () => OperatingSystem.IsWindows() && new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        using var pipe = new NamedPipeClientStream(".", args[1], PipeDirection.InOut, PipeOptions.Asynchronous);
        try { await pipe.ConnectAsync(15000).ConfigureAwait(false); }
        catch (Exception ex) when (ex is TimeoutException or IOException) { return 65; }
        using var reader = new StreamReader(pipe);
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
    private StreamReader? reader;
    private StreamWriter? writer;

    public bool IsOpen => pipe is { IsConnected: true };

    public static StartupSession Elevated(string helperPath) => new(name =>
    {
        if (!File.Exists(helperPath)) throw new FileNotFoundException("The administrator helper is missing from this installation.", helperPath);
        var info = new ProcessStartInfo(helperPath) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        info.ArgumentList.Add(StartupHost.Command); info.ArgumentList.Add(name);
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
        var name = "tidywindow-startup-" + Guid.NewGuid().ToString("N");
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

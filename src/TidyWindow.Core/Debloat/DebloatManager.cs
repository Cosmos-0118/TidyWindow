using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;

namespace TidyWindow.Next.Core;

/// <summary>What happened to each item when several were put back at once. <paramref name="Declined"/> is set when the person said no to the administrator prompt.</summary>
public sealed record DebloatRestoreBatch(IReadOnlyList<(DebloatItem Item, DebloatOutcome Outcome)> Results, bool Declined);

/// <summary>Brings an app back after it was removed: WinGet installs it again from the Store. Items with no Store id are only allowed back for new accounts.</summary>
public sealed class DebloatReinstaller(ICommandRunner runner, IExecutableLocator locator)
{
    /// <summary>A Store product id is twelve letters and digits (9P1J8S7CCWWT); a WinGet id has a dot (Microsoft.Edge).</summary>
    public static bool IsStoreProduct(string id) => id.Length == 12 && id.All(char.IsAsciiLetterOrDigit);

    public async Task<DebloatOutcome> InstallAsync(DebloatItem item, CancellationToken cancellationToken = default)
    {
        if (item.StoreId.Length == 0 || !DebloatGuard.IsToken(item.StoreId)) return new(true, null, "Open the Microsoft Store to install it again.");
        if (locator.Find(ManagerKind.Winget) is not { } winget) return new(true, null, "WinGet is not available. Install it from the Microsoft Store.");
        var arguments = new List<string> { "install", "--id", item.StoreId, "--exact", "--source", IsStoreProduct(item.StoreId) ? "msstore" : "winget", "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity" };
        var result = await runner.RunAsync(new CommandSpec(winget, arguments, TimeSpan.FromMinutes(10)), cancellationToken).ConfigureAwait(false);
        return result.ExitCode == 0 ? new(true) : new(false, $"WinGet could not install {item.Name}. Open the Microsoft Store to install it.");
    }
}

/// <summary>
/// Decides how a removal is made. Removing needs administrator rights, so everything goes through the helper after one permission prompt;
/// the page itself never changes anything. What was removed is kept so that it can be put back, even after the app is closed.
/// </summary>
public sealed class DebloatManager(IDebloatChanger machine, Func<Task> unlock, DebloatHistory history, DebloatReinstaller? reinstaller = null, Func<DateTimeOffset>? clock = null)
{
    private readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.Now);

    public DebloatHistory History => history;

    /// <exception cref="OperationCanceledException">The person declined the administrator prompt.</exception>
    public async Task<DebloatOutcome> RemoveAsync(DebloatEntry entry)
    {
        if (!entry.CanRemove) return new(false, entry.Lock ?? "That cannot be removed.");
        var outcome = await SendAsync(new DebloatRequest(entry.Id)).ConfigureAwait(false);
        if (outcome.Ok) history.Add(new(entry.Id, entry.Item.Name, now(), outcome.Families ?? entry.Families));
        return outcome;
    }

    /// <summary>Lets Windows have the item back and installs it again where that is possible.</summary>
    public async Task<DebloatOutcome> RestoreAsync(DebloatItem item, CancellationToken cancellationToken = default)
    {
        var saved = history.Get(item.Id);
        var outcome = await SendAsync(new DebloatRequest(item.Id, Restore: true, saved?.Families)).ConfigureAwait(false);
        if (!outcome.Ok) return outcome;
        var note = outcome.Note;
        if (reinstaller is not null && item.Kind != DebloatKind.Recall)
        {
            var install = await reinstaller.InstallAsync(item, cancellationToken).ConfigureAwait(false);
            if (!install.Ok) return install;
            note = install.Note ?? note;
        }
        history.Remove(item.Id);
        return new(true, null, note);
    }

    /// <summary>
    /// Puts back several items one after another. The permission prompt appears once, because the helper stays open between them. An item that fails does not stop the
    /// others, but a declined prompt or a helper that has gone does, since asking again for each would only repeat the prompt.
    /// </summary>
    public async Task<DebloatRestoreBatch> RestoreAllAsync(IReadOnlyList<DebloatItem> items, Action<DebloatItem, DebloatOutcome?>? progress = null, CancellationToken cancellationToken = default)
    {
        var results = new List<(DebloatItem Item, DebloatOutcome Outcome)>();
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Deliberately without ConfigureAwait(false): progress is reported on the caller's thread, which for the page is the one that may touch the screen.
            progress?.Invoke(item, null);
            try
            {
                var outcome = await RestoreAsync(item, cancellationToken);
                results.Add((item, outcome));
                progress?.Invoke(item, outcome);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return new(results, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                results.Add((item, new(false, ex.Message)));
                break;
            }
        }
        return new(results, false);
    }

    private async Task<DebloatOutcome> SendAsync(DebloatRequest request)
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

public sealed record DebloatMessage(string Op, DebloatRequest? Request = null);

/// <summary>
/// The elevated side of the page. It rebuilds the item from the catalog and from the PC itself and applies the same rules as the app,
/// so it only ever removes what the app would also remove. It leaves by itself when idle.
/// </summary>
public static class DebloatHost
{
    public const string Command = "--debloat";
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public static async Task<int> RunAsync(string[] args, IDebloatChanger? changer = null, Func<bool>? isAdministrator = null, TimeSpan? idle = null)
    {
        if (args.Length < 2 || args[0] != Command) return 64;
        if (changer is null)
        {
            if (!OperatingSystem.IsWindows()) return 66;
            var system = new WindowsDebloatSystem(elevated: true);
            changer = new LocalDebloatChanger(system, new AppxRemover(system, new WindowsAppxStore(), new WindowsAppxOps()), new WindowsPlaybooks(), DebloatCatalog.Load());
        }
        isAdministrator ??= () => OperatingSystem.IsWindows() && new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        using var pipe = new NamedPipeClientStream(".", args[1], PipeDirection.InOut, PipeOptions.Asynchronous);
        try { await pipe.ConnectAsync(15000).ConfigureAwait(false); }
        catch (Exception ex) when (ex is TimeoutException or IOException) { return 65; }
        using var reader = new StreamReader(pipe);
        var writer = new StreamWriter(pipe) { AutoFlush = true };

        async Task Reply(DebloatOutcome reply)
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
            DebloatMessage? message;
            try { message = JsonSerializer.Deserialize<DebloatMessage>(line, Json); }
            catch (JsonException) { message = null; }
            if (message is null) { await Reply(new(false, "That request was not understood.")).ConfigureAwait(false); continue; }

            switch (message.Op)
            {
                case "bye": return 0;
                case "hello": admin = isAdministrator(); await Reply(new(admin)).ConfigureAwait(false); break;
                case "run" when admin && message.Request is { } request:
                    DebloatOutcome outcome;
                    try { outcome = changer.Change(request); }
                    catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException or Win32Exception or JsonException or ArgumentException)
                    {
                        outcome = new(false, ex.Message);
                    }
                    await Reply(outcome).ConfigureAwait(false);
                    break;
                default: await Reply(new(false, "That request was not understood.")).ConfigureAwait(false); break;
            }
        }
    }
}

/// <summary>The app side: starts the helper through the Windows permission prompt and sends it each request over a private pipe.</summary>
public sealed class DebloatSession(Func<string, Task> launch) : IDebloatChanger, IDisposable
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private readonly object gate = new();
    private NamedPipeServerStream? pipe;
    private StreamReader? reader;
    private StreamWriter? writer;

    public bool IsOpen => pipe is { IsConnected: true };

    public static DebloatSession Elevated(string helperPath) => new(name =>
    {
        if (!File.Exists(helperPath)) throw new FileNotFoundException("The administrator helper is missing from this installation.", helperPath);
        var info = new ProcessStartInfo(helperPath) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        info.ArgumentList.Add(DebloatHost.Command); info.ArgumentList.Add(name);
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
        var name = "tidywindow-debloat-" + Guid.NewGuid().ToString("N");
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
            await output.WriteLineAsync(JsonSerializer.Serialize(new DebloatMessage("hello"), Json)).ConfigureAwait(false);
            var reply = JsonSerializer.Deserialize<DebloatOutcome>(await input.ReadLineAsync().ConfigureAwait(false) ?? "", Json);
            if (reply is not { Ok: true }) throw new UnauthorizedAccessException("The helper did not get administrator permission.");
            lock (gate) { pipe = server; reader = input; writer = output; }
        }
        catch
        {
            server.Dispose();
            throw;
        }
    }

    public DebloatOutcome Change(DebloatRequest request)
    {
        lock (gate)
        {
            if (!IsOpen || reader is null || writer is null) throw new InvalidOperationException("The administrator helper is not running.");
            DebloatOutcome? reply;
            try
            {
                writer.WriteLine(JsonSerializer.Serialize(new DebloatMessage("run", request), Json));
                reply = JsonSerializer.Deserialize<DebloatOutcome>(reader.ReadLine() ?? "", Json);
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
        try { writer?.WriteLine(JsonSerializer.Serialize(new DebloatMessage("bye"), Json)); } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        try { pipe?.Dispose(); } catch (IOException) { }
        pipe = null; reader = null; writer = null;
    }

    public void Dispose() => Close();
}

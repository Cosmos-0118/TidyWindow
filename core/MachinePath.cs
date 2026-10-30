using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;

namespace TidyWindow.Next.Core;

public sealed record PathRequest(string Op, string? Expected = null, string? Value = null);
public sealed record PathReply(bool Ok, bool Stale = false, string? Error = null);

/// <summary>
/// The elevated side of Windows-wide PATH editing. TidyWindow itself never runs as administrator, so this runs as a separate process that stays
/// for as long as the person keeps editing unlocked. It can do one thing, replace the Windows-wide PATH, and only when that PATH is still the
/// one the caller based the change on, so it can neither run anything nor touch any other setting. It leaves by itself after sitting idle.
/// </summary>
public static class MachinePathHost
{
    public const string Command = "--machine-path";
    private const int MaxLength = 32000;
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public static async Task<int> RunAsync(string[] args, Func<string?>? read = null, Action<string>? write = null, Func<bool>? isAdministrator = null, TimeSpan? idle = null)
    {
        if (args.Length < 2 || args[0] != Command) return 64;
        read ??= () => OperatingSystem.IsWindows() ? new RegistryPathStore().Read(PathScope.Machine) : null;
        write ??= value => { if (OperatingSystem.IsWindows()) new RegistryPathStore().WriteMachine(value); };
        isAdministrator ??= () => OperatingSystem.IsWindows() && new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        using var pipe = new NamedPipeClientStream(".", args[1], PipeDirection.InOut, PipeOptions.Asynchronous);
        try { await pipe.ConnectAsync(15000).ConfigureAwait(false); }
        catch (Exception ex) when (ex is TimeoutException or IOException) { return 65; }
        using var reader = new StreamReader(pipe);
        var writer = new StreamWriter(pipe) { AutoFlush = true };

        async Task Reply(PathReply reply)
        {
            try { await writer.WriteLineAsync(JsonSerializer.Serialize(reply, Json)).ConfigureAwait(false); }
            catch (IOException) { }
        }

        while (true)
        {
            string? line;
            using (var timeout = new CancellationTokenSource(idle ?? TimeSpan.FromMinutes(10)))
            {
                try { line = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false); }
                catch (Exception ex) when (ex is OperationCanceledException or IOException) { return 0; }
            }
            if (line is null) return 0;
            PathRequest? request;
            try { request = JsonSerializer.Deserialize<PathRequest>(line, Json); }
            catch (JsonException) { request = null; }
            if (request is null) { await Reply(new(false, Error: "That request was not understood.")).ConfigureAwait(false); continue; }

            switch (request.Op)
            {
                case "bye": return 0;
                case "hello": await Reply(new(isAdministrator())).ConfigureAwait(false); break;
                case "write": await Reply(Write(request, read, write)).ConfigureAwait(false); break;
                default: await Reply(new(false, Error: "That request was not understood.")).ConfigureAwait(false); break;
            }
        }
    }

    private static PathReply Write(PathRequest request, Func<string?> read, Action<string> write)
    {
        var value = request.Value ?? "";
        // A damaged PATH breaks every program on the PC, so anything that is empty, enormous or not plain text is refused here whatever asked for it.
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxLength || value.Any(char.IsControl)) return new(false, Error: "That PATH is not valid, so nothing was changed.");
        if (!string.Equals(read() ?? "", request.Expected ?? "", StringComparison.Ordinal)) return new(false, Stale: true);
        try { write(value); return new(true); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException or System.Security.SecurityException) { return new(false, Error: ex.Message); }
    }
}

/// <summary>The app side: starts the elevated helper once, then sends it each edit over a private pipe until editing is locked again.</summary>
public sealed class MachinePathSession(Func<string, Task> launch) : IDisposable
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private readonly object gate = new();
    private NamedPipeServerStream? pipe;
    private StreamReader? reader;
    private StreamWriter? writer;

    public bool IsOpen => pipe is { IsConnected: true };

    /// <summary>Starts the helper through the Windows permission prompt.</summary>
    public static MachinePathSession Elevated(string helperPath) => new(name =>
    {
        if (!File.Exists(helperPath)) throw new FileNotFoundException("The administrator helper is missing from this installation.", helperPath);
        var info = new ProcessStartInfo(helperPath) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        info.ArgumentList.Add(MachinePathHost.Command); info.ArgumentList.Add(name);
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
        var name = "tidywindow-path-" + Guid.NewGuid().ToString("N");
        var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        try
        {
            // Starting an elevated process waits for the permission prompt, so it never runs on the caller's thread and has no timer of its own.
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
            await output.WriteLineAsync(JsonSerializer.Serialize(new PathRequest("hello"), Json)).ConfigureAwait(false);
            var reply = JsonSerializer.Deserialize<PathReply>(await input.ReadLineAsync().ConfigureAwait(false) ?? "", Json);
            if (reply is not { Ok: true }) throw new UnauthorizedAccessException("The helper did not get administrator permission.");
            lock (gate) { pipe = server; reader = input; writer = output; }
        }
        catch
        {
            server.Dispose();
            throw;
        }
    }

    /// <summary>Replaces the Windows-wide PATH if it still equals <paramref name="expected"/>.</summary>
    public void Write(string? expected, string value)
    {
        lock (gate)
        {
            if (!IsOpen || reader is null || writer is null) throw new InvalidOperationException("Editing the Windows-wide PATH is locked.");
            PathReply? reply;
            try
            {
                writer.WriteLine(JsonSerializer.Serialize(new PathRequest("write", expected ?? "", value), Json));
                reply = JsonSerializer.Deserialize<PathReply>(reader.ReadLine() ?? "", Json);
            }
            catch (Exception ex) when (ex is IOException or JsonException or ObjectDisposedException)
            {
                CloseCore();
                throw new IOException("The administrator helper stopped. Unlock editing again to continue.");
            }
            if (reply is null) { CloseCore(); throw new IOException("The administrator helper stopped. Unlock editing again to continue."); }
            if (reply.Stale) throw new IOException(PathDoctor.Stale);
            if (!reply.Ok) throw new IOException(reply.Error ?? "The Windows-wide PATH could not be saved.");
        }
    }

    public void Close() { lock (gate) CloseCore(); }

    private void CloseCore()
    {
        try { writer?.WriteLine(JsonSerializer.Serialize(new PathRequest("bye"), Json)); } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        try { pipe?.Dispose(); } catch (IOException) { }
        pipe = null; reader = null; writer = null;
    }

    public void Dispose() => Close();
}

/// <summary>Reads PATH the ordinary way and sends writes of the Windows-wide list through the elevated helper.</summary>
public sealed class ElevatedPathStore(IPathStore inner, MachinePathSession session) : IPathStore
{
    public string? Read(PathScope scope) => inner.Read(scope);
    public string? Variable(PathScope scope, string name) => inner.Variable(scope, name);

    public void Write(PathScope scope, string value, string? previous)
    {
        if (scope == PathScope.Machine) session.Write(previous, value);
        else inner.Write(scope, value, previous);
    }
}

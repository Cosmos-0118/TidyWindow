using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;

namespace TidyWindow.Next.Core;

public sealed record RepairRequest(string Op, string? Path = null, KeyBackup? Backup = null);
public sealed record RepairReply(bool Ok, string? Error = null, KeyBackup? Backup = null);

/// <summary>
/// The elevated side of repairs. It runs for one repair, asks Windows for permission once, and can only remove or restore the keys on
/// <see cref="RegistryRepair.Machine"/>'s allow-list, so it cannot run anything or touch any other setting. It leaves by itself when idle.
/// </summary>
public static class RepairHost
{
    public const string Command = "--repair";
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public static async Task<int> RunAsync(string[] args, Func<string, KeyBackup?>? delete = null, Action<KeyBackup>? restore = null, Func<string, bool>? allowed = null, Func<bool>? isAdministrator = null, TimeSpan? idle = null)
    {
        if (args.Length < 2 || args[0] != Command) return 64;
        if (delete is null || restore is null || allowed is null)
        {
            if (!OperatingSystem.IsWindows()) return 66;
            delete ??= RegistryRepair.Machine.Delete;
            restore ??= RegistryRepair.Machine.Restore;
            allowed ??= RegistryRepair.Machine.IsAllowed;
        }
        isAdministrator ??= () => OperatingSystem.IsWindows() && new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        using var pipe = new NamedPipeClientStream(".", args[1], PipeDirection.InOut, PipeOptions.Asynchronous);
        try { await pipe.ConnectAsync(15000).ConfigureAwait(false); }
        catch (Exception ex) when (ex is TimeoutException or IOException) { return 65; }
        using var reader = new StreamReader(pipe);
        var writer = new StreamWriter(pipe) { AutoFlush = true };

        async Task Reply(RepairReply reply)
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
            RepairRequest? request;
            try { request = JsonSerializer.Deserialize<RepairRequest>(line, Json); }
            catch (JsonException) { request = null; }
            if (request is null) { await Reply(new(false, "That request was not understood.")).ConfigureAwait(false); continue; }

            switch (request.Op)
            {
                case "bye": return 0;
                case "hello": admin = isAdministrator(); await Reply(new(admin)).ConfigureAwait(false); break;
                case "delete" when admin:
                    await Reply(Run(request.Path, allowed, path => new RepairReply(true, Backup: delete(path)))).ConfigureAwait(false); break;
                case "restore" when admin && request.Backup is { } backup:
                    await Reply(Run(backup.Path, allowed, _ => { restore(backup); return new RepairReply(true); })).ConfigureAwait(false); break;
                default: await Reply(new(false, "That request was not understood.")).ConfigureAwait(false); break;
            }
        }
    }

    private static RepairReply Run(string? path, Func<string, bool> allowed, Func<string, RepairReply> action)
    {
        if (string.IsNullOrWhiteSpace(path) || !allowed(path)) return new(false, "TidyWindow does not change that part of the registry.");
        try { return action(path); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException or System.Security.SecurityException)
        { return new(false, ex.Message); }
    }
}

/// <summary>The app side: starts the helper through the Windows permission prompt and sends it each change over a private pipe.</summary>
public sealed class RepairSession(Func<string, Task> launch) : IKeyEditor, IDisposable
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private readonly object gate = new();
    private NamedPipeServerStream? pipe;
    private StreamReader? reader;
    private StreamWriter? writer;

    public bool IsOpen => pipe is { IsConnected: true };

    public static RepairSession Elevated(string helperPath) => new(name =>
    {
        if (!File.Exists(helperPath)) throw new FileNotFoundException("The administrator helper is missing from this installation.", helperPath);
        var info = new ProcessStartInfo(helperPath) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        info.ArgumentList.Add(RepairHost.Command); info.ArgumentList.Add(name);
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
        var name = "tidywindow-repair-" + Guid.NewGuid().ToString("N");
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
            await output.WriteLineAsync(JsonSerializer.Serialize(new RepairRequest("hello"), Json)).ConfigureAwait(false);
            var reply = JsonSerializer.Deserialize<RepairReply>(await input.ReadLineAsync().ConfigureAwait(false) ?? "", Json);
            if (reply is not { Ok: true }) throw new UnauthorizedAccessException("The helper did not get administrator permission.");
            lock (gate) { pipe = server; reader = input; writer = output; }
        }
        catch
        {
            server.Dispose();
            throw;
        }
    }

    /// <summary>Removes a Windows-wide key and returns a copy of it, or null if it was already gone.</summary>
    public KeyBackup? Delete(string path) => Send(new("delete", path)).Backup;

    public void Restore(KeyBackup backup) => Send(new("restore", backup.Path, backup));

    private RepairReply Send(RepairRequest request)
    {
        lock (gate)
        {
            if (!IsOpen || reader is null || writer is null) throw new InvalidOperationException("The administrator helper is not running.");
            RepairReply? reply;
            try
            {
                writer.WriteLine(JsonSerializer.Serialize(request, Json));
                reply = JsonSerializer.Deserialize<RepairReply>(reader.ReadLine() ?? "", Json);
            }
            catch (Exception ex) when (ex is IOException or JsonException or ObjectDisposedException)
            {
                CloseCore();
                throw new IOException("The administrator helper stopped. Try again.");
            }
            if (reply is null) { CloseCore(); throw new IOException("The administrator helper stopped. Try again."); }
            if (!reply.Ok) throw new IOException(reply.Error ?? "The change could not be made.");
            return reply;
        }
    }

    public void Close() { lock (gate) CloseCore(); }

    private void CloseCore()
    {
        try { writer?.WriteLine(JsonSerializer.Serialize(new RepairRequest("bye"), Json)); } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        try { pipe?.Dispose(); } catch (IOException) { }
        pipe = null; reader = null; writer = null;
    }

    public void Dispose() => Close();
}

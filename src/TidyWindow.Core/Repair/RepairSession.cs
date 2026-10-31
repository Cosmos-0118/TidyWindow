using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;

namespace TidyWindow.Next.Core;

public sealed record RepairRequest(string Op, string? Path = null, KeyBackup? Backup = null, string? Name = null, FootprintRequest? Footprint = null, string? Sid = null, string? File = null, MsiProduct? Product = null, string? Value = null);
public sealed record RepairReply(bool Ok, string? Error = null, KeyBackup? Backup = null, bool Flag = false, FootprintPlan? Plan = null, bool Denied = false, RunResult? Run = null);

/// <summary>
/// The elevated side of repairs. It runs for one repair, asks Windows for permission once, and can only remove or restore the keys on
/// <see cref="RegistryRepair.Elevated"/>'s allow-list, repair a program from its own installer file or stop the Windows Installer service,
/// so it cannot run anything else or touch any other setting. It leaves by itself when idle.
/// </summary>
public static class RepairHost
{
    public const string Command = "--repair";
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public static async Task<int> RunAsync(string[] args, Func<string, IKeyEditor>? editorFor = null, IElevatedOps? ops = null, Func<bool>? isAdministrator = null, TimeSpan? idle = null)
    {
        if (args.Length < 2 || args[0] != Command) return 64;
        if (editorFor is null || ops is null)
        {
            if (!OperatingSystem.IsWindows()) return 66;
            editorFor ??= sid => { if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException(); RegistryNative.EnablePrivileges(); return RegistryRepair.Elevated(sid); };
            ops ??= new WindowsInstallerOps();
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

        IKeyEditor? editor = null;
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

            if (request.Op == "bye") return 0;
            if (request.Op == "hello")
            {
                var admin = isAdministrator();
                // HKCU in the helper means the app user's hive, whoever approved the permission prompt.
                if (admin) editor = editorFor(request.Sid ?? (OperatingSystem.IsWindows() ? RegistryRepair.CurrentSid() : ""));
                await Reply(new(admin)).ConfigureAwait(false);
                continue;
            }
            if (editor is null) { await Reply(new(false, "That request was not understood.")).ConfigureAwait(false); continue; }
            await Reply(Handle(request, editor, ops)).ConfigureAwait(false);
        }
    }

    private static RepairReply Handle(RepairRequest request, IKeyEditor editor, IElevatedOps ops)
    {
        const string Refused = "TidyWindow does not change that part of the registry.";
        try
        {
            switch (request.Op)
            {
                case "exists" when Allowed(editor, request.Path): return new(true, Flag: editor.Exists(request.Path!));
                case "snapshot" when Allowed(editor, request.Path): return new(true, Backup: editor.Snapshot(request.Path!));
                case "delete" when Allowed(editor, request.Path): return new(true, Backup: editor.Delete(request.Path!));
                case "deletevalue" when Allowed(editor, request.Path) && !string.IsNullOrEmpty(request.Name): return new(true, Backup: editor.DeleteValue(request.Path!, request.Name));
                case "settext" when Allowed(editor, request.Path) && !string.IsNullOrEmpty(request.Name) && request.Value is not null: return new(true, Backup: editor.SetText(request.Path!, request.Name, request.Value));
                case "restore" when request.Backup is { } backup && Allowed(editor, backup.Path): editor.Restore(backup); return new(true);
                case "footprint" when request.Footprint is { } footprint: return new(true, Plan: editor.Footprint(footprint));
                case "reinstall" when request.File is { Length: > 0 } file && request.Product is { } product: return new(true, Run: ops.Reinstall(file, product));
                case "stopservice": return new(true, Flag: ops.StopInstallerService());
                case "exists" or "snapshot" or "delete" or "deletevalue" or "settext" or "restore": return new(false, Refused);
                default: return new(false, "That request was not understood.");
            }
        }
        catch (UnauthorizedAccessException ex) { return new(false, ex.Message, Denied: true); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Security.SecurityException or ArgumentException) { return new(false, ex.Message); }
    }

    private static bool Allowed(IKeyEditor editor, string? path) => !string.IsNullOrWhiteSpace(path) && editor.IsAllowed(path);
}

/// <summary>The app side: starts the helper through the Windows permission prompt and sends it each change over a private pipe.</summary>
public sealed class RepairSession(Func<string, Task> launch) : IKeyEditor, IElevatedOps, IDisposable
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
            await output.WriteLineAsync(JsonSerializer.Serialize(new RepairRequest("hello", Sid: OperatingSystem.IsWindows() ? RegistryRepair.CurrentSid() : null), Json)).ConfigureAwait(false);
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

    /// <summary>Removes a key and returns a copy of it, or null if it was already gone.</summary>
    public KeyBackup? Delete(string path) => Send(new("delete", path)).Backup;

    public KeyBackup? DeleteValue(string path, string name) => Send(new("deletevalue", path, Name: name)).Backup;

    public KeyBackup? SetText(string path, string name, string value) => Send(new("settext", path, Name: name, Value: value)).Backup;

    public void Restore(KeyBackup backup) => Send(new("restore", backup.Path, backup));

    public bool Exists(string path) => Send(new("exists", path)).Flag;

    public KeyBackup? Snapshot(string path) => Send(new("snapshot", path)).Backup;

    public FootprintPlan Footprint(FootprintRequest request) => Send(new("footprint", Footprint: request)).Plan ?? FootprintPlan.Empty;

    public RunResult Reinstall(string msiPath, MsiProduct product) => Send(new("reinstall", File: msiPath, Product: product)).Run ?? new(1, "The helper gave no answer.");

    public bool StopInstallerService() => Send(new("stopservice")).Flag;

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
            if (!reply.Ok) throw reply.Denied ? new UnauthorizedAccessException(reply.Error) : new IOException(reply.Error ?? "The change could not be made.");
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

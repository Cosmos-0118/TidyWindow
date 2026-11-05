using System.ComponentModel;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;

namespace TidyWindow.Next.Core;

/// <summary>What the search program prints: one line of JSON.</summary>
public sealed record DriverScanReply(IReadOnlyList<DriverOffer> Offers, string? Error);

/// <summary>
/// The read-only half of the Drivers page: asks Windows Update for the drivers it offers and prints them. It runs as the ordinary account, with no
/// permission prompt, in its own program so the app can end it when the person cancels. A search can take minutes and the Windows Update Agent cannot be
/// interrupted from inside the app.
/// </summary>
public static class DriverScanHost
{
    public const string Command = "--driver-scan";
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public static Task<int> RunAsync(string[] args, IDriverUpdates? updates = null, TextWriter? output = null)
    {
        if (args.Length < 1 || args[0] != Command) return Task.FromResult(64);
        if (updates is null)
        {
            if (!OperatingSystem.IsWindows()) return Task.FromResult(66);
            updates = new WindowsUpdateDrivers();
        }
        DriverScanReply reply;
        try { reply = new(updates.Search(), null); }
        catch (DriverUpdateException ex) { reply = new([], ex.Message); }
        // Whatever else goes wrong has to reach the page as a sentence, not as a program that ends without a word.
        catch (Exception ex) { reply = new([], "Windows Update could not be asked: " + ex.Message); }
        (output ?? Console.Out).WriteLine(JsonSerializer.Serialize(reply, Json));
        return Task.FromResult(0);
    }

    /// <summary>Reads the program's output. The last non-empty line is the reply; anything before it is noise.</summary>
    public static DriverScanReply? Parse(string output)
    {
        var line = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
        if (line is null) return null;
        try { return JsonSerializer.Deserialize<DriverScanReply>(line, Json); }
        catch (JsonException) { return null; }
    }
}

/// <summary>The app side of a scan: runs the search program, reads the installed drivers, and sets each offer beside the driver it would replace.</summary>
/// <param name="hostPath">The helper program, which also does the read-only search. Null when this installation does not have it.</param>
public sealed class DriverScanner(ICommandRunner runner, string? hostPath, IInstalledDrivers installed, Func<IReadOnlyList<string>> notices, Func<string> maker)
{
    /// <summary>How long a search may run before it is given up on. Windows Update is usually done in seconds, but a slow or flooded connection is not rare.</summary>
    public static readonly TimeSpan Limit = TimeSpan.FromMinutes(15);

    /// <summary>What can be known without asking Windows Update: the maker tools that fit this PC, and any policy that limits drivers. Quick, so the page shows it on opening.</summary>
    public async Task<(IReadOnlyList<DriverMakerTool> Tools, IReadOnlyList<string> Notices)> LocalAsync(CancellationToken cancellationToken)
    {
        var drivers = await Task.Run(installed.Read, cancellationToken).ConfigureAwait(false);
        return (DriverMakers.For(drivers, maker()), notices());
    }

    /// <exception cref="OperationCanceledException">The search was cancelled; the program is ended.</exception>
    public async Task<DriverScan> ScanAsync(CancellationToken cancellationToken)
    {
        var drivers = await Task.Run(installed.Read, cancellationToken).ConfigureAwait(false);
        var reasons = notices();
        DriverScan Failed(string message) => new([], reasons, message, drivers);
        if (hostPath is null || !File.Exists(hostPath)) return Failed("The search program is missing from this installation of TidyWindow.");
        CommandResult result;
        try { result = await runner.RunAsync(new CommandSpec(hostPath, [DriverScanHost.Command], Limit), cancellationToken).ConfigureAwait(false); }
        catch (TimeoutException) { return Failed("Windows Update took too long to answer. Try again later."); }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or IOException) { return Failed("The search could not be started: " + ex.Message); }
        var reply = DriverScanHost.Parse(result.Output);
        if (reply is null) return Failed(result.ExitCode == 0 ? "Windows Update gave an answer that could not be read." : $"The search stopped before it answered (exit code {result.ExitCode}).");
        if (reply.Error is not null) return Failed(reply.Error);
        var findings = reply.Offers.Where(x => DriverGuard.IsUpdateId(x.UpdateId)).Select(x => DriverCompare.Assess(x, drivers))
            // A device Windows says has a problem comes first, then what is worth installing, then what is the same, and the older ones last.
            .OrderBy(x => x.Offer.ProblemCode != 0 ? 0 : 1).ThenBy(x => DriverCompare.IsActionable(x) ? 0 : x.Verdict == DriverVerdict.Same ? 1 : 2)
            .ThenBy(x => x.Offer.DeviceClass, StringComparer.CurrentCultureIgnoreCase).ThenBy(x => x.Offer.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
        return new(findings, reasons, null, drivers);
    }
}

public sealed record DriverMessage(string Op, string? Id = null);
/// <summary>What the helper sends back: "ok" for the greeting, "out" for a sentence about what it is doing, "done" with the result, "fail" with an error.</summary>
public sealed record DriverEvent(string Type, string Text = "", DriverResult? Result = null, string Error = "");

/// <summary>
/// The elevated side of Drivers. It installs one driver, or takes one back out, by id: the id of an update it finds in Windows Update itself, or of a
/// saved driver in its own protected folder. It is never given a file, a link or a driver package, and it leaves by itself when idle.
/// </summary>
public static class DriverHost
{
    public const string Command = "--drivers";
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public static async Task<int> RunAsync(string[] args, DriverInstaller? installer = null, Func<bool>? isAdministrator = null, TimeSpan? idle = null)
    {
        if (args.Length < 2 || args[0] != Command) return 64;
        if (installer is null)
        {
            if (!OperatingSystem.IsWindows()) return 66;
            installer = new DriverInstaller(new WindowsUpdateDrivers(), new WindowsInstalledDrivers(), new DriverBackups(new ProtectedStore("drivers")), new PnpUtil(new ProcessRunner()));
        }
        isAdministrator ??= () => OperatingSystem.IsWindows() && new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        using var pipe = await HelperPipe.TryConnectAsync(args[1]).ConfigureAwait(false);
        if (pipe is null) return 65;
        var reader = new PipeLineReader(pipe);
        var writer = new StreamWriter(pipe) { AutoFlush = true };
        var gate = new object();

        void Send(DriverEvent value)
        {
            try { lock (gate) writer.WriteLine(JsonSerializer.Serialize(value, Json)); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        }

        var admin = false;
        Task<string?>? pending = null;
        while (true)
        {
            var read = pending ?? reader.ReadLineAsync();
            pending = null;
            var first = await Task.WhenAny(read, Task.Delay(idle ?? TimeSpan.FromMinutes(5))).ConfigureAwait(false);
            if (first != read) return 0;
            string? line;
            try { line = await read.ConfigureAwait(false); }
            catch (IOException) { return 0; }
            if (line is null) return 0;
            DriverMessage? message;
            try { message = JsonSerializer.Deserialize<DriverMessage>(line, Json); }
            catch (JsonException) { message = null; }
            if (message is null) { Send(new("fail", Error: "That request was not understood.")); continue; }

            switch (message.Op)
            {
                case "bye": return 0;
                // A cancel that arrived while an install ran was already acted on; there is nothing to answer.
                case "cancel": break;
                case "hello": admin = isAdministrator(); Send(new(admin ? "ok" : "fail", Error: admin ? "" : "The helper did not get administrator permission.")); break;
                case "install" or "restore" when admin:
                    // While the work runs, anything arriving on the pipe (a cancel, or the app closing) asks it to stop. The installer decides how far a stop can go.
                    using (var cancel = new CancellationTokenSource())
                    {
                        pending = reader.ReadLineAsync();
                        _ = pending.ContinueWith(_ => { try { cancel.Cancel(); } catch (ObjectDisposedException) { } }, TaskScheduler.Default);
                        try
                        {
                            void Say(string text) => Send(new("out", text));
                            var result = message.Op == "install" ? await installer.InstallAsync(message.Id ?? "", Say, cancel.Token).ConfigureAwait(false) : await installer.RestoreAsync(message.Id ?? "", Say, cancel.Token).ConfigureAwait(false);
                            Send(new("done", Result: result));
                        }
                        // Whatever goes wrong has to be reported, not end the helper: a helper that just vanishes tells the person nothing.
                        catch (Exception ex) { Send(new("fail", Error: "The driver could not be changed: " + ex.Message)); }
                    }
                    break;
                default: Send(new("fail", Error: "That request was not understood.")); break;
            }
        }
    }
}

/// <summary>What the page uses to install and restore drivers. The real one asks the administrator helper; tests use a stand-in.</summary>
public interface IDriverChanger
{
    /// <summary>Asks for permission and connects, or checks that the helper from last time is still there. Throws <see cref="OperationCanceledException"/> if the person says no.</summary>
    Task OpenAsync(CancellationToken cancellationToken = default);
    Task<DriverResult> InstallAsync(string updateId, IProgress<string> say, CancellationToken cancellationToken);
    Task<DriverResult> RestoreAsync(string backupId, IProgress<string> say, CancellationToken cancellationToken);
}

/// <summary>The app side: starts the helper through the Windows permission prompt, sends it one request at a time, and passes what it says on as it arrives.</summary>
public sealed class DriverSession(Func<string, Task> launch, TimeSpan? replyTimeout = null) : IDriverChanger, IDisposable
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private readonly TimeSpan replyTimeout = replyTimeout ?? TimeSpan.FromSeconds(15);
    private readonly SemaphoreSlim one = new(1, 1);
    private readonly object gate = new();
    private NamedPipeServerStream? pipe;
    private PipeLineReader? reader;
    private StreamWriter? writer;

    public bool IsOpen => pipe is { IsConnected: true };

    public static DriverSession Elevated(string helperPath) => new(name => HelperPipe.LaunchElevated(helperPath, DriverHost.Command, name));

    public async Task OpenAsync(CancellationToken cancellationToken = default)
    {
        await one.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            PipeLineReader? input; StreamWriter? output;
            lock (gate) { input = reader; output = writer; }
            // The helper leaves after five idle minutes, and a pipe only shows that once something is written to it, so a greeting goes first.
            if (IsOpen && input is not null && output is not null && await GreetAsync(input, output, cancellationToken).ConfigureAwait(false) is { Type: "ok" }) return;
            Close();
            var name = HelperPipe.NewName("drivers");
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
                var fresh = new PipeLineReader(server);
                var toHelper = new StreamWriter(server) { AutoFlush = true };
                var reply = await GreetAsync(fresh, toHelper, cancellationToken).ConfigureAwait(false) ?? throw new IOException("The administrator helper connected but did not answer.");
                if (reply.Type != "ok") throw new UnauthorizedAccessException("The helper did not get administrator permission.");
                lock (gate) { pipe = server; reader = fresh; writer = toHelper; }
            }
            catch
            {
                server.Dispose();
                throw;
            }
        }
        finally { one.Release(); }
    }

    private async Task<DriverEvent?> GreetAsync(PipeLineReader input, StreamWriter output, CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(replyTimeout);
        try
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(new DriverMessage("hello"), Json).AsMemory(), limit.Token).ConfigureAwait(false);
            var line = await input.ReadLineAsync(limit.Token).ConfigureAwait(false);
            return line is null ? null : JsonSerializer.Deserialize<DriverEvent>(line, Json);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return null; }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or JsonException) { return null; }
    }

    public Task<DriverResult> InstallAsync(string updateId, IProgress<string> say, CancellationToken cancellationToken) => RunAsync("install", updateId, say, cancellationToken);

    public Task<DriverResult> RestoreAsync(string backupId, IProgress<string> say, CancellationToken cancellationToken) => RunAsync("restore", backupId, say, cancellationToken);

    private async Task<DriverResult> RunAsync(string op, string id, IProgress<string> say, CancellationToken cancellationToken)
    {
        await one.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            PipeLineReader input; StreamWriter output;
            lock (gate)
            {
                if (!IsOpen || reader is null || writer is null) throw new InvalidOperationException("The administrator helper is not running.");
                input = reader; output = writer;
            }
            try
            {
                lock (gate) output.WriteLine(JsonSerializer.Serialize(new DriverMessage(op, id), Json));
                using var register = cancellationToken.Register(() => { try { lock (gate) output.WriteLine(JsonSerializer.Serialize(new DriverMessage("cancel"), Json)); } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { } });
                while (true)
                {
                    var line = await input.ReadLineAsync(CancellationToken.None).ConfigureAwait(false) ?? throw new IOException("The administrator helper stopped. Try again.");
                    var value = JsonSerializer.Deserialize<DriverEvent>(line, Json) ?? throw new IOException("The administrator helper sent something unreadable.");
                    switch (value.Type)
                    {
                        case "out": say.Report(value.Text); break;
                        case "done": return value.Result ?? new DriverResult(false, "The administrator helper did not say how it went.");
                        default: throw new InvalidOperationException(value.Error.Length > 0 ? value.Error : "The administrator helper could not do that.");
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or ObjectDisposedException)
            {
                Close();
                throw new IOException("The administrator helper stopped. Try again.");
            }
        }
        finally { one.Release(); }
    }

    public void Close() { lock (gate) CloseCore(); }

    private void CloseCore()
    {
        try { writer?.WriteLine(JsonSerializer.Serialize(new DriverMessage("bye"), Json)); } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        try { pipe?.Dispose(); } catch (IOException) { }
        pipe = null; reader = null; writer = null;
    }

    public void Dispose() { Close(); one.Dispose(); }
}

/// <summary>Installs and restores drivers through the helper and keeps a record of what was changed, so the page can offer to put an earlier driver back.</summary>
public sealed class DriverManager(IDriverChanger changer, DriverHistory history, Func<DateTimeOffset>? clock = null)
{
    private readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.Now);

    public DriverHistory History => history;

    /// <exception cref="OperationCanceledException">The person declined the administrator prompt, or asked to stop before the helper began.</exception>
    public async Task<DriverResult> InstallAsync(DriverFinding finding, IProgress<string> say, CancellationToken cancellationToken)
    {
        // The helper judges again, with its own look at Windows Update and at the PC. This is only so a refused downgrade never costs a permission prompt.
        if (!finding.CanInstall) return new(false, finding.Reason);
        await changer.OpenAsync(cancellationToken).ConfigureAwait(false);
        var result = await changer.InstallAsync(finding.Offer.UpdateId, say, cancellationToken).ConfigureAwait(false);
        if (result.Ok)
        {
            try { history.Add(new(finding.Offer.UpdateId, finding.Offer.Title, finding.Installed?.DeviceName ?? (finding.Offer.Model.Length > 0 ? finding.Offer.Model : finding.Offer.Title), result.FromVersion, result.ToVersion, now(), result.BackupId)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* The list of what changed is a convenience; the install itself is done. */ }
        }
        return result;
    }

    public async Task<DriverResult> RestoreAsync(DriverChange change, IProgress<string> say, CancellationToken cancellationToken)
    {
        if (change.BackupId is not { } id || !DriverGuard.IsBackupId(id)) return new(false, "There is no saved copy of the earlier driver.");
        await changer.OpenAsync(cancellationToken).ConfigureAwait(false);
        var result = await changer.RestoreAsync(id, say, cancellationToken).ConfigureAwait(false);
        if (result.Ok)
        {
            try { history.MarkRestored(id); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return result;
    }
}

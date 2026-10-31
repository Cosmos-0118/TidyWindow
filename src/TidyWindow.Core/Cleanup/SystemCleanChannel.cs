using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;

namespace TidyWindow.Next.Core;

/// <summary>One line on the pipe between TidyWindow and its elevated helper.</summary>
public sealed record HostMessage(string Kind, string? Name = null, long Freed = 0, int Removed = 0, SystemCleanReport? Report = null, string? Error = null, bool Starting = false, List<MeasuredItem>? Measured = null);

/// <summary>
/// The elevated side. It is a separate process because TidyWindow itself never runs as administrator. It takes rule names only,
/// never paths: everything it touches comes from the built-in catalog, so a forged request cannot point it at other files.
/// </summary>
public static class SystemCleanHost
{
    public const string Command = "--system-clean";
    public const string MeasureMarker = "@measure";
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public static async Task<int> RunAsync(string[] args, IReadOnlyList<CleanupRule>? catalog = null, CleanupContext? context = null, ISystemTools? tools = null)
    {
        if (args.Length < 3 || args[0] != Command) return 64;
        context ??= CleanupContext.Current;
        using var pipe = new NamedPipeClientStream(".", args[1], PipeDirection.InOut, PipeOptions.Asynchronous);
        try { await pipe.ConnectAsync(15000).ConfigureAwait(false); }
        catch (Exception ex) when (ex is TimeoutException or IOException) { return 65; }
        using var reader = new StreamReader(pipe);
        var writer = new StreamWriter(pipe) { AutoFlush = true };
        var send = new SemaphoreSlim(1, 1);
        async Task Send(HostMessage message)
        {
            await send.WaitAsync().ConfigureAwait(false);
            try { await writer.WriteLineAsync(JsonSerializer.Serialize(message, Json)).ConfigureAwait(false); }
            catch (IOException) { }
            finally { send.Release(); }
        }

        using var cancel = new CancellationTokenSource();
        // The app asks to stop by writing a line; the helper finishes the file it is on and returns what it has done so far.
        _ = Task.Run(async () => { try { if (await reader.ReadLineAsync().ConfigureAwait(false) is not null) cancel.Cancel(); } catch (IOException) { } });
        try
        {
            var ids = args[2].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var job = new SystemCleanJob(context, tools ?? new WindowsSystemTools());
            // A leading marker asks for sizes only; it is not a rule id, so it can never select anything to clean.
            if (ids.Length > 0 && ids[0] == MeasureMarker)
            {
                var items = await job.MeasureAsync(catalog ?? CleanupCatalog.Load(), ids[1..], cancel.Token).ConfigureAwait(false);
                await Send(new HostMessage("measured", Measured: items.ToList())).ConfigureAwait(false);
                return 0;
            }
            var progress = new Relay(step => _ = Send(new HostMessage("progress", step.Name, step.FreedBytes, step.Removed, Starting: step.Starting)));
            var report = await job.RunAsync(catalog ?? CleanupCatalog.Load(), ids, progress, cancel.Token).ConfigureAwait(false);
            await Send(new HostMessage("done", Report: report)).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException) { await Send(new HostMessage("failed", Error: "Cleaning was stopped.")).ConfigureAwait(false); return 1; }
        catch (Exception ex) { await Send(new HostMessage("failed", Error: ex.Message)).ConfigureAwait(false); return 1; }
    }

    private sealed class Relay(Action<CleanupProgress> action) : IProgress<CleanupProgress> { public void Report(CleanupProgress value) => action(value); }
}

/// <summary>What the helper found when asked to size administrator rules.</summary>
public sealed record SystemMeasureReport(IReadOnlyList<MeasuredItem> Items, string? Error, bool Declined);

/// <summary>The app side: starts the elevated helper once, listens to it, and returns what it did.</summary>
public sealed class SystemCleanLauncher(Func<string, IReadOnlyList<string>, Task> start)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <param name="start">Launches the helper with the pipe name and rule ids; the task completes when it exits. Throws <see cref="OperationCanceledException"/> if permission is declined.</param>
    public async Task<SystemCleanReport> RunAsync(IReadOnlyList<string> ruleIds, IProgress<CleanupProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        SystemCleanReport? report = null;
        var (error, declined) = await ConverseAsync(ruleIds, message =>
        {
            switch (message.Kind)
            {
                case "progress": progress?.Report(new CleanupProgress(message.Name ?? "", message.Freed, message.Removed, message.Starting)); break;
                case "done": report = message.Report; break;
            }
        }, cancellationToken).ConfigureAwait(false);
        if (declined) return Declined(ruleIds.Count);
        return report ?? Failed(ruleIds.Count, error ?? "The helper stopped before it finished.");
    }

    /// <summary>Asks the elevated helper how much each administrator rule could free. Nothing is cleaned.</summary>
    public async Task<SystemMeasureReport> MeasureAsync(IReadOnlyList<string> ruleIds, CancellationToken cancellationToken = default)
    {
        List<MeasuredItem>? items = null;
        var (error, declined) = await ConverseAsync([SystemCleanHost.MeasureMarker, .. ruleIds], message => { if (message.Kind == "measured") items = message.Measured; }, cancellationToken).ConfigureAwait(false);
        if (declined) return new SystemMeasureReport([], "Administrator permission was declined.", true);
        return items is null ? new SystemMeasureReport([], error ?? "The helper stopped before it finished.", false) : new SystemMeasureReport(items, null, false);
    }

    /// <summary>One conversation with a freshly started helper. Returns the helper's error, if any, and whether the permission prompt was declined.</summary>
    private async Task<(string? Error, bool Declined)> ConverseAsync(IReadOnlyList<string> ids, Action<HostMessage> onMessage, CancellationToken cancellationToken)
    {
        var name = "tidywindow-clean-" + Guid.NewGuid().ToString("N");
        await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        // Starting an elevated process waits for the permission prompt, so it never runs on the caller's thread.
        var launched = Task.Run(() => start(name, ids), CancellationToken.None);
        var connecting = server.WaitForConnectionAsync(cancellationToken);
        // No timer here: the permission prompt can sit open as long as the person likes. A helper that starts but never connects
        // gives up on its own after 15 s and exits, which completes <c>launched</c>.
        var first = await Task.WhenAny(connecting, launched).ConfigureAwait(false);
        if (first != connecting)
        {
            if (launched.IsCanceled) return (null, true);
            if (launched.IsFaulted) return (launched.Exception!.GetBaseException().Message, false);
            return ("The helper stopped before it could report back.", false);
        }
        await connecting.ConfigureAwait(false);

        using var reader = new StreamReader(server);
        var writer = new StreamWriter(server) { AutoFlush = true };
        using var stop = cancellationToken.Register(() => { try { writer.WriteLine("cancel"); } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { } });
        string? error = null;
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            HostMessage? message;
            try { message = JsonSerializer.Deserialize<HostMessage>(line, Json); }
            catch (JsonException) { continue; }
            if (message is null) continue;
            if (message.Kind == "failed") error = message.Error;
            onMessage(message);
        }
        try { await launched.ConfigureAwait(false); } catch (Exception ex) when (ex is not OperationCanceledException) { error ??= ex.Message; }
        return (error, false);
    }

    /// <summary>Starts the real helper through the Windows permission prompt.</summary>
    public static SystemCleanLauncher Elevated(string helperPath) => new((pipe, ids) =>
    {
        if (!File.Exists(helperPath)) throw new FileNotFoundException("The cleanup helper is missing from this installation.", helperPath);
        var info = new ProcessStartInfo(helperPath) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        info.ArgumentList.Add(SystemCleanHost.Command); info.ArgumentList.Add(pipe); info.ArgumentList.Add(string.Join(",", ids));
        Process process;
        try { process = Process.Start(info) ?? throw new InvalidOperationException("The cleanup helper could not be started."); }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { throw new OperationCanceledException("Administrator permission was declined."); }
        return process.WaitForExitAsync().ContinueWith(_ => process.Dispose(), TaskScheduler.Default);
    });

    private static SystemCleanReport Declined(int count) => new(new CleanupResult(0, 0, 0, 0, 0, null, 0, TimeSpan.Zero), ["Administrator permission was declined, so the Windows system areas were not cleaned."]);
    private static SystemCleanReport Failed(int count, string message) => new(new CleanupResult(0, 0, 0, 0, count, null, 0, TimeSpan.Zero), [message]);
}

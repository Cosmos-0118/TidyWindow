using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace TidyWindow.Next.Core;

public sealed class ProcessRunner(TimeSpan? pipeGrace = null) : ICommandRunner
{
    // A program can end while something it started still holds its output pipes open (an installer's helper, a background service). Waiting for the pipes
    // to close would then wait for that helper, so once the program is gone the output gets this long to finish and the rest is let go.
    private readonly TimeSpan pipeGrace = pipeGrace ?? TimeSpan.FromSeconds(5);
    private static readonly TimeSpan KillWait = TimeSpan.FromSeconds(10);

    public async Task<CommandResult> RunAsync(CommandSpec command, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(command.FileName)
        {
            UseShellExecute = command.Elevate,
            CreateNoWindow = !command.Elevate,
            RedirectStandardOutput = !command.Elevate,
            RedirectStandardError = !command.Elevate,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };
        foreach (var argument in command.Arguments) start.ArgumentList.Add(argument);
        if (command.Elevate) start.Verb = "runas";
        else
        {
            start.StandardOutputEncoding = Encoding.UTF8;
            start.StandardErrorEncoding = Encoding.UTF8;
            start.Environment["NO_COLOR"] = "1";
        }
        using var process = new Process { StartInfo = start };
        if (command.Elevate) Report(command, new("Approve the Windows administrator prompt. Output will appear in the separate administrator window; it cannot be captured by this launch method.", CommandOutputKind.Notice));
        else if (command.MayShowInstaller) Report(command, new("Follow any installer or Windows permission window that opens. The queue waits for this command to finish.", CommandOutputKind.Notice));
        // Starting a process can take tens of milliseconds (antivirus scans the executable), so never do it on the caller's thread: callers are often the UI thread.
        try { await Task.Run(() => process.Start(), cancellationToken).ConfigureAwait(false); }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            Report(command, new("Administrator permission was declined. Nothing was started.", CommandOutputKind.Notice));
            return new(-1, "", "Administrator permission was declined. Nothing was started.");
        }
        if (command.Elevate) Report(command, new("Running in a separate administrator window. Complete any installer steps there. Live output is available in that window, not inside TidyWindow.", CommandOutputKind.Notice));

        // Drain both pipes concurrently, but bound retained output independently of CLI verbosity.
        using var drain = new CancellationTokenSource();
        var output = new Capture(); var error = new Capture();
        var reading = command.Elevate ? Task.CompletedTask : Task.WhenAll(
            ReadBoundedAsync(process.StandardOutput.BaseStream, command, CommandOutputKind.Output, output, drain.Token),
            ReadBoundedAsync(process.StandardError.BaseStream, command, CommandOutputKind.Error, error, drain.Token));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(command.Timeout);
        try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            { throw new InvalidOperationException("The installer is still running in its own window. Check that window before trying again.", ex); }
            // Killing the tree should end it at once; if it does not, the caller must still get control back.
            try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(KillWait).ConfigureAwait(false); }
            catch (TimeoutException) { }
            await FinishReadingAsync(reading, drain, command).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested) throw;
            throw new TimeoutException("The package manager did not respond in time. Check your connection and try again.");
        }
        await FinishReadingAsync(reading, drain, command).ConfigureAwait(false);
        return new(process.ExitCode, output.Text(), error.Text());
    }

    /// <summary>Lets the output pipes finish for a moment, then stops reading them. Whatever arrived by then is kept.</summary>
    private async Task FinishReadingAsync(Task reading, CancellationTokenSource drain, CommandSpec command)
    {
        try { await reading.WaitAsync(pipeGrace).ConfigureAwait(false); return; }
        catch (TimeoutException) { }
        drain.Cancel();
        try { await reading.WaitAsync(KillWait).ConfigureAwait(false); }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException) { }
        Report(command, new("Something the command started is still running in the background, so TidyWindow stopped waiting for its output.", CommandOutputKind.Notice));
    }

    private sealed class Capture
    {
        private readonly StringBuilder text = new();
        public void Append(string value)
        {
            lock (text) { text.Append(value); if (text.Length > 131072) text.Remove(0, text.Length - 131072); }
        }
        public string Text() { lock (text) return text.ToString(); }
    }

    private static async Task ReadBoundedAsync(Stream stream, CommandSpec command, CommandOutputKind kind, Capture capture, CancellationToken stop)
    {
        var bytes = new byte[4096];
        var buffer = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
        var decoder = Encoding.UTF8.GetDecoder();
        var messages = new PowerShellOutputDecoder(kind);
        void Publish(IReadOnlyList<CommandOutput> values)
        {
            foreach (var value in values) { capture.Append(value.Text); Report(command, value); }
        }
        var atStart = true;
        try
        {
            int count;
            while ((count = await stream.ReadAsync(bytes.AsMemory(), stop).ConfigureAwait(false)) > 0)
            {
                var characters = decoder.GetChars(bytes.AsSpan(0, count), buffer.AsSpan(), flush: false);
                var offset = 0;
                if (atStart && characters > 0) { atStart = false; if (buffer[0] == '\uFEFF') offset = 1; }
                if (characters > offset) Publish(messages.Feed(new string(buffer, offset, characters - offset)));
            }
        }
        // Reading was given up on (see FinishReadingAsync) or the pipe broke: keep what arrived.
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException) { }
        var remaining = decoder.GetChars(ReadOnlySpan<byte>.Empty, buffer.AsSpan(), flush: true);
        Publish(messages.Feed(remaining > 0 ? new string(buffer, 0, remaining) : "", final: true));
    }
    private static void Report(CommandSpec command, CommandOutput output)
    { try { command.Progress?.Report(output); } catch { /* A display failure must not stop draining the process pipes. */ } }
}

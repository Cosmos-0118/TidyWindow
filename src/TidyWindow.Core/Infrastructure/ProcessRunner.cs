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
            // A program's starting folder is on its DLL search path, so a program that runs as administrator must not start in a folder this account can write to.
            WorkingDirectory = command.Elevate ? Environment.SystemDirectory : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
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
            var orphaned = await EndAsync(process, command).ConfigureAwait(false);
            await FinishReadingAsync(reading, drain, command, output, error).ConfigureAwait(false);
            if (orphaned) Report(command, new("The program is still running in its own window; TidyWindow has no right to end it. Check that window before trying again.", CommandOutputKind.Notice));
            // A stop is a stop, whatever was left running; only a timeout is reported by what it left behind.
            if (cancellationToken.IsCancellationRequested) throw;
            if (orphaned) throw new InvalidOperationException("The installer is still running in its own window. Check that window before trying again.", null);
            throw new TimeoutException("The package manager did not respond in time. Check your connection and try again.");
        }
        await FinishReadingAsync(reading, drain, command, output, error).ConfigureAwait(false);
        return new(process.ExitCode, output.Text(), error.Text());
    }

    /// <summary>
    /// Ends a program that is being stopped or has run out of time. True when something is left running that this process has no right to end: a program
    /// started through the administrator prompt, or an elevated installer inside the program's tree.
    /// </summary>
    private static async Task<bool> EndAsync(Process process, CommandSpec command)
    {
        // Windows gives a non-administrator no right to end a program it started through the permission prompt, so asking would only fail.
        if (command.Elevate) return !process.HasExited;
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        // It ended by itself in the meantime, which is what was wanted.
        catch (InvalidOperationException) { return false; }
        // Win32Exception: this process may not end it. AggregateException: the program went, but part of its tree (an elevated installer) could not be ended.
        catch (Exception ex) when (ex is Win32Exception or AggregateException) { return true; }
        // Ending the tree should finish it at once; if it does not, the caller must still get control back.
        try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(KillWait).ConfigureAwait(false); return false; }
        catch (TimeoutException) { return true; }
    }

    /// <summary>Lets the output pipes finish for a moment, then stops reading them. Whatever arrived by then is kept; anything later is not shown.</summary>
    private async Task FinishReadingAsync(Task reading, CancellationTokenSource drain, CommandSpec command, params Capture[] captures)
    {
        if (await FinishReadingAsync(reading, drain, pipeGrace, KillWait).ConfigureAwait(false)) return;
        foreach (var capture in captures) capture.Close();
        Report(command, new("Something the command started is still running in the background, so TidyWindow stopped waiting for its output.", CommandOutputKind.Notice));
    }

    /// <summary>
    /// Waits for the output readers to end on their own for <paramref name="grace"/>, then cancels them and waits at most <paramref name="limit"/> more.
    /// True when every reader ended by itself. Shared with the fix executor, which reads a program's pipes the same way.
    /// </summary>
    internal static async Task<bool> FinishReadingAsync(Task reading, CancellationTokenSource drain, TimeSpan grace, TimeSpan limit)
    {
        try { await reading.WaitAsync(grace).ConfigureAwait(false); return true; }
        catch (TimeoutException) { }
        drain.Cancel();
        try { await reading.WaitAsync(limit).ConfigureAwait(false); }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException) { }
        return false;
    }

    private sealed class Capture
    {
        private readonly StringBuilder text = new();
        private bool closed;
        public void Append(string value)
        {
            lock (text) { if (closed) return; text.Append(value); if (text.Length > 131072) text.Remove(0, text.Length - 131072); }
        }
        public string Text() { lock (text) return text.ToString(); }
        /// <summary>Output that turns up after the command was given up on belongs to whatever is still running, not to this result.</summary>
        public void Close() { lock (text) closed = true; }
        public bool Closed { get { lock (text) return closed; } }
    }

    private static async Task ReadBoundedAsync(Stream stream, CommandSpec command, CommandOutputKind kind, Capture capture, CancellationToken stop)
    {
        var bytes = new byte[4096];
        var buffer = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
        var decoder = Encoding.UTF8.GetDecoder();
        var messages = new PowerShellOutputDecoder(kind);
        void Publish(IReadOnlyList<CommandOutput> values)
        {
            if (capture.Closed) return;
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
                if (atStart && characters > 0) { atStart = false; if (buffer[0] == '﻿') offset = 1; }
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

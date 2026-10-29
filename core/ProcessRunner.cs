using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace TidyWindow.Next.Core;

public sealed class ProcessRunner : ICommandRunner
{
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
        try { process.Start(); }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            Report(command, new("Administrator permission was declined. Nothing was started.", CommandOutputKind.Notice));
            return new(-1, "", "Administrator permission was declined. Nothing was started.");
        }
        if (command.Elevate) Report(command, new("Running in a separate administrator window. Complete any installer steps there. Live output is available in that window, not inside TidyWindow.", CommandOutputKind.Notice));

        // Drain both pipes concurrently, but bound retained output independently of CLI verbosity.
        var output = command.Elevate ? Task.FromResult("") : ReadBoundedAsync(process.StandardOutput.BaseStream, command, CommandOutputKind.Output);
        var error = command.Elevate ? Task.FromResult("") : ReadBoundedAsync(process.StandardError.BaseStream, command, CommandOutputKind.Error);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(command.Timeout);
        try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            { throw new InvalidOperationException("The installer is still running in its own window. Check that window before trying again.", ex); }
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.WhenAll(output, error).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested) throw;
            throw new TimeoutException("The package manager did not respond in time. Check your connection and try again.");
        }
        return new(process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
    }

    private static async Task<string> ReadBoundedAsync(Stream stream, CommandSpec command, CommandOutputKind kind)
    {
        var text = new StringBuilder();
        var bytes = new byte[4096];
        var buffer = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
        var decoder = Encoding.UTF8.GetDecoder();
        var messages = new PowerShellOutputDecoder(kind);
        void Publish(IReadOnlyList<CommandOutput> values)
        {
            foreach (var value in values) { text.Append(value.Text); Report(command, value); }
            if (text.Length > 131072) text.Remove(0, text.Length - 131072);
        }
        var atStart = true;
        int count;
        while ((count = await stream.ReadAsync(bytes.AsMemory()).ConfigureAwait(false)) > 0)
        {
            var characters = decoder.GetChars(bytes.AsSpan(0, count), buffer.AsSpan(), flush: false);
            var offset = 0;
            if (atStart && characters > 0) { atStart = false; if (buffer[0] == '\uFEFF') offset = 1; }
            if (characters > offset) Publish(messages.Feed(new string(buffer, offset, characters - offset)));
        }
        var remaining = decoder.GetChars(ReadOnlySpan<byte>.Empty, buffer.AsSpan(), flush: true);
        Publish(messages.Feed(remaining > 0 ? new string(buffer, 0, remaining) : "", final: true));
        return text.ToString();
    }
    private static void Report(CommandSpec command, CommandOutput output)
    { try { command.Progress?.Report(output); } catch { /* A display failure must not stop draining the process pipes. */ } }
}

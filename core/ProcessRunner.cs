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
        try { process.Start(); }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        { return new(-1, "", "Administrator permission was declined. Nothing was started."); }

        // Drain both pipes concurrently, but bound retained output independently of CLI verbosity.
        var output = command.Elevate ? Task.FromResult("") : ReadBoundedAsync(process.StandardOutput);
        var error = command.Elevate ? Task.FromResult("") : ReadBoundedAsync(process.StandardError);
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

    private static async Task<string> ReadBoundedAsync(StreamReader reader)
    {
        var text = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
        {
            text.Append(buffer, 0, count);
            if (text.Length > 131072) text.Remove(0, text.Length - 131072);
        }
        return text.ToString();
    }
}

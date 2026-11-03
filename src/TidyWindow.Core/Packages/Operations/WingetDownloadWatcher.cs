using System.Text.RegularExpressions;

namespace TidyWindow.Next.Core;

/// <summary>
/// WinGet draws no progress bar when its output is piped: it prints "Downloading https://…" and then nothing until the hash check. It downloads through
/// Windows' Delivery Optimization service, which does keep count, so while that line is the newest one this reads the service's progress and passes it
/// on as <see cref="CommandOutputKind.Transfer"/>. Everything WinGet prints is forwarded untouched.
/// </summary>
public sealed partial class WingetDownloadWatcher(IProgress<CommandOutput> inner, ICommandRunner runner, string powerShell = "powershell.exe") : IProgress<CommandOutput>, IDisposable
{
    private static readonly TimeSpan MaxWatch = TimeSpan.FromMinutes(45);
    private readonly object gate = new();
    private CancellationTokenSource? watching;
    private int percent = -1;
    private bool disposed;

    [GeneratedRegex(@"^\s*Downloading\s+https?://", RegexOptions.IgnoreCase)]
    private static partial Regex DownloadLine();
    [GeneratedRegex(@"^(\d+)/(\d+)$")]
    private static partial Regex Count();

    public void Report(CommandOutput value)
    {
        inner.Report(value);
        if (value.Kind is not (CommandOutputKind.Output or CommandOutputKind.Error)) return;
        var lines = PackageOutputParser.Clean(value.Text).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        // Any line after the download line means WinGet has moved on, so the download is over.
        var started = Array.FindLastIndex(lines, DownloadLine().IsMatch);
        if (started >= 0 && started == lines.Length - 1) Start();
        else if (lines.Length > 0) Stop();
    }

    private void Start()
    {
        CancellationTokenSource source;
        lock (gate)
        {
            if (disposed || watching is not null) return;
            source = watching = new();
        }
        _ = Task.Run(() => WatchAsync(source.Token));
    }

    private void Stop()
    {
        CancellationTokenSource? source;
        lock (gate) { source = watching; watching = null; percent = -1; }
        if (source is null) return;
        try { source.Cancel(); source.Dispose(); } catch (ObjectDisposedException) { }
        Send("");
    }

    public void Dispose()
    {
        lock (gate) disposed = true;
        Stop();
    }

    private async Task WatchAsync(CancellationToken token)
    {
        // One PowerShell process polls for the whole download, instead of starting one every half second. It ends if this app does.
        var script = "$ErrorActionPreference='SilentlyContinue'; $end=(Get-Date).AddMinutes(" + (int)MaxWatch.TotalMinutes + "); " +
            $"while((Get-Date) -lt $end -and (Get-Process -Id {Environment.ProcessId})) {{ " +
            "$j = Get-DeliveryOptimizationStatus | Where-Object { $_.Status -eq 'Downloading' -and $_.Priority -eq 'Foreground' } | Select-Object -First 1; " +
            "if ($j -and $j.FileSize -gt 0) { \"$($j.TotalBytesDownloaded)/$($j.FileSize)\" }; Start-Sleep -Milliseconds 500 }";
        var spec = new CommandSpec(powerShell, ["-NoProfile", "-NonInteractive", "-Command", script], MaxWatch + TimeSpan.FromMinutes(1))
        { Progress = new Lines(line => { if (!token.IsCancellationRequested) Update(line); }) };
        try { await runner.RunAsync(spec, token).ConfigureAwait(false); }
        // Without the service's numbers the queue keeps saying "Running"; a missing percentage must never disturb the install.
        catch { }
    }

    private void Update(string line)
    {
        var match = Count().Match(line.Trim());
        if (!match.Success || !long.TryParse(match.Groups[1].Value, out var done) || !long.TryParse(match.Groups[2].Value, out var total) || total <= 0) return;
        var value = (int)Math.Clamp(done * 100 / total, 0, 99);
        lock (gate)
        {
            if (watching is null || value == percent) return;
            percent = value;
        }
        Send($"Downloading {value}%");
    }

    private void Send(string text)
    {
        try { inner.Report(new(text, CommandOutputKind.Transfer)); }
        catch { /* A display failure must not reach the process that is being read. */ }
    }

    /// <summary>Splits what the poller prints into lines, whatever size the chunks arrive in.</summary>
    private sealed class Lines(Action<string> line) : IProgress<CommandOutput>
    {
        private string pending = "";
        public void Report(CommandOutput value)
        {
            if (value.Kind != CommandOutputKind.Output) return;
            var parts = (pending + value.Text).Split('\n');
            pending = parts[^1];
            foreach (var part in parts[..^1]) line(part);
        }
    }
}

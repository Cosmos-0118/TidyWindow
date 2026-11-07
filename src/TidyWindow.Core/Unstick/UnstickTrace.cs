using System.Text;

namespace TidyWindow.Next.Core;

/// <summary>The things one Unstick action did, in order, written for a person to read in Logs. Safe to add to from any thread.</summary>
public sealed class UnstickTrace
{
    private const int MaxLines = 80, MaxLine = 300;
    private readonly List<string> lines = [];
    private readonly object gate = new();
    private int dropped;

    public void Add(string line, bool indent = true)
    {
        line = line.Trim();
        if (line.Length == 0) return;
        if (line.Length > MaxLine) line = line[..MaxLine] + "…";
        lock (gate)
        {
            if (lines.Count >= MaxLines) { dropped++; return; }
            lines.Add(indent ? $"{lines.Count(x => x.Length > 0 && char.IsDigit(x[0])) + 1}. {line}" : "   " + line);
        }
    }

    public string Text
    {
        get
        {
            lock (gate)
            {
                var text = new StringBuilder(string.Join('\n', lines));
                if (dropped > 0) text.Append($"\n   …and {dropped} more lines.");
                return text.ToString();
            }
        }
    }
}

/// <summary>
/// Passes every call to the real system and writes down what was asked and what Windows answered. The runner does not know it is there, so
/// the account in Logs is of what really ran, not of what the page says it does.
/// </summary>
internal sealed class TracedSystem(IUnstickSystem inner, UnstickTrace trace) : IUnstickSystem
{
    private static string Exe(IReadOnlyList<string> programs) => string.Join(", ", programs.Select(x => x.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? x : x + ".exe"));

    public bool Elevated => inner.Elevated;

    public EndResult End(IReadOnlyList<string> programs, string? commandLine = null)
    {
        var what = commandLine is null ? Exe(programs) : $"{Exe(programs)} (only the one started with {commandLine})";
        trace.Add($"Closing {what} for your account…");
        var result = inner.End(programs, commandLine);
        trace.Add(result.Ended == 0 && result.Denied == 0 ? "None of it was running, so nothing was closed.": $"   {Plural(result.Ended, "process")} closed{(result.Denied > 0 ? $", {result.Denied} refused by Windows" : "")}.", indent: false);
        return result;
    }

    public bool Running(IReadOnlyList<string> programs)
    {
        var running = inner.Running(programs);
        trace.Add($"Checked whether {Exe(programs)} is running: {(running ? "yes" : "no")}.");
        return running;
    }

    public bool Start(string file)
    {
        var started = inner.Start(file);
        trace.Add(started ? $"Started {file}." : $"Tried to start {file}, but Windows did not start it.");
        return started;
    }

    public bool HasProgram(string file) => inner.HasProgram(file);

    public async Task RestartExplorerAsync()
    {
        trace.Add("Closing File Explorer (explorer.exe) and starting it again…");
        try { await inner.RestartExplorerAsync().ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { trace.Add($"   It did not restart: {ex.Message}", indent: false); throw; }
        trace.Add("   File Explorer is running again.", indent: false);
    }

    public void RefreshDisplay()
    {
        trace.Add("Pressing Win+Ctrl+Shift+B for you, which asks Windows to redraw the screen…");
        try { inner.RefreshDisplay(); }
        catch (Exception ex) when (ex is not OperationCanceledException) { trace.Add($"   The key press failed: {ex.Message}", indent: false); throw; }
        trace.Add("   The key press was sent.", indent: false);
    }

    public void RefreshAssociations()
    {
        trace.Add("Telling Windows that file associations changed (SHChangeNotify) and that the environment changed (WM_SETTINGCHANGE)…");
        inner.RefreshAssociations();
        trace.Add("   Windows was told.", indent: false);
    }

    public string? CheckClipboard()
    {
        var holder = inner.CheckClipboard();
        trace.Add(holder is null ? "Checked the clipboard: it is free." : $"Checked the clipboard: {holder} is holding it.");
        return holder;
    }

    public bool FlushDns()
    {
        trace.Add("Asking the DNS Client to forget its cached addresses (DnsFlushResolverCache)…");
        var ok = inner.FlushDns();
        trace.Add(ok ? "   Windows cleared the cache." : "   Windows did not clear the cache.", indent: false);
        return ok;
    }

    public UserServiceOutcome RestartUserService(string name)
    {
        trace.Add($"Stopping and starting your {name} service…");
        var outcome = inner.RestartUserService(name);
        trace.Add($"   {Describe(outcome, "restarted")}", indent: false);
        return outcome;
    }

    public UserServiceOutcome RestartServiceHost(string name)
    {
        trace.Add($"Closing the svchost.exe that only hosts your {name} service, so Windows starts it again…");
        var outcome = inner.RestartServiceHost(name);
        trace.Add($"   {Describe(outcome, "closed and started again by Windows")}", indent: false);
        return outcome;
    }

    public async Task<int> RunAsync(string file, string arguments, TimeSpan timeout, CancellationToken token)
    {
        trace.Add($"Running {file} {arguments} (up to {timeout.TotalSeconds:0} s)…");
        var code = await inner.RunAsync(file, arguments, timeout, token).ConfigureAwait(false);
        trace.Add(code == -1 ? $"   {file} did not finish in time and was stopped." : $"   {file} finished with exit code {code}.", indent: false);
        return code;
    }

    private static string Plural(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}es";

    private static string Describe(UserServiceOutcome outcome, string done) => outcome switch
    {
        UserServiceOutcome.Restarted => $"The service was {done}.",
        UserServiceOutcome.Denied => "Windows did not allow it.",
        _ => "The service was not running, so there was nothing to restart."
    };
}

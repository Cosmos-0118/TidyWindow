using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TidyWindow.Next.Core;

/// <summary>One step of a fix, ready to run. The helper never receives a command: it receives the fix id, the options and the step's position, and rebuilds the step from the catalog itself.</summary>
public sealed record FixStepContext(FixItem Item, IReadOnlyList<string> Options, int Index, FixStep Step);

public interface IFixStepExecutor
{
    /// <summary>Runs one step, reporting its output as it arrives, and returns its exit code.</summary>
    /// <exception cref="TimeoutException">The step ran longer than its limit and was stopped.</exception>
    /// <exception cref="OperationCanceledException">The step was stopped before it finished. A step marked uninterruptible is let finish instead, and returns its exit code.</exception>
    Task<int> ExecuteAsync(FixStepContext context, IProgress<CommandOutput> progress, CancellationToken cancellationToken);
}

/// <summary>
/// Runs a fix one step at a time and says what happens as it happens. A step that fails stops the fix, unless it is optional, and so does a stop
/// from outside; either way the steps that put things back still run. Nothing is run that the plan does not contain.
/// </summary>
/// <param name="watching">Told which step is running, and null when none is.</param>
public sealed class FixRunner(IFixStepExecutor executor, Action<FixStepContext?>? watching = null)
{
    private static string Seconds(TimeSpan time) => time.TotalSeconds < 90 ? $"{Math.Max(1, (int)time.TotalSeconds)} s" : $"{(int)time.TotalMinutes} min";

    public async Task<PackageActionResult> RunAsync(FixItem item, IReadOnlyList<string> options, IProgress<CommandOutput> progress, CancellationToken cancellationToken)
    {
        var plan = FixPlan.Build(item, options);
        var summary = new List<string>();
        var restart = false; var warnings = 0; var stopped = false; string? failure = null;
        var started = Stopwatch.StartNew();
        progress.Report(new($"{item.Name}: {plan.Count} {(plan.Count == 1 ? "step" : "steps")}.\n", CommandOutputKind.Information));
        try
        {
            for (var i = 0; i < plan.Count; i++)
            {
                var step = plan[i];
                // After a failure only the steps that put things back are still run. The others are listed, so the record shows what did not happen.
                if (failure is not null && !step.Always) { summary.Add($"NOT RUN  {step.Title} (an earlier step failed)\n         would have run: {Command(step)}"); continue; }
                // A stop asked for between steps takes effect before the next one. The steps that put things back are not what it stops.
                var announced = false;
                if (!stopped && !step.Always && cancellationToken.IsCancellationRequested)
                {
                    stopped = true; announced = true; summary.Add($"STOPPED  before “{step.Title}”\n         would have run: {Command(step)}");
                    progress.Report(new("Stopped. Only the steps that put things back will still run.\n", CommandOutputKind.Warning));
                }
                if (stopped && !step.Always) { if (!announced) summary.Add($"NOT RUN  {step.Title} (stopped)\n         would have run: {Command(step)}"); continue; }
                progress.Report(new($"Step {i + 1} of {plan.Count}: {step.Title}\n", CommandOutputKind.Progress));
                progress.Report(new($"Running: {Command(step)}\n", CommandOutputKind.Information));
                var clock = Stopwatch.StartNew();
                // The Logs page keeps the summary, not the live output, so a step that goes wrong also records the last thing it said.
                var tail = new LastLine(progress);
                var context = new FixStepContext(item, options, Array.IndexOf(item.Steps, step), step);
                watching?.Invoke(context);
                int code;
                // A step that puts things back is never cut short: a service left stopped is worse than a few seconds more.
                try { code = await executor.ExecuteAsync(context, tail, step.Always ? CancellationToken.None : cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    stopped = true; summary.Add(Block($"STOPPED  {step.Title}", step, null, clock.Elapsed, tail));
                    progress.Report(new("Stopped before it finished. Only the steps that put things back will still run.\n", CommandOutputKind.Warning));
                    continue;
                }
                catch (TimeoutException)
                {
                    code = int.MinValue;
                    tail.Report(new($"This step took longer than {step.Minutes} minutes and was stopped.\n", CommandOutputKind.Error));
                }
                catch (Exception ex) when (ex is IOException or Win32Exception or InvalidOperationException or UnauthorizedAccessException)
                {
                    code = int.MinValue;
                    tail.Report(new($"{ex.Message}\n", CommandOutputKind.Error));
                }
                var took = clock.Elapsed;
                if (step.Restart.Contains(code)) { restart = true; summary.Add(Block($"OK       {step.Title} (restart needed to finish)", step, code, took, tail)); progress.Report(new($"Done in {Seconds(took)}. Windows needs a restart to finish this.\n", CommandOutputKind.Success)); }
                else if (step.Ok.Contains(code)) { summary.Add(Block($"OK       {step.Title}", step, code, took, tail)); progress.Report(new($"Done in {Seconds(took)}.\n", CommandOutputKind.Success)); }
                else if (step.Warn.Contains(code)) { warnings++; summary.Add(Block($"WARNING  {step.Title} (exit code {code})", step, code, took, tail)); progress.Report(new($"Finished with exit code {code}. This is reported as a warning, not a failure.\n", CommandOutputKind.Warning)); }
                else if (step.Optional) { warnings++; summary.Add(Block($"SKIPPED  {step.Title} (exit code {Describe(code)})", step, code, took, tail)); progress.Report(new($"This step did not work (exit code {Describe(code)}). It is optional, so the fix goes on.\n", CommandOutputKind.Warning)); }
                else if (failure is not null || stopped) { warnings++; summary.Add(Block($"SKIPPED  {step.Title} (exit code {Describe(code)})", step, code, took, tail)); progress.Report(new($"This clean-up step did not work (exit code {Describe(code)}).\n", CommandOutputKind.Warning)); }
                else { failure = step.Title; summary.Add(Block($"FAILED   {step.Title} (exit code {Describe(code)})", step, code, took, tail)); progress.Report(new($"Failed with exit code {Describe(code)}. Only the steps that put things back will still run.\n", CommandOutputKind.Error)); }
            }
        }
        finally { watching?.Invoke(null); }
        var detail = string.Join('\n', summary) + $"\nTook {Seconds(started.Elapsed)}.";
        if (failure is not null) return new(ActionOutcome.Failed, $"Couldn’t finish {item.Name}", $"Stopped at “{failure}”.\n{detail}");
        if (stopped)
        {
            progress.Report(new($"{item.Name} was stopped before it finished.\n", CommandOutputKind.Warning));
            return new(ActionOutcome.Failed, $"Stopped {item.Name}", $"It was stopped before it finished.\n{detail}");
        }
        restart |= item.Restart == FixRestart.Always;
        if (warnings > 0)
        {
            var gave = warnings == 1 ? "one step" : $"{warnings} steps";
            // A restart that is still needed is the one thing left for the person to do, so a warning must not hide it.
            if (restart)
            {
                progress.Report(new($"{item.Name} finished, but {gave} gave a warning. Restart Windows to finish.\n", CommandOutputKind.Warning));
                return new(ActionOutcome.RestartRequired, $"{item.Name} finished with warnings; restart required", detail);
            }
            progress.Report(new($"{item.Name} finished, but {gave} gave a warning.\n", CommandOutputKind.Warning));
            return new(ActionOutcome.Unverified, $"{item.Name} finished with warnings", detail);
        }
        if (restart)
        {
            progress.Report(new($"{item.Name} finished. Restart Windows to finish.\n", CommandOutputKind.Warning));
            return new(ActionOutcome.RestartRequired, $"{item.Name} finished; restart required", detail);
        }
        progress.Report(new($"{item.Name} finished.\n", CommandOutputKind.Success));
        return new(ActionOutcome.Completed, $"{item.Name} finished", detail);
    }

    /// <summary>What a step runs, as a person would type it. Long PowerShell is cut short; the Fixes page shows it whole.</summary>
    private static string Command(FixStep step) => FixPlan.Describe(step) is { Length: > 400 } text ? text[..400] + "…" : FixPlan.Describe(step);

    private static string Took(TimeSpan time) => time.TotalSeconds < 1 ? "under 1 s" : Seconds(time);

    /// <summary>
    /// One step as the record keeps it: how it ended, the command that ran, its exit code and how long it took, and the last few things it said.
    /// A step that never finished has no exit code to report.
    /// </summary>
    private static string Block(string head, FixStep step, int? code, TimeSpan took, LastLine tail)
    {
        var text = new StringBuilder(head);
        text.Append($"\n         ran: {Command(step)}");
        text.Append(code is { } value ? $"\n         exit code {Describe(value)}, took {Took(took)}" : $"\n         did not finish, ran for {Took(took)}");
        foreach (var line in tail.Recent) text.Append($"\n         “{line}”");
        return text.ToString();
    }

    private static string Describe(int code) => code switch
    {
        int.MinValue => "not available",
        unchecked((int)0xC0000005) => "0xC0000005, the program crashed",
        < 0 => $"{code}, 0x{code:X8}",
        _ => code.ToString(CultureInfo.InvariantCulture)
    };

    /// <summary>Passes output on and remembers the newest few lines that said anything.</summary>
    private sealed class LastLine(IProgress<CommandOutput> inner) : IProgress<CommandOutput>
    {
        private const int Keep = 4;
        private readonly Queue<string> recent = new();
        private readonly object gate = new();

        public string? Text { get { lock (gate) return recent.Count == 0 ? null : recent.Last(); } }

        /// <summary>The newest lines, oldest first, so the record shows how the step ended and not only its last word.</summary>
        public IReadOnlyList<string> Recent { get { lock (gate) return recent.ToArray(); } }

        public void Report(CommandOutput value)
        {
            inner.Report(value);
            if (value.Kind is not (CommandOutputKind.Output or CommandOutputKind.Error or CommandOutputKind.Warning)) return;
            lock (gate)
                foreach (var line in value.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    recent.Enqueue(line.Length > 240 ? line[..240] + "…" : line);
                    if (recent.Count > Keep) recent.Dequeue();
                }
        }
    }
}

/// <summary>
/// Runs queued fixes: finds the fix in the catalog and chooses who runs it, this process or, for machine-wide fixes, the administrator helper after
/// one permission prompt. One fix runs at a time wherever it was asked for (the Queue, the Unstick page): two fixes with their steps interleaved
/// would stop a service the other had just started.
/// </summary>
/// <param name="pendingRestart">Why Windows is waiting for a restart, or null; replaceable in tests. Fixes that mind are told before they run.</param>
public sealed class FixService(IReadOnlyList<FixItem> catalog, IFixStepExecutor local, IFixStepExecutor? elevated, Func<bool> isAdministrator, Func<Task>? unlock, FixRunStore? history = null, Func<DateTimeOffset>? clock = null, Func<bool>? helperOpen = null, Func<string?>? pendingRestart = null, ActivityLog? log = null)
{
    private readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.Now);
    private readonly Func<string?> restartPending = pendingRestart ?? PendingRestart.Find;
    private readonly SemaphoreSlim turn = new(1, 1);
    private volatile FixStepContext? running;

    [SupportedOSPlatform("windows")]
    public static bool IsAdministrator() => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    /// <summary>The step a fix is on right now, or null between steps and when nothing runs. The window's close guard uses it to say what closing would cut off.</summary>
    public FixStepContext? Running => running;

    /// <summary>True from the moment a fix is asked for until its result is recorded, including the time the permission prompt is up.</summary>
    public bool Busy => turn.CurrentCount == 0;

    /// <summary>Completes once no fix is running. A fix asked for in the meantime may start right after.</summary>
    public async Task WhenIdleAsync(CancellationToken cancellationToken = default)
    {
        await turn.WaitAsync(cancellationToken).ConfigureAwait(false);
        turn.Release();
    }

    public async Task<PackageActionResult> RunAsync(FixRun run, IProgress<CommandOutput> progress, CancellationToken cancellationToken)
    {
        var item = catalog.FirstOrDefault(x => x.Id.Equals(run.Id, StringComparison.OrdinalIgnoreCase));
        if (item is null) return new(ActionOutcome.Failed, "That fix is not available", "It is not in this version of TidyWindow.");
        // One that was stopped but is still finishing a step keeps its turn until it really ends, so this one says why it has not started and can itself be stopped.
        if (turn.CurrentCount == 0) progress.Report(new("Another fix is still finishing. This one starts when it has; stop it here to give up waiting.", CommandOutputKind.Notice));
        await turn.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var executor = local;
            if (item.Scope == FixScope.Machine && !isAdministrator())
            {
                if (elevated is null || unlock is null) return Record(item, new(ActionOutcome.Failed, $"Couldn’t run {item.Name}", "This fix needs administrator rights, and the administrator helper is not available."));
                // The helper stays connected between fixes, so the prompt only comes the first time.
                if (helperOpen?.Invoke() != true) progress.Report(new("This fix needs administrator rights. Approve the Windows permission prompt; the output will appear here.", CommandOutputKind.Notice));
                try { await unlock().ConfigureAwait(false); }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return Record(item, new(ActionOutcome.Failed, $"{item.Name} was not run", "Administrator permission was declined. Nothing was changed.")); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                { return Record(item, new(ActionOutcome.Failed, $"Couldn’t run {item.Name}", ex.Message)); }
                executor = elevated;
                progress.Report(new("Running with administrator rights.", CommandOutputKind.Notice));
            }
            var pending = item.RestartFirst ? restartPending() : null;
            if (pending is not null) progress.Report(new($"Windows has a restart pending: {pending}. Until Windows has restarted, this fix can fail or report problems it cannot repair.\n", CommandOutputKind.Warning));
            LogStart(item, run.Options, administrator: executor != local || isAdministrator());
            var result = await new FixRunner(executor, context => running = context).RunAsync(item, run.Options, progress, cancellationToken).ConfigureAwait(false);
            if (pending is not null && result.Outcome != ActionOutcome.Completed)
                result = result with { Detail = result.Detail + $"\nWindows had a restart pending while this ran ({pending}). Restart Windows and run it again before trusting a warning or failure from it." };
            return Record(item, result);
        }
        finally { running = null; turn.Release(); }
    }

    /// <summary>
    /// Says in Logs what is about to run, before it does: who runs it and every command in order. If the app closes or Windows hangs
    /// half way, this is still there; the finished fix adds its own entry with how each step went.
    /// </summary>
    private void LogStart(FixItem item, IReadOnlyList<string> options, bool administrator)
    {
        if (log is null) return;
        var plan = FixPlan.Build(item, options);
        var text = new StringBuilder();
        text.AppendLine(administrator ? "Runs with administrator rights." : "Runs as you, without administrator permission.");
        var chosen = item.Options.Where(x => options.Contains(x.Id, StringComparer.OrdinalIgnoreCase)).Select(x => x.Label).ToList();
        if (chosen.Count > 0) text.AppendLine("Options on: " + string.Join("; ", chosen) + ".");
        text.AppendLine($"{plan.Count} {(plan.Count == 1 ? "step" : "steps")}, in this order:");
        for (var i = 0; i < plan.Count; i++)
        {
            var command = FixPlan.Describe(plan[i]);
            text.AppendLine($"{i + 1}. {plan[i].Title}").Append("      ").AppendLine(command.Length > 300 ? command[..300] + "…" : command);
        }
        log.Write(ActivityLevel.Info, ActivityArea.App, $"Started: {item.Name}", text.ToString());
    }

    private PackageActionResult Record(FixItem item, PackageActionResult result)
    {
        try { history?.Set(new(item.Id, now(), result.Outcome.ToString(), result.Message, result.Detail)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* A note about the last run is never worth failing a fix for. */ }
        return result;
    }
}

/// <summary>How the last run of a fix went. Detail is the step-by-step summary, so the reason for a failure is still there after the app restarts.</summary>
public sealed record FixRunNote(string Id, DateTimeOffset When, string Outcome, string Message, string Detail = "");

/// <summary>The outcome of the last run of each fix, kept on disk so the page can say when it was last used.</summary>
public sealed class FixRunStore(string folder)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly object gate = new();
    private string FilePath => Path.Combine(folder, "last-runs.json");

    public IReadOnlyList<FixRunNote> All() { lock (gate) return Load(); }

    public FixRunNote? Get(string id) { lock (gate) return Load().FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase)); }

    public void Set(FixRunNote note)
    {
        lock (gate)
        {
            var list = Load();
            list.RemoveAll(x => string.Equals(x.Id, note.Id, StringComparison.OrdinalIgnoreCase));
            list.Add(note);
            Directory.CreateDirectory(folder);
            var temp = FilePath + ".tmp";
            System.IO.File.WriteAllText(temp, JsonSerializer.Serialize(list, Json));
            System.IO.File.Move(temp, FilePath, overwrite: true);
        }
    }

    private List<FixRunNote> Load()
    {
        try { return JsonSerializer.Deserialize<List<FixRunNote>>(System.IO.File.ReadAllText(FilePath), Json) ?? []; }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return []; }
    }
}

/// <summary>
/// Turns the raw text of a program into lines. Programs that redraw one progress line with a bare carriage return (DISM, chkdsk, sfc) would
/// otherwise produce hundreds of lines, so those updates are shown about every ten percent.
/// </summary>
public sealed partial class OutputSplitter(Action<string, CommandOutputKind> report)
{
    private readonly StringBuilder current = new();
    private string? held;
    private int lastBucket = -1;
    private long lastProgressTick;
    private string lastProgress = "";
    private readonly Func<long> ticks = () => Environment.TickCount64;

    [GeneratedRegex(@"(\d{1,3}(?:[.,]\d+)?)\s*%")]
    private static partial Regex Percent();

    [GeneratedRegex(@"^\[[= ]*\d{1,3}(?:[.,]\d+)?%[= ]*\]$")]
    private static partial Regex Bar();

    public void Feed(string text, CommandOutputKind kind)
    {
        foreach (var c in text)
        {
            if (c == '\r')
            {
                // Two carriage returns in a row before a line feed are one line ending, not two redraws.
                if (held is null && current.Length > 0) { held = current.ToString(); current.Clear(); }
            }
            else if (c == '\n')
            {
                if (held is not null) { Line(held, kind); held = null; }
                else if (current.Length > 0) { Line(current.ToString(), kind); current.Clear(); }
            }
            else
            {
                if (held is not null) { Progress(held, kind); held = null; }
                current.Append(c);
            }
        }
    }

    public void Finish(CommandOutputKind kind)
    {
        if (held is not null) { Line(held, kind); held = null; }
        if (current.Length > 0) { Line(current.ToString(), kind); current.Clear(); }
    }

    private void Line(string text, CommandOutputKind kind)
    {
        text = text.TrimEnd();
        if (text.Length == 0) return;
        // DISM ends every update of its bar with a full line ending (\r[====  4.9%  ] \r\n), so it has to be recognised by its look.
        if (Bar().IsMatch(text)) { Progress(text, kind); return; }
        lastBucket = -1; lastProgress = "";
        report(text, kind);
    }

    private void Progress(string text, CommandOutputKind kind)
    {
        text = text.Trim();
        if (text.Length == 0) return;
        if (Percent().Match(text) is { Success: true } match && double.TryParse(match.Groups[1].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
        {
            var bucket = (int)Math.Clamp(percent / 10, 0, 10);
            if (bucket == lastBucket) return;
            lastBucket = bucket;
        }
        else
        {
            var tick = ticks();
            if (text == lastProgress || tick - lastProgressTick < 1000) return;
            lastProgress = text; lastProgressTick = tick;
        }
        report(text, kind);
    }
}

/// <summary>The parts of running a fix that are plain logic, apart from the Windows-only executor.</summary>
public static class FixSupport
{
    /// <summary>The exit codes of a service step. 3 means the service is turned off, which no fix may change; 4 means this PC does not have the service.</summary>
    public const int ServiceDisabled = 3, ServiceMissing = 4;

    // A service that was only just stopped can fail its first start (Windows Search does after its database is deleted) and be restarted by Windows
    // itself about 30 seconds later, so a start is tried a few times and then waited for, up to 75 seconds, before it counts as failed.
    private const string StartScript = """
        $ErrorActionPreference = 'Stop'
        try {
          $service = Microsoft.PowerShell.Management\Get-Service -Name '__NAME__' -ErrorAction SilentlyContinue
          if (-not $service) { '__NAME__ is not on this PC, so there is nothing to start.'; exit __MISSING__ }
          if ($service.Status -eq 'Running') { "$($service.DisplayName) is already running."; exit 0 }
          if ($service.StartType -eq 'Disabled') { "$($service.DisplayName) is turned off (its startup type is Disabled), so Windows cannot start it. Something else on this PC turned it off. Set its startup type to Manual or Automatic in Services, then run this fix again."; exit __DISABLED__ }
          $limit = [DateTime]::UtcNow.AddSeconds(75); $tries = 0; $problem = ''; $denied = $false
          do {
            if ($service.Status -eq 'Stopped' -and $tries -lt 3) {
              $tries++
              try { Microsoft.PowerShell.Management\Start-Service -Name '__NAME__' -WarningAction SilentlyContinue }
              catch { $inner = $_.Exception; while ($inner.InnerException) { $inner = $inner.InnerException }; $problem = $inner.Message; if ($inner -is [System.ComponentModel.Win32Exception] -and $inner.NativeErrorCode -eq 5) { $denied = $true } }
            }
            $service.Refresh()
            if ($service.Status -ne 'Running') { Microsoft.PowerShell.Utility\Start-Sleep -Seconds 2; $service.Refresh() }
          } while ($service.Status -ne 'Running' -and -not $denied -and [DateTime]::UtcNow -lt $limit)
          if ($service.Status -eq 'Running') { "$($service.DisplayName) is running." } elseif ($denied) { "Windows refused to start __NAME__ ($problem). This needs administrator rights."; exit 1 } else { "Could not start __NAME__ within a minute. $problem"; exit 1 }
        } catch { 'Could not start __NAME__: ' + $_.Exception.Message; exit 1 }
        """;

    private const string StopScript = """
        $ErrorActionPreference = 'Stop'
        try {
          $service = Microsoft.PowerShell.Management\Get-Service -Name '__NAME__' -ErrorAction SilentlyContinue
          if (-not $service) { '__NAME__ is not on this PC, so there is nothing to stop.'; exit __MISSING__ }
          if ($service.Status -eq 'Stopped') { "$($service.DisplayName) is already stopped."; exit 0 }
          $limit = [DateTime]::UtcNow.AddSeconds(60); $tries = 0; $problem = ''; $denied = $false
          do {
            if ($service.Status -ne 'Stopped' -and $service.Status -ne 'StopPending' -and $tries -lt 3) {
              $tries++
              try { Microsoft.PowerShell.Management\Stop-Service -Name '__NAME__' -Force -WarningAction SilentlyContinue }
              catch { $inner = $_.Exception; while ($inner.InnerException) { $inner = $inner.InnerException }; $problem = $inner.Message; if ($inner -is [System.ComponentModel.Win32Exception] -and $inner.NativeErrorCode -eq 5) { $denied = $true } }
            }
            $service.Refresh()
            if ($service.Status -ne 'Stopped') { Microsoft.PowerShell.Utility\Start-Sleep -Seconds 2; $service.Refresh() }
          } while ($service.Status -ne 'Stopped' -and -not $denied -and [DateTime]::UtcNow -lt $limit)
          if ($service.Status -eq 'Stopped') { "$($service.DisplayName) is stopped." } elseif ($denied) { "Windows refused to stop __NAME__ ($problem). This needs administrator rights."; exit 1 } else { "Could not stop __NAME__ within a minute. $problem"; exit 1 }
        } catch { 'Could not stop __NAME__: ' + $_.Exception.Message; exit 1 }
        """;

    /// <summary>
    /// Starting or stopping a service as a program run, so its output, time limit and stop handling are the same as any other step. The name is checked
    /// against the guard's list and only then written into the command, between quotes.
    /// net.exe is not used for this: it ends with 2 both when a service is already in the wanted state and when it is turned off or broken, so a service
    /// that never started looked like a step that worked.
    /// </summary>
    public static FixStepContext ServiceStep(FixStepContext context)
    {
        var step = context.Step;
        if (!FixGuard.AllowsService(step.Service)) throw new InvalidOperationException("That service cannot be controlled here.");
        var template = step.Kind == FixStepKind.StartService ? StartScript : StopScript;
        var script = template.Replace("__NAME__", step.Service).Replace("__DISABLED__", ServiceDisabled.ToString(CultureInfo.InvariantCulture)).Replace("__MISSING__", ServiceMissing.ToString(CultureInfo.InvariantCulture));
        var run = new FixStep { Title = step.Title, File = "powershell.exe", Args = ["-NoProfile", "-NonInteractive", "-Command", script], Encoding = FixEncoding.Utf8, Minutes = step.Minutes, Ok = step.Ok, Warn = step.Warn };
        return context with { Step = run };
    }

    /// <summary>Takes the read-only and system marks off everything in a folder so it can be deleted. Links are left alone: what they point at is not ours to touch.</summary>
    public static void ClearReadOnly(string folder)
    {
        var stack = new Stack<string>();
        stack.Push(folder);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            foreach (var entry in new DirectoryInfo(current).EnumerateFileSystemInfos())
            {
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                try
                {
                    if (entry.Attributes.HasFlag(FileAttributes.ReadOnly) || entry.Attributes.HasFlag(FileAttributes.System)) entry.Attributes = FileAttributes.Normal;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* The delete will say what is wrong. */ }
                if (entry is DirectoryInfo) stack.Push(entry.FullName);
            }
        }
    }
}

/// <summary>The real thing: starts programs and does the few file steps, reading output as it arrives.</summary>
/// <param name="pipeGrace">Once a program has ended, how long whatever it started gets to let go of its output before the step moves on without it.</param>
[SupportedOSPlatform("windows")]
public sealed class FixStepExecutor(TimeSpan? pipeGrace = null) : IFixStepExecutor
{
    static FixStepExecutor() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private static readonly TimeSpan KillWait = TimeSpan.FromSeconds(10);
    private readonly TimeSpan pipeGrace = pipeGrace ?? TimeSpan.FromSeconds(5);

    public async Task<int> ExecuteAsync(FixStepContext context, IProgress<CommandOutput> progress, CancellationToken cancellationToken)
    {
        var step = context.Step;
        switch (step.Kind)
        {
            case FixStepKind.Run: return await RunProgramAsync(context, progress, cancellationToken).ConfigureAwait(false);
            case FixStepKind.DeleteFiles: return await Task.Run(() => DeleteFiles(context, progress), cancellationToken).ConfigureAwait(false);
            case FixStepKind.RenameFolder: return await Task.Run(() => RenameFolder(context, progress, cancellationToken), cancellationToken).ConfigureAwait(false);
            case FixStepKind.StartService:
            case FixStepKind.StopService: return await RunProgramAsync(FixSupport.ServiceStep(context), progress, cancellationToken).ConfigureAwait(false);
            case FixStepKind.SetRegistry: return await Task.Run(() => SetRegistry(context, progress), cancellationToken).ConfigureAwait(false);
            case FixStepKind.DeleteRegistry: return await Task.Run(() => DeleteRegistry(context, progress), cancellationToken).ConfigureAwait(false);
            case FixStepKind.StopExplorer:
            case FixStepKind.StartExplorer:
            case FixStepKind.RestartExplorer:
                if (context.Item.Scope != FixScope.User) throw new InvalidOperationException("That step cannot be run here.");
                if (step.Kind != FixStepKind.StartExplorer) await ExplorerShell.StopAsync().ConfigureAwait(false);
                if (step.Kind != FixStepKind.StopExplorer) await ExplorerShell.StartAsync().ConfigureAwait(false);
                progress.Report(new(step.Kind switch { FixStepKind.StopExplorer => "File Explorer was closed. The taskbar is gone until the next step.\n", FixStepKind.StartExplorer => "File Explorer is running again.\n", _ => "File Explorer was restarted.\n" }, CommandOutputKind.Information));
                return 0;
            default: throw new InvalidOperationException("That step cannot be run here.");
        }
    }

    private static int SetRegistry(FixStepContext context, IProgress<CommandOutput> progress)
    {
        var step = context.Step;
        if (!FixGuard.AllowsRegistry(context.Item.Scope, step.Path, step.Name, step.Data)) throw new InvalidOperationException("That setting cannot be changed here.");
        using var subKey = OpenKey(step.Path, writable: true);
        if (subKey is null) { progress.Report(new($"{step.Path} does not exist, so there is nothing to reset.\n", CommandOutputKind.Information)); return 0; }
        var before = subKey.GetValue(step.Name);
        subKey.SetValue(step.Name, step.Data, Microsoft.Win32.RegistryValueKind.DWord);
        progress.Report(new($"Set {step.Name} to {step.Data} (it was {before ?? "not set"}).\n", CommandOutputKind.Information));
        TellProgramsAboutProxyChange(step.Path);
        return 0;
    }

    private static int DeleteRegistry(FixStepContext context, IProgress<CommandOutput> progress)
    {
        var step = context.Step;
        if (!FixGuard.AllowsRegistryRemoval(context.Item.Scope, step.Path, step.Name)) throw new InvalidOperationException("That setting cannot be removed here.");
        using var subKey = OpenKey(step.Path, writable: true);
        var before = subKey?.GetValue(step.Name);
        if (before is null) { progress.Report(new($"{step.Name} is not set, so there is nothing to remove.\n", CommandOutputKind.Information)); return 0; }
        // The old value is written out so that a proxy someone did want can be typed in again.
        subKey!.DeleteValue(step.Name, throwOnMissingValue: false);
        progress.Report(new($"Removed {step.Name} (it was {before}).\n", CommandOutputKind.Information));
        TellProgramsAboutProxyChange(step.Path);
        return 0;
    }

    // The hive is the part before the first backslash. Only the keys on the guard's lists get this far. The 64-bit view is used so that a 32-bit build sees the same keys.
    private static Microsoft.Win32.RegistryKey? OpenKey(string path, bool writable)
    {
        var split = path.IndexOf('\\');
        var hive = path[..split] switch
        {
            "HKLM" => Microsoft.Win32.RegistryHive.LocalMachine,
            "HKCU" => Microsoft.Win32.RegistryHive.CurrentUser,
            _ => throw new InvalidOperationException("That setting cannot be changed here.")
        };
        using var root = Microsoft.Win32.RegistryKey.OpenBaseKey(hive, Microsoft.Win32.RegistryView.Registry64);
        return root.OpenSubKey(path[(split + 1)..], writable);
    }

    [System.Runtime.InteropServices.DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetSetOption(IntPtr internet, int option, IntPtr buffer, int length);

    // Programs that are already running read the proxy once and keep it. Windows' own Settings page sends these two notices after it changes a proxy,
    // and so does this, so a browser that is open picks the change up without being restarted.
    private static void TellProgramsAboutProxyChange(string path)
    {
        if (!path.EndsWith("Internet Settings", StringComparison.OrdinalIgnoreCase)) return;
        const int SettingsChanged = 39, Refresh = 37;
        try { InternetSetOption(IntPtr.Zero, SettingsChanged, IntPtr.Zero, 0); InternetSetOption(IntPtr.Zero, Refresh, IntPtr.Zero, 0); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { /* The setting itself was changed; only the notice failed. */ }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint GetOEMCP();

    /// <summary>
    /// What console programs such as DISM write in. A culture's own OEM code page can be 1 ("none", as for Hindi, Tamil, Kannada and other Indian languages),
    /// which is not an encoding at all, so Windows is asked which one is really in use and anything unusable falls back to UTF-8.
    /// </summary>
    internal static Encoding OemEncoding()
    {
        foreach (var page in new[] { (int)GetOEMCP(), CultureInfo.CurrentCulture.TextInfo.OEMCodePage })
        {
            if (page <= 1) continue;
            try { return Encoding.GetEncoding(page); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { }
        }
        return new UTF8Encoding(false);
    }

    /// <summary>
    /// The module folders a step's PowerShell should use: the Program Files and System32 ones, which only administrators can write to. This is
    /// defence in depth only: Windows PowerShell prepends the account's Documents module folder on startup whatever this is set to, so the real
    /// protection is that every cmdlet in the catalog names its module (the guard enforces it), which stops discovery searching the path at all.
    /// </summary>
    internal static string ModulePath() => string.Join(';',
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsPowerShell", "Modules"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "Modules"));

    private async Task<int> RunProgramAsync(FixStepContext context, IProgress<CommandOutput> progress, CancellationToken cancellationToken)
    {
        var step = context.Step;
        var path = FixGuard.Resolve(step.File);
        if (!FixGuard.IsKnownProgram(step.File)) throw new InvalidOperationException("That program cannot be run here.");
        // OneDrive, for one, may simply not be installed; saying so is better than an exit code.
        if (path is null || !File.Exists(path)) throw new InvalidOperationException($"{(step.File.Equals("onedrive", StringComparison.OrdinalIgnoreCase) ? "OneDrive" : step.File)} is not installed on this PC, so there is nothing to run.");
        var encoding = step.Encoding switch
        {
            FixEncoding.Utf16 => Encoding.Unicode,
            FixEncoding.Utf8 => new UTF8Encoding(false),
            _ => OemEncoding()
        };
        var start = new ProcessStartInfo(path)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = encoding,
            StandardErrorEncoding = encoding,
            // A program's starting folder is on its DLL search path, so it is never one this account can write to.
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System)
        };
        var powershell = step.File.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase);
        foreach (var argument in step.Args)
        {
            var text = Environment.ExpandEnvironmentVariables(argument);
            // Windows PowerShell writes to a pipe in the console's code page unless told otherwise.
            start.ArgumentList.Add(powershell && step.Encoding == FixEncoding.Utf8 && argument == step.Args[^1] ? "[Console]::OutputEncoding = [Text.Encoding]::UTF8; " + text : text);
        }
        if (powershell) start.Environment["PSModulePath"] = ModulePath();
        start.Environment["NO_COLOR"] = "1";
        using var process = new Process { StartInfo = start };
        await Task.Run(process.Start, cancellationToken).ConfigureAwait(false);
        using var drain = new CancellationTokenSource();
        var pumps = new Pumps(progress);
        var output = pumps.RunAsync(process.StandardOutput, CommandOutputKind.Output, powershell, drain.Token);
        // A step that accepts a non-zero exit code (net says "already started" with 2) is not an error, so its messages are not shown in red.
        var error = pumps.RunAsync(process.StandardError, step.Ok.Any(code => code != 0) ? CommandOutputKind.Warning : CommandOutputKind.Error, powershell, drain.Token);
        var reading = Task.WhenAll(output, error);
        if (step.Uninterruptible) await WaitUninterruptedAsync(process, step, progress, cancellationToken).ConfigureAwait(false);
        else
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(step.Minutes));
            try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or AggregateException) { }
                try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(KillWait).ConfigureAwait(false); } catch (TimeoutException) { }
                await FinishAsync(reading, drain, pumps, progress).ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested) throw;
                throw new TimeoutException();
            }
        }
        await FinishAsync(reading, drain, pumps, progress).ConfigureAwait(false);
        return process.ExitCode;
    }

    /// <summary>
    /// DISM, SFC and the WMI salvage change Windows in place; ended half way they leave it damaged, which is worse than any wait. So the program runs
    /// to the end whatever happens: a stop (the app closing, the queue being stopped) and the time limit are only said in the output.
    /// </summary>
    private static async Task WaitUninterruptedAsync(Process process, FixStep step, IProgress<CommandOutput> progress, CancellationToken cancellationToken)
    {
        void Note(string text) { try { progress.Report(new(text, CommandOutputKind.Warning)); } catch { /* A display failure must not stop the step. */ } }
        var exited = process.WaitForExitAsync(CancellationToken.None);
        using var noticed = cancellationToken.Register(() => Note($"A stop was asked for, but {step.Title} is allowed to finish: ending {step.File} half way could leave Windows damaged.\n"));
        // WaitAsync keeps its own timer and leaves the exit task to be awaited below, so nothing is left running unobserved.
        try { await exited.WaitAsync(TimeSpan.FromMinutes(step.Minutes)).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            Note($"This step has been running for more than {step.Minutes} minutes. It is not stopped, because ending {step.File} half way could leave Windows damaged; it finishes when Windows is done.\n");
            await exited.ConfigureAwait(false);
        }
    }

    /// <summary>Lets the output pipes finish for a moment, then stops reading them. Whatever arrived by then is kept; anything later belongs to something else.</summary>
    private async Task FinishAsync(Task reading, CancellationTokenSource drain, Pumps pumps, IProgress<CommandOutput> progress)
    {
        if (await ProcessRunner.FinishReadingAsync(reading, drain, pipeGrace, KillWait).ConfigureAwait(false)) return;
        pumps.Close();
        try { progress.Report(new("Something this step started is still running in the background, so its output is no longer followed.\n", CommandOutputKind.Information)); }
        catch { /* A display failure must not fail the step. */ }
    }

    /// <summary>Reads a program's two pipes into lines. Once closed, whatever still arrives is dropped: it comes from something the step left behind.</summary>
    private sealed class Pumps(IProgress<CommandOutput> progress)
    {
        private volatile bool closed;

        public void Close() => closed = true;

        public async Task RunAsync(StreamReader reader, CommandOutputKind kind, bool powershell, CancellationToken stop)
        {
            var decoder = powershell ? new PowerShellOutputDecoder(kind) : null;
            var splitter = new OutputSplitter((text, k) => { if (closed) return; try { progress.Report(new(text + "\n", k)); } catch { /* A display failure must not stop draining the pipe. */ } });
            var buffer = new char[4096];
            try
            {
                int count;
                while ((count = await reader.ReadAsync(buffer.AsMemory(), stop).ConfigureAwait(false)) > 0)
                {
                    var text = new string(buffer, 0, count);
                    if (decoder is null) splitter.Feed(text, kind);
                    else foreach (var message in decoder.Feed(text)) splitter.Feed(message.Text, message.Kind);
                }
            }
            // Reading was given up on (see FinishAsync) or the pipe broke: keep what arrived.
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException) { }
            if (decoder is not null) foreach (var message in decoder.Feed("", final: true)) splitter.Feed(message.Text, message.Kind);
            splitter.Finish(kind);
        }
    }

    private static int DeleteFiles(FixStepContext context, IProgress<CommandOutput> progress)
    {
        var step = context.Step;
        foreach (var pattern in step.Patterns)
            if (!FixGuard.AllowsDelete(context.Item.Scope, step.Path, pattern)) throw new InvalidOperationException("That folder cannot be cleared here.");
        var folder = Environment.ExpandEnvironmentVariables(step.Path);
        if (!Directory.Exists(folder)) { progress.Report(new($"{folder} does not exist, so there is nothing to remove.\n", CommandOutputKind.Information)); return 0; }
        int removed = 0, kept = 0;
        foreach (var pattern in step.Patterns)
            foreach (var file in Directory.EnumerateFiles(folder, pattern, SearchOption.TopDirectoryOnly).ToList())
            {
                try { File.SetAttributes(file, FileAttributes.Normal); File.Delete(file); removed++; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { kept++; }
            }
        progress.Report(new($"Removed {removed} {(removed == 1 ? "file" : "files")} from {folder}.\n", CommandOutputKind.Information));
        if (kept > 0) progress.Report(new($"{kept} {(kept == 1 ? "file was" : "files were")} in use and left as they are.\n", CommandOutputKind.Warning));
        return kept > 0 ? 1 : 0;
    }

    private static int RenameFolder(FixStepContext context, IProgress<CommandOutput> progress, CancellationToken cancellationToken, int attempts = 3, TimeSpan? pause = null)
    {
        var step = context.Step;
        if (!FixGuard.AllowsRename(context.Item.Scope, step.Path)) throw new InvalidOperationException("That folder cannot be renamed here.");
        var folder = Environment.ExpandEnvironmentVariables(step.Path);
        var backup = folder + ".bak";
        if (!Directory.Exists(folder)) { progress.Report(new($"{folder} does not exist, so there is nothing to rename.\n", CommandOutputKind.Information)); return 0; }
        if (Directory.Exists(backup))
        {
            progress.Report(new($"Removing the older copy, {backup}.\n", CommandOutputKind.Information));
            // Directory.Delete cannot remove a read-only file or folder, and the update folders keep some, so a second run after a first would stop here.
            FixSupport.ClearReadOnly(backup);
            Directory.Delete(backup, recursive: true);
        }
        // A service that was only just stopped can still be letting go of its files, and Windows may start one again by itself within seconds.
        for (var attempt = 1; ; attempt++)
        {
            try { Directory.Move(folder, backup); break; }
            catch (Exception ex) when (attempt < attempts && ex is IOException or UnauthorizedAccessException)
            {
                progress.Report(new($"{Path.GetFileName(folder)} is still in use ({ex.Message.TrimEnd()}). Trying again in a moment.\n", CommandOutputKind.Warning));
                cancellationToken.WaitHandle.WaitOne(pause ?? TimeSpan.FromSeconds(2));
            }
        }
        progress.Report(new($"Renamed {folder} to {Path.GetFileName(backup)}. Windows makes a fresh one.\n", CommandOutputKind.Information));
        return 0;
    }
}

public sealed record FixMessage(string Op, string? Id = null, string[]? Options = null, int Index = 0);
/// <summary>What the helper sends back: "ok" for the greeting, "out" for a line of output, "done" with the exit code, "timeout", "cancelled" or "fail".</summary>
public sealed record FixEvent(string Type, string Text = "", CommandOutputKind Kind = CommandOutputKind.Output, int Code = 0, string Error = "");

/// <summary>
/// The elevated side of fixes. It runs one step of one machine-wide catalog fix, identified by id, options and position, and sends the
/// output back line by line as it is produced. It cannot be asked to run anything else, and it leaves by itself when idle.
/// </summary>
public static class FixHost
{
    public const string Command = "--fix";
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public static async Task<int> RunAsync(string[] args, IFixStepExecutor? executor = null, Func<bool>? isAdministrator = null, TimeSpan? idle = null, IReadOnlyList<FixItem>? catalog = null)
    {
        if (args.Length < 2 || args[0] != Command) return 64;
        if (executor is null)
        {
            if (!OperatingSystem.IsWindows()) return 66;
            executor = new FixStepExecutor();
        }
        catalog ??= FixCatalog.Load();
        isAdministrator ??= () => OperatingSystem.IsWindows() && new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        using var pipe = await HelperPipe.TryConnectAsync(args[1]).ConfigureAwait(false);
        if (pipe is null) return 65;
        var reader = new PipeLineReader(pipe);
        var writer = new StreamWriter(pipe) { AutoFlush = true };
        var gate = new object();

        void Send(FixEvent value)
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
            FixMessage? message;
            try { message = JsonSerializer.Deserialize<FixMessage>(line, Json); }
            catch (JsonException) { message = null; }
            if (message is null) { Send(new("fail", Error: "That request was not understood.")); continue; }

            switch (message.Op)
            {
                case "bye": return 0;
                // A cancel that arrived while a step ran was already acted on; there is nothing to answer.
                case "cancel": break;
                case "hello": admin = isAdministrator(); Send(new(admin ? "ok" : "fail", Error: admin ? "" : "The helper did not get administrator permission.")); break;
                case "step" when admin:
                    var item = catalog.FirstOrDefault(x => string.Equals(x.Id, message.Id, StringComparison.OrdinalIgnoreCase));
                    var plan = item is null ? null : FixPlan.Build(item, message.Options ?? []);
                    if (item is null || item.Scope != FixScope.Machine || message.Index < 0 || message.Index >= item.Steps.Length || plan is null || !plan.Contains(item.Steps[message.Index]))
                    { Send(new("fail", Error: "That step is not one the helper runs.")); break; }
                    // While a step runs, anything arriving on the pipe (a cancel, or the app closing) asks it to stop. A step that must not be interrupted
                    // takes that as a notice and finishes first; the helper stays until it has.
                    using (var cancel = new CancellationTokenSource())
                    {
                        pending = reader.ReadLineAsync();
                        _ = pending.ContinueWith(_ => { try { cancel.Cancel(); } catch (ObjectDisposedException) { } }, TaskScheduler.Default);
                        try
                        {
                            var code = await executor.ExecuteAsync(new(item, message.Options ?? [], message.Index, item.Steps[message.Index]), new Reporter(output => Send(new("out", output.Text, output.Kind))), cancel.Token).ConfigureAwait(false);
                            Send(new("done", Code: code));
                        }
                        catch (TimeoutException) { Send(new("timeout")); }
                        catch (OperationCanceledException) { Send(new("cancelled")); }
                        catch (Exception ex) when (ex is IOException or Win32Exception or InvalidOperationException or UnauthorizedAccessException) { Send(new("fail", Error: ex.Message)); }
                        // Whatever else goes wrong in a step must be reported, not end the helper: a helper that just vanishes tells the person nothing.
                        catch (Exception ex) { Send(new("fail", Error: "The step could not be run: " + ex.Message)); }
                    }
                    break;
                default: Send(new("fail", Error: "That request was not understood.")); break;
            }
        }
    }

    private sealed class Reporter(Action<CommandOutput> report) : IProgress<CommandOutput>
    {
        public void Report(CommandOutput value) => report(value);
    }
}

/// <summary>The app side: starts the helper through the Windows permission prompt, sends it one step at a time, and passes its output on as it arrives.</summary>
/// <param name="replyTimeout">How long the helper gets to answer a greeting. A connected helper answers at once; silence means it died, or something else holds the pipe.</param>
public sealed class FixSession(Func<string, Task> launch, TimeSpan? replyTimeout = null) : IFixStepExecutor, IDisposable
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private readonly TimeSpan replyTimeout = replyTimeout ?? TimeSpan.FromSeconds(15);
    private readonly SemaphoreSlim one = new(1, 1);
    private readonly object gate = new();
    private NamedPipeServerStream? pipe;
    private PipeLineReader? reader;
    private StreamWriter? writer;

    public bool IsOpen => pipe is { IsConnected: true };

    public static FixSession Elevated(string helperPath) => new(name => HelperPipe.LaunchElevated(helperPath, FixHost.Command, name));

    /// <summary>
    /// Asks for permission and connects, or makes sure the helper from last time is still there. The helper leaves by itself after five idle minutes,
    /// and a pipe only shows that once something is written to it, so a greeting goes first: the first step of the next fix must not be what finds out.
    /// Throws <see cref="OperationCanceledException"/> if the person says no.
    /// </summary>
    public async Task OpenAsync(CancellationToken cancellationToken = default)
    {
        await one.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            PipeLineReader? input; StreamWriter? output;
            lock (gate) { input = reader; output = writer; }
            if (IsOpen && input is not null && output is not null && await GreetAsync(input, output, cancellationToken).ConfigureAwait(false) is { Type: "ok" }) return;
            Close();
            var name = HelperPipe.NewName("fix");
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

    /// <summary>Sends the greeting and reads the answer. Null when the helper is gone: the pipe broke or closed, or nothing readable came back in time.</summary>
    private async Task<FixEvent?> GreetAsync(PipeLineReader input, StreamWriter output, CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(replyTimeout);
        try
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(new FixMessage("hello"), Json).AsMemory(), limit.Token).ConfigureAwait(false);
            var line = await input.ReadLineAsync(limit.Token).ConfigureAwait(false);
            return line is null ? null : JsonSerializer.Deserialize<FixEvent>(line, Json);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return null; }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or JsonException) { return null; }
    }

    public async Task<int> ExecuteAsync(FixStepContext context, IProgress<CommandOutput> progress, CancellationToken cancellationToken)
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
                lock (gate) output.WriteLine(JsonSerializer.Serialize(new FixMessage("step", context.Item.Id, context.Options.ToArray(), context.Index), Json));
                using var register = cancellationToken.Register(() => { try { lock (gate) output.WriteLine(JsonSerializer.Serialize(new FixMessage("cancel"), Json)); } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { } });
                while (true)
                {
                    var line = await input.ReadLineAsync(CancellationToken.None).ConfigureAwait(false) ?? throw new IOException("The administrator helper stopped. Try again.");
                    var value = JsonSerializer.Deserialize<FixEvent>(line, Json) ?? throw new IOException("The administrator helper sent something unreadable.");
                    switch (value.Type)
                    {
                        case "out": progress.Report(new(value.Text, value.Kind)); break;
                        case "done": return value.Code;
                        case "timeout": throw new TimeoutException();
                        case "cancelled": throw new OperationCanceledException("The step was stopped before it finished.", cancellationToken);
                        default: throw new InvalidOperationException(value.Error.Length > 0 ? value.Error : "The administrator helper could not run that step.");
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
        try { writer?.WriteLine(JsonSerializer.Serialize(new FixMessage("bye"), Json)); } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        try { pipe?.Dispose(); } catch (IOException) { }
        pipe = null; reader = null; writer = null;
    }

    public void Dispose() { Close(); one.Dispose(); }
}

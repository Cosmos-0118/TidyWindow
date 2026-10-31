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
    Task<int> ExecuteAsync(FixStepContext context, IProgress<CommandOutput> progress, CancellationToken cancellationToken);
}

/// <summary>
/// Runs a fix one step at a time and says what happens as it happens. A step that fails stops the fix, unless it is optional.
/// Nothing is run that the plan does not contain.
/// </summary>
public sealed class FixRunner(IFixStepExecutor executor)
{
    private static string Seconds(TimeSpan time) => time.TotalSeconds < 90 ? $"{Math.Max(1, (int)time.TotalSeconds)} s" : $"{(int)time.TotalMinutes} min";

    public async Task<PackageActionResult> RunAsync(FixItem item, IReadOnlyList<string> options, IProgress<CommandOutput> progress, CancellationToken cancellationToken)
    {
        var plan = FixPlan.Build(item, options);
        var summary = new List<string>();
        var restart = false; var warnings = 0; string? failure = null;
        var started = Stopwatch.StartNew();
        progress.Report(new($"{item.Name}: {plan.Count} {(plan.Count == 1 ? "step" : "steps")}.\n", CommandOutputKind.Information));
        for (var i = 0; i < plan.Count; i++)
        {
            var step = plan[i];
            // After a failure only the steps that put things back are still run.
            if (failure is not null && !step.Always) continue;
            progress.Report(new($"Step {i + 1} of {plan.Count}: {step.Title}\n", CommandOutputKind.Progress));
            var clock = Stopwatch.StartNew();
            // The Logs page keeps the summary, not the live output, so a step that goes wrong also records the last thing it said.
            var tail = new LastLine(progress);
            int code;
            try { code = await executor.ExecuteAsync(new(item, options, Array.IndexOf(item.Steps, step), step), tail, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
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
            var why = tail.Text is { } last ? $"\n           “{last}”" : "";
            if (step.Restart.Contains(code)) { restart = true; summary.Add($"OK       {step.Title} (restart needed to finish)"); progress.Report(new($"Done in {Seconds(clock.Elapsed)}. Windows needs a restart to finish this.\n", CommandOutputKind.Success)); }
            else if (step.Ok.Contains(code)) { summary.Add($"OK       {step.Title}"); progress.Report(new($"Done in {Seconds(clock.Elapsed)}.\n", CommandOutputKind.Success)); }
            else if (step.Warn.Contains(code)) { warnings++; summary.Add($"WARNING  {step.Title} (exit code {code}){why}"); progress.Report(new($"Finished with exit code {code}. This is reported as a warning, not a failure.\n", CommandOutputKind.Warning)); }
            else if (step.Optional) { warnings++; summary.Add($"SKIPPED  {step.Title} (exit code {Describe(code)}){why}"); progress.Report(new($"This step did not work (exit code {Describe(code)}). It is optional, so the fix goes on.\n", CommandOutputKind.Warning)); }
            else if (failure is not null) { warnings++; summary.Add($"SKIPPED  {step.Title} (exit code {Describe(code)}){why}"); progress.Report(new($"This clean-up step did not work (exit code {Describe(code)}).\n", CommandOutputKind.Warning)); }
            else { failure = step.Title; summary.Add($"FAILED   {step.Title} (exit code {Describe(code)}){why}"); progress.Report(new($"Failed with exit code {Describe(code)}. Only the steps that put things back will still run.\n", CommandOutputKind.Error)); }
        }
        var detail = string.Join('\n', summary) + $"\nTook {Seconds(started.Elapsed)}.";
        if (failure is not null) return new(ActionOutcome.Failed, $"Couldn’t finish {item.Name}", $"Stopped at “{failure}”.\n{detail}");
        restart |= item.Restart == FixRestart.Always;
        if (warnings > 0)
        {
            progress.Report(new($"{item.Name} finished, but {(warnings == 1 ? "one step" : $"{warnings} steps")} gave a warning.\n", CommandOutputKind.Warning));
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

    private static string Describe(int code) => code switch
    {
        int.MinValue => "not available",
        unchecked((int)0xC0000005) => "0xC0000005, the program crashed",
        < 0 => $"{code}, 0x{code:X8}",
        _ => code.ToString(CultureInfo.InvariantCulture)
    };

    /// <summary>Passes output on and remembers the newest line that said anything.</summary>
    private sealed class LastLine(IProgress<CommandOutput> inner) : IProgress<CommandOutput>
    {
        public string? Text { get; private set; }

        public void Report(CommandOutput value)
        {
            inner.Report(value);
            if (value.Kind is not (CommandOutputKind.Output or CommandOutputKind.Error or CommandOutputKind.Warning)) return;
            var line = value.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
            if (!string.IsNullOrWhiteSpace(line)) Text = line.Length > 240 ? line[..240] + "…" : line;
        }
    }
}

/// <summary>Runs queued fixes: finds the fix in the catalog and chooses who runs it, this process or, for machine-wide fixes, the administrator helper after one permission prompt.</summary>
public sealed class FixService(IReadOnlyList<FixItem> catalog, IFixStepExecutor local, IFixStepExecutor? elevated, Func<bool> isAdministrator, Func<Task>? unlock, FixRunStore? history = null, Func<DateTimeOffset>? clock = null, Func<bool>? helperOpen = null)
{
    private readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.Now);

    [SupportedOSPlatform("windows")]
    public static bool IsAdministrator() => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    public async Task<PackageActionResult> RunAsync(FixRun run, IProgress<CommandOutput> progress, CancellationToken cancellationToken)
    {
        var item = catalog.FirstOrDefault(x => x.Id.Equals(run.Id, StringComparison.OrdinalIgnoreCase));
        if (item is null) return new(ActionOutcome.Failed, "That fix is not available", "It is not in this version of TidyWindow.");
        var executor = local;
        if (item.Scope == FixScope.Machine && !isAdministrator())
        {
            if (elevated is null || unlock is null) return Record(item, new(ActionOutcome.Failed, $"Couldn’t run {item.Name}", "This fix needs administrator rights, and the administrator helper is not available."));
            // The helper stays connected between fixes, so the prompt only comes the first time.
            if (helperOpen?.Invoke() != true) progress.Report(new("This fix needs administrator rights. Approve the Windows permission prompt; the output will appear here.", CommandOutputKind.Notice));
            try { await unlock().ConfigureAwait(false); }
            catch (OperationCanceledException) { return Record(item, new(ActionOutcome.Failed, $"{item.Name} was not run", "Administrator permission was declined. Nothing was changed.")); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            { return Record(item, new(ActionOutcome.Failed, $"Couldn’t run {item.Name}", ex.Message)); }
            executor = elevated;
            progress.Report(new("Running with administrator rights.", CommandOutputKind.Notice));
        }
        var result = await new FixRunner(executor).RunAsync(item, run.Options, progress, cancellationToken).ConfigureAwait(false);
        return Record(item, result);
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

/// <summary>The real thing: starts programs and does the few file steps, reading output as it arrives.</summary>
[SupportedOSPlatform("windows")]
public sealed class FixStepExecutor : IFixStepExecutor
{
    static FixStepExecutor() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public async Task<int> ExecuteAsync(FixStepContext context, IProgress<CommandOutput> progress, CancellationToken cancellationToken)
    {
        var step = context.Step;
        switch (step.Kind)
        {
            case FixStepKind.Run: return await RunProgramAsync(context, progress, cancellationToken).ConfigureAwait(false);
            case FixStepKind.DeleteFiles: return await Task.Run(() => DeleteFiles(context, progress), cancellationToken).ConfigureAwait(false);
            case FixStepKind.RenameFolder: return await Task.Run(() => RenameFolder(context, progress), cancellationToken).ConfigureAwait(false);
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

    private static async Task<int> RunProgramAsync(FixStepContext context, IProgress<CommandOutput> progress, CancellationToken cancellationToken)
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
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System)
        };
        var powershell = step.File.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase);
        foreach (var argument in step.Args)
        {
            var text = Environment.ExpandEnvironmentVariables(argument);
            // Windows PowerShell writes to a pipe in the console's code page unless told otherwise.
            start.ArgumentList.Add(powershell && step.Encoding == FixEncoding.Utf8 && argument == step.Args[^1] ? "[Console]::OutputEncoding = [Text.Encoding]::UTF8; " + text : text);
        }
        start.Environment["NO_COLOR"] = "1";
        using var process = new Process { StartInfo = start };
        await Task.Run(process.Start, cancellationToken).ConfigureAwait(false);
        var output = PumpAsync(process.StandardOutput, CommandOutputKind.Output, powershell, progress);
        // A step that accepts a non-zero exit code (net says "already started" with 2) is not an error, so its messages are not shown in red.
        var error = PumpAsync(process.StandardError, step.Ok.Any(code => code != 0) ? CommandOutputKind.Warning : CommandOutputKind.Error, powershell, progress);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(step.Minutes));
        try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.WhenAll(output, error).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested) throw;
            throw new TimeoutException();
        }
        await Task.WhenAll(output, error).ConfigureAwait(false);
        return process.ExitCode;
    }

    private static async Task PumpAsync(StreamReader reader, CommandOutputKind kind, bool powershell, IProgress<CommandOutput> progress)
    {
        var decoder = powershell ? new PowerShellOutputDecoder(kind) : null;
        var splitter = new OutputSplitter((text, k) => { try { progress.Report(new(text + "\n", k)); } catch { /* A display failure must not stop draining the pipe. */ } });
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) > 0)
        {
            var text = new string(buffer, 0, count);
            if (decoder is null) splitter.Feed(text, kind);
            else foreach (var message in decoder.Feed(text)) splitter.Feed(message.Text, message.Kind);
        }
        if (decoder is not null) foreach (var message in decoder.Feed("", final: true)) splitter.Feed(message.Text, message.Kind);
        splitter.Finish(kind);
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

    private static int RenameFolder(FixStepContext context, IProgress<CommandOutput> progress)
    {
        var step = context.Step;
        if (!FixGuard.AllowsRename(context.Item.Scope, step.Path)) throw new InvalidOperationException("That folder cannot be renamed here.");
        var folder = Environment.ExpandEnvironmentVariables(step.Path);
        var backup = folder + ".bak";
        if (!Directory.Exists(folder)) { progress.Report(new($"{folder} does not exist, so there is nothing to rename.\n", CommandOutputKind.Information)); return 0; }
        if (Directory.Exists(backup))
        {
            progress.Report(new($"Removing the older copy, {backup}.\n", CommandOutputKind.Information));
            Directory.Delete(backup, recursive: true);
        }
        Directory.Move(folder, backup);
        progress.Report(new($"Renamed {folder} to {Path.GetFileName(backup)}. Windows makes a fresh one.\n", CommandOutputKind.Information));
        return 0;
    }
}

public sealed record FixMessage(string Op, string? Id = null, string[]? Options = null, int Index = 0);
/// <summary>What the helper sends back: "ok" for the greeting, "out" for a line of output, "done" with the exit code, "timeout" or "fail".</summary>
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
        using var pipe = new NamedPipeClientStream(".", args[1], PipeDirection.InOut, PipeOptions.Asynchronous);
        try { await pipe.ConnectAsync(15000).ConfigureAwait(false); }
        catch (Exception ex) when (ex is TimeoutException or IOException) { return 65; }
        using var reader = new StreamReader(pipe);
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
                    // While a step runs, anything arriving on the pipe (a cancel, or the app closing) stops it.
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
                        catch (OperationCanceledException) { Send(new("fail", Error: "The step was cancelled.")); }
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
public sealed class FixSession(Func<string, Task> launch) : IFixStepExecutor, IDisposable
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private readonly SemaphoreSlim one = new(1, 1);
    private readonly object gate = new();
    private NamedPipeServerStream? pipe;
    private StreamReader? reader;
    private StreamWriter? writer;

    public bool IsOpen => pipe is { IsConnected: true };

    public static FixSession Elevated(string helperPath) => new(name =>
    {
        if (!File.Exists(helperPath)) throw new FileNotFoundException("The administrator helper is missing from this installation.", helperPath);
        var info = new ProcessStartInfo(helperPath) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        info.ArgumentList.Add(FixHost.Command); info.ArgumentList.Add(name);
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
        var name = "tidywindow-fix-" + Guid.NewGuid().ToString("N");
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
            await output.WriteLineAsync(JsonSerializer.Serialize(new FixMessage("hello"), Json)).ConfigureAwait(false);
            var reply = JsonSerializer.Deserialize<FixEvent>(await input.ReadLineAsync().ConfigureAwait(false) ?? "", Json);
            if (reply is not { Type: "ok" }) throw new UnauthorizedAccessException("The helper did not get administrator permission.");
            lock (gate) { pipe = server; reader = input; writer = output; }
        }
        catch
        {
            server.Dispose();
            throw;
        }
    }

    public async Task<int> ExecuteAsync(FixStepContext context, IProgress<CommandOutput> progress, CancellationToken cancellationToken)
    {
        await one.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            StreamReader input; StreamWriter output;
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

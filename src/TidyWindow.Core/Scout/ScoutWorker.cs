using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace TidyWindow.Next.Core;

/// <summary>One line sent to the long-running search helper.</summary>
public sealed record ScoutWorkerRequest(int Id, string Op, ScoutRequest? Request = null, bool Fuzzy = false);
/// <summary>One line the helper answers with.</summary>
public sealed record ScoutWorkerReply(int Id, ScoutReply Reply);

/// <summary>Talks to the helper process that owns the native Everything SDK, so a native failure can never take the app down.</summary>
public interface IScoutWorker : IDisposable
{
    /// <summary>Whether a separate, slower search for near matches can be requested.</summary>
    bool SupportsFuzzy { get; }
    /// <summary>A null request asks whether the index is ready.</summary>
    Task<ScoutReply> QueryAsync(ScoutRequest? request, bool fuzzy, CancellationToken token);
}

public static class ScoutWorkerErrors
{
    /// <summary>Turns a helper that would not start or stopped early into a message that says what to do.</summary>
    public static string Describe(int exitCode, string? detail)
    {
        detail = detail?.Trim() ?? "";
        if (exitCode == 150 || detail.Contains("You must install", StringComparison.OrdinalIgnoreCase) || detail.Contains("framework", StringComparison.OrdinalIgnoreCase) && detail.Contains(".NET", StringComparison.OrdinalIgnoreCase))
            return "The search helper could not start because the .NET runtime it needs was not found. Reinstall TidyWindow, or choose a folder to search directly.";
        if (detail.Contains("Everything64.dll", StringComparison.OrdinalIgnoreCase) && detail.Contains("load", StringComparison.OrdinalIgnoreCase))
            return "The search helper could not load the Everything library it ships with. Reinstall TidyWindow, or choose a folder to search directly.";
        return $"The index worker stopped unexpectedly (code {exitCode}). Folder search is still available.";
    }
}

/// <summary>Starts the helper once per request. Simple and stateless; every search pays process start-up, so it is the fallback.</summary>
public sealed class OneShotScoutWorker(ICommandRunner runner, string hostPath) : IScoutWorker
{
    public bool SupportsFuzzy => false;

    public async Task<ScoutReply> QueryAsync(ScoutRequest? request, bool fuzzy, CancellationToken token)
    {
        if (!File.Exists(hostPath)) return new(false, [], Error: "The search worker is unavailable. Rebuild TidyWindow.");
        var argument = request is null ? "--status" : Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request)));
        try
        {
            var result = await runner.RunAsync(new(hostPath, [argument], TimeSpan.FromSeconds(request is null ? 3 : 10)), token).ConfigureAwait(false);
            if (result.ExitCode != 0) return new(false, [], Error: ScoutWorkerErrors.Describe(result.ExitCode, result.Error + " " + result.Output));
            return JsonSerializer.Deserialize<ScoutReply>(result.Output.Trim()) ?? new(false, [], Error: "The index worker returned no result.");
        }
        catch (TimeoutException) { return new(false, [], Error: "The local file index took too long to respond. Try a more specific name or refresh instant search."); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return new(false, [], Error: ex.Message); }
    }

    public void Dispose() { }
}

/// <summary>
/// Keeps one helper alive and sends it a line per request, so a search costs the query itself (milliseconds) instead of a
/// process start (about 150 ms). A helper that crashes or stalls is replaced on the next request, and a request that
/// reached a crashing helper is retried once on a fresh one.
/// </summary>
public sealed class PersistentScoutWorker(string hostPath, bool killOnCancel = false) : IScoutWorker
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private static readonly TimeSpan StatusLimit = TimeSpan.FromSeconds(3), SearchLimit = TimeSpan.FromSeconds(10), FuzzyLimit = TimeSpan.FromSeconds(20);
    private readonly SemaphoreSlim gate = new(1, 1);
    private Session? session;
    private int nextId;
    private bool disposed;

    public bool SupportsFuzzy => true;

    public async Task<ScoutReply> QueryAsync(ScoutRequest? request, bool fuzzy, CancellationToken token)
    {
        if (!File.Exists(hostPath)) return new(false, [], Error: "The search worker is unavailable. Rebuild TidyWindow.");
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            string failure = "The index worker did not answer.";
            for (var attempt = 0; attempt < 2; attempt++)
            {
                token.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(disposed, this);
                var current = session is { Alive: true } ? session : session = await Task.Run(() => Session.Start(hostPath, Json), token).ConfigureAwait(false);
                if (current.StartError is { } startError) { session = null; return new(false, [], Error: startError); }
                var id = ++nextId;
                var answer = current.Expect(id);
                try
                {
                    await current.SendAsync(new ScoutWorkerRequest(id, request is null ? "status" : "search", request, fuzzy)).ConfigureAwait(false);
                    using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
                    limit.CancelAfter(request is null ? StatusLimit : fuzzy ? FuzzyLimit : SearchLimit);
                    return await answer.WaitAsync(limit.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    // Stale searches while typing: let a quick one finish and ignore its answer; stop a slow one outright.
                    if (killOnCancel) Drop(current);
                    throw;
                }
                catch (OperationCanceledException)
                {
                    Drop(current);
                    return new(false, [], Error: "The local file index took too long to respond. Try a more specific name or refresh instant search.");
                }
                catch (ScoutWorkerExited exited)
                {
                    Drop(current);
                    failure = ScoutWorkerErrors.Describe(exited.ExitCode, exited.Detail);
                    // A helper that died before ever answering may just have been unlucky; one fresh start is cheap. A helper that cannot start at all is reported at once.
                    if (current.AnsweredAny || exited.ExitCode == 150) break;
                }
                catch (IOException) { Drop(current); failure = "The index worker closed unexpectedly. Folder search is still available."; }
            }
            return new(false, [], Error: failure);
        }
        finally { gate.Release(); }
    }

    private void Drop(Session dead) { dead.Kill(); if (ReferenceEquals(session, dead)) session = null; }

    public void Dispose()
    {
        disposed = true;
        session?.Kill(); session = null;
        gate.Dispose();
    }

    private sealed class ScoutWorkerExited(int exitCode, string detail) : Exception("The index worker exited.")
    {
        public int ExitCode { get; } = exitCode;
        public string Detail { get; } = detail;
    }

    private sealed class Session
    {
        private readonly Process process;
        private readonly StreamWriter input;
        private readonly ConcurrentDictionary<int, TaskCompletionSource<ScoutReply>> pending = new();
        private readonly StringBuilder errors = new();
        private readonly JsonSerializerOptions json;
        private volatile bool answered, exited;

        public string? StartError { get; private init; }
        public bool Alive => StartError is null && !exited;
        public bool AnsweredAny => answered;

        private Session(Process process, JsonSerializerOptions json)
        {
            this.process = process; this.json = json;
            input = process.StandardInput;
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (errors) { errors.AppendLine(e.Data); if (errors.Length > 2000) errors.Remove(0, errors.Length - 2000); } };
            process.BeginErrorReadLine();
            _ = Task.Run(ReadAsync);
        }

        private Session(string error) { process = null!; input = null!; json = null!; StartError = error; }

        public static Session Start(string hostPath, JsonSerializerOptions json)
        {
            var start = new ProcessStartInfo(hostPath)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = Path.GetDirectoryName(hostPath) ?? ""
            };
            start.ArgumentList.Add("--serve");
            try { return new Session(Process.Start(start) ?? throw new InvalidOperationException("The index worker could not be started."), json); }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
            { return new Session("The index worker could not be started. " + ex.Message); }
        }

        public Task<ScoutReply> Expect(int id)
        {
            var source = new TaskCompletionSource<ScoutReply>(TaskCreationOptions.RunContinuationsAsynchronously);
            pending[id] = source;
            // A process that already ended answers nothing; fail now rather than waiting out the timeout.
            if (exited) Fail(source);
            return source.Task;
        }

        public async Task SendAsync(ScoutWorkerRequest request)
        {
            await input.WriteLineAsync(JsonSerializer.Serialize(request, json)).ConfigureAwait(false);
            await input.FlushAsync().ConfigureAwait(false);
        }

        private async Task ReadAsync()
        {
            try
            {
                while (await process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
                {
                    ScoutWorkerReply? reply;
                    try { reply = JsonSerializer.Deserialize<ScoutWorkerReply>(line, json); }
                    catch (JsonException) { lock (errors) errors.AppendLine(line); continue; }
                    if (reply is null) continue;
                    answered = true;
                    if (pending.TryRemove(reply.Id, out var source)) source.TrySetResult(reply.Reply);
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
            exited = true;
            foreach (var source in pending.Values) Fail(source);
        }

        private void Fail(TaskCompletionSource<ScoutReply> source)
        {
            int code;
            try { process.WaitForExit(2000); code = process.HasExited ? process.ExitCode : -1; }
            catch (InvalidOperationException) { code = -1; }
            string detail; lock (errors) detail = errors.ToString();
            source.TrySetException(new ScoutWorkerExited(code, detail));
        }

        public void Kill()
        {
            exited = true;
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
            try { process.Dispose(); } catch (InvalidOperationException) { }
        }
    }
}

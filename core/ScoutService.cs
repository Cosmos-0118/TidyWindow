using System.Text;
using System.Text.Json;

namespace TidyWindow.Next.Core;

public sealed class ScoutService(ICommandRunner runner, string hostPath, IScoutEngine? engine = null) : IDisposable
{
    private readonly ScoutFolderIndex folder = new();
    private readonly IScoutEngine engine = engine ?? new WindowsScoutEngine();
    public async Task<ScoutBackendStatus> CheckBackendAsync(CancellationToken token = default)
    {
        var installation = await Task.Run(this.engine.Inspect, token).ConfigureAwait(false);
        var response = await QueryHostAsync(null, token).ConfigureAwait(false);
        if (!response.Ready && !response.Indexing && !installation.UserProcessRunning && installation.ExecutablePath is { } path)
        {
            try { this.engine.Start(path); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
            { return new(true, false, Error: "Everything is installed but could not start. " + ex.Message); }
            // A new user process needs time to create its IPC window. Index building is a separate state.
            for (var attempt = 0; attempt < 4 && !response.Ready && !response.Indexing; attempt++)
            {
                await Task.Delay(250, token).ConfigureAwait(false);
                response = await QueryHostAsync(null, token).ConfigureAwait(false);
            }
        }
        return new(installation.Installed || response.Ready || response.Indexing, response.Ready, response.Indexing, response.Error);
    }
    public async Task<ScoutResult> SearchAsync(ScoutRequest request, bool indexed, IProgress<ScoutResult>? progress, CancellationToken token)
    {
        if (!indexed) return await folder.SearchAsync(request, progress, token).ConfigureAwait(false);
        var response = await QueryHostAsync(request, token).ConfigureAwait(false);
        return new(response.Entries, "Everything index", response.Total, 0, true, response.Limited, response.Error);
    }
    public void RefreshFolder() => folder.Refresh();
    private async Task<ScoutReply> QueryHostAsync(ScoutRequest? request, CancellationToken token)
    {
        if (!File.Exists(hostPath)) return new(false, [], Error: "The search worker is unavailable. Rebuild TidyWindow.");
        var argument = request is null ? "--status" : Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request)));
        try
        {
            var result = await runner.RunAsync(new(hostPath, [argument], TimeSpan.FromSeconds(request is null ? 3 : 10)), token).ConfigureAwait(false);
            if (result.ExitCode != 0) return new(false, [], Error: "The index worker stopped unexpectedly. Folder search is still available.");
            return JsonSerializer.Deserialize<ScoutReply>(result.Output.Trim()) ?? new(false, [], Error: "The index worker returned no result.");
        }
        catch (TimeoutException) { return new(false, [], Error: "The local file index took too long to respond. Try a more specific name or refresh instant search."); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return new(false, [], Error: ex.Message); }
    }
    public void Dispose() => folder.Dispose();
}

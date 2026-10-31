namespace TidyWindow.Next.Core;

/// <param name="worker">Answers whole-computer queries through the Everything index. Defaults to starting the helper per request.</param>
/// <param name="fuzzyWorker">A separate helper for the slower search for near matches, so it can be stopped without affecting quick searches.</param>
public sealed class ScoutService(ICommandRunner runner, string hostPath, IScoutEngine? engine = null, IScoutWorker? worker = null, IScoutWorker? fuzzyWorker = null) : IDisposable
{
    /// <summary>Near-match search runs only when the exact search found fewer results than this.</summary>
    public const int FuzzyBelow = 25;
    private readonly ScoutFolderIndex folder = new();
    private readonly IScoutEngine engine = engine ?? new WindowsScoutEngine();
    private readonly IScoutWorker worker = worker ?? new OneShotScoutWorker(runner, hostPath);

    public async Task<ScoutBackendStatus> CheckBackendAsync(CancellationToken token = default)
    {
        var installation = await Task.Run(this.engine.Inspect, token).ConfigureAwait(false);
        var response = await worker.QueryAsync(null, false, token).ConfigureAwait(false);
        if (!response.Ready && !response.Indexing && !installation.UserProcessRunning && installation.ExecutablePath is { } path)
        {
            try { this.engine.Start(path); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
            { return new(true, false, Error: "Everything is installed but could not start. " + ex.Message); }
            // A new user process needs time to create its IPC window. Index building is a separate state.
            for (var attempt = 0; attempt < 4 && !response.Ready && !response.Indexing; attempt++)
            {
                await Task.Delay(250, token).ConfigureAwait(false);
                response = await worker.QueryAsync(null, false, token).ConfigureAwait(false);
            }
        }
        return new(installation.Installed || response.Ready || response.Indexing, response.Ready, response.Indexing, response.Error);
    }

    public async Task<ScoutResult> SearchAsync(ScoutRequest request, bool indexed, IProgress<ScoutResult>? progress, CancellationToken token)
    {
        if (!indexed) return await folder.SearchAsync(request, progress, token).ConfigureAwait(false);
        var strict = await worker.QueryAsync(request, false, token).ConfigureAwait(false);
        var result = new ScoutResult(strict.Entries, "Everything index", strict.Total, 0, true, strict.Limited, strict.Error);
        if (strict.Error is not null || fuzzyWorker is null || !fuzzyWorker.SupportsFuzzy || !WantsNearMatches(request, strict.Entries)) return result;

        // Exact results are shown at once; the slower search for typos and abbreviations follows and is merged in.
        progress?.Report(result with { Engine = "Everything index · checking close matches…" });
        var near = await fuzzyWorker.QueryAsync(request, true, token).ConfigureAwait(false);
        if (near.Error is not null || near.Entries.Count == 0) return result;
        var query = new ScoutQuery(request.Query, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        var merged = query.Rank(strict.Entries.Concat(near.Entries).DistinctBy(x => x.FullPath, StringComparer.OrdinalIgnoreCase), request.Kind, token: token);
        return new(merged, "Everything index", Math.Max(strict.Total, near.Total), 0, true, strict.Limited || near.Limited, null);
    }

    private static bool WantsNearMatches(ScoutRequest request, IReadOnlyList<ScoutEntry> found)
    {
        if (found.Count >= FuzzyBelow) return false;
        var query = new ScoutQuery(request.Query);
        // Wildcards and many-word queries are precise; typo search is for a short name that matched nothing exactly.
        return !query.HasWildcards && query.Terms.Count <= 3 && !found.Any(x => query.Score(x) >= ScoutQuery.ExactScore);
    }

    public void RefreshFolder() => folder.Refresh();

    public void Dispose() { folder.Dispose(); worker.Dispose(); fuzzyWorker?.Dispose(); }
}

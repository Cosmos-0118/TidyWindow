namespace TidyWindow.Next.Core;

/// <summary>
/// Knows how many programs this process has started and is still supervising (PowerShell, package managers, DISM). Cancelling the work that owns
/// one ends its process tree, but that happens a moment later on another thread. If the app exits in that moment the program is left running with
/// nobody reading its output, and it reports a broken pipe. Closing therefore waits here, briefly, for the supervisors to finish ending theirs.
/// </summary>
public static class ChildProcesses
{
    private static readonly object Gate = new();
    private static int active;
    private static TaskCompletionSource idle = Done();

    /// <summary>How many supervised programs are running right now.</summary>
    public static int Active { get { lock (Gate) return active; } }

    /// <summary>Marks one program as supervised until the returned lease is disposed. Take it before starting the program and keep it until the program has ended.</summary>
    public static IDisposable Begin()
    {
        lock (Gate)
        {
            if (active++ == 0) idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        return new Lease();
    }

    /// <summary>Completes when no supervised program is left, or after <paramref name="limit"/>. True when none is left.</summary>
    public static async Task<bool> WaitIdleAsync(TimeSpan limit)
    {
        Task waiting; lock (Gate) waiting = idle.Task;
        try { await waiting.WaitAsync(limit).ConfigureAwait(false); return true; }
        catch (TimeoutException) { return false; }
    }

    private static TaskCompletionSource Done() { var source = new TaskCompletionSource(); source.SetResult(); return source; }

    private sealed class Lease : IDisposable
    {
        private int released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) == 1) return;
            lock (Gate) { if (--active == 0) idle.TrySetResult(); }
        }
    }
}

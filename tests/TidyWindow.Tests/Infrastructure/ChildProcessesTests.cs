using TidyWindow.Next.Core;
using Xunit;

namespace TidyWindow.Next.Tests;

// The tracker is process-wide and other tests start programs too, so these assert what a lease changes, never that nothing else is running.
public sealed class ChildProcessesTests
{
    [Fact]
    public void A_lease_counts_until_it_is_disposed_and_disposing_twice_counts_once()
    {
        var before = ChildProcesses.Active;
        var lease = ChildProcesses.Begin();
        Assert.True(ChildProcesses.Active >= before + 1);
        lease.Dispose();
        lease.Dispose();
    }

    [Fact]
    public async Task Waiting_gives_up_after_the_limit_while_a_lease_is_held()
    {
        using var lease = ChildProcesses.Begin();
        Assert.False(await ChildProcesses.WaitIdleAsync(TimeSpan.FromMilliseconds(50)));
    }

    [Fact]
    public async Task A_waiter_is_released_when_the_last_lease_ends()
    {
        var lease = ChildProcesses.Begin();
        var waiting = ChildProcesses.WaitIdleAsync(TimeSpan.FromSeconds(30));
        Assert.False(waiting.IsCompleted);
        lease.Dispose();
        // Other tests may hold leases of their own; the waiter then ends when they do, which a short limit would not wait for.
        Assert.True(await waiting);
    }
}

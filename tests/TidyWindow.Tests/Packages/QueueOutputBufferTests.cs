using TidyWindow.Next.Core;
using Xunit;
using Xunit.Abstractions;

namespace TidyWindow.Next.Tests;

/// <summary>The queue keeps the newest 65,536 characters of an operation's output in at most 512 runs of one kind. It now does so without rebuilding that text for every chunk.</summary>
public sealed class QueueOutputBufferTests(ITestOutputHelper output)
{
    private static readonly CommandOutputKind[] Kinds = [CommandOutputKind.Output, CommandOutputKind.Error, CommandOutputKind.Warning, CommandOutputKind.Information, CommandOutputKind.Progress, CommandOutputKind.Success];

    /// <summary>How the queue turned chunks into its output before: rebuild the whole text and its runs after every chunk.</summary>
    private static List<(string Text, CommandOutput[] Runs)> Original(CommandOutput[] chunks, int lookEvery)
    {
        var states = new List<(string, CommandOutput[])>();
        var segments = new List<CommandOutput>();
        for (var i = 0; i < chunks.Length; i++)
        {
            var chunk = chunks[i];
            if (segments.Count > 0 && segments[^1].Kind == chunk.Kind) segments[^1] = segments[^1] with { Text = segments[^1].Text + chunk.Text };
            else segments.Add(chunk);
            var length = segments.Sum(x => x.Text.Length);
            while (length > 65536 && segments.Count > 0)
            {
                var excess = length - 65536;
                if (segments[0].Text.Length <= excess) { length -= segments[0].Text.Length; segments.RemoveAt(0); }
                else { segments[0] = segments[0] with { Text = segments[0].Text[excess..] }; length -= excess; }
            }
            while (segments.Count > 512) segments.RemoveAt(0);
            if ((i + 1) % lookEvery == 0) states.Add((string.Concat(segments.Select(x => x.Text)), segments.ToArray()));
        }
        states.Add((string.Concat(segments.Select(x => x.Text)), segments.ToArray()));
        return states;
    }

    private static CommandOutput[] Stream(int seed, int count, int maxLength, int kindChange)
    {
        var random = new Random(seed); var kind = CommandOutputKind.Output;
        return Enumerable.Range(0, count).Select(i =>
        {
            if (random.Next(kindChange) == 0) kind = Kinds[random.Next(Kinds.Length)];
            return new CommandOutput(new string((char)('a' + i % 26), random.Next(0, maxLength + 1)), kind);
        }).ToArray();
    }

    /// <summary>Runs the chunks through a real queue, looking at the entry after every <paramref name="lookEvery"/> chunks, and returns what it showed.</summary>
    private static async Task<List<(string Text, CommandOutput[] Runs)>> Observe(CommandOutput[] chunks, int lookEvery)
    {
        var seen = new List<(string, CommandOutput[])>();
        PackageOperationQueue? queue = null;
        queue = PackageOperationQueue.WithLiveOutput((_, progress, _) =>
        {
            for (var i = 0; i < chunks.Length; i++)
            {
                progress.Report(chunks[i]);
                if ((i + 1) % lookEvery == 0) { var entry = queue!.Snapshot().Entries.Single(); seen.Add((entry.Output, entry.OutputSegments!.ToArray())); }
            }
            return Task.FromResult(new PackageActionResult(ActionOutcome.Completed, "done", ""));
        });
        queue.Enqueue([PackageOperation.Install(new(ManagerKind.Winget, "Vendor.App", "App", "1", "winget"))]);
        await queue.WhenIdleAsync();
        var final = queue.Snapshot().Entries.Single();
        seen.Add((final.Output, final.OutputSegments?.ToArray() ?? []));
        return seen;
    }

    [Theory]
    [InlineData(1, 20000, 120, 1000)]   // a progress line redrawn tens of thousands of times
    [InlineData(2, 3000, 30000, 4)]     // a few huge chunks that overflow the limit at once
    [InlineData(3, 4000, 40, 1)]        // the kind changes at almost every chunk, so there are more than 512 runs to drop
    [InlineData(4, 700, 4000, 6)]
    public async Task WhatTheEntryShowsIsWhatTheOriginalAlgorithmProduced(int seed, int count, int maxLength, int kindChange)
    {
        var chunks = Stream(seed, count, maxLength, kindChange);
        var lookEvery = Math.Max(1, count / 40);
        var seen = await Observe(chunks, lookEvery);
        var expected = Original(chunks, lookEvery);
        Assert.True(seen.Count >= 40);
        Assert.Equal(expected.Count, seen.Count);
        for (var i = 0; i < seen.Count; i++)
        {
            Assert.Equal(expected[i].Text, seen[i].Text);
            Assert.Equal(expected[i].Runs, seen[i].Runs);
        }
        Assert.InRange(seen[^1].Text.Length, 0, 65536);
        Assert.InRange(seen[^1].Runs.Length, 0, 512);
    }

    [Fact]
    public async Task OutputThatArrivesAfterTheLastLookIsStillOnTheFinishedEntry()
    {
        var queue = PackageOperationQueue.WithLiveOutput((_, progress, _) =>
        {
            progress.Report(new("first\n")); progress.Report(new("warning\n", CommandOutputKind.Warning)); progress.Report(new("last\n"));
            return Task.FromResult(new PackageActionResult(ActionOutcome.Completed, "done", ""));
        });
        var finished = new TaskCompletionSource<QueueEntry>();
        queue.Finished += finished.SetResult;
        queue.Enqueue([PackageOperation.Install(new(ManagerKind.Winget, "Vendor.App", "App", "1", "winget"))]);
        var entry = await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("first\nwarning\nlast\n", entry.Output);
        Assert.Equal(["first\n", "warning\n", "last\n"], entry.OutputSegments!.Select(x => x.Text));
        Assert.Equal(entry.Output, Assert.Single(queue.Snapshot().Entries).Output);
    }

    [Fact]
    public async Task ARedrawnProgressLineCostsAlmostNothingPerChunk()
    {
        const int chunks = 20000;
        long allocated = 0;
        var queue = PackageOperationQueue.WithLiveOutput((_, progress, _) =>
        {
            // Fill the buffer first, so every chunk after this one has to push older text out.
            for (var i = 0; i < 2000; i++) progress.Report(new($"\rProgress: Downloading example.package 1.2.3... {i % 100,3}%  ({i % 997} MB of 998 MB)       "));
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < chunks; i++) progress.Report(new($"\rProgress: Downloading example.package 1.2.3... {i % 100,3}%  ({i % 997} MB of 998 MB)       "));
            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            return Task.FromResult(new PackageActionResult(ActionOutcome.Completed, "done", ""));
        });
        queue.Enqueue([PackageOperation.Install(new(ManagerKind.Chocolatey, "example.package", "Example", "1", "chocolatey"))]);
        await queue.WhenIdleAsync();
        output.WriteLine($"{allocated / chunks:N0} bytes allocated per output chunk (it was about 250,000).");
        Assert.True(allocated / chunks < 2048, $"{allocated / chunks} bytes per chunk");
    }
}

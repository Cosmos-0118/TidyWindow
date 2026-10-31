using System.Diagnostics;
using System.Globalization;

namespace TidyWindow.Next.Core;

/// <summary>One thing a check measures, and which direction is better.</summary>
public sealed record ProbeKind(string Id, string Name, string Unit, bool HigherIsBetter, string What);

public static class ProbeKinds
{
    public static readonly ProbeKind Wake = new("wake", "Wake-up speed", "ms", false, "How long a short burst of work takes after the processor has been idle. A power plan that keeps the processor ready makes this smaller.");
    public static readonly ProbeKind Throughput = new("throughput", "All-core work", "million steps/s", true, "How much work every processor core does together in a second. This follows heat and power limits more than any setting.");
    public static readonly ProbeKind Copy = new("copy", "Memory copy", "GB/s", true, "How fast a large block of memory can be copied.");
    public static readonly ProbeKind Random = new("random", "Random memory access", "ns", false, "How long it takes to read a random spot in memory. Memory integrity runs Windows on top of the hypervisor, which can make this slower.");

    public static IReadOnlyList<ProbeKind> All { get; } = [Wake, Throughput, Copy, Random];

    public static ProbeKind? Find(string id) => All.FirstOrDefault(x => x.Id == id);
}

/// <summary>How long and how big a check is. <see cref="Full"/> is what the page runs; <see cref="Quick"/> exists so tests finish fast.</summary>
public sealed record ProbeProfile(int Rounds, int WakeSamples, int WakeSleepMs, int WakeIterations, int ThroughputMs, int CopyMegabytes, int CopyPasses, int ChaseEntries, int ChaseSteps)
{
    public static ProbeProfile Full { get; } = new(3, 30, 40, 2_000_000, 2000, 128, 6, 32_000_000, 10_000_000);
    public static ProbeProfile Quick { get; } = new(2, 4, 5, 100_000, 100, 4, 2, 1_000_000, 500_000);
}

public static class ProbeMath
{
    public static double Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return 0;
        var sorted = values.Order().ToList();
        var middle = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }

    /// <summary>How far apart the rounds were, in percent of their median. Small means the check can be trusted.</summary>
    public static double Spread(IReadOnlyList<double> values)
    {
        var median = Median(values);
        return values.Count < 2 || median <= 0 ? 0 : (values.Max() - values.Min()) / median * 100;
    }
}

/// <summary>
/// Measures this PC with small, repeatable tests that need no download and no administrator rights. Each test runs in several rounds and
/// the middle result is kept, with how much the rounds differed, so a later comparison can tell a real change from noise.
/// </summary>
public sealed class ProbeRunner(ProbeProfile profile)
{
    private static long sink;

    public Task<IReadOnlyList<ProbeMetric>> RunAsync(IProgress<string> progress, CancellationToken cancellationToken) =>
        Task.Run(() => Run(progress, cancellationToken), cancellationToken);

    private IReadOnlyList<ProbeMetric> Run(IProgress<string> progress, CancellationToken token)
    {
        progress.Report("Getting memory ready for the memory test.");
        var chain = BuildChain(profile.ChaseEntries, token);
        var results = ProbeKinds.All.ToDictionary(x => x.Id, _ => new List<double>());
        for (var round = 1; round <= profile.Rounds; round++)
        {
            token.ThrowIfCancellationRequested();
            progress.Report($"Round {round} of {profile.Rounds}");
            Measure(ProbeKinds.Wake, () => Wake(token), results, progress);
            Measure(ProbeKinds.Throughput, () => Throughput(token), results, progress);
            Measure(ProbeKinds.Copy, () => Copy(token), results, progress);
            Measure(ProbeKinds.Random, () => Chase(chain, token), results, progress);
        }
        return ProbeKinds.All.Select(x => new ProbeMetric(x.Id, ProbeMath.Median(results[x.Id]), ProbeMath.Spread(results[x.Id]))).ToList();
    }

    private static void Measure(ProbeKind kind, Func<double> test, Dictionary<string, List<double>> results, IProgress<string> progress)
    {
        var value = test();
        results[kind.Id].Add(value);
        progress.Report($"  {kind.Name}: {Format(value)} {kind.Unit}");
    }

    public static string Format(double value) => value >= 100 ? value.ToString("0", CultureInfo.InvariantCulture) : value >= 10 ? value.ToString("0.0", CultureInfo.InvariantCulture) : value.ToString("0.00", CultureInfo.InvariantCulture);

    // A short burst of work after the processor has been left idle. Windows power plans decide how quickly the clock speed comes back up.
    private double Wake(CancellationToken token)
    {
        var times = new List<double>(profile.WakeSamples);
        for (var i = 0; i < profile.WakeSamples; i++)
        {
            token.ThrowIfCancellationRequested();
            Thread.Sleep(profile.WakeSleepMs);
            var clock = Stopwatch.StartNew();
            Volatile.Write(ref sink, (long)Spin(profile.WakeIterations));
            times.Add(clock.Elapsed.TotalMilliseconds);
        }
        return ProbeMath.Median(times);
    }

    private double Throughput(CancellationToken token)
    {
        var threads = Environment.ProcessorCount;
        var blocks = new long[threads];
        var clock = Stopwatch.StartNew();
        var workers = Enumerable.Range(0, threads).Select(index => new Thread(() =>
        {
            long done = 0; ulong last = 0;
            while (clock.ElapsedMilliseconds < profile.ThroughputMs && !token.IsCancellationRequested) { last ^= Spin(100_000); done++; }
            blocks[index] = done;
            Volatile.Write(ref sink, (long)last);
        }) { IsBackground = true }).ToList();
        workers.ForEach(x => x.Start());
        workers.ForEach(x => x.Join());
        token.ThrowIfCancellationRequested();
        return blocks.Sum() * 100_000.0 / clock.Elapsed.TotalSeconds / 1e6;
    }

    private double Copy(CancellationToken token)
    {
        var size = profile.CopyMegabytes * 1024 * 1024;
        var from = new byte[size]; var to = new byte[size];
        from.AsSpan().Fill(1);
        from.AsSpan().CopyTo(to); // the first pass only touches the memory, so it is not timed
        var clock = Stopwatch.StartNew();
        for (var pass = 0; pass < profile.CopyPasses; pass++) { token.ThrowIfCancellationRequested(); from.AsSpan().CopyTo(to); }
        Volatile.Write(ref sink, to[size / 2]);
        return size * (double)profile.CopyPasses / clock.Elapsed.TotalSeconds / 1e9;
    }

    // Follows a random loop through a block of memory much bigger than the processor cache, so every step waits for memory.
    private double Chase(int[] chain, CancellationToken token)
    {
        var at = 0;
        var clock = Stopwatch.StartNew();
        var steps = profile.ChaseSteps;
        for (var i = 0; i < steps; i++)
        {
            at = chain[at];
            if ((i & 0xFFFFF) == 0) token.ThrowIfCancellationRequested();
        }
        var elapsed = clock.Elapsed;
        Volatile.Write(ref sink, at);
        return elapsed.TotalMilliseconds * 1e6 / steps;
    }

    // One single loop through every entry, in an order that cannot be guessed (Sattolo's shuffle).
    private static int[] BuildChain(int entries, CancellationToken token)
    {
        var chain = new int[entries];
        for (var i = 0; i < entries; i++) chain[i] = i;
        ulong state = 0x9E3779B97F4A7C15;
        for (var i = entries - 1; i > 0; i--)
        {
            state ^= state << 13; state ^= state >> 7; state ^= state << 17;
            var j = (int)(state % (ulong)i);
            (chain[i], chain[j]) = (chain[j], chain[i]);
            if ((i & 0xFFFFF) == 0) token.ThrowIfCancellationRequested();
        }
        return chain;
    }

    private static ulong Spin(int iterations)
    {
        var x = 88172645463325252UL;
        for (var i = 0; i < iterations; i++) { x ^= x << 13; x ^= x >> 7; x ^= x << 17; }
        return x;
    }
}

public enum ProbeVerdict { Better, Worse, NoDifference }

/// <param name="Percent">How much better (positive) or worse (negative) the newer check is, whichever direction counts as better for this test.</param>
public sealed record ProbeChange(ProbeKind Kind, double Before, double After, double Percent, ProbeVerdict Verdict);

public static class ProbeCompare
{
    /// <summary>
    /// The smallest change, in percent, that counts at all. Two checks of the very same PC differed by up to 4% in memory access when this was
    /// measured, because the rounds of one check move together more than separate checks do.
    /// </summary>
    public const double Floor = 5;

    /// <summary>
    /// Compares two checks. A difference only counts when it is bigger than the floor and bigger than twice how much the rounds of either check
    /// differed from each other; otherwise the answer is "no difference", which is the honest answer for most settings.
    /// </summary>
    public static IReadOnlyList<ProbeChange> Between(ProbeRun before, ProbeRun after)
    {
        var changes = new List<ProbeChange>();
        foreach (var kind in ProbeKinds.All)
        {
            var a = before.Metrics.FirstOrDefault(x => x.Id == kind.Id); var b = after.Metrics.FirstOrDefault(x => x.Id == kind.Id);
            if (a is null || b is null || a.Value <= 0) continue;
            var change = (b.Value - a.Value) / a.Value * 100;
            var percent = kind.HigherIsBetter ? change : -change;
            var limit = Math.Max(Floor, 2 * Math.Max(a.Spread, b.Spread));
            changes.Add(new(kind, a.Value, b.Value, percent, Math.Abs(percent) < limit ? ProbeVerdict.NoDifference : percent > 0 ? ProbeVerdict.Better : ProbeVerdict.Worse));
        }
        return changes;
    }
}

using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace TidyWindow.Next.Core;

/// <summary>One thing a check measures, which direction is better, and the smallest change in percent that is worth calling a difference for it.</summary>
public sealed record ProbeKind(string Id, string Name, string Unit, bool HigherIsBetter, string What, double Floor = ProbeCompare.Floor);

public static class ProbeKinds
{
    public static readonly ProbeKind Wake = new("wake", "Wake-up speed", "ms", false, "How long a short burst of work takes after the processor has been idle. A power plan that keeps the processor ready makes this smaller.", 6);
    public static readonly ProbeKind Throughput = new("throughput", "All-core work", "million steps/s", true, "How much work every processor core does together in a second. This follows heat and power limits more than any setting.");
    public static readonly ProbeKind Copy = new("copy", "Memory copy", "GB/s", true, "How fast a large block of memory can be copied.");
    public static readonly ProbeKind Random = new("random", "Random memory access", "ns", false, "How long it takes to read a random spot in memory. Memory integrity runs Windows on top of the hypervisor, which can make this slower.", 7);

    public static IReadOnlyList<ProbeKind> All { get; } = [Wake, Throughput, Copy, Random];

    public static ProbeKind? Find(string id) => All.FirstOrDefault(x => x.Id == id);
}

/// <summary>
/// How long and how big a check is. <see cref="Full"/> is what the page runs; <see cref="Quick"/> exists so tests finish fast.
/// <paramref name="SettleMs"/> is a pause after the all-core test, so the heat it makes does not leak into the memory tests that follow, and
/// <paramref name="WarmUp"/> runs every test once, untimed, before the rounds count (code is compiled and clock speeds settle on the first pass, not the second).
/// </summary>
public sealed record ProbeProfile(int Rounds, int WakeSamples, int WakeSleepMs, int WakeIterations, int ThroughputMs, int CopyMegabytes, int CopyPasses, int ChaseEntries, int ChaseSteps, int SettleMs = 0, bool WarmUp = false)
{
    public static ProbeProfile Full { get; } = new(5, 24, 40, 2_000_000, 1000, 128, 6, 32_000_000, 8_000_000, 300, true);
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

/// <summary>Where a check is: how many of its steps are done, what it just said, and which round it is in.</summary>
public sealed record ProbeStep(int Done, int Total, string Line, int Round = 0, int Rounds = 0)
{
    public double Fraction => Total <= 0 ? 0 : Math.Clamp((double)Done / Total, 0, 1);
}

/// <summary>What Windows says about the whole PC, read without any permission.</summary>
internal static class ProbeSystem
{
    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime { public uint Low, High; public readonly ulong Ticks => ((ulong)High << 32) | Low; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerStatus { public byte AcLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public uint BatteryLifeTime, BatteryFullLifeTime; }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out PowerStatus status);

    /// <summary>Processor time of the whole PC so far, in 100 ns units: how much was spent idle and how much in total (kernel time includes idle).</summary>
    public static (ulong Idle, ulong Total)? Times() =>
        GetSystemTimes(out var idle, out var kernel, out var user) ? (idle.Ticks, kernel.Ticks + user.Ticks) : null;

    /// <summary>True on battery, false on mains, null when Windows does not say.</summary>
    public static bool? OnBattery() => GetSystemPowerStatus(out var status) ? status.AcLineStatus switch { 0 => true, 1 => false, _ => null } : null;
}

/// <summary>
/// Measures this PC with small, repeatable tests that need no download and no administrator rights. Each test runs in several rounds and
/// the middle result is kept, with every round, so a later comparison can tell a real change from noise. Before the rounds count, every test
/// runs once untimed, and while they run the check watches how much the rest of the PC is using, so a disturbed check can be told apart.
/// </summary>
public sealed class ProbeRunner(ProbeProfile profile)
{
    /// <summary>
    /// The version of the tests. Raise it whenever a change to the tests would move the numbers, so old checks are not compared with new ones.
    /// 1: three rounds. 2: warm-up, five rounds, a pause after the all-core test, and tests compiled up front.
    /// </summary>
    public const int Method = 2;

    private static long sink;

    /// <summary>How much of the processor the rest of the PC used while the last check ran, in percent. Null when Windows would not say.</summary>
    public double? Busy { get; private set; }

    /// <summary>Whether the PC ran on battery when the last check started. Null when Windows would not say.</summary>
    public bool? OnBattery { get; private set; }

    public Task<IReadOnlyList<ProbeMetric>> RunAsync(IProgress<string> progress, CancellationToken cancellationToken) =>
        Task.Run(() => Run(step => progress.Report(step.Line), cancellationToken), cancellationToken);

    public Task<IReadOnlyList<ProbeMetric>> RunAsync(IProgress<ProbeStep> progress, CancellationToken cancellationToken) =>
        Task.Run(() => Run(progress.Report, cancellationToken), cancellationToken);

    private IReadOnlyList<ProbeMetric> Run(Action<ProbeStep> report, CancellationToken token)
    {
        var total = 1 + (profile.WarmUp ? 1 : 0) + profile.Rounds * ProbeKinds.All.Count;
        var done = 0; var round = 0;
        void Say(string line, bool counts) { if (counts) done++; report(new(done, total, line, round, profile.Rounds)); }

        // A little above normal, so a background app's burst takes less from a test. It is put back before the thread goes back to the pool.
        var priority = Thread.CurrentThread.Priority;
        Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;
        var systemBefore = ProbeSystem.Times(); var ownBefore = Process.GetCurrentProcess().TotalProcessorTime;
        OnBattery = ProbeSystem.OnBattery();
        try
        {
            Say("Getting memory ready for the memory test.", false);
            var chain = BuildChain(profile.ChaseEntries, token);
            Say("Memory ready.", true);

            if (profile.WarmUp)
            {
                Say("Warming up.", false);
                var warm = new ProbeRunner(profile with { WakeSamples = 4, ThroughputMs = 250, CopyPasses = 2, ChaseSteps = Math.Min(profile.ChaseSteps, 2_000_000), SettleMs = 0 });
                warm.Wake(token); warm.Throughput(token); warm.Copy(token); warm.Chase(chain, token);
                Say("Warmed up.", true);
            }

            var results = ProbeKinds.All.ToDictionary(x => x.Id, _ => new List<double>());

            void Measure(ProbeKind kind, Func<double> test)
            {
                var value = test();
                results[kind.Id].Add(value);
                Say($"  {kind.Name}: {Format(value)} {kind.Unit}", true);
            }

            for (round = 1; round <= profile.Rounds; round++)
            {
                token.ThrowIfCancellationRequested();
                Say($"Round {round} of {profile.Rounds}", false);
                Measure(ProbeKinds.Wake, () => Wake(token));
                Measure(ProbeKinds.Throughput, () => Throughput(token));
                if (profile.SettleMs > 0) Thread.Sleep(profile.SettleMs);
                Measure(ProbeKinds.Copy, () => Copy(token));
                Measure(ProbeKinds.Random, () => Chase(chain, token));
            }

            Busy = Others(systemBefore, ProbeSystem.Times(), ownBefore, Process.GetCurrentProcess().TotalProcessorTime);
            return ProbeKinds.All.Select(x => new ProbeMetric(x.Id, ProbeMath.Median(results[x.Id]), ProbeMath.Spread(results[x.Id]), results[x.Id].ToList())).ToList();
        }
        finally { Thread.CurrentThread.Priority = priority; }
    }

    /// <summary>The share of the PC's processor time that went to everything except this program, in percent.</summary>
    internal static double? Others((ulong Idle, ulong Total)? before, (ulong Idle, ulong Total)? after, TimeSpan ownBefore, TimeSpan ownAfter)
    {
        if (before is not { } a || after is not { } b || b.Total <= a.Total) return null;
        var total = (double)(b.Total - a.Total);
        var busy = total - (b.Idle - a.Idle);
        var own = (ownAfter - ownBefore).Ticks;
        return Math.Clamp((busy - own) / total * 100, 0, 100);
    }

    public static string Format(double value) => value >= 100 ? value.ToString("0", CultureInfo.InvariantCulture) : value >= 10 ? value.ToString("0.0", CultureInfo.InvariantCulture) : value.ToString("0.00", CultureInfo.InvariantCulture);

    // A short burst of work after the processor has been left idle. Windows power plans decide how quickly the clock speed comes back.
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
        }) { IsBackground = true, Priority = ThreadPriority.AboveNormal }).ToList();
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
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
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

    // Compiled fully optimised from the first call. Left to the default, the first calls run in a slower, quickly-made form and later ones in a faster
    // one, and where that switch falls differs from check to check.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static ulong Spin(int iterations)
    {
        var x = 88172645463325252UL;
        for (var i = 0; i < iterations; i++) { x ^= x << 13; x ^= x >> 7; x ^= x << 17; }
        return x;
    }
}

public enum ProbeVerdict { Better, Worse, NoDifference }

/// <param name="Percent">How much better (positive) or worse (negative) the newer check is, whichever direction counts as better for this test.</param>
/// <param name="Needed">How big the change had to be, in percent, to count at all. Shown so "no clear difference" can say how far off it was.</param>
/// <param name="Separate">Whether every round of one check was better than every round of the other. Without it, a change is only a hunch.</param>
/// <param name="Disturbed">Whether other programs were busy during either check. A disturbed check measures the other programs as much as the setting, so no verdict is given.</param>
public sealed record ProbeChange(ProbeKind Kind, double Before, double After, double Percent, ProbeVerdict Verdict, double Needed = 0, bool Separate = true, bool Disturbed = false);

public static class ProbeCompare
{
    /// <summary>
    /// The smallest change, in percent, that counts at all. Two checks of the very same PC differed by up to 4% in memory access when this was
    /// measured, because the rounds of one check move together more than separate checks do.
    /// </summary>
    public const double Floor = 5;

    /// <summary>Above this share of the processor used by other programs, a check is called disturbed.</summary>
    public const double BusyLimit = 8;

    /// <summary>Only checks made by the same version of the tests mean the same thing. A newer version measures differently on purpose.</summary>
    public static bool Comparable(ProbeRun a, ProbeRun b) => a.Method == b.Method;

    /// <summary>The chance, in a rank test of the rounds of two checks, below which the rounds are taken to really differ.</summary>
    public const double Significance = 0.05;

    /// <summary>
    /// Compares two checks. A difference only counts when it is big enough and not an accident of the rounds. Checks that kept their rounds need the median
    /// to move by more than the test's floor and the rounds to separate (a Mann-Whitney rank test, p below 0.05: with five rounds each, at most two of the 25
    /// pairs of rounds may point the other way). Rounds in one check moved by 12% while its middle result moved by 3%, so a bare percentage is not enough on its
    /// own, and separate checks of one PC differ by a few percent even when the rounds inside each look tight, so a rank test is not enough on its own either.
    /// Checks without rounds (saved by an older version) fall back to the percentage, which must also beat twice how much their rounds differed.
    /// Otherwise the answer is "no difference", which is the honest answer for most settings. If other programs used more than <see cref="BusyLimit"/> percent of
    /// the processor during either check, nothing is called a difference at all: that check measured those programs as much as the setting.
    /// </summary>
    public static IReadOnlyList<ProbeChange> Between(ProbeRun before, ProbeRun after)
    {
        var changes = new List<ProbeChange>();
        var disturbed = before.Busy > BusyLimit || after.Busy > BusyLimit;
        foreach (var kind in ProbeKinds.All)
        {
            var a = before.Metrics.FirstOrDefault(x => x.Id == kind.Id); var b = after.Metrics.FirstOrDefault(x => x.Id == kind.Id);
            if (a is null || b is null || a.Value <= 0) continue;
            var change = (b.Value - a.Value) / a.Value * 100;
            var percent = kind.HigherIsBetter ? change : -change;
            var haveRounds = a.Rounds is { Count: > 0 } && b.Rounds is { Count: > 0 };
            var limit = haveRounds ? kind.Floor : Math.Max(kind.Floor, 2 * Math.Max(a.Spread, b.Spread));
            var separate = !haveRounds || RankP(kind, a.Rounds!, b.Rounds!, percent) < Significance;
            var counts = Math.Abs(percent) >= limit && separate && !disturbed;
            changes.Add(new(kind, a.Value, b.Value, percent, counts ? (percent > 0 ? ProbeVerdict.Better : ProbeVerdict.Worse) : ProbeVerdict.NoDifference, limit, separate, disturbed));
        }
        return changes;
    }

    /// <summary>
    /// Two-sided chance that rounds this far apart (or further) came from the same spread. Counts the pairs of rounds that point against the direction the
    /// medians moved (a tie counts half) and looks that count up in the exact distribution of the rank statistic, so there is no table to get wrong.
    /// </summary>
    internal static double RankP(ProbeKind kind, IReadOnlyList<double> before, IReadOnlyList<double> after, double percent)
    {
        if (percent == 0) return 1;
        // "Better" numbers up: the sign puts lower-is-better tests on the same footing, and the direction of the move picks which check should win.
        var sign = (kind.HigherIsBetter ? 1 : -1) * (percent > 0 ? 1 : -1);
        var against = 0.0;
        foreach (var x in after) foreach (var y in before) { var d = (x - y) * sign; against += d < 0 ? 1 : d == 0 ? 0.5 : 0; }
        return Math.Min(1, 2 * LowerTail(after.Count, before.Count, against));
    }

    /// <summary>The chance that the rank statistic U of two samples of these sizes is at most <paramref name="u"/>, when both come from the same spread.</summary>
    internal static double LowerTail(int n, int m, double u)
    {
        // counts[k] = number of ways to order n and m values so that U is exactly k.
        var counts = Counts(n, m);
        var total = counts.Sum();
        var cutoff = (int)Math.Floor(u + 1e-9);
        // A half (a tie) rounds down, which keeps the answer on the cautious side.
        return counts.Take(Math.Min(cutoff, counts.Length - 1) + 1).Sum() / total;
    }

    private static double[] Counts(int n, int m)
    {
        // f[i, j, k]: arrangements of i and j values with U = k. U for (i, j) comes from the largest value being in the first or the second sample.
        var f = new double[n + 1, m + 1][];
        for (var i = 0; i <= n; i++)
            for (var j = 0; j <= m; j++)
            {
                var row = new double[i * j + 1];
                if (i == 0 || j == 0) row[0] = 1;
                else
                    for (var k = 0; k < row.Length; k++)
                    {
                        // The largest value is in the first sample: it beats all j of the other, adding j to U.
                        if (k >= j && k - j < f[i - 1, j].Length) row[k] += f[i - 1, j][k - j];
                        // Or in the second sample: it beats none of the first.
                        if (k < f[i, j - 1].Length) row[k] += f[i, j - 1][k];
                    }
                f[i, j] = row;
            }
        return f[n, m];
    }
}

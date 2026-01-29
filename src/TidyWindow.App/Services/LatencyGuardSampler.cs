using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace TidyWindow.App.Services;

public sealed class LatencyGuardSampler : ILatencyGuardSampler
{
    public record GpuProcessSample(int Pid, string Name, double UtilizationPercent);

    public record LatencySample(double GpuUtilPercent, double DpcUtilPercent, IReadOnlyList<GpuProcessSample> TopProcesses);

    public async Task<LatencySample> SampleAsync()
    {
        // Performance counters require two reads with a delay between them for accurate data.
        // First read initializes the counter; second read gives the actual value.

        var gpuCounters = CreateGpuCounters();
        var dpcCounter = CreateDpcCounter();

        // Prime all counters
        foreach (var (counter, _) in gpuCounters)
        {
            try { counter.NextValue(); } catch { }
        }
        try { dpcCounter?.NextValue(); } catch { }

        // Wait for sampling interval
        await Task.Delay(250).ConfigureAwait(false);

        // Second read for actual values
        var gpuData = ReadGpuCounters(gpuCounters);
        var dpc = ReadDpcCounter(dpcCounter);

        // Dispose counters
        foreach (var (counter, _) in gpuCounters)
        {
            try { counter.Dispose(); } catch { }
        }
        try { dpcCounter?.Dispose(); } catch { }

        return new LatencySample(
            gpuData.TotalUtilization,
            dpc,
            gpuData.ByProcess
                .OrderByDescending(p => p.UtilizationPercent)
                .Take(5)
                .ToArray());
    }

    private static List<(PerformanceCounter Counter, string Instance)> CreateGpuCounters()
    {
        var counters = new List<(PerformanceCounter, string)>();

        try
        {
            var category = new PerformanceCounterCategory("GPU Engine");
            var instances = category.GetInstanceNames();

            foreach (var inst in instances)
            {
                // Include all engine types (3D, Compute, Copy, Video, etc.)
                if (string.IsNullOrEmpty(inst) || !inst.Contains("pid_", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    var counter = new PerformanceCounter("GPU Engine", "Utilization Percentage", inst, true);
                    counters.Add((counter, inst));
                }
                catch
                {
                    // skip invalid instances
                }
            }
        }
        catch
        {
            // GPU Engine category not available
        }

        return counters;
    }

    private static PerformanceCounter? CreateDpcCounter()
    {
        try
        {
            return new PerformanceCounter("Processor", "% DPC Time", "_Total", true);
        }
        catch
        {
            return null;
        }
    }

    private static (double TotalUtilization, List<GpuProcessSample> ByProcess) ReadGpuCounters(
        List<(PerformanceCounter Counter, string Instance)> counters)
    {
        var byProcess = new Dictionary<int, double>();
        double total = 0;

        foreach (var (counter, inst) in counters)
        {
            try
            {
                var value = counter.NextValue();
                if (value < 0.01)
                {
                    continue;
                }

                total += value;

                var pid = TryParsePid(inst);
                if (pid is null)
                {
                    continue;
                }

                if (byProcess.TryGetValue(pid.Value, out var existing))
                {
                    byProcess[pid.Value] = existing + value;
                }
                else
                {
                    byProcess[pid.Value] = value;
                }
            }
            catch
            {
                // ignore read errors
            }
        }

        var list = new List<GpuProcessSample>();
        foreach (var kvp in byProcess)
        {
            string name;
            try
            {
                using var process = Process.GetProcessById(kvp.Key);
                name = process.ProcessName;
            }
            catch
            {
                name = "(exited)";
            }

            list.Add(new GpuProcessSample(kvp.Key, name, Math.Round(kvp.Value, 1)));
        }

        // Cap total at 100% (sum of per-engine can exceed 100% on multi-GPU)
        return (Math.Min(Math.Round(total, 1), 100.0), list);
    }

    private static double ReadDpcCounter(PerformanceCounter? counter)
    {
        if (counter is null)
        {
            return 0d;
        }

        try
        {
            var value = counter.NextValue();
            return Math.Round(value, 1);
        }
        catch
        {
            return 0d;
        }
    }

    private static int? TryParsePid(string instanceName)
    {
        // pattern: pid_1234_luid_0x000..._engtype_3D
        var idx = instanceName.IndexOf("pid_", StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return null;
        }

        idx += 4;
        var end = instanceName.IndexOf('_', idx);
        if (end < 0)
        {
            end = instanceName.Length;
        }

        var span = instanceName.AsSpan(idx, end - idx);
        if (int.TryParse(span, out var pid))
        {
            return pid;
        }

        return null;
    }
}

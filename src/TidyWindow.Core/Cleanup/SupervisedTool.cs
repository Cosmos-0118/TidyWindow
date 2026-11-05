using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TidyWindow.Next.Core;

/// <summary>How a supervised program came to an end.</summary>
public enum ToolEnd
{
    /// <summary>Every process the program started has ended.</summary>
    Finished,
    /// <summary>It ran past its time limit and was ended.</summary>
    TimedOut,
    /// <summary>It did no work (no processor time, no disk reads or writes, in any of its processes) for the whole stall limit, so it was ended.</summary>
    Stalled
}

public sealed record ToolOutcome(int ExitCode, ToolEnd End);

/// <summary>
/// Runs one of Windows' own programs (Disk Cleanup, for one) and does not trust the program to behave. Disk Cleanup hands its real work to child processes and
/// can exit while they are still going, can be closed by the person from its own window, and can sit finished but idle until someone moves the mouse.
/// So the program starts inside a job object, which tracks it and everything it starts: the run is over when the last of them has ended, not when the first
/// one has. Its windows are switched off as they appear, so they cannot be closed or cancelled. A run that does no work for the stall limit is ended, and the job dies with this
/// process unless told otherwise, so a crash can never leave a Windows tool running unattended.
/// </summary>
internal static class SupervisedTool
{
    private const int JobObjectBasicProcessIdList = 3, JobObjectBasicAndIoAccounting = 8, JobObjectExtendedLimit = 9;
    private const uint KillOnJobClose = 0x2000;
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(250);

    [StructLayout(LayoutKind.Sequential)]
    private struct Accounting
    {
        public long TotalUserTime, TotalKernelTime, ThisPeriodUserTime, ThisPeriodKernelTime;
        public uint PageFaults, TotalProcesses, ActiveProcesses, TerminatedProcesses;
        public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters { public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes; }

    private delegate bool WindowCallback(IntPtr window, IntPtr parameter);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObjectW(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, byte[] info, int size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(IntPtr job, int infoClass, out Accounting info, int size, IntPtr returned);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(IntPtr job, int infoClass, byte[] info, int size, IntPtr returned);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(IntPtr job, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessIoCounters(IntPtr process, out IoCounters counters);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(WindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowEnabled(IntPtr window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnableWindow(IntPtr window, [MarshalAs(UnmanagedType.Bool)] bool enable);

    /// <param name="stallLimit">How long all of the program's processes may do nothing before it is ended; <see cref="Timeout.InfiniteTimeSpan"/> for never.</param>
    /// <param name="lockWindows">Switch off every window the program opens, so nobody can close or cancel it half way. The windows stay showing: Disk Cleanup is no more reliable with its window hidden, and the person can see it is working.</param>
    /// <param name="endWithUs">End the program if this process ends. Off for programs that must be allowed to finish what they started (the component store cleanup).</param>
    /// <param name="alsoWatch">Names of Windows services (without .exe) that do work on the program's behalf outside its own processes, such as the servicing worker behind Disk Cleanup. Their activity counts as the program being busy.</param>
    /// <exception cref="OperationCanceledException">A stop was requested; the program has been ended.</exception>
    public static async Task<ToolOutcome> RunAsync(string fileName, string[] arguments, TimeSpan timeout, TimeSpan stallLimit, bool lockWindows, bool endWithUs, CancellationToken cancellationToken, string[]? alsoWatch = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Supervised programs run on Windows only.");
        var start = new ProcessStartInfo(fileName) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = lockWindows ? ProcessWindowStyle.Normal : ProcessWindowStyle.Hidden };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        var job = CreateJob(endWithUs);
        try
        {
            using var process = Process.Start(start) ?? throw new InvalidOperationException($"{fileName} could not be started.");
            // A process that cannot join a job (one already in a job that forbids it) is still watched, just without its children.
            var tracked = job != IntPtr.Zero && AssignProcessToJobObject(job, process.Handle);
            var clock = Stopwatch.StartNew();
            var lastWork = Stopwatch.StartNew();
            var seen = (Cpu: -1L, Io: ulong.MaxValue);
            var pids = new HashSet<uint>();
            var service = (Cpu: 0L, Io: 0UL); var serviceAt = Stopwatch.StartNew();
            while (true)
            {
                bool running;
                // Work done for the program by Windows services shows in no process of its own, so it is looked at separately, every couple of seconds.
                if (alsoWatch is { Length: > 0 } && serviceAt.Elapsed > TimeSpan.FromSeconds(2))
                {
                    serviceAt.Restart();
                    var now = ServiceWork(alsoWatch);
                    if (now != service) { service = now; lastWork.Restart(); }
                }
                if (tracked)
                {
                    if (!QueryInformationJobObject(job, JobObjectBasicAndIoAccounting, out var info, Marshal.SizeOf<Accounting>(), IntPtr.Zero)) { tracked = false; continue; }
                    running = info.ActiveProcesses > 0;
                    var work = (Cpu: info.TotalUserTime + info.TotalKernelTime, Io: info.ReadBytes + info.WriteBytes + info.ReadOps + info.WriteOps);
                    if (work != seen) { seen = work; lastWork.Restart(); }
                }
                else
                {
                    running = !process.HasExited;
                    var cpu = running ? SafeCpu(process) : seen.Cpu;
                    if (cpu != seen.Cpu) { seen = (cpu, 0); lastWork.Restart(); }
                }
                if (!running) break;
                if (cancellationToken.IsCancellationRequested) { End(job, process, tracked); cancellationToken.ThrowIfCancellationRequested(); }
                if (clock.Elapsed > timeout) { End(job, process, tracked); return new ToolOutcome(Exit(process), ToolEnd.TimedOut); }
                if (stallLimit != Timeout.InfiniteTimeSpan && lastWork.Elapsed > stallLimit) { End(job, process, tracked); return new ToolOutcome(Exit(process), ToolEnd.Stalled); }
                if (lockWindows) LockWindows(job, process, tracked, pids);
                try { await Task.Delay(Poll, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { End(job, process, tracked); throw; }
            }
            return new ToolOutcome(Exit(process), ToolEnd.Finished);
        }
        finally { if (job != IntPtr.Zero) CloseHandle(job); }
    }

    private static int Exit(Process process)
    {
        try { process.WaitForExit(2000); return process.HasExited ? process.ExitCode : -1; }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { return -1; }
    }

    /// <summary>Processor time and bytes moved so far by every running process of the named services. Anything that cannot be read counts as nothing.</summary>
    private static (long Cpu, ulong Io) ServiceWork(string[] names)
    {
        long cpu = 0; ulong io = 0;
        foreach (var name in names)
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    cpu += SafeCpu(process);
                    var handle = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, (uint)process.Id);
                    if (handle == IntPtr.Zero) continue;
                    try { if (GetProcessIoCounters(handle, out var counters)) io += counters.ReadBytes + counters.WriteBytes + counters.ReadOps + counters.WriteOps; }
                    finally { CloseHandle(handle); }
                }
            }
        }
        return (cpu, io);
    }

    private static long SafeCpu(Process process)
    {
        try { return process.TotalProcessorTime.Ticks; }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { return -1; }
    }

    private static void End(IntPtr job, Process process, bool tracked)
    {
        if (tracked) TerminateJobObject(job, 1);
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
    }

    private static IntPtr CreateJob(bool endWithUs)
    {
        var job = CreateJobObjectW(IntPtr.Zero, null);
        if (job == IntPtr.Zero) return IntPtr.Zero;
        if (endWithUs)
        {
            // JOBOBJECT_EXTENDED_LIMIT_INFORMATION is 144 bytes on 64-bit Windows; LimitFlags sits 16 bytes in.
            var limits = new byte[IntPtr.Size == 8 ? 144 : 112];
            BitConverter.TryWriteBytes(limits.AsSpan(16, 4), KillOnJobClose);
            if (!SetInformationJobObject(job, JobObjectExtendedLimit, limits, limits.Length)) { CloseHandle(job); return IntPtr.Zero; }
        }
        return job;
    }

    private static void LockWindows(IntPtr job, Process process, bool tracked, HashSet<uint> pids)
    {
        pids.Clear();
        if (tracked)
        {
            // JOBOBJECT_BASIC_PROCESS_ID_LIST: two counts, then the ids.
            var list = new byte[8 + IntPtr.Size * 64];
            if (QueryInformationJobObject(job, JobObjectBasicProcessIdList, list, list.Length, IntPtr.Zero))
            {
                var count = Math.Min(BitConverter.ToInt32(list, 4), 64);
                for (var i = 0; i < count; i++) pids.Add((uint)(IntPtr.Size == 8 ? BitConverter.ToInt64(list, 8 + i * 8) : BitConverter.ToInt32(list, 8 + i * 4)));
            }
        }
        else
        {
            try { pids.Add((uint)process.Id); } catch (InvalidOperationException) { }
        }
        if (pids.Count == 0) return;
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var owner);
            // Switched off again if the program turns it back on. A window that is already off costs nothing.
            if (pids.Contains(owner) && IsWindowVisible(window) && IsWindowEnabled(window)) EnableWindow(window, false);
            return true;
        }, IntPtr.Zero);
    }
}

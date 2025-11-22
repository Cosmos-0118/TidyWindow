using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TidyWindow.App.Services;

public enum ResourceCloseMode
{
    Graceful,
    Force
}

public readonly record struct ResourceLockHandle(int ProcessId, DateTime? ProcessStartTimeUtc);

public sealed record ResourceLockInfo(
    ResourceLockHandle Handle,
    string DisplayName,
    string Description,
    bool IsService,
    bool IsCritical,
    bool IsRestartable,
    IReadOnlyList<string> ResourcePaths)
{
    public int ProcessId => Handle.ProcessId;
}

public sealed record ResourceCloseResult(bool Success, string Message, int TargetCount);

public interface IResourceLockService
{
    Task<IReadOnlyList<ResourceLockInfo>> InspectAsync(IEnumerable<string> resourcePaths, CancellationToken cancellationToken = default);

    Task<ResourceCloseResult> CloseAsync(IEnumerable<ResourceLockHandle> handles, ResourceCloseMode mode, CancellationToken cancellationToken = default);
}

/// <summary>
/// Wraps the Windows Restart Manager API to discover and close processes locking a resource set.
/// </summary>
public sealed class ResourceLockService : IResourceLockService
{
    private const int GracefulCloseWaitMilliseconds = 4000;
    private const int ForceCloseWaitMilliseconds = 2500;
    private static readonly TimeSpan ProcessStartTolerance = TimeSpan.FromSeconds(2);

    public Task<IReadOnlyList<ResourceLockInfo>> InspectAsync(IEnumerable<string> resourcePaths, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult<IReadOnlyList<ResourceLockInfo>>(Array.Empty<ResourceLockInfo>());
        }

        if (resourcePaths is null)
        {
            throw new ArgumentNullException(nameof(resourcePaths));
        }

        var normalized = resourcePaths
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(static path => path.Trim())
            .Where(static path => path.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (normalized.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<ResourceLockInfo>>(Array.Empty<ResourceLockInfo>());
        }

        return Task.Run(() => InspectInternalAsync(normalized, cancellationToken), cancellationToken);
    }

    public Task<ResourceCloseResult> CloseAsync(IEnumerable<ResourceLockHandle> handles, ResourceCloseMode mode, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult(new ResourceCloseResult(false, "Closing apps is only supported on Windows.", 0));
        }

        if (handles is null)
        {
            throw new ArgumentNullException(nameof(handles));
        }

        var snapshot = handles.Where(static handle => handle.ProcessId > 0).Distinct().ToList();
        if (snapshot.Count == 0)
        {
            return Task.FromResult(new ResourceCloseResult(false, "No running apps were selected for closing.", 0));
        }

        return Task.Run(() => CloseInternalAsync(snapshot, mode, cancellationToken), cancellationToken);
    }

    private static IReadOnlyList<ResourceLockInfo> InspectInternalAsync(IReadOnlyList<string> resources, CancellationToken cancellationToken)
    {
        var processMap = new Dictionary<int, ProcessLockAccumulator>();

        foreach (var path in resources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var processes = QueryRestartManager(path);
            if (processes.Count == 0)
            {
                continue;
            }

            foreach (var process in processes)
            {
                var pid = (int)process.Process.dwProcessId;
                if (!processMap.TryGetValue(pid, out var accumulator))
                {
                    accumulator = new ProcessLockAccumulator(process);
                    processMap[pid] = accumulator;
                }

                accumulator.AddPath(path);
            }
        }

        return processMap.Values
            .OrderByDescending(static acc => acc.PathCount)
            .ThenBy(static acc => acc.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(static acc => acc.ToInfo())
            .ToList();
    }

    private static ResourceCloseResult CloseInternalAsync(IReadOnlyList<ResourceLockHandle> handles, ResourceCloseMode mode, CancellationToken cancellationToken)
    {
        var session = 0u;
        var sessionKey = Guid.NewGuid().ToString();
        var result = NativeMethods.RmStartSession(out session, 0, sessionKey);
        if (result != 0)
        {
            return new ResourceCloseResult(false, $"Unable to start Restart Manager session (0x{result:X}).", handles.Count);
        }

        try
        {
            var uniqueProcesses = BuildUniqueProcessArray(handles);
            if (uniqueProcesses.Length == 0)
            {
                return new ResourceCloseResult(false, "Apps exited before we could close them.", 0);
            }

            result = NativeMethods.RmRegisterResources(session, 0, null, (uint)uniqueProcesses.Length, uniqueProcesses, 0, null);
            if (result != 0)
            {
                return new ResourceCloseResult(false, $"Unable to register processes with Restart Manager (0x{result:X}).", uniqueProcesses.Length);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var shutdownFlags = mode == ResourceCloseMode.Force
                ? NativeMethods.RM_SHUTDOWN_TYPE.RmForceShutdown | NativeMethods.RM_SHUTDOWN_TYPE.RmShutdownOnlyRegistered
                : NativeMethods.RM_SHUTDOWN_TYPE.RmShutdownOnlyRegistered;

            result = NativeMethods.RmShutdown(session, shutdownFlags, IntPtr.Zero);
            if (result == 0)
            {
                return new ResourceCloseResult(true, $"Requested shutdown for {uniqueProcesses.Length} app(s).", uniqueProcesses.Length);
            }

            if (result == NativeMethods.ERROR_FAIL_NOACTION_REBOOT)
            {
                return new ResourceCloseResult(false, "Windows marked at least one locking app as critical; restart Windows to close it.", uniqueProcesses.Length);
            }

            var fallback = TryFallbackCloseProcesses(handles, mode, result, cancellationToken);
            if (fallback is not null)
            {
                return fallback;
            }

            return new ResourceCloseResult(false, BuildRmFailureMessage(result), uniqueProcesses.Length);
        }
        finally
        {
            NativeMethods.RmEndSession(session);
        }
    }

    private static NativeMethods.RM_UNIQUE_PROCESS[] BuildUniqueProcessArray(IEnumerable<ResourceLockHandle> handles)
    {
        var list = new List<NativeMethods.RM_UNIQUE_PROCESS>();
        foreach (var handle in handles)
        {
            if (handle.ProcessId <= 0)
            {
                continue;
            }

            var unique = new NativeMethods.RM_UNIQUE_PROCESS
            {
                dwProcessId = (uint)handle.ProcessId,
                ProcessStartTime = handle.ProcessStartTimeUtc.HasValue
                    ? NativeMethods.ToFileTime(handle.ProcessStartTimeUtc.Value)
                    : NativeMethods.FILETIME.Zero
            };

            list.Add(unique);
        }

        return list.ToArray();
    }

    private static IReadOnlyList<NativeMethods.RM_PROCESS_INFO> QueryRestartManager(string resourcePath)
    {
        var session = 0u;
        var sessionKey = Guid.NewGuid().ToString();
        var result = NativeMethods.RmStartSession(out session, 0, sessionKey);
        if (result != 0)
        {
            return Array.Empty<NativeMethods.RM_PROCESS_INFO>();
        }

        try
        {
            var resources = new[] { resourcePath };
            result = NativeMethods.RmRegisterResources(session, (uint)resources.Length, resources, 0, null, 0, null);
            if (result != 0)
            {
                return Array.Empty<NativeMethods.RM_PROCESS_INFO>();
            }

            var needed = 0u;
            var actual = 0u;
            var rebootReasons = 0u;
            result = NativeMethods.RmGetList(session, out needed, ref actual, null, ref rebootReasons);
            if (result != NativeMethods.ERROR_MORE_DATA)
            {
                return Array.Empty<NativeMethods.RM_PROCESS_INFO>();
            }

            var processes = new NativeMethods.RM_PROCESS_INFO[needed];
            actual = needed;
            rebootReasons = 0;
            result = NativeMethods.RmGetList(session, out needed, ref actual, processes, ref rebootReasons);
            if (result != 0)
            {
                return Array.Empty<NativeMethods.RM_PROCESS_INFO>();
            }

            return processes.Take((int)actual).ToArray();
        }
        finally
        {
            NativeMethods.RmEndSession(session);
        }
    }

    private static string BuildRmFailureMessage(int errorCode)
    {
        return errorCode switch
        {
            NativeMethods.ERROR_FAIL_SHUTDOWN => "Windows could not close some of the selected apps. Try force close or restart Windows.",
            NativeMethods.ERROR_FAIL_RESTART => "Windows could not restart the apps it closed earlier.",
            NativeMethods.ERROR_CANCELLED => "Closing apps was cancelled before Windows finished.",
            NativeMethods.ERROR_SEM_TIMEOUT => "Windows timed out while trying to close the selected apps.",
            NativeMethods.ERROR_BAD_ARGUMENTS => "Windows rejected the shutdown request because it considered the parameters invalid.",
            NativeMethods.ERROR_WRITE_FAULT => "Windows encountered a write fault while closing the selected apps.",
            NativeMethods.ERROR_OUTOFMEMORY => "Windows ran out of memory while attempting to close the apps.",
            NativeMethods.ERROR_INVALID_HANDLE => "Windows lost the Restart Manager session handle before it could close the apps.",
            _ => BuildDefaultErrorMessage(errorCode)
        };
    }

    private static string BuildDefaultErrorMessage(int errorCode)
    {
        try
        {
            var exceptionMessage = new Win32Exception(errorCode).Message;
            if (!string.IsNullOrWhiteSpace(exceptionMessage))
            {
                return $"Closing apps failed: {exceptionMessage} (0x{errorCode:X}).";
            }
        }
        catch
        {
            // Ignore lookup failures; we will fall back to hex.
        }

        return $"Closing apps failed with 0x{errorCode:X}.";
    }

    private static ResourceCloseResult? TryFallbackCloseProcesses(IReadOnlyList<ResourceLockHandle> handles, ResourceCloseMode mode, int rmErrorCode, CancellationToken cancellationToken)
    {
        if (handles.Count == 0)
        {
            return null;
        }

        var (attempted, closed, remaining) = CloseProcessesDirectly(handles, mode, cancellationToken);
        if (attempted == 0)
        {
            return null;
        }

        var builder = new StringBuilder();
        builder.Append($"Windows Restart Manager returned 0x{rmErrorCode:X}. ");

        if (remaining.Count == 0)
        {
            builder.Append(mode == ResourceCloseMode.Force
                ? $"Terminated {closed} blocking process(es) directly."
                : $"Closed {closed} blocking process(es) by sending close messages.");
            return new ResourceCloseResult(true, builder.ToString(), attempted);
        }

        builder.Append(mode == ResourceCloseMode.Force
            ? $"Terminated {closed} of {attempted} process(es); still running: "
            : $"Requested close for {attempted} process(es); still running: ");
        builder.Append(string.Join(", ", remaining.Take(3)));
        if (remaining.Count > 3)
        {
            builder.Append(", ...");
        }

        builder.Append('.');
        return new ResourceCloseResult(false, builder.ToString(), attempted);
    }

    private static (int Attempted, int Closed, List<string> Remaining) CloseProcessesDirectly(IReadOnlyList<ResourceLockHandle> handles, ResourceCloseMode mode, CancellationToken cancellationToken)
    {
        var attempted = 0;
        var closed = 0;
        var remaining = new List<string>();

        foreach (var handle in handles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var process = TryGetProcess(handle);
            if (process is null)
            {
                continue;
            }

            var descriptor = DescribeProcess(process);
            using (process)
            {
                attempted++;
                var succeeded = mode == ResourceCloseMode.Force
                    ? TryKillProcess(process)
                    : TryCloseProcessGracefully(process);

                if (succeeded)
                {
                    closed++;
                }
                else
                {
                    remaining.Add(descriptor);
                }
            }
        }

        return (attempted, closed, remaining);
    }

    private static Process? TryGetProcess(ResourceLockHandle handle)
    {
        try
        {
            var process = Process.GetProcessById(handle.ProcessId);
            if (process.HasExited)
            {
                process.Dispose();
                return null;
            }

            if (handle.ProcessStartTimeUtc.HasValue)
            {
                try
                {
                    var startTimeUtc = process.StartTime.ToUniversalTime();
                    if ((startTimeUtc - handle.ProcessStartTimeUtc.Value).Duration() > ProcessStartTolerance)
                    {
                        process.Dispose();
                        return null;
                    }
                }
                catch
                {
                    // Unable to read start time; fall back to PID-only verification.
                }
            }

            return process;
        }
        catch
        {
            return null;
        }
    }

    private static bool TryCloseProcessGracefully(Process process)
    {
        try
        {
            if (process.HasExited)
            {
                return true;
            }

            if (!process.CloseMainWindow())
            {
                return process.HasExited;
            }

            return process.WaitForExit(GracefulCloseWaitMilliseconds);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryKillProcess(Process process)
    {
        try
        {
            if (process.HasExited)
            {
                return true;
            }

            process.Kill(entireProcessTree: true);
            return process.WaitForExit(ForceCloseWaitMilliseconds);
        }
        catch
        {
            return false;
        }
    }

    private static string DescribeProcess(Process process)
    {
        try
        {
            var name = process.ProcessName;
            if (string.IsNullOrWhiteSpace(name))
            {
                return $"PID {process.Id}";
            }

            return $"{name} (PID {process.Id})";
        }
        catch
        {
            return $"PID {process.Id}";
        }
    }

    private sealed class ProcessLockAccumulator
    {
        private readonly NativeMethods.RM_PROCESS_INFO _processInfo;
        private readonly HashSet<string> _paths = new(StringComparer.OrdinalIgnoreCase);

        public ProcessLockAccumulator(NativeMethods.RM_PROCESS_INFO processInfo)
        {
            _processInfo = processInfo;
        }

        public int PathCount => _paths.Count;

        public string DisplayName => !string.IsNullOrWhiteSpace(_processInfo.strAppName)
            ? _processInfo.strAppName
            : !string.IsNullOrWhiteSpace(_processInfo.strServiceShortName)
                ? _processInfo.strServiceShortName
                : $"Process {_processInfo.Process.dwProcessId}";

        public void AddPath(string path)
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                _paths.Add(path);
            }
        }

        public ResourceLockInfo ToInfo()
        {
            var handle = new ResourceLockHandle((int)_processInfo.Process.dwProcessId, NativeMethods.ToDateTime(_processInfo.Process.ProcessStartTime));
            var descriptionBuilder = new StringBuilder();
            descriptionBuilder.Append(_processInfo.strAppName ?? string.Empty);
            if (!string.IsNullOrWhiteSpace(_processInfo.strServiceShortName))
            {
                if (descriptionBuilder.Length > 0)
                {
                    descriptionBuilder.Append(" • ");
                }

                descriptionBuilder.Append(_processInfo.strServiceShortName);
            }

            var description = descriptionBuilder.Length == 0
                ? "Application"
                : descriptionBuilder.ToString();

            var status = (NativeMethods.RmAppStatus)_processInfo.AppStatus;

            return new ResourceLockInfo(
                handle,
                DisplayName,
                description,
                _processInfo.ApplicationType == NativeMethods.RM_APP_TYPE.RmService,
                status.HasFlag(NativeMethods.RmAppStatus.RmCritical),
                _processInfo.bRestartable,
                _paths.ToList());
        }
    }

    private static class NativeMethods
    {
        public const int ERROR_MORE_DATA = 234;
        public const int ERROR_FAIL_NOACTION_REBOOT = 350;
        public const int ERROR_FAIL_SHUTDOWN = 351;
        public const int ERROR_FAIL_RESTART = 352;
        public const int ERROR_CANCELLED = 1223;
        public const int ERROR_SEM_TIMEOUT = 121;
        public const int ERROR_BAD_ARGUMENTS = 160;
        public const int ERROR_WRITE_FAULT = 29;
        public const int ERROR_OUTOFMEMORY = 14;
        public const int ERROR_INVALID_HANDLE = 6;

        [Flags]
        public enum RM_SHUTDOWN_TYPE : uint
        {
            RmShutdownOnlyRegistered = 0x00,
            RmForceShutdown = 0x01
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct FILETIME
        {
            public uint dwLowDateTime;
            public uint dwHighDateTime;

            public static FILETIME Zero => new() { dwHighDateTime = 0, dwLowDateTime = 0 };
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RM_UNIQUE_PROCESS
        {
            public uint dwProcessId;
            public FILETIME ProcessStartTime;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct RM_PROCESS_INFO
        {
            public RM_UNIQUE_PROCESS Process;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string? strAppName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string? strServiceShortName;
            public RM_APP_TYPE ApplicationType;
            public uint AppStatus;
            public uint TSSessionId;
            [MarshalAs(UnmanagedType.Bool)]
            public bool bRestartable;
        }

        [Flags]
        public enum RmAppStatus : uint
        {
            RmStatusUnknown = 0x0,
            RmAppStatusStopped = 0x1,
            RmAppStatusRestarted = 0x2,
            RmAppStatusErrorOnStop = 0x4,
            RmAppStatusErrorOnRestart = 0x8,
            RmAppStatusShutdownMasked = 0x10,
            RmCritical = 0x20
        }

        public enum RM_APP_TYPE
        {
            RmUnknownApp = 0,
            RmMainWindow = 1,
            RmOtherWindow = 2,
            RmService = 3,
            RmExplorer = 4,
            RmConsole = 5,
            RmCritical = 1000
        }

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        public static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, string strSessionKey);

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        public static extern int RmRegisterResources(uint dwSessionHandle, uint nFiles, string[]? rgsFilenames, uint nApplications, RM_UNIQUE_PROCESS[]? rgApplications, uint nServices, string[]? rgsServiceNames);

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        public static extern int RmGetList(uint dwSessionHandle, out uint pnProcInfoNeeded, ref uint pnProcInfo, [In, Out] RM_PROCESS_INFO[]? rgAffectedApps, ref uint lpdwRebootReasons);

        [DllImport("rstrtmgr.dll")]
        public static extern int RmShutdown(uint dwSessionHandle, RM_SHUTDOWN_TYPE lActionFlags, IntPtr fnStatus);

        [DllImport("rstrtmgr.dll")]
        public static extern int RmEndSession(uint dwSessionHandle);

        public static FILETIME ToFileTime(DateTime utc)
        {
            var value = utc.ToFileTimeUtc();
            return new FILETIME
            {
                dwLowDateTime = (uint)(value & 0xFFFFFFFF),
                dwHighDateTime = (uint)(value >> 32)
            };
        }

        public static DateTime? ToDateTime(FILETIME fileTime)
        {
            if (fileTime.dwHighDateTime == 0 && fileTime.dwLowDateTime == 0)
            {
                return null;
            }

            var value = ((long)fileTime.dwHighDateTime << 32) | fileTime.dwLowDateTime;
            try
            {
                return DateTime.FromFileTimeUtc(value);
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }
    }
}

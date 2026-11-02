using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace TidyWindow.Next.Core;

/// <summary>The real thing. Everything here works as the signed-in user: no administrator permission is used or asked for.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsUnstickSystem : IUnstickSystem
{
    private static readonly int Session = Process.GetCurrentProcess().SessionId;
    private static readonly Lazy<bool> elevated = new(CleanupNative.IsAdministrator);

    public bool Elevated => elevated.Value;

    public EndResult End(IReadOnlyList<string> programs, string? commandLine = null)
    {
        int ended = 0, denied = 0;
        foreach (var name in programs)
        {
            foreach (var process in InSession(name))
            {
                using (process)
                {
                    try
                    {
                        if (commandLine is not null && CommandLine(process.Id)?.Contains(commandLine, StringComparison.OrdinalIgnoreCase) != true) continue;
                        process.Kill();
                        process.WaitForExit(3000);
                        ended++;
                    }
                    catch (Win32Exception) when (Exited(process)) { ended++; }
                    catch (Win32Exception) { denied++; }
                    catch (InvalidOperationException) { /* It closed by itself first. */ }
                }
            }
        }
        return new(ended, denied);
    }

    public bool Running(IReadOnlyList<string> programs)
    {
        foreach (var name in programs)
            foreach (var process in InSession(name))
            {
                process.Dispose();
                return true;
            }
        return false;
    }

    public bool HasProgram(string file) => File.Exists(Path.Combine(Environment.SystemDirectory, file));

    public bool Start(string file)
    {
        try
        {
            Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, file)) { UseShellExecute = false, CreateNoWindow = true })?.Dispose();
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { return false; }
    }

    public Task RestartExplorerAsync() => ExplorerShell.RestartAsync();

    public void RefreshDisplay()
    {
        // Win+Ctrl+Shift+B is handled by Windows itself, which asks the Desktop Window Manager for a fresh surface.
        ushort[] keys = [VkLeftWin, VkLeftControl, VkLeftShift, VkB];
        var input = new Input[keys.Length * 2];
        for (var i = 0; i < keys.Length; i++)
        {
            input[i] = Key(keys[i], up: false);
            input[input.Length - 1 - i] = Key(keys[i], up: true);
        }
        if (SendInput((uint)input.Length, input, Marshal.SizeOf<Input>()) != input.Length) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    public void RefreshAssociations()
    {
        SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
        EnvironmentBroadcast.Notify();
    }

    public string? CheckClipboard()
    {
        for (var i = 0; i < 5; i++)
        {
            if (OpenClipboard(IntPtr.Zero)) { CloseClipboard(); return null; }
            Thread.Sleep(100);
        }
        var window = GetOpenClipboardWindow();
        if (window != IntPtr.Zero && GetWindowThreadProcessId(window, out var pid) != 0)
        {
            try { using var holder = Process.GetProcessById((int)pid); return holder.ProcessName; }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { }
        }
        return "Another app";
    }

    public bool FlushDns() => DnsFlushResolverCache();

    public UserServiceOutcome RestartUserService(string name)
    {
        var instance = Services(name + "_").FirstOrDefault(x => x.State is 4 or 2);
        if (instance.Name is null) return UserServiceOutcome.NotFound;
        var manager = OpenSCManager(null, null, 0x0001);
        if (manager == IntPtr.Zero) return UserServiceOutcome.Denied;
        try
        {
            // Query status, start and stop: the rights a signed-in user is given on this service.
            var service = OpenService(manager, instance.Name, 0x0004 | 0x0010 | 0x0020);
            if (service == IntPtr.Zero) return Marshal.GetLastWin32Error() == 5 ? UserServiceOutcome.Denied : UserServiceOutcome.NotFound;
            try
            {
                if (!ControlService(service, 1, out _) && Marshal.GetLastWin32Error() is var stop && stop != 1062) return stop == 5 ? UserServiceOutcome.Denied : UserServiceOutcome.NotFound;
                if (!WaitFor(service, 1)) return UserServiceOutcome.Denied;
                if (!StartService(service, 0, IntPtr.Zero) && Marshal.GetLastWin32Error() is var start && start != 1056) return start == 5 ? UserServiceOutcome.Denied : UserServiceOutcome.NotFound;
                return WaitFor(service, 4) ? UserServiceOutcome.Restarted : UserServiceOutcome.Denied;
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }

    public UserServiceOutcome RestartServiceHost(string name)
    {
        var instances = Services(name + "_").Where(x => x.State == 4 && x.ProcessId != 0).ToList();
        var closed = new List<uint>();
        var denied = false;
        foreach (var instance in instances)
        {
            try
            {
                using var process = Process.GetProcessById((int)instance.ProcessId);
                // Only a process that hosts this one service is closed. A shared svchost.exe would take other services down with it.
                if (process.SessionId != Session || CommandLine(process.Id)?.Contains("-s " + name, StringComparison.OrdinalIgnoreCase) != true) continue;
                process.Kill();
                process.WaitForExit(3000);
                closed.Add(instance.ProcessId);
            }
            catch (Win32Exception) { denied = true; }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { /* It closed by itself first. */ }
        }
        if (closed.Count == 0) return denied ? UserServiceOutcome.Denied : UserServiceOutcome.NotFound;
        // Windows brings the service back after about three seconds. Waiting for it is a courtesy: it also starts on next use.
        for (var i = 0; i < 20; i++)
        {
            if (Services(name + "_").Any(x => x.State == 4 && !closed.Contains(x.ProcessId))) break;
            Thread.Sleep(250);
        }
        return UserServiceOutcome.Restarted;
    }

    public async Task<int> RunAsync(string file, string arguments, TimeSpan timeout, CancellationToken token)
    {
        using var process = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, file), arguments) { UseShellExecute = false, CreateNoWindow = true })
            ?? throw new InvalidOperationException($"{file} did not start.");
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            return process.ExitCode;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            try { process.Kill(); } catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
            return -1;
        }
    }

    // ---- processes ----

    private static List<Process> InSession(string name)
    {
        var found = new List<Process>();
        foreach (var process in Process.GetProcessesByName(name))
        {
            try { if (process.SessionId == Session) { found.Add(process); continue; } }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
            process.Dispose();
        }
        return found;
    }

    private static bool Exited(Process process)
    {
        try { return process.HasExited; }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { return false; }
    }

    /// <summary>The text a program was started with, or null when Windows does not let this user read it.</summary>
    internal static string? CommandLine(int id)
    {
        var handle = OpenProcess(0x1000, false, id);
        if (handle == IntPtr.Zero) return null;
        try
        {
            for (var size = 1024; size <= 1 << 16; size *= 4)
            {
                var buffer = Marshal.AllocHGlobal(size);
                try
                {
                    var status = NtQueryInformationProcess(handle, 60, buffer, size, out _);
                    if (status == 0)
                    {
                        // A UNICODE_STRING whose text follows it in the same buffer.
                        var length = Marshal.ReadInt16(buffer);
                        var text = Marshal.ReadIntPtr(buffer, IntPtr.Size);
                        return length == 0 || text == IntPtr.Zero ? "" : Marshal.PtrToStringUni(text, length / 2);
                    }
                    // Too small: try a bigger buffer. Anything else will not get better.
                    if (status is not (unchecked((int)0xC0000004) or unchecked((int)0xC0000023) or unchecked((int)0x80000005))) return null;
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            return null;
        }
        finally { CloseHandle(handle); }
    }

    // ---- services ----

    private readonly record struct ServiceInstance(string Name, uint State, uint ProcessId);

    /// <summary>Services whose name starts with this text, with their state and the process each lives in.</summary>
    private static List<ServiceInstance> Services(string prefix)
    {
        var list = new List<ServiceInstance>();
        var manager = OpenSCManager(null, null, 0x0004);
        if (manager == IntPtr.Zero) return list;
        var size = 1 << 18;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            uint resume = 0;
            var stride = Marshal.SizeOf<EnumServiceStatusProcess>();
            while (true)
            {
                var ok = EnumServicesStatusEx(manager, 0, 0x30, 3, buffer, (uint)size, out _, out var count, ref resume, null);
                if (!ok && Marshal.GetLastWin32Error() != 234) break;
                for (var i = 0; i < count; i++)
                {
                    var item = Marshal.PtrToStructure<EnumServiceStatusProcess>(buffer + i * stride);
                    if (Marshal.PtrToStringUni(item.ServiceName) is { } name && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        list.Add(new(name, item.Status.CurrentState, item.Status.ProcessId));
                }
                if (ok) break;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            CloseServiceHandle(manager);
        }
        return list;
    }

    private static bool WaitFor(IntPtr service, uint state)
    {
        for (var i = 0; i < 40; i++)
        {
            if (QueryServiceStatus(service, out var status) && status.CurrentState == state) return true;
            Thread.Sleep(250);
        }
        return false;
    }

    // ---- keys ----

    private const ushort VkLeftWin = 0x5B, VkLeftControl = 0xA2, VkLeftShift = 0xA0, VkB = 0x42;

    private static Input Key(ushort key, bool up) => new() { Type = 1, Union = new() { Keyboard = new() { Key = key, Flags = up ? 0x0002u : 0 } } };

    [StructLayout(LayoutKind.Sequential)]
    private struct Input { public uint Type; public InputUnion Union; }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KeyboardInput Keyboard;
        // Never used, but it is the biggest member, and Windows checks the size of the whole structure.
        [FieldOffset(0)] public MouseInput Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput { public ushort Key, Scan; public uint Flags, Time; public UIntPtr Extra; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput { public int X, Y; public uint Data, Flags, Time; public UIntPtr Extra; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus { public uint Type, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess { public uint Type, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint, ProcessId, Flags; }

    [StructLayout(LayoutKind.Sequential)]
    private struct EnumServiceStatusProcess { public IntPtr ServiceName; public IntPtr DisplayName; public ServiceStatusProcess Status; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, [In] Input[] inputs, int size);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr owner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll")]
    private static extern IntPtr GetOpenClipboardWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int events, uint flags, IntPtr item1, IntPtr item2);

    [DllImport("dnsapi.dll", EntryPoint = "DnsFlushResolverCache")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DnsFlushResolverCache();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int id);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr process, int information, IntPtr buffer, int size, out int returned);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenSCManager(string? machine, string? database, uint access);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenService(IntPtr manager, string name, uint access);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumServicesStatusEx(IntPtr manager, int infoLevel, uint serviceType, uint serviceState, IntPtr buffer, uint size, out uint bytesNeeded, out uint count, ref uint resume, string? group);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatus(IntPtr service, out ServiceStatus status);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ControlService(IntPtr service, uint control, out ServiceStatus status);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartService(IntPtr service, uint argc, IntPtr argv);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);
}

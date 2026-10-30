using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace TidyWindow.Next.Core;

/// <summary>The real PC: the service list and what is running, Task Scheduler, and Windows policy values.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsBackgroundSystem : IBackgroundSystem
{
    private const string ServicesPath = @"SYSTEM\CurrentControlSet\Services";
    private Dictionary<string, ServiceFact>? services;
    private dynamic? scheduler;

    public string WindowsFolder { get; } = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    public string Edition { get; } = ReadEdition();

    public string Expand(string text) => Environment.ExpandEnvironmentVariables(text);

    private static string ReadEdition()
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            return key?.GetValue("EditionID") as string ?? "";
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { return ""; }
    }

    public void Refresh()
    {
        services = ReadServices();
        scheduler = null;
    }

    public ServiceFact? Service(string name)
    {
        services ??= ReadServices();
        return services.GetValueOrDefault(name);
    }

    public IReadOnlyList<ServiceFact> Dependents(string name)
    {
        services ??= ReadServices();
        return services.Values.Where(x => x.Start != 4 && x.DependsOn.Contains(name, StringComparer.OrdinalIgnoreCase)).ToList();
    }

    private static Dictionary<string, ServiceFact> ReadServices()
    {
        var running = RunningServices();
        var found = new Dictionary<string, ServiceFact>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var list = root.OpenSubKey(ServicesPath);
            if (list is null) return found;
            foreach (var name in list.GetSubKeyNames())
            {
                try
                {
                    using var key = list.OpenSubKey(name);
                    if (key is null || key.GetValue("Type") is not int type || (type & 0x30) == 0) continue;
                    var depends = (key.GetValue("DependOnService") as string[] ?? []).Where(x => x.Length > 0 && x[0] != '+').ToArray();
                    var trigger = key.GetSubKeyNames().Contains("TriggerInfo", StringComparer.OrdinalIgnoreCase);
                    found[name] = new(name, Text(key, "DisplayName"), key.GetValue("Start") as int? ?? 3, key.GetValue("DelayedAutostart") as int? == 1, running.Contains(name), trigger, Text(key, "ImagePath"), type, depends);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
        return found;
    }

    private static string Text(RegistryKey key, string name) => key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "";

    private static HashSet<string> RunningServices()
    {
        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var manager = OpenSCManager(null, null, 0x0004);
        if (manager == IntPtr.Zero) return running;
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
                    if (item.Status.CurrentState is 4 or 2 && Marshal.PtrToStringUni(item.ServiceName) is { } name) running.Add(name);
                }
                if (ok) break;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            CloseServiceHandle(manager);
        }
        return running;
    }

    public TaskFact? Task(string path)
    {
        try
        {
            var split = path.LastIndexOf('\\');
            dynamic folder = Scheduler().GetFolder(split == 0 ? "\\" : path[..split]);
            dynamic task = folder.GetTask(path[(split + 1)..]);
            return new(path, (bool)task.Enabled, (int)task.State == 4);
        }
        catch (Exception ex) when (ex is IOException or COMException or UnauthorizedAccessException or InvalidOperationException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { return null; }
    }

    private dynamic Scheduler()
    {
        if (scheduler is not null) return scheduler;
        var type = Type.GetTypeFromProgID("Schedule.Service") ?? throw new InvalidOperationException("Task Scheduler is not available.");
        dynamic service = Activator.CreateInstance(type)!;
        service.Connect();
        scheduler = service;
        return service;
    }

    public int? Policy(string key, string name)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var policy = root.OpenSubKey(key);
            return policy?.GetValue(name) as int?;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { return null; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess { public uint Type, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint, ProcessId, Flags; }

    [StructLayout(LayoutKind.Sequential)]
    private struct EnumServiceStatusProcess { public IntPtr ServiceName; public IntPtr DisplayName; public ServiceStatusProcess Status; }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenSCManager(string? machine, string? database, uint access);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumServicesStatusEx(IntPtr manager, int infoLevel, uint serviceType, uint serviceState, IntPtr buffer, uint size, out uint bytesNeeded, out uint count, ref uint resume, string? group);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);
}

/// <summary>The only writes the background page makes: a service's start type and a stop or start, a task's Enabled flag, and a policy value.</summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsBackgroundWriter : IBackgroundWriter
{
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_.\- ]{0,127}$")]
    private static partial Regex ServiceName();

    private static Exception Failure()
    {
        var code = Marshal.GetLastWin32Error();
        return code == 5 ? new UnauthorizedAccessException("Administrator permission is needed to change a service.") : new Win32Exception(code);
    }

    private static void Check(string name)
    {
        if (!ServiceName().IsMatch(name)) throw new InvalidOperationException("That service cannot be changed.");
    }

    public void SetServiceStart(string name, int start, bool delayed)
    {
        Check(name);
        if (start is not (2 or 3 or 4)) throw new InvalidOperationException("That service cannot be changed.");
        var manager = OpenSCManager(null, null, 0x0001);
        if (manager == IntPtr.Zero) throw Failure();
        try
        {
            var service = OpenService(manager, name, 0x0001 | 0x0002);
            if (service == IntPtr.Zero) throw Failure();
            try
            {
                if (!ChangeServiceConfig(service, 0xFFFFFFFF, (uint)start, 0xFFFFFFFF, null, null, IntPtr.Zero, null, null, null, null)) throw Failure();
                var info = new DelayedAutoStart { Delayed = delayed && start == 2 };
                if (!ChangeServiceConfig2(service, 3, ref info)) throw Failure();
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }

    public bool StopService(string name)
    {
        Check(name);
        var manager = OpenSCManager(null, null, 0x0001);
        if (manager == IntPtr.Zero) throw Failure();
        try
        {
            var service = OpenService(manager, name, 0x0004 | 0x0020);
            if (service == IntPtr.Zero) throw Failure();
            try
            {
                if (!QueryServiceStatus(service, out var status)) throw Failure();
                if (status.CurrentState == 1) return true;
                if (!ControlService(service, 1, out status))
                {
                    var code = Marshal.GetLastWin32Error();
                    if (code == 1062) return true;
                    // 1051: other services that depend on it are running. They are left alone.
                    if (code == 1051) return false;
                    throw Failure();
                }
                var clock = Stopwatch.StartNew();
                while (status.CurrentState != 1 && clock.Elapsed < TimeSpan.FromSeconds(10))
                {
                    Thread.Sleep(250);
                    if (!QueryServiceStatus(service, out status)) break;
                }
                return status.CurrentState == 1;
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }

    public void StartService(string name)
    {
        Check(name);
        var manager = OpenSCManager(null, null, 0x0001);
        if (manager == IntPtr.Zero) throw Failure();
        try
        {
            var service = OpenService(manager, name, 0x0010);
            if (service == IntPtr.Zero) throw Failure();
            try
            {
                if (!StartServiceW(service, 0, IntPtr.Zero) && Marshal.GetLastWin32Error() != 1056) throw Failure();
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }

    public void SetTask(string path, bool enabled)
    {
        if (path.Length < 3 || path[0] != '\\' || path.EndsWith('\\') || path.IndexOfAny(['*', '?']) >= 0) throw new InvalidOperationException("That task cannot be changed.");
        try
        {
            var type = Type.GetTypeFromProgID("Schedule.Service") ?? throw new InvalidOperationException("Task Scheduler is not available.");
            dynamic service = Activator.CreateInstance(type)!;
            service.Connect();
            var split = path.LastIndexOf('\\');
            dynamic task = service.GetFolder(split == 0 ? "\\" : path[..split]).GetTask(path[(split + 1)..]);
            task.Enabled = enabled;
            if (!enabled && (int)task.State == 4) task.Stop(0);
        }
        catch (COMException ex) when ((uint)ex.HResult == 0x80070005) { throw new UnauthorizedAccessException(ex.Message, ex); }
        catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException ex) when (ex.Message.Contains("0x80070005", StringComparison.Ordinal) || ex.Message.Contains("denied", StringComparison.OrdinalIgnoreCase)) { throw new UnauthorizedAccessException(ex.Message, ex); }
    }

    public void SetPolicy(string key, string name, int? value)
    {
        if (!key.StartsWith(@"SOFTWARE\Policies\Microsoft\", StringComparison.OrdinalIgnoreCase) || key.Contains("..") || name.Length == 0 || name.IndexOfAny(['\\', '/']) >= 0) throw new InvalidOperationException("That setting cannot be changed.");
        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            if (value is null)
            {
                using var existing = root.OpenSubKey(key, writable: true);
                existing?.DeleteValue(name, throwOnMissingValue: false);
                return;
            }
            using var policy = root.CreateSubKey(key, writable: true);
            policy.SetValue(name, value.Value, RegistryValueKind.DWord);
        }
        catch (System.Security.SecurityException ex) { throw new UnauthorizedAccessException(ex.Message, ex); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DelayedAutoStart { [MarshalAs(UnmanagedType.Bool)] public bool Delayed; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus { public uint Type, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint; }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenSCManager(string? machine, string? database, uint access);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenService(IntPtr manager, string name, uint access);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeServiceConfig(IntPtr service, uint type, uint start, uint errorControl, string? path, string? group, IntPtr tag, string? dependencies, string? account, string? password, string? displayName);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeServiceConfig2(IntPtr service, uint level, ref DelayedAutoStart info);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatus(IntPtr service, out ServiceStatus status);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ControlService(IntPtr service, uint control, out ServiceStatus status);

    [DllImport("advapi32.dll", EntryPoint = "StartServiceW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartServiceW(IntPtr service, uint argc, IntPtr argv);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);
}

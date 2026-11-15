using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace TidyWindow.Next.Core;

/// <summary>How worn a battery is. Good is 80% of its original capacity or more, Fair 60 to 79%, Worn below that.</summary>
public enum BatteryGrade { Unknown, Good, Fair, Worn }

/// <summary>
/// What one battery says about itself. Capacities are in milliwatt-hours, unless <paramref name="Relative"/> is set: then the battery counts in its own
/// units, the ratio between the two is still right, and the numbers must not be shown as mWh.
/// </summary>
/// <param name="DesignCapacity">What the battery held when it was new. 0 when the battery does not say.</param>
/// <param name="FullChargeCapacity">What it holds now when charged full. 0 when the battery does not say.</param>
/// <param name="CycleCount">Full charge cycles it has been through. Null when the battery does not count them.</param>
public sealed record BatteryReading(string Name, string Manufacturer, string Chemistry, long DesignCapacity, long FullChargeCapacity, int? CycleCount, bool Relative)
{
    public const double GoodFrom = 80, FairFrom = 60;

    /// <summary>Full charge as a share of design capacity, capped at 100 (a new battery can report slightly more than it was built for). Null when it cannot be worked out.</summary>
    public double? HealthPercent => DesignCapacity > 0 && FullChargeCapacity > 0 ? Math.Min(100, (double)FullChargeCapacity / DesignCapacity * 100) : null;

    public BatteryGrade Grade => GradeFor(HealthPercent);

    /// <summary>What has been lost to wear, as a share of the original capacity. Null when the health cannot be worked out.</summary>
    public double? WearPercent => HealthPercent is { } health ? 100 - health : null;

    /// <summary>Capacity lost to wear, in the same units as the capacities. Never negative.</summary>
    public long? WornCapacity => HealthPercent is null ? null : Math.Max(0, DesignCapacity - FullChargeCapacity);

    public static BatteryGrade GradeFor(double? percent) => percent switch
    {
        null => BatteryGrade.Unknown,
        >= GoodFrom => BatteryGrade.Good,
        >= FairFrom => BatteryGrade.Fair,
        _ => BatteryGrade.Worn
    };

    /// <summary>The same as one battery: all of them added together, for a PC with more than one. Null when there are none or none can be compared.</summary>
    public static double? CombinedPercent(IReadOnlyList<BatteryReading> readings)
    {
        var usable = readings.Where(x => x.HealthPercent is not null && !x.Relative).ToList();
        // Batteries counting in their own units cannot be added to others, so they are only averaged when nothing else is there.
        if (usable.Count == 0) usable = readings.Where(x => x.HealthPercent is not null).ToList();
        if (usable.Count == 0) return null;
        var design = usable.Sum(x => (double)x.DesignCapacity);
        var full = usable.Sum(x => (double)x.FullChargeCapacity);
        return design > 0 ? Math.Min(100, full / design * 100) : null;
    }
}

/// <summary>Reads the batteries in this PC. Reading never changes anything.</summary>
public interface IBatteryReader
{
    /// <summary>One reading for each battery that answers. Empty when the PC has none. Throws when there are batteries but none could be read.</summary>
    IReadOnlyList<BatteryReading> Read();
}

/// <summary>
/// Asks each battery's driver for its design and full-charge capacity, the same numbers <c>powercfg /batteryreport</c> prints. It starts no program,
/// writes no file and needs no administrator permission.
/// </summary>
public sealed class WindowsBatteryReader : IBatteryReader
{
    // GUID_DEVCLASS_BATTERY, which is also the interface the battery drivers register.
    private static readonly Guid BatteryInterface = new(0x72631e54, 0x78a4, 0x11d0, 0xbc, 0xf7, 0x00, 0xaa, 0x00, 0xb7, 0xb3, 0x2a);

    private const uint DigcfPresent = 0x2, DigcfDeviceInterface = 0x10;
    private const uint GenericRead = 0x80000000, GenericWrite = 0x40000000, ShareRead = 1, ShareWrite = 2, OpenExisting = 3;
    private const uint IoctlQueryTag = 0x294040, IoctlQueryInformation = 0x294044;
    private const int LevelInformation = 0, LevelDeviceName = 4, LevelManufactureName = 6;
    private const uint CapacityRelative = 0x40000000, Unknown = 0xFFFFFFFF;
    private static readonly IntPtr InvalidHandle = new(-1);
    private const int ErrorNoMoreItems = 259, ErrorInsufficientBuffer = 122;

    [StructLayout(LayoutKind.Sequential)]
    private struct InterfaceData { public int Size; public Guid Class; public int Flags; public IntPtr Reserved; }

    [StructLayout(LayoutKind.Sequential)]
    private struct QueryInformation { public uint Tag; public int Level; public uint AtRate; }

    // BATTERY_INFORMATION: Technology and Reserved are one byte each, and Chemistry is four characters, so the three fields after Capabilities are read as bytes.
    [StructLayout(LayoutKind.Sequential)]
    private struct BatteryInformation
    {
        public uint Capabilities;
        public byte Technology, Reserved0, Reserved1, Reserved2;
        public byte Chemistry0, Chemistry1, Chemistry2, Chemistry3;
        public uint DesignedCapacity, FullChargedCapacity, DefaultAlert1, DefaultAlert2, CriticalBias, CycleCount;
    }

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, IntPtr enumerator, IntPtr parent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr info, ref Guid classGuid, uint index, ref InterfaceData data);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr set, ref InterfaceData data, IntPtr detail, int size, out int required, IntPtr info);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, ref uint input, int inputSize, out uint output, int outputSize, out int returned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, ref QueryInformation input, int inputSize, out BatteryInformation output, int outputSize, out int returned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, ref QueryInformation input, int inputSize, byte[] output, int outputSize, out int returned, IntPtr overlapped);

    public IReadOnlyList<BatteryReading> Read()
    {
        if (!OperatingSystem.IsWindows()) return [];
        var paths = Paths();
        var readings = new List<BatteryReading>();
        Exception? lastFailure = null;
        foreach (var path in paths)
        {
            try { if (ReadOne(path) is { } reading) readings.Add(reading); }
            // One battery that will not answer must not hide the others.
            catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException) { lastFailure = ex; }
        }
        if (readings.Count == 0 && lastFailure is not null) throw new InvalidOperationException("Windows would not let TidyWindow read the battery. " + lastFailure.Message, lastFailure);
        return readings;
    }

    /// <summary>The device path of each battery Windows has right now. A desktop has none.</summary>
    private static List<string> Paths()
    {
        var paths = new List<string>();
        var guid = BatteryInterface;
        var set = SetupDiGetClassDevsW(ref guid, IntPtr.Zero, IntPtr.Zero, DigcfPresent | DigcfDeviceInterface);
        if (set == InvalidHandle || set == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            for (uint index = 0; ; index++)
            {
                var data = new InterfaceData { Size = Marshal.SizeOf<InterfaceData>() };
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, index, ref data))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == ErrorNoMoreItems) break;
                    throw new Win32Exception(error);
                }
                // The first call only says how much room the path needs.
                SetupDiGetDeviceInterfaceDetailW(set, ref data, IntPtr.Zero, 0, out var needed, IntPtr.Zero);
                if (needed <= 4 || Marshal.GetLastWin32Error() != ErrorInsufficientBuffer) continue;
                var buffer = Marshal.AllocHGlobal(needed);
                try
                {
                    // The size field is the size of the fixed part: a 4-byte count and one character, padded to 8 in a 64-bit process.
                    Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetailW(set, ref data, buffer, needed, out _, IntPtr.Zero)) continue;
                    if (Marshal.PtrToStringUni(buffer + 4) is { Length: > 0 } path) paths.Add(path);
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return paths;
    }

    private static BatteryReading? ReadOne(string path)
    {
        // The questions below only read, so read access is asked for first. Some drivers want the write right as well, even to be read.
        var device = CreateFileW(path, GenericRead, ShareRead | ShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (device.IsInvalid)
        {
            device.Dispose();
            device = CreateFileW(path, GenericRead | GenericWrite, ShareRead | ShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (device.IsInvalid) { var error = Marshal.GetLastWin32Error(); device.Dispose(); throw new Win32Exception(error); }
        }
        using (device) return ReadFrom(device);
    }

    private static BatteryReading? ReadFrom(SafeFileHandle device)
    {

        uint wait = 0;
        // A battery slot with no battery in it has no tag; that is not a failure.
        if (!DeviceIoControl(device, IoctlQueryTag, ref wait, sizeof(uint), out var tag, sizeof(uint), out _, IntPtr.Zero) || tag == 0) return null;

        var query = new QueryInformation { Tag = tag, Level = LevelInformation };
        if (!DeviceIoControl(device, IoctlQueryInformation, ref query, Marshal.SizeOf<QueryInformation>(), out var info, Marshal.SizeOf<BatteryInformation>(), out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error());

        var chemistry = new string(new[] { (char)info.Chemistry0, (char)info.Chemistry1, (char)info.Chemistry2, (char)info.Chemistry3 }).Trim('\0', ' ');
        return new BatteryReading(
            Text(device, tag, LevelDeviceName), Text(device, tag, LevelManufactureName), ChemistryName(chemistry),
            info.DesignedCapacity == Unknown ? 0 : info.DesignedCapacity,
            info.FullChargedCapacity == Unknown ? 0 : info.FullChargedCapacity,
            info.CycleCount is 0 or Unknown ? null : (int)Math.Min(info.CycleCount, int.MaxValue),
            (info.Capabilities & CapacityRelative) != 0);
    }

    private static string Text(SafeFileHandle device, uint tag, int level)
    {
        var query = new QueryInformation { Tag = tag, Level = level };
        var buffer = new byte[256];
        // Not every battery has a name or a maker, and a refusal here is only a missing detail.
        if (!DeviceIoControl(device, IoctlQueryInformation, ref query, Marshal.SizeOf<QueryInformation>(), buffer, buffer.Length, out var returned, IntPtr.Zero) || returned < 2) return "";
        return System.Text.Encoding.Unicode.GetString(buffer, 0, returned).TrimEnd('\0').Trim();
    }

    /// <summary>The four-letter code a battery reports, in words.</summary>
    internal static string ChemistryName(string code) => code.ToUpperInvariant() switch
    {
        "LION" or "LI-I" => "Lithium-ion",
        "LIP" or "LIPO" or "LI-P" => "Lithium polymer",
        "PBAC" => "Lead acid",
        "NICD" => "Nickel-cadmium",
        "NIMH" => "Nickel-metal hydride",
        "NIZN" => "Nickel-zinc",
        "RAM" => "Rechargeable alkaline",
        "" => "",
        _ => code
    };
}

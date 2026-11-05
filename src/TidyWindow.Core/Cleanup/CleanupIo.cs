using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace TidyWindow.Next.Core;

internal enum DeleteResult { Removed, Gone, InUse, Denied, Failed }

/// <summary>What kind of disk a folder lives on, which decides how many deleters it can feed at once.</summary>
internal enum StorageKind { Solid, Unknown, Rotational }

/// <summary>
/// The low-level pieces of a gentle clean. Deleting millions of small files is limited by NTFS metadata writes, not by how many threads ask, so the aim is
/// the opposite of "as parallel as possible": a few workers matched to the disk, running in Windows' background mode (low CPU, I/O and memory priority) so the
/// rest of the PC stays usable, and calls that report failure as a number instead of an exception (an exception costs tens of microseconds, and a cache
/// full of files that are open produces thousands).
/// </summary>
internal static class CleanupIo
{
    private const int ErrorFileNotFound = 2, ErrorPathNotFound = 3, ErrorAccessDenied = 5, ErrorSharing = 32, ErrorLock = 33, ErrorMapped = 1224;
    private const uint InvalidAttributes = 0xFFFFFFFF, ReadOnly = 0x1;
    private const uint ThreadModeBackgroundBegin = 0x00010000, ThreadModeBackgroundEnd = 0x00020000;
    private const uint IoctlStorageQueryProperty = 0x002D1400;
    private const int StorageDeviceSeekPenaltyProperty = 7;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteFileW(string path);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveDirectoryW(string path);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFileAttributesW(string path);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileAttributesW(string path, uint attributes);
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetThreadPriority(IntPtr thread, uint priority);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[] input, int inputSize, byte[] output, int outputSize, out int returned, IntPtr overlapped);

    /// <summary>Paths near the old 260 limit are given the extended prefix so a deeply nested cache folder can still be deleted.</summary>
    internal static string Extended(string path)
    {
        if (path.Length < 240 || path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path;
        return path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;
    }

    /// <summary>
    /// Deletes one file. A read-only file has the flag lifted (and put back if the delete still fails); a file another program has open is reported as in use,
    /// and anything else that goes wrong is a failure rather than being dressed up as "in use".
    /// </summary>
    public static DeleteResult DeleteFile(string path)
    {
        if (!OperatingSystem.IsWindows()) return DeleteManaged(path);
        var native = Extended(path);
        if (DeleteFileW(native)) return DeleteResult.Removed;
        var error = Marshal.GetLastWin32Error();
        if (error == ErrorAccessDenied)
        {
            var attributes = GetFileAttributesW(native);
            if (attributes != InvalidAttributes && (attributes & ReadOnly) != 0 && SetFileAttributesW(native, attributes & ~ReadOnly))
            {
                if (DeleteFileW(native)) return DeleteResult.Removed;
                error = Marshal.GetLastWin32Error();
                SetFileAttributesW(native, attributes);
            }
        }
        return Classify(error);
    }

    private static DeleteResult Classify(int error) => error switch
    {
        ErrorFileNotFound or ErrorPathNotFound => DeleteResult.Gone,
        ErrorAccessDenied => DeleteResult.Denied,
        ErrorSharing or ErrorLock or ErrorMapped => DeleteResult.InUse,
        _ => DeleteResult.Failed
    };

    private static DeleteResult DeleteManaged(string path)
    {
        try { File.Delete(path); return DeleteResult.Removed; }
        catch (UnauthorizedAccessException) { return DeleteResult.Denied; }
        catch (IOException) { return DeleteResult.Failed; }
    }

    /// <summary>Removes a folder only if it is empty. A folder that still holds something is the normal case, and costs no exception.</summary>
    public static bool TryRemoveDirectory(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            try { Directory.Delete(path, recursive: false); return true; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        }
        return RemoveDirectoryW(Extended(path));
    }

    /// <summary>
    /// Puts the calling thread into Windows' background mode until disposed: lower CPU priority, very low I/O priority and low memory priority. It must be
    /// begun and ended on the same thread, so it is only ever held across synchronous work.
    /// </summary>
    public readonly struct Background : IDisposable
    {
        private readonly bool active;
        private Background(bool active) => this.active = active;
        public static Background Begin(bool enabled) => new(enabled && OperatingSystem.IsWindows() && SetThreadPriority(GetCurrentThread(), ThreadModeBackgroundBegin));
        public void Dispose() { if (active) SetThreadPriority(GetCurrentThread(), ThreadModeBackgroundEnd); }
    }

    private static readonly ConcurrentDictionary<char, StorageKind> Kinds = new();

    /// <summary>Whether the drive holding <paramref name="path"/> is a spinning disk (moving the heads is the cost), a solid state drive, or cannot be told (network, virtual).</summary>
    public static StorageKind KindOf(string path)
    {
        if (!OperatingSystem.IsWindows() || path.Length < 2 || path[1] != ':' || !char.IsAsciiLetter(path[0])) return StorageKind.Unknown;
        return Kinds.GetOrAdd(char.ToUpperInvariant(path[0]), Probe);
    }

    private static StorageKind Probe(char drive)
    {
        try
        {
            // Asking about a volume needs no access to its contents, so this works without administrator rights.
            using var volume = CreateFileW($@"\\.\{drive}:", 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (volume.IsInvalid) return StorageKind.Unknown;
            var query = new byte[12];
            BitConverter.TryWriteBytes(query.AsSpan(0, 4), StorageDeviceSeekPenaltyProperty);
            var answer = new byte[12];
            if (!DeviceIoControl(volume, IoctlStorageQueryProperty, query, query.Length, answer, answer.Length, out var returned, IntPtr.Zero) || returned < 9) return StorageKind.Unknown;
            return answer[8] != 0 ? StorageKind.Rotational : StorageKind.Solid;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return StorageKind.Unknown; }
    }
}

/// <summary>
/// How hard a scan or clean may push the machine. Decided once per run from the disks it touches, the cores and the memory. A low-end machine (a spinning or
/// unknown disk, four cores or fewer, or 8 GB of memory or less) gets two workers in Windows' background mode, which keeps it usable at some cost in speed.
/// A capable machine is not slowed down for nothing: it gets more workers at normal priority.
/// </summary>
internal readonly record struct CleanupLoad(int Workers, bool Background)
{
    private const long LowMemory = 8L * 1024 * 1024 * 1024;

    /// <summary>Whether this machine, with files on a disk of the given kind, should be treated gently.</summary>
    public static bool IsLowEnd(StorageKind kind) =>
        kind != StorageKind.Solid || Environment.ProcessorCount <= 4 || GC.GetGCMemoryInfo().TotalAvailableMemoryBytes <= LowMemory;

    public static CleanupLoad ForClean(CleanupContext context, IEnumerable<string> paths)
    {
        if (!context.LowImpact) return new(context.Parallelism, false);
        var lowEnd = IsLowEnd(Worst(paths));
        var workers = lowEnd ? 2 : Math.Clamp(Environment.ProcessorCount, 3, 8);
        return new(Math.Max(1, Math.Min(context.Parallelism, workers)), lowEnd);
    }

    /// <summary>Reading directories tolerates more parallelism on an SSD, but on a spinning disk it only adds seeking.</summary>
    public static CleanupLoad ForScan(CleanupContext context, IEnumerable<string> paths)
    {
        if (!context.LowImpact) return new(context.Parallelism, false);
        var lowEnd = IsLowEnd(Worst(paths));
        return new(Math.Max(1, lowEnd ? Math.Min(context.Parallelism, 3) : context.Parallelism), lowEnd);
    }

    private static StorageKind Worst(IEnumerable<string> paths)
    {
        var worst = StorageKind.Solid;
        foreach (var drive in paths.Select(x => x.Length >= 2 && x[1] == ':' ? x[..2] : x).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var kind = CleanupIo.KindOf(drive);
            if (kind > worst) worst = kind;
        }
        return worst;
    }
}

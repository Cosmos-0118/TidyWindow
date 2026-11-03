using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;

namespace TidyWindow.Next.Core;

/// <summary>
/// Whether an installer file (.msi) or an update file (.msp) that Windows Installer saved is usable. The question is only ever "is this certainly broken":
/// a file that cannot be read right now (in use, no permission) is reported as fine, because calling a healthy program broken is worse than missing a broken one.
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class PackageHealth
{
    // Every Windows Installer file is an OLE compound file, which starts with these eight bytes.
    private static readonly byte[] CompoundHeader = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    // ERROR_OPEN_FAILED, ERROR_INSTALL_PACKAGE_OPEN_FAILED, ERROR_INSTALL_PACKAGE_INVALID: Windows Installer's own words for "this is not an installer file".
    private static bool NotAPackage(uint error) => error is 110 or 1619 or 1620;

    /// <summary>An .msi that is empty, cut short, not an installer file, or the installer of another product than <paramref name="productCode"/>.</summary>
    public static bool InstallerDamaged(string path, string productCode)
    {
        if (BadShape(path)) return true;
        var opened = MsiOpenDatabase(path, IntPtr.Zero, out var database);
        if (opened != 0) return NotAPackage(opened);
        try
        {
            var code = Property(database, "ProductCode");
            return code is null || !string.Equals(code, productCode, StringComparison.OrdinalIgnoreCase);
        }
        finally { MsiCloseHandle(database); }
    }

    /// <summary>An .msp that is empty, cut short, not an installer file, or (when a code is given) a different update than the one Windows Installer registered.</summary>
    public static bool PatchDamaged(string path, string? patchCode = null)
    {
        if (BadShape(path)) return true;
        var found = ReadPatchCode(path, out var error);
        if (found is null) return NotAPackage(error);
        return patchCode is not null && !string.Equals(found, patchCode, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The update code ({GUID}) an .msp file carries in its summary information, or null when it is not a readable update.</summary>
    public static string? ReadPatchCode(string path) => ReadPatchCode(path, out _);

    private static string? ReadPatchCode(string path, out uint error)
    {
        // Passing the file's path instead of an open database reads only the summary stream, which is all an update's code needs.
        error = MsiGetSummaryInformation(IntPtr.Zero, path, 0, out var summary);
        if (error != 0) return null;
        try
        {
            var text = new StringBuilder(128); uint length = 128; uint type = 0; int number = 0; long time = 0;
            error = MsiSummaryInfoGetProperty(summary, 9, ref type, ref number, ref time, text, ref length);
            if (error != 0) return null;
            return Guid().Match(text.ToString()) is { Success: true } match ? match.Value.ToUpperInvariant() : null;
        }
        finally { MsiCloseHandle(summary); }
    }

    /// <summary>Empty, too small to be a compound file, or not starting like one. An unreadable file is not "bad shape".</summary>
    private static bool BadShape(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length < 512) return true;
            var head = new byte[CompoundHeader.Length];
            return stream.Read(head, 0, head.Length) < head.Length || !head.AsSpan().SequenceEqual(CompoundHeader);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
    }

    private static string? Property(IntPtr database, string name)
    {
        if (MsiDatabaseOpenView(database, $"SELECT `Value` FROM `Property` WHERE `Property`='{name}'", out var view) != 0) return null;
        try
        {
            if (MsiViewExecute(view, IntPtr.Zero) != 0 || MsiViewFetch(view, out var record) != 0) return null;
            try
            {
                var value = new StringBuilder(256); uint length = 256;
                return MsiRecordGetString(record, 1, value, ref length) == 0 ? value.ToString() : null;
            }
            finally { MsiCloseHandle(record); }
        }
        finally { MsiCloseHandle(view); }
    }

    [GeneratedRegex(@"\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}")]
    private static partial Regex Guid();

    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern uint MsiOpenDatabase(string path, IntPtr persist, out IntPtr database);

    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern uint MsiDatabaseOpenView(IntPtr database, string query, out IntPtr view);

    [DllImport("msi.dll")]
    private static extern uint MsiViewExecute(IntPtr view, IntPtr record);

    [DllImport("msi.dll")]
    private static extern uint MsiViewFetch(IntPtr view, out IntPtr record);

    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern uint MsiRecordGetString(IntPtr record, uint field, StringBuilder value, ref uint length);

    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern uint MsiGetSummaryInformation(IntPtr database, string? path, uint updates, out IntPtr summary);

    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern uint MsiSummaryInfoGetProperty(IntPtr summary, uint property, ref uint type, ref int number, ref long time, StringBuilder text, ref uint length);

    [DllImport("msi.dll")]
    private static extern uint MsiCloseHandle(IntPtr handle);
}

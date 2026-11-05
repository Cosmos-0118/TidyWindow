using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace TidyWindow.Next.Core;

/// <summary>Windows Update could not be asked, or refused. The message says why in words a person can use.</summary>
public sealed class DriverUpdateException(string message) : IOException(message);

/// <summary>What the driver pages need from Windows Update, so the rules around it can be tested without a network.</summary>
public interface IDriverUpdates
{
    /// <summary>Asks Windows Update for drivers that are not installed. Can take minutes.</summary>
    /// <exception cref="DriverUpdateException">Windows Update could not be reached or would not answer.</exception>
    IReadOnlyList<DriverOffer> Search();

    /// <summary>
    /// Downloads and installs one driver from the last <see cref="Search"/>. An id that search did not return is refused, so the helper can only ever install
    /// something it just saw Windows Update offer.
    /// </summary>
    DriverInstallOutcome Install(string updateId, Action<string> say);
}

/// <param name="Ok">Windows Update reported success, with or without warnings.</param>
public sealed record DriverInstallOutcome(bool Ok, bool RebootRequired, string? Error);

/// <summary>The drivers Windows has installed, read the way Windows keeps them.</summary>
public interface IInstalledDrivers
{
    IReadOnlyList<InstalledDriver> Read();
}

/// <summary>Reads the drivers Windows has installed from its own device records. A standard account may read them; nothing is written.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsInstalledDrivers : IInstalledDrivers
{
    private const string Enum = @"SYSTEM\CurrentControlSet\Enum";
    private const string Classes = @"SYSTEM\CurrentControlSet\Control\Class";

    public IReadOnlyList<InstalledDriver> Read()
    {
        var list = new List<InstalledDriver>();
        // Most devices do not carry their class name; it is on the class itself, once for all the devices of that class.
        var classNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var enumKey = Registry.LocalMachine.OpenSubKey(Enum);
        using var classKey = Registry.LocalMachine.OpenSubKey(Classes);
        if (enumKey is null || classKey is null) return list;
        foreach (var bus in Names(enumKey))
        {
            using var busKey = Open(enumKey, bus);
            if (busKey is null) continue;
            foreach (var device in Names(busKey))
            {
                using var deviceKey = Open(busKey, device);
                if (deviceKey is null) continue;
                foreach (var instance in Names(deviceKey))
                {
                    using var key = Open(deviceKey, instance);
                    if (key?.GetValue("Driver") is not string driver || driver.Length == 0) continue;
                    using var driverKey = Open(classKey, driver.Replace('/', '\\'));
                    if (driverKey is null) continue;
                    var version = driverKey.GetValue("DriverVersion") as string ?? "";
                    var infName = driverKey.GetValue("InfPath") as string ?? "";
                    if (version.Length == 0 && infName.Length == 0) continue;
                    var ids = Strings(key.GetValue("HardwareID")).Concat(Strings(key.GetValue("CompatibleIDs"))).Where(x => x.Length > 0).ToList();
                    var guid = driver.Replace('/', '\\').Split('\\')[0];
                    if (!classNames.TryGetValue(guid, out var className))
                    {
                        using var classOfDriver = Open(classKey, guid);
                        classNames[guid] = className = classOfDriver?.GetValue("Class") as string ?? "";
                    }
                    list.Add(new($@"{bus}\{device}\{instance}", Name(key, driverKey), key.GetValue("Class") as string is { Length: > 0 } own ? own : className, version, ParseDate(driverKey.GetValue("DriverDate") as string), Text(driverKey.GetValue("ProviderName") as string), infName, ids));
                }
            }
        }
        return list;
    }

    private static string[] Names(RegistryKey key)
    {
        try { return key.GetSubKeyNames(); }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return []; }
    }

    private static RegistryKey? Open(RegistryKey parent, string name)
    {
        try { return parent.OpenSubKey(name); }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }

    private static string[] Strings(object? value) => value switch { string[] many => many, string one => [one], _ => [] };

    private static string Name(RegistryKey device, RegistryKey driver)
    {
        foreach (var raw in new[] { device.GetValue("FriendlyName") as string, driver.GetValue("DriverDesc") as string, device.GetValue("DeviceDesc") as string })
            if (Text(raw) is { Length: > 0 } text) return text;
        return "Unnamed device";
    }

    /// <summary>Windows stores some names as "@oem3.inf,%key%;The real name"; the real name is after the last semicolon.</summary>
    internal static string Text(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var text = raw.Trim();
        if (text.StartsWith('@')) text = text[(text.LastIndexOf(';') + 1)..];
        return text.Trim();
    }

    /// <summary>A driver's date as an INF writes it: month-day-year.</summary>
    internal static DateOnly? ParseDate(string? text) =>
        DateOnly.TryParseExact(text?.Trim(), ["M-d-yyyy", "M/d/yyyy", "MM-dd-yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;
}

/// <summary>Reads the machine-wide Windows Update policies that decide whether drivers are offered.</summary>
[SupportedOSPlatform("windows")]
public static class WindowsDriverPolicy
{
    public static IReadOnlyList<string> Notices() => DriverPolicy.Notices(Dword, Text);

    private static int? Dword(string path, string name)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(path);
            return key?.GetValue(name) is int value ? value : null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }

    private static string? Text(string path, string name)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(path);
            return key?.GetValue(name) as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }

    /// <summary>The PC's maker, for choosing which maker tool to point to.</summary>
    public static string ComputerMaker()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
            return key?.GetValue("SystemManufacturer") as string ?? "";
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return ""; }
    }
}

/// <summary>
/// Windows Update through its own COM object (the Windows Update Agent), created by name at run time. There is no reference to it in the project: a
/// compile-time reference would need a type library that a plain <c>dotnet build</c> on CI does not have.
/// </summary>
/// <remarks>
/// The agent's asynchronous calls need a COM callback object, which cannot be given from here, so a search or an install is one call that waits. The app
/// therefore runs a search in a program it can end, and the helper runs an install as steps it only lets itself be stopped between.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsUpdateDrivers : IDriverUpdates
{
    private const string Criteria = "IsInstalled=0 and Type='Driver'";
    private dynamic? session;
    private readonly Dictionary<string, object> found = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<DriverOffer> Search()
    {
        found.Clear();
        try
        {
            var type = Type.GetTypeFromProgID("Microsoft.Update.Session") ?? throw new DriverUpdateException("Windows Update is not available on this PC.");
            session = Activator.CreateInstance(type) ?? throw new DriverUpdateException("Windows Update is not available on this PC.");
            dynamic searcher = session.CreateUpdateSearcher();
            dynamic result = searcher.Search(Criteria);
            if ((int)result.ResultCode is not (2 or 3)) throw new DriverUpdateException($"Windows Update did not finish looking for drivers (result {(int)result.ResultCode}).");
            var offers = new List<DriverOffer>();
            foreach (dynamic update in result.Updates)
            {
                var offer = Describe(update);
                if (!DriverGuard.IsUpdateId(offer.UpdateId)) continue;
                found[offer.UpdateId] = (object)update;
                offers.Add(offer);
            }
            return offers;
        }
        catch (COMException ex) { throw new DriverUpdateException(Explain(ex.HResult, "Windows Update could not look for drivers")); }
    }

    private static DriverOffer Describe(dynamic update)
    {
        DateOnly? date = null;
        try { date = DateOnly.FromDateTime((DateTime)update.DriverVerDate); } catch (Exception ex) when (ex is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or ArgumentOutOfRangeException) { }
        return new(Get(() => (string)update.Identity.UpdateID, ""), Get(() => (int)update.Identity.RevisionNumber, 0), Get(() => (string)update.Title, ""), Get(() => (string)update.DriverClass, ""),
            Get(() => (string)update.DriverManufacturer, ""), Get(() => (string)update.DriverProvider, ""), Get(() => (string)update.DriverModel, ""), Get(() => (string)update.DriverHardwareID, ""),
            date, Get(() => Convert.ToInt64((object)update.MaxDownloadSize, CultureInfo.InvariantCulture), 0L), Get(() => (int)update.DeviceProblemNumber, 0),
            Get(() => (bool)update.RebootRequired, false), Get(() => (bool)update.EulaAccepted, true));
    }

    private static T Get<T>(Func<T> read, T fallback)
    {
        try { return read() ?? fallback; }
        catch (Exception ex) when (ex is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or OverflowException) { return fallback; }
    }

    public DriverInstallOutcome Install(string updateId, Action<string> say)
    {
        if (session is null || !found.TryGetValue(updateId, out var update)) throw new InvalidOperationException("That driver was not in what Windows Update just offered.");
        try
        {
            dynamic collection = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.UpdateColl")!)!;
            collection.Add(update);
            say("Downloading the driver from Windows Update…");
            dynamic downloader = session.CreateUpdateDownloader();
            downloader.Updates = collection;
            dynamic downloaded = downloader.Download();
            if ((int)downloaded.ResultCode is not (2 or 3)) return new(false, false, Explain((int)downloaded.HResult, "Windows Update could not download the driver"));
            say("Installing the driver…");
            dynamic installer = session.CreateUpdateInstaller();
            installer.Updates = collection;
            installer.AllowSourcePrompts = false;
            installer.ForceQuiet = true;
            dynamic installed = installer.Install();
            var code = (int)installed.ResultCode;
            var restart = (bool)installed.RebootRequired;
            if (code is 2 or 3) return new(true, restart, null);
            return new(false, restart, Explain((int)installed.HResult, "Windows Update could not install the driver"));
        }
        catch (COMException ex) { return new(false, false, Explain(ex.HResult, "Windows Update could not install the driver")); }
    }

    /// <summary>A Windows Update error in words. The code is kept at the end, because it is what a search for help needs.</summary>
    internal static string Explain(int hresult, string what)
    {
        var why = unchecked((uint)hresult) switch
        {
            0x80240044 => "Windows Update needs administrator rights for this.",
            0x80070005 => "Access was denied.",
            0x80072EE2 or 0x80072EE7 or 0x80072EFD or 0x80072EFE or 0x8024402C => "Windows Update could not be reached. Check the internet connection.",
            0x8024001E => "Windows Update was shutting down.",
            0x80240016 => "Another install is in progress, or Windows is waiting for a restart. Try again after it finishes or after restarting.",
            _ => ""
        };
        return $"{what}. {(why.Length > 0 ? why + " " : "")}(0x{unchecked((uint)hresult):X8})";
    }
}

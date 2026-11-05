using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TidyWindow.Next.Core;

/// <summary>
/// One driver Windows Update offers for this PC. It is Microsoft's own list (the same one as Settings, Windows Update, Optional updates), so nothing here
/// is a guess made by TidyWindow. <paramref name="HardwareId"/> is the hardware id the offer was matched on; <paramref name="Date"/> is the driver's own date.
/// </summary>
public sealed record DriverOffer(string UpdateId, int Revision, string Title, string DeviceClass, string Manufacturer, string Provider, string Model, string HardwareId, DateOnly? Date, long SizeBytes, int ProblemCode, bool RebootRequired, bool EulaAccepted);

/// <summary>A driver as Windows has it installed for one device. <paramref name="DeviceKey"/> names the device; <paramref name="HardwareIds"/> are its hardware and compatible ids.</summary>
public sealed record InstalledDriver(string DeviceKey, string DeviceName, string DeviceClass, string Version, DateOnly? Date, string Provider, string InfName, IReadOnlyList<string> HardwareIds);

public enum DriverVerdict
{
    /// <summary>No driver is recorded for the device, or the offer is for a device that has none yet.</summary>
    New,
    Newer,
    Same,
    /// <summary>Older than what is installed. Windows Update can offer these; TidyWindow does not install them.</summary>
    Older,
    /// <summary>Neither a version nor a date could be compared.</summary>
    Unknown
}

/// <summary>An offer set beside what is installed for the same hardware.</summary>
public sealed record DriverFinding(DriverOffer Offer, InstalledDriver? Installed, DriverVerdict Verdict, string Reason)
{
    /// <summary>Everything but a downgrade can be installed. A downgrade is refused here and again by the helper.</summary>
    public bool CanInstall => Verdict != DriverVerdict.Older;
}

/// <summary>What a scan found: the offers beside what is installed, why Windows may be offering less than it could, and any error.</summary>
public sealed record DriverScan(IReadOnlyList<DriverFinding> Findings, IReadOnlyList<string> Notices, string? Error, IReadOnlyList<InstalledDriver> Installed);

/// <summary>How an install or a restore ended. <paramref name="BackupId"/> names the saved copy of the driver that was replaced, when one could be made.</summary>
public sealed record DriverResult(bool Ok, string? Error = null, string? Note = null, bool RebootRequired = false, string? BackupId = null, string? FromVersion = null, string? ToVersion = null, bool Verified = true);

/// <summary>A driver TidyWindow installed, kept on this account's side for display. The helper trusts its own copy in the protected folder, never this one.</summary>
public sealed record DriverChange(string UpdateId, string Title, string DeviceName, string? FromVersion, string? ToVersion, DateTimeOffset When, string? BackupId, bool Restored = false);

/// <summary>The copy of a replaced driver the helper keeps, in a folder only administrators can change.</summary>
public sealed record DriverBackupRecord(string Id, string DeviceKey, string DeviceName, string InfName, string Version, DateOnly? Date, string Provider, string? NewInfName, string? NewVersion, DateTimeOffset When);

public static partial class DriverGuard
{
    [GeneratedRegex(@"^oem\d{1,6}\.inf$", RegexOptions.IgnoreCase)]
    private static partial Regex OemInf();

    /// <summary>A Windows Update id. Anything else never reaches the helper's search.</summary>
    public static bool IsUpdateId(string? value) => value is { Length: 36 } && Guid.TryParseExact(value, "D", out _);

    /// <summary>A saved-driver id: a GUID without dashes.</summary>
    public static bool IsBackupId(string? value) => value is { Length: 32 } && Guid.TryParseExact(value, "N", out _);

    /// <summary>
    /// A driver Windows copied into its store from a vendor package (oem12.inf). Drivers that come with Windows (netrasa.inf) cannot be exported or removed,
    /// and Windows keeps them anyway, so they are never named to a program.
    /// </summary>
    public static bool IsOemInf(string? value) => value is not null && OemInf().IsMatch(value);
}

public static partial class DriverCompare
{
    // A version at the end of an update's title: "Realtek Semiconductor Corp. - MEDIA - 6.0.9700.1" or "...Driver Update (1.0.4.55794)". At least three parts, so "Intel(R) 10" is not one.
    [GeneratedRegex(@"(?<![\w.])(\d+(?:\.\d+){2,3})(?![\w.])")]
    private static partial Regex Dotted();

    /// <summary>The last version-looking part of <paramref name="text"/>, or null.</summary>
    public static Version? VersionIn(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var last = Dotted().Matches(text).LastOrDefault();
        return last is not null && Version.TryParse(last.Value, out var version) ? version : null;
    }

    /// <summary>
    /// The installed driver for the hardware an offer is for: any device that lists the offer's hardware id, or a more specific one that starts with it.
    /// With several (two identical network cards), the newest installed one counts, so an offer is never judged only against the oldest.
    /// </summary>
    public static InstalledDriver? Match(DriverOffer offer, IEnumerable<InstalledDriver> installed)
    {
        if (offer.HardwareId.Length == 0) return null;
        bool Covers(string id) => id.Equals(offer.HardwareId, StringComparison.OrdinalIgnoreCase) || id.StartsWith(offer.HardwareId + "&", StringComparison.OrdinalIgnoreCase);
        return installed.Where(x => x.HardwareIds.Any(Covers))
            .OrderByDescending(x => VersionIn(x.Version) ?? new Version(0, 0))
            .ThenByDescending(x => x.Date ?? DateOnly.MinValue)
            .FirstOrDefault();
    }

    public static DriverFinding Assess(DriverOffer offer, IReadOnlyList<InstalledDriver> installed)
    {
        var current = Match(offer, installed);
        if (current is null) return new(offer, null, DriverVerdict.New, "Windows has no driver recorded for this hardware, or it is a software component that is not installed yet.");
        var offered = VersionIn(offer.Title);
        var have = VersionIn(current.Version);
        var shown = DescribeInstalled(current);
        if (offered is not null && have is not null && offered != have)
            return offered > have
                ? new(offer, current, DriverVerdict.Newer, $"Newer than the installed driver ({shown}).")
                : new(offer, current, DriverVerdict.Older, $"Older than the installed driver ({shown}). Windows Update sometimes offers an older driver than the one you have, so TidyWindow will not install it.");
        if (offer.Date is { } date && current.Date is { } installedDate)
        {
            if (date > installedDate) return new(offer, current, DriverVerdict.Newer, $"Dated later than the installed driver ({shown}).");
            if (date < installedDate) return new(offer, current, DriverVerdict.Older, $"Dated earlier than the installed driver ({shown}). Windows Update sometimes offers an older driver than the one you have, so TidyWindow will not install it.");
            return new(offer, current, DriverVerdict.Same, $"Same version and date as the installed driver ({shown}).");
        }
        return new(offer, current, DriverVerdict.Unknown, $"It could not be compared with the installed driver ({shown}).");
    }

    public static string DescribeInstalled(InstalledDriver driver)
    {
        var parts = new List<string>();
        if (driver.Version.Length > 0) parts.Add(driver.Version);
        if (driver.Date is { } date) parts.Add(date.ToString("d MMM yyyy", CultureInfo.CurrentCulture));
        return parts.Count == 0 ? driver.DeviceName : string.Join(", ", parts);
    }

    /// <summary>Whether an offer is worth putting first: a driver that is newer, new to the PC or cannot be told. The same or an older one is shown after, without a button.</summary>
    public static bool IsActionable(DriverFinding finding) => finding.Verdict is DriverVerdict.New or DriverVerdict.Newer or DriverVerdict.Unknown;
}

/// <summary>
/// Reasons Windows Update may offer fewer drivers than it could. TidyWindow reports them and goes no further: a policy set by an organisation, or a
/// setting the person made, is not something to get around.
/// </summary>
public static class DriverPolicy
{
    private const string WindowsUpdate = @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate";

    /// <param name="readDword">Reads a number from the machine registry, or null when it is not set.</param>
    public static IReadOnlyList<string> Notices(Func<string, string, int?> readDword, Func<string, string, string?> readText)
    {
        var notices = new List<string>();
        if (readDword(WindowsUpdate, "ExcludeWUDriversInQualityUpdate") == 1 || readDword(@"SOFTWARE\Microsoft\PolicyManager\current\device\Update", "ExcludeWUDriversInQualityUpdate") == 1)
            notices.Add("A policy on this PC tells Windows Update not to include drivers, so it will offer none.");
        if (readDword(WindowsUpdate + @"\AU", "UseWUServer") == 1 && !string.IsNullOrWhiteSpace(readText(WindowsUpdate, "WUServer")))
            notices.Add("This PC gets its updates from an update server set by an organisation (WSUS). That server decides which drivers are offered, so the list may be short or empty.");
        if (readDword(WindowsUpdate, "DoNotConnectToWindowsUpdateInternetLocations") == 1)
            notices.Add("A policy stops this PC from connecting to Windows Update on the internet.");
        if (readDword(@"SOFTWARE\Microsoft\Windows\CurrentVersion\DriverSearching", "SearchOrderConfig") == 0)
            notices.Add("Device installation settings are set to never look for drivers on Windows Update.");
        return notices;
    }
}

/// <summary>A tool the PC or graphics maker publishes for its own drivers, firmware and utilities. Windows Update lags behind these for graphics and for laptop-specific parts.</summary>
public sealed record DriverMakerTool(string Name, string Why, string Url, string? WinGetId);

public static class DriverMakers
{
    private static readonly DriverMakerTool Nvidia = new("NVIDIA app", "Graphics drivers from NVIDIA reach its own app before they reach Windows Update.", "https://www.nvidia.com/en-us/software/nvidia-app/", null);
    private static readonly DriverMakerTool Amd = new("AMD Software: Adrenalin Edition", "Graphics drivers from AMD arrive in its own software first.", "https://www.amd.com/en/support/download/drivers.html", null);
    private static readonly DriverMakerTool Intel = new("Intel Driver & Support Assistant", "Finds the newest Intel graphics, network and chipset drivers for this PC.", "https://www.intel.com/content/www/us/en/support/detect.html", "Intel.IntelDriverAndSupportAssistant");
    private static readonly DriverMakerTool Dell = new("Dell Command | Update", "Dell's own tool for drivers, BIOS and firmware for this model.", "https://www.dell.com/support/kbdoc/en-us/000177325/dell-command-update", "Dell.CommandUpdate");
    private static readonly DriverMakerTool Lenovo = new("Lenovo System Update", "Lenovo's own tool for drivers, BIOS and firmware for this model.", "https://support.lenovo.com/solutions/ht037099", "Lenovo.SystemUpdate");
    private static readonly DriverMakerTool Hp = new("HP Support Assistant", "HP's own tool for drivers, BIOS and firmware for this model.", "https://support.hp.com/us-en/help/hp-support-assistant", "HP.SupportAssistant");

    /// <summary>The tools that fit this PC: the maker of its graphics driver, and the PC's own maker.</summary>
    public static IReadOnlyList<DriverMakerTool> For(IEnumerable<InstalledDriver> installed, string computerMaker)
    {
        var tools = new List<DriverMakerTool>();
        foreach (var provider in installed.Where(x => x.DeviceClass.Equals("Display", StringComparison.OrdinalIgnoreCase)).Select(x => x.Provider))
        {
            if (provider.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)) tools.Add(Nvidia);
            else if (provider.Contains("Advanced Micro Devices", StringComparison.OrdinalIgnoreCase) || provider.Equals("AMD", StringComparison.OrdinalIgnoreCase)) tools.Add(Amd);
            else if (provider.Contains("Intel", StringComparison.OrdinalIgnoreCase)) tools.Add(Intel);
        }
        if (computerMaker.Contains("Dell", StringComparison.OrdinalIgnoreCase)) tools.Add(Dell);
        else if (computerMaker.Contains("LENOVO", StringComparison.OrdinalIgnoreCase)) tools.Add(Lenovo);
        else if (computerMaker.Contains("HP", StringComparison.Ordinal) || computerMaker.Contains("Hewlett", StringComparison.OrdinalIgnoreCase)) tools.Add(Hp);
        return tools.Distinct().ToList();
    }
}

/// <summary>The drivers TidyWindow installed and how to get the old one back, kept in this account's profile for the page to show.</summary>
public sealed class DriverHistory(string folder)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly object gate = new();
    private string FilePath => Path.Combine(folder, "installed.json");

    public IReadOnlyList<DriverChange> All() { lock (gate) return Load().OrderByDescending(x => x.When).ToList(); }

    public void Add(DriverChange change)
    {
        lock (gate)
        {
            var list = Load();
            list.Add(change);
            // A long history is not worth keeping; the newest fifty cover any realistic need.
            Save(list.OrderByDescending(x => x.When).Take(50).ToList());
        }
    }

    public void MarkRestored(string backupId)
    {
        lock (gate)
        {
            var list = Load();
            var index = list.FindIndex(x => string.Equals(x.BackupId, backupId, StringComparison.OrdinalIgnoreCase));
            if (index < 0) return;
            list[index] = list[index] with { Restored = true };
            Save(list);
        }
    }

    private void Save(List<DriverChange> list)
    {
        Directory.CreateDirectory(folder);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(list, Json));
        File.Move(temp, FilePath, overwrite: true);
    }

    private List<DriverChange> Load()
    {
        try { return JsonSerializer.Deserialize<List<DriverChange>>(File.ReadAllText(FilePath), Json) ?? []; }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return []; }
    }
}

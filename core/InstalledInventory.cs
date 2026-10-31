using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace TidyWindow.Next.Core;

public sealed record InstalledApp(ManagerKind? Manager, string? Id, string Name, string Version, string Source = "");
public sealed record CoverageReport(int Total, int Listed, IReadOnlyList<string> Missing)
{
    public double Percent => Total == 0 ? 100 : 100.0 * Listed / Total;
}
public sealed record InventoryResult(ManagerKind Manager, IReadOnlyList<InstalledApp> Apps, string? Error = null);

public sealed partial class InstalledInventory
{
    private readonly Dictionary<string, InstalledApp> identities;
    private readonly Dictionary<string, InstalledApp> names;
    public IReadOnlyList<string> Errors { get; }

    public InstalledInventory(IEnumerable<InstalledApp> apps, IEnumerable<string>? errors = null)
    {
        var records = apps.ToArray();
        identities = records.Where(x => x.Manager is not null && x.Id is not null)
            .DistinctBy(x => Identity(x.Manager!.Value, x.Id!)).ToDictionary(x => Identity(x.Manager!.Value, x.Id!));
        names = records.Where(x => NameKey(x.Name).Length > 0).DistinctBy(x => NameKey(x.Name)).ToDictionary(x => NameKey(x.Name));
        Errors = errors?.ToArray() ?? [];
    }

    public InstalledApp? Find(Package package)
    {
        if (identities.TryGetValue(Identity(package.Manager, package.Id), out var exact)) return exact;
        // Cross-source detection uses an exact normalized display name, never fuzzy similarity:
        // e.g. Google Chrome/googlechrome match; Git/GitHub and stable/preview stay distinct.
        return names.GetValueOrDefault(NameKey(package.Name));
    }

    /// <summary>What Windows reports as installed, with no package manager identities.</summary>
    public static InstalledInventory ReadWindows()
    {
        if (!OperatingSystem.IsWindows()) return new([]);
        var (apps, errors) = WindowsInstalledApps.Read();
        return new(apps, errors);
    }

    /// <summary>How much of what Windows lists in Settings > Installed apps is also in <paramref name="listed"/>, matched by display name.</summary>
    public static CoverageReport Coverage(IEnumerable<InstalledApp> windows, IEnumerable<string> listed)
    {
        var keys = listed.Select(NameKey).Where(x => x.Length > 0).ToHashSet();
        var missing = new List<string>();
        var total = 0;
        foreach (var name in windows.Select(x => x.Name).DistinctBy(NameKey))
        {
            var key = NameKey(name);
            if (key.Length == 0) continue;
            total++;
            // "Visual Studio Code (User)" is the same program as "Visual Studio Code" installed for one user.
            if (!keys.Contains(key) && !keys.Any(x => x.Length >= 6 && key.StartsWith(x, StringComparison.Ordinal))) missing.Add(name);
        }
        return new(total, total - missing.Count, missing);
    }

    /// <summary>The programs Windows shows in Settings > Installed apps, for checking another list against.</summary>
    public static IReadOnlyList<InstalledApp> ReadListedByWindows() => OperatingSystem.IsWindows() ? WindowsInstalledApps.Read(listedByWindows: true).Apps : [];

    private static string Identity(ManagerKind manager, string id) => $"{manager}:{id.ToUpperInvariant()}";
    // Registry entries often add a generic trailing word the package name lacks ("MSYS2" vs "MSYS2 Installer").
    // Dropping only that word keeps the comparison exact: "Git" still does not match "GitHub".
    private static string NameKey(string name) => string.Concat(TrimGenericSuffix(name).Normalize(NormalizationForm.FormKC)
        .EnumerateRunes().Where(x => Rune.IsLetterOrDigit(x) || x.Value is '+' or '#').Select(x => Rune.ToUpperInvariant(x).ToString()));
    private static string TrimGenericSuffix(string name)
    {
        for (var i = 0; i < 2; i++)
        {
            var trimmed = GenericSuffix().Replace(name, "");
            if (trimmed == name) break;
            name = trimmed;
        }
        return name;
    }
    [GeneratedRegex(@"(?<=\S)[\s\-_]+(?:installer|setup)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex GenericSuffix();
}

internal static class WindowsInstalledApps
{
    [SupportedOSPlatform("windows")]
    /// <param name="listedByWindows">Only what Windows shows in Settings: no hidden system components and no updates of other products.</param>
    public static (IReadOnlyList<InstalledApp> Apps, IReadOnlyList<string> Errors) Read(bool listedByWindows = false)
    {
        var apps = new List<InstalledApp>();
        var errors = new List<string>();
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        foreach (var view in Environment.Is64BitOperatingSystem ? new[] { RegistryView.Registry64, RegistryView.Registry32 } : new[] { RegistryView.Registry32 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall is null) continue;
                foreach (var id in uninstall.GetSubKeyNames())
                {
                    try
                    {
                        using var app = uninstall.OpenSubKey(id);
                        if (app?.GetValue("DisplayName") is not string name || string.IsNullOrWhiteSpace(name)) continue;
                        if (listedByWindows && !ListedByWindows(app)) continue;
                        apps.Add(new(null, null, name, app.GetValue("DisplayVersion") as string ?? ""));
                    }
                    catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
                    { errors.Add("Some Windows app entries could not be read."); }
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            { errors.Add("Windows installed-app information could not be fully read."); }
        }
        return (apps, errors.Distinct().ToArray());
    }

    // SystemComponent is a DWORD for most installers but a string for some, so it is read as text.
    [SupportedOSPlatform("windows")]
    private static bool ListedByWindows(RegistryKey app) =>
        !(app.GetValue("SystemComponent")?.ToString() is { Length: > 0 } hidden && hidden != "0") &&
        app.GetValue("ParentKeyName") is null && app.GetValue("ParentDisplayName") is null &&
        !(app.GetValue("ReleaseType") as string is "Update" or "Hotfix" or "Security Update" or "Update Rollup" or "ServicePack");
}

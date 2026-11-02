using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace TidyWindow.Next.Core;

public enum DebloatGroup { Microsoft, Promoted, Ai, Manufacturer, Components }
/// <summary>How an item is removed. Appx is a Store-style app; the others each need their own careful steps.</summary>
public enum DebloatKind { Appx, Recall, OneDrive, Edge }
public enum DebloatRisk { Safe, Careful }

/// <summary>One thing that can be removed, described for a person. Items are data (debloat-catalog.json).</summary>
public sealed class DebloatItem
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required DebloatGroup Group { get; init; }
    public DebloatKind Kind { get; init; } = DebloatKind.Appx;
    public string Topic { get; init; } = "";
    public string Icon { get; init; } = "";
    public required string Summary { get; init; }
    public required string Effect { get; init; }
    public DebloatRisk Risk { get; init; } = DebloatRisk.Safe;
    /// <summary>Offered first, and picked by "Select recommended". Only ever items that nobody misses.</summary>
    public bool Recommended { get; init; }
    /// <summary>Package names this item covers. A name ending in * covers every package that starts with it. Only for <see cref="DebloatKind.Appx"/>.</summary>
    public string[] Packages { get; init; } = [];
    /// <summary>Packages that one of <see cref="Packages"/> would cover but that are kept: a maker's hardware controls among its promotional apps. Spelled like a package pattern.</summary>
    public string[] Except { get; init; } = [];
    /// <summary>How to get it back: a Store product id (such as 9P1J8S7CCWWT) or a WinGet id (such as Microsoft.OneDrive). Empty when it can only be found in the Store.</summary>
    public string StoreId { get; init; } = "";
    public string[] Sources { get; init; } = [];
}

/// <summary>One installed package, as Windows reports it. <paramref name="InstallLocation"/> is the folder that holds its manifest and logo; empty when unknown.</summary>
public sealed record AppxFact(string Name, string FullName, string FamilyName, bool NonRemovable, bool Framework, IReadOnlyList<string> Users, string InstallLocation = "");
/// <summary>A package that Windows installs for every new account. <paramref name="InstallLocation"/> is the folder of its manifest; empty when unknown.</summary>
public sealed record ProvisionedFact(string DisplayName, string PackageName, string InstallLocation = "");
/// <summary>What the scanner needs to know about OneDrive, Edge and Recall, none of which are ordinary packages.</summary>
/// <param name="OneDriveFolders">The known folders that live in OneDrive, each naming its account when more than one account was looked at.</param>
public sealed record PlaybookFacts(bool OneDrive, IReadOnlyList<string> OneDriveFolders, bool Edge, bool OtherBrowser, bool Recall);

/// <summary>What the scanner reads from the PC, so tests can describe a PC without touching it.</summary>
public interface IDebloatSystem
{
    int Build { get; }
    /// <summary>Installed packages. Without administrator rights these are the current user's; with them, everyone's.</summary>
    IReadOnlyList<AppxFact> Packages();
    /// <summary>Packages Windows installs for new accounts. Read from Windows' own package store, which anyone may read, so an app that was only uninstalled for one account still shows.</summary>
    IReadOnlyList<ProvisionedFact> Provisioned();
    /// <param name="userSid">The account that asked, so what is per account (OneDrive) is looked for in that account's profile rather than in the profile of whoever this process runs as.</param>
    PlaybookFacts Playbooks(string? userSid = null);
}

/// <param name="UserSid">The account that asked. The helper may run as another administrator, so per-account checks need to know whose profile to look at.</param>
public sealed record DebloatRequest(string Id, bool Restore = false, IReadOnlyList<string>? Families = null, string? UserSid = null);
/// <param name="AllowedOnly">Windows may have the item again, but nothing installed it: there is no Store id to install from, or no WinGet to do it with.</param>
public sealed record DebloatOutcome(bool Ok, string? Error = null, string? Note = null, IReadOnlyList<string>? Families = null, bool AllowedOnly = false);

public interface IDebloatChanger
{
    DebloatOutcome Change(DebloatRequest request);
}

/// <summary>An item TidyWindow removed, kept on disk so it can be put back later. <paramref name="Note"/> says what happened when a put-back allowed it but did not install it.</summary>
public sealed record DebloatRemoved(string Id, string Name, DateTimeOffset When, IReadOnlyList<string> Families, string? Note = null);

/// <summary>An item as found on this PC.</summary>
public sealed record DebloatEntry(DebloatItem Item, IReadOnlyList<AppxFact> Packages, IReadOnlyList<ProvisionedFact> Provisioned, string? Lock)
{
    public string Id => Item.Id;
    public bool CanRemove => Lock is null;
    public IReadOnlyList<string> Families => Packages.Select(x => x.FamilyName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    /// <summary>How many apps this covers: every package family that is installed, or 1 for a component.</summary>
    public int AppCount => Item.Kind == DebloatKind.Appx ? Math.Max(1, Families.Count) : 1;
}

public sealed record DebloatScanResult(IReadOnlyList<DebloatEntry> Entries, int NotOnThisPc);

public static class DebloatCatalog
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };
    private static IReadOnlyList<DebloatItem>? loaded;

    public static IReadOnlyList<DebloatItem> Load() => loaded ??= Read();

    private static IReadOnlyList<DebloatItem> Read()
    {
        using var stream = typeof(DebloatCatalog).Assembly.GetManifestResourceStream("debloat-catalog.json") ?? throw new InvalidOperationException("The app catalog is missing from the app.");
        var file = JsonSerializer.Deserialize<CatalogFile>(stream, Options) ?? throw new InvalidOperationException("The app catalog is empty.");
        var problems = DebloatGuard.Validate(file.Items);
        if (problems.Count > 0) throw new InvalidOperationException("The app catalog is not valid: " + string.Join(" ", problems));
        return file.Items;
    }

    private sealed class CatalogFile { public List<DebloatItem> Items { get; init; } = []; }
}

/// <summary>
/// The rules for what may be removed. They are a second list, separate from the catalog: a package is only ever removed when both agree,
/// and the administrator helper checks this again, so neither a catalog mistake nor a request from elsewhere can remove something Windows needs.
/// </summary>
public static partial class DebloatGuard
{
    /// <summary>Packages that Windows, the Store, WinGet, sign-in or other apps depend on. A package whose name starts with one of these is never removed.</summary>
    private static readonly string[] Protected =
    [
        // Shared runtimes every app needs
        "Microsoft.VCLibs", "Microsoft.UI.Xaml", "Microsoft.NET.Native", "Microsoft.WindowsAppRuntime", "Microsoft.WinAppRuntime", "Microsoft.WebView2", "Microsoft.EdgeWebView",
        "Microsoft.MicrosoftEdge", "Windows.", "NcsiUwpApp",
        // Store, WinGet and sign-in
        "Microsoft.WindowsStore", "Microsoft.StorePurchaseApp", "Microsoft.Services.Store", "Microsoft.DesktopAppInstaller", "Microsoft.Winget",
        "Microsoft.XboxIdentityProvider", "Microsoft.Xbox.TCUI", "Microsoft.GamingServices", "Microsoft.AAD.BrokerPlugin", "Microsoft.AccountsControl", "Microsoft.CredDialogHost", "Microsoft.LockApp", "Microsoft.BioEnrollment",
        // The shell: Start, taskbar, search, setup, security
        "Microsoft.Windows.ShellExperienceHost", "Microsoft.Windows.StartMenuExperienceHost", "Microsoft.Windows.Search", "Microsoft.Windows.CloudExperienceHost", "Microsoft.Windows.ContentDeliveryManager",
        "Microsoft.Windows.OOBENetwork", "Microsoft.Windows.PeopleExperienceHost", "Microsoft.Windows.SecureAssessmentBrowser", "Microsoft.Windows.CapturePicker", "Microsoft.Windows.AssignedAccessLockApp",
        "Microsoft.Windows.PinningConfirmationDialog", "Microsoft.Windows.ParentalControls", "Microsoft.Windows.XGpuEjectDialog", "Microsoft.Windows.Apprep.ChxApp", "Microsoft.Windows.CallingShellApp",
        "Microsoft.Windows.NarratorQuickStart", "Microsoft.SecHealthUI", "Microsoft.Win32WebViewHost", "Microsoft.ECApp", "Microsoft.AsyncTextService",
        // Only the Windows client packages that ship in C:\Windows\SystemApps. MicrosoftWindows.Client.WebExperience is a Store package that holds nothing but Widgets, so it is not here.
        "MicrosoftWindows.Client.CBS", "MicrosoftWindows.Client.Core", "MicrosoftWindows.Client.FileExp", "MicrosoftWindows.Client.OOBE", "MicrosoftWindows.Client.Photon", "MicrosoftWindows.Client.AIX",
        "MicrosoftWindows.UndockedDevKit",
        // Office re-stages these itself whenever it runs maintenance, so removing them only makes them reappear
        "Microsoft.Office.ActionsServer", "Microsoft.OfficePushNotificationUtility",
        // The everyday tools: nobody should lose a calculator, a screenshot tool, an image viewer or a terminal by ticking a box
        "Microsoft.WindowsCalculator", "Microsoft.WindowsNotepad", "Microsoft.Paint", "Microsoft.ScreenSketch", "Microsoft.Windows.Photos", "Microsoft.WindowsTerminal", "Microsoft.WindowsCamera"
    ];

    [GeneratedRegex(@"^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex Slug();
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]*\*?$")]
    private static partial Regex Pattern();
    [GeneratedRegex(@"^[0-9a-fA-F]{8}(-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}$")]
    private static partial Regex GuidName();
    [GeneratedRegex(@"^[A-Za-z0-9._~-]+$")]
    private static partial Regex Token();
    [GeneratedRegex(@"^S-1-[0-9]+(-[0-9]+)+$")]
    private static partial Regex Sid();
    [GeneratedRegex(@"^(9[A-Z0-9]{11}|XP[A-Z0-9]{12})$")]
    private static partial Regex StoreProduct();
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9+_-]*(\.[A-Za-z0-9][A-Za-z0-9+_-]*)+$")]
    private static partial Regex WinGetId();

    /// <summary>Package names, full names and family names go into registry paths and commands, so only plain ones are accepted.</summary>
    public static bool IsToken(string? text) => text is { Length: > 0 and <= 256 } && Token().IsMatch(text);
    public static bool IsSid(string? text) => text is { Length: > 0 and <= 184 } && Sid().IsMatch(text);

    /// <summary>A Store product id: twelve characters starting with 9 (9P1J8S7CCWWT), or the newer fourteen starting with XP (XP8BT8DW290MPQ). Those go to the Store source of WinGet.</summary>
    public static bool IsStoreProduct(string? id) => id is not null && StoreProduct().IsMatch(id);

    /// <summary>A WinGet package id: publisher and name joined with dots (Microsoft.Edge).</summary>
    public static bool IsWinGetId(string? id) => id is { Length: > 0 and <= 128 } && WinGetId().IsMatch(id);

    /// <summary>True for a package that must never be removed: a runtime, a Windows component, or one named in <see cref="Protected"/>.</summary>
    public static bool IsProtected(string name) =>
        GuidName().IsMatch(name) || Protected.Any(x => name.StartsWith(x, StringComparison.OrdinalIgnoreCase));

    public static bool Matches(string pattern, string name) =>
        pattern.EndsWith('*') ? name.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase) : string.Equals(pattern, name, StringComparison.OrdinalIgnoreCase);

    /// <summary>The family name is the package name, an underscore and the publisher id. Only the name part is judged.</summary>
    public static string NameOf(string familyOrFullName) => familyOrFullName.Split('_')[0];

    /// <summary>
    /// Where a package sits in the item's own list. Packages go in this order, so a package that can reinstall another (WidgetBoard puts
    /// the Widgets runtime back) is removed before the one it would repair.
    /// </summary>
    public static int CatalogOrder(DebloatItem item, string name)
    {
        for (var i = 0; i < item.Packages.Length; i++)
            if (Matches(item.Packages[i], name)) return i;
        return item.Packages.Length;
    }

    /// <summary>Whether an installed package belongs to this item and may be removed. What the item keeps (<see cref="DebloatItem.Except"/>) is never covered.</summary>
    public static bool Covers(DebloatItem item, string name, bool framework) =>
        item.Kind == DebloatKind.Appx && !framework && !IsProtected(name) && item.Packages.Any(p => Matches(p, name)) && !item.Except.Any(p => Matches(p, name));

    /// <summary>Everything wrong with a catalog, as sentences. Empty when it is fine.</summary>
    public static List<string> Validate(IReadOnlyList<DebloatItem> items)
    {
        var problems = new List<string>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            var who = $"“{item.Id}”";
            if (!Slug().IsMatch(item.Id)) problems.Add($"{who} needs a lowercase id with dashes.");
            if (!ids.Add(item.Id)) problems.Add($"{who} is listed twice.");
            if (string.IsNullOrWhiteSpace(item.Name) || string.IsNullOrWhiteSpace(item.Summary) || string.IsNullOrWhiteSpace(item.Effect)) problems.Add($"{who} needs a name, a summary and an effect.");
            if (item.Kind == DebloatKind.Appx && item.Packages.Length == 0) problems.Add($"{who} removes nothing.");
            if (item.Kind != DebloatKind.Appx && (item.Packages.Length > 0 || item.Except.Length > 0)) problems.Add($"{who} is a component and cannot list packages.");
            if (item.Recommended && item.Risk != DebloatRisk.Safe) problems.Add($"{who} is recommended but not safe.");
            // An id that WinGet would not recognise could never put the item back, and the Store ids now come in two lengths.
            if (item.StoreId.Length > 0 && !IsStoreProduct(item.StoreId) && !IsWinGetId(item.StoreId)) problems.Add($"{who}: “{item.StoreId}” is neither a Store product id nor a WinGet id.");
            foreach (var pattern in item.Packages)
            {
                var literal = pattern.TrimEnd('*');
                if (!Pattern().IsMatch(pattern)) problems.Add($"{who}: “{pattern}” is not a package name.");
                else if (pattern.EndsWith('*') && literal.Length < 6) problems.Add($"{who}: “{pattern}” is too short to be safe.");
                else if (Protected.Any(p => pattern.EndsWith('*') ? p.StartsWith(literal, StringComparison.OrdinalIgnoreCase) || literal.StartsWith(p, StringComparison.OrdinalIgnoreCase) : literal.StartsWith(p, StringComparison.OrdinalIgnoreCase)) || GuidName().IsMatch(literal))
                    problems.Add($"{who}: “{pattern}” is something Windows needs.");
            }
            foreach (var pattern in item.Except)
            {
                if (!Pattern().IsMatch(pattern)) problems.Add($"{who}: “{pattern}” is not a package name.");
                else if (!item.Packages.Any(p => Matches(p, pattern.TrimEnd('*')))) problems.Add($"{who} keeps “{pattern}”, which none of its packages would remove anyway.");
            }
        }
        return problems;
    }
}

/// <summary>Finds what on this PC the catalog covers.</summary>
public static class DebloatScan
{
    public const string OneDriveFoldersLock = "Desktop, Documents or Pictures are backed up to OneDrive. Stop that in OneDrive's settings first, or those folders would be left pointing at nothing.";
    public const string OnlyBrowserLock = "Edge is the only browser installed for everyone on this PC. Install another browser for all users first, or you would have no way to open the web.";

    public static DebloatScanResult Run(IDebloatSystem system, IReadOnlyList<DebloatItem> catalog)
    {
        var packages = new Lazy<IReadOnlyList<AppxFact>>(system.Packages);
        var provisioned = new Lazy<IReadOnlyList<ProvisionedFact>>(system.Provisioned);
        var facts = new Lazy<PlaybookFacts>(() => system.Playbooks());
        var entries = new List<DebloatEntry>();
        foreach (var item in catalog)
            if (Build(item, packages, provisioned, facts) is { } entry) entries.Add(entry);
        return new(entries, catalog.Count - entries.Count);
    }

    /// <summary>One item as found on this PC. Only what the item needs is read: a component does not list every package, and an app does not look at OneDrive.</summary>
    public static DebloatEntry? Find(IDebloatSystem system, IReadOnlyList<DebloatItem> catalog, string id, string? userSid = null) =>
        catalog.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase)) is { } item
            ? Build(item, new Lazy<IReadOnlyList<AppxFact>>(system.Packages), new Lazy<IReadOnlyList<ProvisionedFact>>(system.Provisioned), new Lazy<PlaybookFacts>(() => system.Playbooks(userSid)))
            : null;

    private static DebloatEntry? Build(DebloatItem item, Lazy<IReadOnlyList<AppxFact>> packages, Lazy<IReadOnlyList<ProvisionedFact>> provisioned, Lazy<PlaybookFacts> facts)
    {
        switch (item.Kind)
        {
            case DebloatKind.Appx:
                var found = packages.Value.Where(x => DebloatGuard.Covers(item, x.Name, x.Framework)).OrderBy(x => DebloatGuard.CatalogOrder(item, x.Name)).ToList();
                var staged = provisioned.Value.Where(x => DebloatGuard.Covers(item, x.DisplayName, false)).OrderBy(x => DebloatGuard.CatalogOrder(item, x.DisplayName)).ToList();
                return found.Count == 0 && staged.Count == 0 ? null : new(item, found, staged, null);
            case DebloatKind.Recall:
                return facts.Value.Recall ? new(item, [], [], null) : null;
            case DebloatKind.OneDrive:
                return facts.Value.OneDrive ? new(item, [], [], facts.Value.OneDriveFolders.Count > 0 ? $"{OneDriveFoldersLock} Backed up now: {string.Join(", ", facts.Value.OneDriveFolders)}." : null) : null;
            case DebloatKind.Edge:
                return facts.Value.Edge ? new(item, [], [], facts.Value.OtherBrowser ? null : OnlyBrowserLock) : null;
            default:
                return null;
        }
    }
}

/// <summary>What TidyWindow removed, kept on disk so it can be put back later.</summary>
public sealed class DebloatHistory(string folder)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly object gate = new();
    private string FilePath => Path.Combine(folder, "removed.json");

    public IReadOnlyList<DebloatRemoved> All()
    {
        lock (gate) return Load().OrderByDescending(x => x.When).ToList();
    }

    public DebloatRemoved? Get(string id)
    {
        lock (gate) return Load().FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Records a removal. Removing more of the same item later adds to what is kept.</summary>
    public void Add(DebloatRemoved removed)
    {
        lock (gate)
        {
            var list = Load();
            var old = list.FirstOrDefault(x => string.Equals(x.Id, removed.Id, StringComparison.OrdinalIgnoreCase));
            list.RemoveAll(x => string.Equals(x.Id, removed.Id, StringComparison.OrdinalIgnoreCase));
            var families = (old?.Families ?? []).Concat(removed.Families).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            list.Add(removed with { Families = families });
            Save(list);
        }
    }

    /// <summary>Marks a record with what happened when a put-back allowed the item but did not install it, so the list can say so.</summary>
    public void Note(string id, string? note)
    {
        lock (gate)
        {
            var list = Load();
            var index = list.FindIndex(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
            if (index < 0) return;
            list[index] = list[index] with { Note = note };
            Save(list);
        }
    }

    public void Remove(string id)
    {
        lock (gate)
        {
            var list = Load();
            if (list.RemoveAll(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase)) > 0) Save(list);
        }
    }

    private List<DebloatRemoved> Load()
    {
        try { return JsonSerializer.Deserialize<List<DebloatRemoved>>(File.ReadAllText(FilePath), Json) ?? []; }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return []; }
    }

    private void Save(List<DebloatRemoved> list)
    {
        Directory.CreateDirectory(folder);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(list, Json));
        File.Move(temp, FilePath, overwrite: true);
    }
}

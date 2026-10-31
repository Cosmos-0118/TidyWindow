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
    /// <summary>How to get it back: a Store product id (such as 9P1J8S7CCWWT) or a WinGet id (such as Microsoft.OneDrive). Empty when it can only be found in the Store.</summary>
    public string StoreId { get; init; } = "";
    public string[] Sources { get; init; } = [];
}

/// <summary>One installed package, as Windows reports it. <paramref name="InstallLocation"/> is the folder that holds its manifest and logo; empty when unknown.</summary>
public sealed record AppxFact(string Name, string FullName, string FamilyName, bool NonRemovable, bool Framework, IReadOnlyList<string> Users, string InstallLocation = "");
/// <summary>A package that Windows installs for every new account. <paramref name="InstallLocation"/> is the folder of its manifest; empty when unknown.</summary>
public sealed record ProvisionedFact(string DisplayName, string PackageName, string InstallLocation = "");
/// <summary>What the scanner needs to know about OneDrive, Edge and Recall, none of which are ordinary packages.</summary>
public sealed record PlaybookFacts(bool OneDrive, IReadOnlyList<string> OneDriveFolders, bool Edge, bool OtherBrowser, bool Recall);

/// <summary>What the scanner reads from the PC, so tests can describe a PC without touching it.</summary>
public interface IDebloatSystem
{
    int Build { get; }
    /// <summary>Installed packages. Without administrator rights these are the current user's; with them, everyone's.</summary>
    IReadOnlyList<AppxFact> Packages();
    /// <summary>Packages Windows installs for new accounts. Read from Windows' own package store, which anyone may read, so an app that was only uninstalled for one account still shows.</summary>
    IReadOnlyList<ProvisionedFact> Provisioned();
    PlaybookFacts Playbooks();
}

public sealed record DebloatRequest(string Id, bool Restore = false, IReadOnlyList<string>? Families = null);
public sealed record DebloatOutcome(bool Ok, string? Error = null, string? Note = null, IReadOnlyList<string>? Families = null);

public interface IDebloatChanger
{
    DebloatOutcome Change(DebloatRequest request);
}

/// <summary>An item TidyWindow removed, kept on disk so it can be put back later.</summary>
public sealed record DebloatRemoved(string Id, string Name, DateTimeOffset When, IReadOnlyList<string> Families);

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
        "MicrosoftWindows.Client.", "MicrosoftWindows.UndockedDevKit",
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

    /// <summary>Package names, full names and family names go into registry paths and commands, so only plain ones are accepted.</summary>
    public static bool IsToken(string? text) => text is { Length: > 0 and <= 256 } && Token().IsMatch(text);
    public static bool IsSid(string? text) => text is { Length: > 0 and <= 184 } && Sid().IsMatch(text);

    /// <summary>True for a package that must never be removed: a runtime, a Windows component, or one named in <see cref="Protected"/>.</summary>
    public static bool IsProtected(string name) =>
        GuidName().IsMatch(name) || Protected.Any(x => name.StartsWith(x, StringComparison.OrdinalIgnoreCase));

    public static bool Matches(string pattern, string name) =>
        pattern.EndsWith('*') ? name.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase) : string.Equals(pattern, name, StringComparison.OrdinalIgnoreCase);

    /// <summary>The family name is the package name, an underscore and the publisher id. Only the name part is judged.</summary>
    public static string NameOf(string familyOrFullName) => familyOrFullName.Split('_')[0];

    /// <summary>Whether an installed package belongs to this item and may be removed.</summary>
    public static bool Covers(DebloatItem item, string name, bool framework) =>
        item.Kind == DebloatKind.Appx && !framework && !IsProtected(name) && item.Packages.Any(p => Matches(p, name));

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
            if (item.Kind != DebloatKind.Appx && item.Packages.Length > 0) problems.Add($"{who} is a component and cannot list packages.");
            if (item.Recommended && item.Risk != DebloatRisk.Safe) problems.Add($"{who} is recommended but not safe.");
            foreach (var pattern in item.Packages)
            {
                var literal = pattern.TrimEnd('*');
                if (!Pattern().IsMatch(pattern)) problems.Add($"{who}: “{pattern}” is not a package name.");
                else if (pattern.EndsWith('*') && literal.Length < 6) problems.Add($"{who}: “{pattern}” is too short to be safe.");
                else if (Protected.Any(p => pattern.EndsWith('*') ? p.StartsWith(literal, StringComparison.OrdinalIgnoreCase) || literal.StartsWith(p, StringComparison.OrdinalIgnoreCase) : literal.StartsWith(p, StringComparison.OrdinalIgnoreCase)) || GuidName().IsMatch(literal))
                    problems.Add($"{who}: “{pattern}” is something Windows needs.");
            }
        }
        return problems;
    }
}

/// <summary>Finds what on this PC the catalog covers.</summary>
public static class DebloatScan
{
    public const string OneDriveFoldersLock = "Desktop, Documents or Pictures are backed up to OneDrive. Stop that in OneDrive's settings first, or those folders would be left pointing at nothing.";
    public const string OnlyBrowserLock = "Edge is the only browser on this PC. Install another browser first, or you would have no way to open the web.";

    public static DebloatScanResult Run(IDebloatSystem system, IReadOnlyList<DebloatItem> catalog)
    {
        var packages = system.Packages();
        var provisioned = system.Provisioned();
        var facts = system.Playbooks();
        var entries = new List<DebloatEntry>();
        foreach (var item in catalog)
            if (Build(item, packages, provisioned, facts) is { } entry) entries.Add(entry);
        return new(entries, catalog.Count - entries.Count);
    }

    public static DebloatEntry? Find(IDebloatSystem system, IReadOnlyList<DebloatItem> catalog, string id) =>
        catalog.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase)) is { } item
            ? Build(item, system.Packages(), system.Provisioned(), system.Playbooks())
            : null;

    private static DebloatEntry? Build(DebloatItem item, IReadOnlyList<AppxFact> packages, IReadOnlyList<ProvisionedFact> provisioned, PlaybookFacts facts)
    {
        switch (item.Kind)
        {
            case DebloatKind.Appx:
                var found = packages.Where(x => DebloatGuard.Covers(item, x.Name, x.Framework)).ToList();
                var staged = provisioned.Where(x => DebloatGuard.Covers(item, x.DisplayName, false)).ToList();
                return found.Count == 0 && staged.Count == 0 ? null : new(item, found, staged, null);
            case DebloatKind.Recall:
                return facts.Recall ? new(item, [], [], null) : null;
            case DebloatKind.OneDrive:
                return facts.OneDrive ? new(item, [], [], facts.OneDriveFolders.Count > 0 ? $"{OneDriveFoldersLock} Backed up now: {string.Join(", ", facts.OneDriveFolders)}." : null) : null;
            case DebloatKind.Edge:
                return facts.Edge ? new(item, [], [], facts.OtherBrowser ? null : OnlyBrowserLock) : null;
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

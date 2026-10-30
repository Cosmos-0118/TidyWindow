using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace TidyWindow.Next.Core;

public enum TuneGroup { Speed, Cleaner, Comfort }
/// <summary>Whose registry an item changes. An item is wholly one or the other, because the administrator helper may belong to a different account.</summary>
public enum TuneScope { User, Machine }
public enum TuneRisk { Safe, Careful }
/// <summary>When a change shows up.</summary>
public enum TuneEffect { Now, Explorer, SignOut, Restart }
public enum TuneKind { Dword, String, Other }
/// <summary>How a value is judged. Exactly: it equals the wanted value. AtMost: a number no larger than it. ClearBits: none of these bits are set.</summary>
public enum TuneRule { Exactly, AtMost, ClearBits }

/// <summary>One registry value an item changes. Names come from the catalog, never from a caller.</summary>
public sealed class TuneTarget
{
    /// <summary>The key under HKEY_CURRENT_USER or HKEY_LOCAL_MACHINE, depending on the item.</summary>
    public required string Key { get; init; }
    /// <summary>The value name. Empty means the key's (Default) value.</summary>
    public string Name { get; init; } = "";
    public TuneKind Kind { get; init; } = TuneKind.Dword;
    public TuneRule Rule { get; init; } = TuneRule.Exactly;
    /// <summary>The wanted value, the largest number allowed, or the bits to clear, depending on <see cref="Rule"/>.</summary>
    public required string Value { get; init; }
    /// <summary>What Windows normally has here. Null means the value is normally not there. Used to put it back when no saved record is available.</summary>
    public string? Normal { get; init; }
    /// <summary>A key created only for this tweak, removed when it is put back and the value was not there before.</summary>
    public string RemoveKey { get; init; } = "";
}

/// <summary>One tweak, described for a person. Items are data (tune-catalog.json).</summary>
public sealed class TuneItem
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required TuneGroup Group { get; init; }
    public required TuneScope Scope { get; init; }
    public string Topic { get; init; } = "";
    public string Icon { get; init; } = "";
    /// <summary>What you notice once it is on.</summary>
    public required string Summary { get; init; }
    /// <summary>What you give up, or that you give up nothing.</summary>
    public required string Effect { get; init; }
    public TuneRisk Risk { get; init; } = TuneRisk.Safe;
    public TuneEffect Takes { get; init; } = TuneEffect.Now;
    /// <summary>The first Windows build that has this setting.</summary>
    public int MinBuild { get; init; }
    /// <summary>With <see cref="MinBuild"/>: the first update of that build that has it (the number after the dot in 26300.8697).</summary>
    public int MinRevision { get; init; }
    /// <summary>Windows Home ignores this setting.</summary>
    public bool NeedsPro { get; init; }
    public string[] Sources { get; init; } = [];
    public required TuneTarget[] Targets { get; init; }
}

public sealed record TuneValue(TuneKind Kind, string Text);

/// <summary>What the scanner reads from the PC, so tests can describe a PC without touching the registry.</summary>
public interface ITuneSystem
{
    /// <summary>The Windows edition id, such as "Professional" or "Core" (Home).</summary>
    string Edition { get; }
    int Build { get; }
    /// <summary>The update number of this build (the number after the dot in 26300.9818).</summary>
    int Revision { get; }
    /// <summary>The value, or null when it is not set. DWORD values are read as unsigned numbers.</summary>
    TuneValue? Read(TuneScope scope, string key, string name);
}

/// <summary>The few writes the page may make. Nothing here touches a value the catalog does not name.</summary>
public interface ITuneWriter
{
    void Set(TuneScope scope, string key, string name, TuneKind kind, string text);
    void Delete(TuneScope scope, string key, string name);
    void DeleteKey(TuneScope scope, string key);
}

/// <summary>How one value looked before it was changed, so it can be put back exactly (including "it was not there").</summary>
public sealed record TuneSaved(string Key, string Name, bool WasSet, TuneKind Kind, string Text, bool WasApplied);

public sealed record TuneRequest(string Id, bool Apply, IReadOnlyList<TuneSaved>? Saved = null);
public sealed record TuneOutcome(bool Ok, string? Error = null, string? Note = null, IReadOnlyList<TuneSaved>? Before = null);

/// <summary>Applies one change and checks it took. Implemented on this PC and, with administrator rights, by the helper.</summary>
public interface ITuneChanger
{
    TuneOutcome Change(TuneRequest request);
}

/// <summary>What TidyWindow changed, so it can all be put back later, even if the app was closed in between.</summary>
public sealed record TuneChange(string Id, string Name, DateTimeOffset When, IReadOnlyList<TuneSaved> Saved);

public static class TuneCatalog
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };
    private static IReadOnlyList<TuneItem>? loaded;

    public static IReadOnlyList<TuneItem> Load() => loaded ??= Read();

    private static IReadOnlyList<TuneItem> Read()
    {
        using var stream = typeof(TuneCatalog).Assembly.GetManifestResourceStream("tune-catalog.json") ?? throw new InvalidOperationException("The tweak catalog is missing from the app.");
        var file = JsonSerializer.Deserialize<CatalogFile>(stream, Options) ?? throw new InvalidOperationException("The tweak catalog is empty.");
        // A catalog that breaks the rules is a bug in the app, so it is refused before anything can be written.
        var problems = TuneGuard.Validate(file.Items);
        if (problems.Count > 0) throw new InvalidOperationException("The tweak catalog is not valid: " + string.Join(" ", problems));
        return file.Items;
    }

    private sealed class CatalogFile { public List<TuneItem> Items { get; init; } = []; }
}

/// <summary>
/// The rules for what may be written. They are a second list, separate from the catalog: a value is only ever written when both agree,
/// and the writer checks this again, so neither a catalog mistake nor a request from elsewhere can reach any other part of the registry.
/// </summary>
public static partial class TuneGuard
{
    private sealed record Place(TuneScope Scope, string Key, string[] Names);

    private const string Explorer = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const string Delivery = @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";
    public const string ClassicMenuClsid = @"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}";

    private static readonly Place[] Allowed =
    [
        new(TuneScope.User, @"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\Bags\AllFolders\Shell", ["FolderType"]),
        new(TuneScope.User, @"Control Panel\Desktop", ["AutoEndTasks", "WaitToKillAppTimeout", "MenuShowDelay"]),
        new(TuneScope.User, @"Control Panel\Desktop\WindowMetrics", ["MinAnimate"]),
        new(TuneScope.User, Explorer, ["TaskbarAnimations", "HideFileExt"]),
        new(TuneScope.User, Explorer + @"\TaskbarDeveloperSettings", ["TaskbarEndTask"]),
        new(TuneScope.User, Delivery, ["ContentDeliveryAllowed", "OemPreinstalledAppsEnabled", "PreInstalledAppsEnabled", "PreInstalledAppsEverEnabled", "SilentInstalledAppsEnabled", "SoftLandingEnabled", "SystemPaneSuggestionsEnabled",
            "SubscribedContent-310093Enabled", "SubscribedContent-338387Enabled", "SubscribedContent-338388Enabled", "SubscribedContent-338389Enabled", "SubscribedContent-338393Enabled", "SubscribedContent-353694Enabled", "SubscribedContent-353696Enabled"]),
        new(TuneScope.User, @"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", ["Enabled"]),
        new(TuneScope.User, @"Software\Microsoft\Windows\CurrentVersion\Privacy", ["TailoredExperiencesWithDiagnosticDataEnabled"]),
        new(TuneScope.User, @"Software\Microsoft\Windows\CurrentVersion\SearchSettings", ["IsWebSuggestionsEnabled"]),
        new(TuneScope.User, ClassicMenuClsid + @"\InprocServer32", [""]),
        new(TuneScope.User, @"Control Panel\Mouse", ["MouseSpeed", "MouseThreshold1", "MouseThreshold2"]),
        new(TuneScope.User, @"Control Panel\Accessibility\StickyKeys", ["Flags"]),
        new(TuneScope.User, @"Control Panel\Accessibility\ToggleKeys", ["Flags"]),
        new(TuneScope.User, @"Control Panel\Accessibility\Keyboard Response", ["Flags"]),
        new(TuneScope.Machine, @"SOFTWARE\Policies\Microsoft\Edge", ["StartupBoostEnabled", "BackgroundModeEnabled"]),
        new(TuneScope.Machine, @"SOFTWARE\Policies\Microsoft\Windows\System", ["PublishUserActivities", "UploadUserActivities", "EnableActivityFeed"]),
        new(TuneScope.Machine, @"SOFTWARE\Policies\Microsoft\Windows\CloudContent", ["DisableWindowsConsumerFeatures"]),
        new(TuneScope.Machine, @"SYSTEM\CurrentControlSet\Control\FileSystem", ["LongPathsEnabled"])
    ];

    // Keys that may be removed whole, because this page created them.
    private static readonly (TuneScope Scope, string Key)[] Removable = [(TuneScope.User, ClassicMenuClsid)];

    [GeneratedRegex(@"^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex Slug();

    public static bool AllowsValue(TuneScope scope, string key, string name) =>
        Allowed.Any(x => x.Scope == scope && string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase) && x.Names.Contains(name, StringComparer.OrdinalIgnoreCase));

    public static bool AllowsRemoval(TuneScope scope, string key) =>
        Removable.Any(x => x.Scope == scope && string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>Everything wrong with a catalog, as sentences. Empty when it is fine.</summary>
    public static List<string> Validate(IReadOnlyList<TuneItem> items)
    {
        var problems = new List<string>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var places = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            var who = $"“{item.Id}”";
            if (!Slug().IsMatch(item.Id)) problems.Add($"{who} needs a lowercase id with dashes.");
            if (!ids.Add(item.Id)) problems.Add($"{who} is listed twice.");
            if (string.IsNullOrWhiteSpace(item.Name) || string.IsNullOrWhiteSpace(item.Summary) || string.IsNullOrWhiteSpace(item.Effect)) problems.Add($"{who} needs a name, a summary and an effect.");
            if (item.Targets.Length == 0) problems.Add($"{who} changes nothing.");
            foreach (var target in item.Targets)
            {
                var where = $"{who}: {target.Key}\\{(target.Name.Length > 0 ? target.Name : "(Default)")}";
                if (!AllowsValue(item.Scope, target.Key, target.Name)) problems.Add($"{where} is not a value this page may write to.");
                if (!places.Add($"{item.Scope}|{target.Key}|{target.Name}")) problems.Add($"{where} is changed by two items.");
                if (target.Kind == TuneKind.Other) problems.Add($"{where} must be a number or text.");
                if (!Parses(target.Kind, target.Rule, target.Value)) problems.Add($"{where} has a wanted value that does not fit its type.");
                if (target.Normal is not null && !Parses(target.Kind, target.Rule, target.Normal)) problems.Add($"{where} has a normal value that does not fit its type.");
                else if (target.Normal is not null && TuneRules.IsApplied(target, new(target.Kind, target.Normal))) problems.Add($"{where} has a normal value that already counts as changed.");
                if (target.Rule == TuneRule.ClearBits && target.Normal is null) problems.Add($"{where} needs a normal value to clear bits from.");
                if (target.RemoveKey.Length > 0)
                {
                    if (!AllowsRemoval(item.Scope, target.RemoveKey)) problems.Add($"{where}: {target.RemoveKey} is not a key this page may remove.");
                    else if (!target.Key.StartsWith(target.RemoveKey + "\\", StringComparison.OrdinalIgnoreCase)) problems.Add($"{where} is not inside the key it would remove.");
                }
            }
            if (item.MinBuild < 0 || item.MinRevision < 0) problems.Add($"{who} has a negative build number.");
        }
        return problems;
    }

    private static bool Parses(TuneKind kind, TuneRule rule, string text) =>
        rule != TuneRule.Exactly || kind == TuneKind.Dword ? ulong.TryParse(text, out var number) && (kind != TuneKind.Dword || number <= uint.MaxValue) : kind == TuneKind.String;
}

/// <summary>Judging a value against a target, and working out what to write.</summary>
public static class TuneRules
{
    public static bool IsApplied(TuneTarget target, TuneValue? current)
    {
        if (current is null || current.Kind == TuneKind.Other) return false;
        switch (target.Rule)
        {
            case TuneRule.AtMost:
                return ulong.TryParse(current.Text, out var number) && number <= ulong.Parse(target.Value);
            case TuneRule.ClearBits:
                return ulong.TryParse(current.Text, out var bits) && (bits & ulong.Parse(target.Value)) == 0;
            default:
                return ulong.TryParse(current.Text, out var a) && ulong.TryParse(target.Value, out var b) ? a == b : string.Equals(current.Text, target.Value, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>The text to write so the target counts as applied.</summary>
    public static string Wanted(TuneTarget target, TuneValue? current)
    {
        if (target.Rule != TuneRule.ClearBits) return target.Value;
        var from = current is { Kind: not TuneKind.Other } && ulong.TryParse(current.Text, out var now) ? now : ulong.TryParse(target.Normal, out var normal) ? normal : 0;
        return (from & ~ulong.Parse(target.Value)).ToString();
    }
}

public sealed record TuneTargetState(TuneTarget Target, TuneValue? Current, bool Applied);

/// <summary>An item as it is on this PC right now. <see cref="Applied"/> means every setting already has the tweaked value.</summary>
public sealed record TuneEntry(TuneItem Item, bool Present, bool Applied, bool Partly, string? Lock, IReadOnlyList<TuneTargetState> Targets)
{
    public string Id => Item.Id;
    /// <summary>It can always be put back; it can be applied unless a rule says to leave it alone.</summary>
    public bool CanChange => Present && (Lock is null || Applied || Partly);
}

public sealed record TuneScanResult(IReadOnlyList<TuneEntry> Entries, int NotOnThisPc);

/// <summary>Reads what is on this PC and works out, for each catalog item, whether it applies here and whether it is already applied.</summary>
public static class TuneScan
{
    public static TuneScanResult Run(ITuneSystem system, IReadOnlyList<TuneItem> catalog)
    {
        var entries = new List<TuneEntry>();
        var missing = 0;
        foreach (var item in catalog)
        {
            var entry = Evaluate(system, item);
            if (entry.Present) entries.Add(entry); else missing++;
        }
        return new(entries, missing);
    }

    public static TuneEntry? Find(ITuneSystem system, IReadOnlyList<TuneItem> catalog, string id) =>
        catalog.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase)) is { } item && Evaluate(system, item) is { Present: true } entry ? entry : null;

    private static TuneEntry Evaluate(ITuneSystem system, TuneItem item)
    {
        if (system.Build < item.MinBuild || system.Build == item.MinBuild && system.Revision < item.MinRevision) return new(item, false, false, false, null, []);
        var states = item.Targets.Select(target =>
        {
            var current = system.Read(item.Scope, target.Key, target.Name);
            return new TuneTargetState(target, current, TuneRules.IsApplied(target, current));
        }).ToList();
        var applied = states.Count(x => x.Applied);
        string? rule = null;
        if (item.NeedsPro && system.Edition.StartsWith("Core", StringComparison.OrdinalIgnoreCase)) rule = "Windows Home does not support this setting.";
        else if (states.Any(x => x.Current?.Kind == TuneKind.Other)) rule = "This setting holds a kind of value this page does not change.";
        return new(item, true, applied == states.Count, applied > 0 && applied < states.Count, rule, states);
    }
}

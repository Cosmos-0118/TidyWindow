using System.Runtime.Versioning;

namespace TidyWindow.Next.Core;

/// <summary>Asks for everything Windows Installer keeps about one product ("product") or one Burn bundle ("bundle", with its Uninstall key in <see cref="Keys"/>).</summary>
public sealed record FootprintRequest(string Kind, string Code = "", string Context = "Machine", string Sid = "", IReadOnlyList<string>? Keys = null, bool Fresh = false);

/// <summary>One value inside a key that other products share, such as a product's link under an upgrade code or component.</summary>
public sealed record ValueRef(string Path, string Name);

/// <summary>Every registry key to remove and every shared value to take out. Unreadable lists what could not be looked at: a plan with any of those is never carried out.</summary>
public sealed record FootprintPlan(IReadOnlyList<string> Keys, IReadOnlyList<ValueRef> Values, IReadOnlyList<string> Unreadable, IReadOnlyList<string> Notes, bool Elsewhere = false)
{
    public bool IsEmpty => Keys.Count == 0 && Values.Count == 0;
    public static FootprintPlan Empty { get; } = new([], [], [], []);
}

/// <summary>
/// Works out the complete registration of one product, in every place Windows Installer keeps it (see docs/repair-research.md), without touching anything.
/// Keys that belong to the product alone are listed whole; links inside keys shared with other products are listed as single values.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class InstallerFootprint(RegistryRepair registry, string currentSid)
{
    private const string Cv = @"SOFTWARE\Microsoft\Windows\CurrentVersion";
    private readonly Dictionary<string, Dictionary<string, List<string>>> componentIndex = new(StringComparer.OrdinalIgnoreCase);

    public FootprintPlan Plan(FootprintRequest request)
    {
        if (request.Fresh) componentIndex.Clear();
        return request.Kind == "bundle" ? Bundle(request) : Product(request);
    }

    private FootprintPlan Product(FootprintRequest request)
    {
        var packed = InstallRepair.Pack(request.Code);
        var machine = request.Context == "Machine";
        var managed = request.Context == "Managed";
        var sid = request.Sid.Length > 0 ? request.Sid : currentSid;
        var sidKey = machine ? "S-1-5-18" : sid;
        var user = sid == currentSid ? "HKCU" : @"HKU\" + sid;
        var userData = $@"HKLM\{Cv}\Installer\UserData\{sidKey}";
        var keys = new List<string>(); var values = new List<ValueRef>(); var unreadable = new List<string>(); var notes = new List<string>();

        List<string> own, upgrade, patchRoots;
        if (machine)
        {
            own = [$@"HKLM\SOFTWARE\Classes\Installer\Products\{packed}", $@"HKLM\SOFTWARE\Classes\Installer\Features\{packed}", $@"HKLM\{Cv}\Installer\Products\{packed}", $@"HKLM\{Cv}\Installer\Features\{packed}"];
            upgrade = [@"HKLM\SOFTWARE\Classes\Installer\UpgradeCodes", $@"HKLM\{Cv}\Installer\UpgradeCodes"];
            patchRoots = [@"HKLM\SOFTWARE\Classes\Installer\Patches"];
        }
        else if (managed)
        {
            var root = $@"HKLM\{Cv}\Installer\Managed\{sidKey}\Installer";
            own = [$@"{root}\Products\{packed}", $@"{root}\Features\{packed}"];
            upgrade = [$@"{root}\UpgradeCodes"];
            patchRoots = [];
        }
        else
        {
            own = [$@"{user}\SOFTWARE\Microsoft\Installer\Products\{packed}", $@"{user}\SOFTWARE\Microsoft\Installer\Features\{packed}", $@"{user}\SOFTWARE\Classes\Installer\Products\{packed}", $@"{user}\SOFTWARE\Classes\Installer\Features\{packed}"];
            upgrade = [$@"{user}\SOFTWARE\Microsoft\Installer\UpgradeCodes", $@"{user}\SOFTWARE\Classes\Installer\UpgradeCodes", $@"{user}\{Cv}\Installer\UpgradeCodes"];
            patchRoots = [$@"{user}\SOFTWARE\Microsoft\Installer\Patches", $@"{user}\SOFTWARE\Classes\Installer\Patches"];
        }

        var data = $@"{userData}\Products\{packed}";
        // The patches this product wears, noted before its record goes. A patch another product also wears stays.
        var patches = Names($@"{data}\Patches", unreadable);
        if (patches.Count > 0)
        {
            var worn = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var other in Names($@"{userData}\Products", unreadable))
                if (!string.Equals(other, packed, StringComparison.OrdinalIgnoreCase)) worn.UnionWith(Names($@"{userData}\Products\{other}\Patches", unreadable));
            foreach (var patch in patches)
            {
                if (worn.Contains(patch)) { notes.Add($"Patch {patch} is also used by another program, so it stays."); continue; }
                foreach (var key in new[] { $@"{userData}\Patches\{patch}" }.Concat(patchRoots.Select(x => $@"{x}\{patch}"))) Add(keys, key, unreadable);
            }
        }

        foreach (var key in own) Add(keys, key, unreadable);
        Add(keys, data, unreadable);

        // The same product code registered in another context is still a working install of it: its shared links and list entry stay.
        var elsewhere = RegisteredElsewhere(packed, machine, managed, user, sid);
        if (elsewhere) notes.Add("The program is also registered another way, so its list entry and shared links stay.");
        else
        {
            foreach (var container in upgrade)
                foreach (var link in Names(container, unreadable))
                    if (Values($@"{container}\{link}", unreadable).Any(x => string.Equals(x, packed, StringComparison.OrdinalIgnoreCase)))
                        values.Add(new($@"{container}\{link}", packed));
            foreach (var arp in Uninstall(request.Code, machine || managed, user)) Add(keys, arp, unreadable);
        }

        foreach (var component in Components($@"{userData}\Components", unreadable).GetValueOrDefault(packed) ?? [])
            values.Add(new($@"{userData}\Components\{component}", packed));

        foreach (var extra in request.Keys ?? []) if (registry.IsAllowed(extra)) Add(keys, extra, unreadable);
        return new(keys.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), values.Distinct().ToList(), unreadable, notes, elsewhere);
    }

    private IEnumerable<string> Uninstall(string code, bool machine, string user)
    {
        var hive = machine ? "HKLM" : user;
        yield return $@"{hive}\{Cv}\Uninstall\{code}";
        yield return $@"{hive}\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\{code}";
    }

    private bool RegisteredElsewhere(string packed, bool machine, bool managed, string user, string sid)
    {
        var places = new List<string>();
        if (!machine) places.AddRange([$@"HKLM\SOFTWARE\Classes\Installer\Products\{packed}", $@"HKLM\{Cv}\Installer\UserData\S-1-5-18\Products\{packed}"]);
        if (machine || managed) places.Add($@"HKCU\SOFTWARE\Microsoft\Installer\Products\{packed}");
        if (!machine && !managed) places.Add($@"HKLM\{Cv}\Installer\Managed\{sid}\Installer\Products\{packed}");
        foreach (var place in places) { try { if (registry.Exists(place)) return true; } catch (UnauthorizedAccessException) { return true; } }
        return false;
    }

    /// <summary>A Burn bundle that is gone: its list entry and the dependency records that name it, unless another bundle still needs it.</summary>
    private FootprintPlan Bundle(FootprintRequest request)
    {
        var keys = new List<string>(); var unreadable = new List<string>(); var notes = new List<string>();
        foreach (var arp in request.Keys ?? [])
        {
            if (!Check(arp, unreadable)) continue;
            keys.Add(arp);
            var provider = Text(arp, "BundleProviderKey", unreadable);
            if (string.IsNullOrWhiteSpace(provider)) provider = arp[(arp.LastIndexOf('\\') + 1)..];
            var hive = arp[..arp.IndexOf('\\')];
            var root = $@"{hive}\SOFTWARE\Classes\Installer\Dependencies";
            var own = $@"{root}\{provider}";
            // Burn names the bundle by its own id (the list entry's key name) in every Dependents list, its own provider's included.
            var id = arp[(arp.LastIndexOf('\\') + 1)..];
            var names = new[] { provider, id }.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (Check(own, unreadable))
            {
                if (Names($@"{own}\Dependents", unreadable).All(x => names.Contains(x, StringComparer.OrdinalIgnoreCase))) keys.Add(own);
                else notes.Add($"Another program still depends on {provider}, so that record stays.");
            }
            foreach (var other in Names(root, unreadable))
                if (!string.Equals(other, provider, StringComparison.OrdinalIgnoreCase))
                    foreach (var name in names)
                        if (Check($@"{root}\{other}\Dependents\{name}", unreadable)) keys.Add($@"{root}\{other}\Dependents\{name}");
        }
        return new(keys.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), [], unreadable, notes);
    }

    // The component lists of a whole machine run to thousands of keys, so they are read once and kept until a fresh plan is asked for.
    private Dictionary<string, List<string>> Components(string container, List<string> unreadable)
    {
        if (componentIndex.TryGetValue(container, out var cached)) return cached;
        var index = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var component in Names(container, unreadable))
            foreach (var product in Values($@"{container}\{component}", unreadable))
                if (product.Length == 32) { if (!index.TryGetValue(product, out var list)) index[product] = list = []; list.Add(component); }
        componentIndex[container] = index;
        return index;
    }

    private void Add(List<string> keys, string key, List<string> unreadable) { if (Check(key, unreadable)) keys.Add(key); }

    private bool Check(string key, List<string> unreadable)
    {
        try { return registry.Exists(key); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException) { unreadable.Add(key); return false; }
    }

    private IReadOnlyList<string> Names(string key, List<string> unreadable)
    {
        try { return registry.SubKeys(key); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException) { unreadable.Add(key); return []; }
    }

    private IReadOnlyList<string> Values(string key, List<string> unreadable)
    {
        try { return registry.ValueNames(key); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException) { unreadable.Add(key); return []; }
    }

    private string? Text(string key, string name, List<string> unreadable)
    {
        try { return registry.ReadText(key, name); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException) { unreadable.Add(key); return null; }
    }
}

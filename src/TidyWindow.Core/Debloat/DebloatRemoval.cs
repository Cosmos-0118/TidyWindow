namespace TidyWindow.Next.Core;

/// <summary>The part of the Windows app registry that the removal steps touch. Every name given here has already passed <see cref="DebloatGuard.IsToken"/>.</summary>
public interface IAppxStore
{
    /// <summary>The account ids Windows keeps app records for.</summary>
    IReadOnlyList<string> UserSids();
    /// <summary>Removes the record that marks a package as part of Windows. False if there was none.</summary>
    bool DeleteInbox(string fullName);
    /// <summary>Adds the temporary "end of life" mark that lets Windows remove a locked package. False if it was already there.</summary>
    bool CreateEndOfLife(string sid, string fullName);
    void DeleteEndOfLife(string sid, string fullName);
    bool HasDeprovisioned(string family);
    /// <summary>Tells Windows not to install this app for new accounts again.</summary>
    void CreateDeprovisioned(string family);
    void DeleteDeprovisioned(string family);
}

/// <summary>The commands that remove packages. Each returns what went wrong, or null; the caller reads the PC again to see whether it worked.</summary>
public interface IAppxOps
{
    /// <summary>Unlocks a package Windows marks as not removable.</summary>
    string? ClearNonRemovable(string family);
    /// <summary>Removes a package for each account that has it, then for all accounts.</summary>
    string? RemovePackage(string fullName, IReadOnlyList<string> sids);
    /// <summary>Stops Windows installing a package for new accounts.</summary>
    string? RemoveProvisioned(string packageName);
}

/// <summary>
/// Removes an app so that it stays removed. A plain Remove-AppxPackage leaves the app installed for new accounts, fails on apps Windows marks as locked,
/// and lets the next update bring it back. This does all of it, in an order that Windows accepts, and undoes what it added if the app would not go.
/// </summary>
public sealed class AppxRemover(IDebloatSystem system, IAppxStore store, IAppxOps ops)
{
    /// <summary>The account Windows itself uses. A locked package also needs its end-of-life mark here.</summary>
    public const string SystemSid = "S-1-5-18";

    public DebloatOutcome Remove(DebloatEntry entry)
    {
        var item = entry.Item;
        // The helper trusts nothing it was handed: everything is judged again against the catalog and the guard.
        foreach (var package in entry.Packages)
            if (!DebloatGuard.Covers(item, package.Name, package.Framework) || !DebloatGuard.IsToken(package.FullName) || !DebloatGuard.IsToken(package.FamilyName))
                return new(false, $"{package.Name} is something Windows needs and was not removed.");
        foreach (var staged in entry.Provisioned)
            if (!DebloatGuard.Covers(item, staged.DisplayName, false) || !DebloatGuard.IsToken(staged.PackageName))
                return new(false, $"{staged.DisplayName} is something Windows needs and was not removed.");
        if (entry.Packages.Any(x => x.Users.Any(u => !DebloatGuard.IsSid(u)))) return new(false, "An account id was not understood.");

        var families = entry.Packages.Select(x => x.FamilyName).Concat(StagedFamilies(entry)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var userSids = store.UserSids().Where(DebloatGuard.IsSid).ToList();
        var madeDeprovisioned = new List<string>();
        var madeEndOfLife = new List<(string Sid, string Name)>();
        var errors = new List<string>();
        try
        {
            // Keep Windows from putting the app back for new accounts or after an update.
            foreach (var family in families.Where(DebloatGuard.IsToken))
                if (!store.HasDeprovisioned(family)) { store.CreateDeprovisioned(family); madeDeprovisioned.Add(family); }

            var unlocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var package in entry.Packages)
            {
                if (package.NonRemovable && unlocked.Add(package.FamilyName) && ops.ClearNonRemovable(package.FamilyName) is { } clearFailed) errors.Add(clearFailed);
                store.DeleteInbox(package.FullName);
                foreach (var sid in package.Users.Append(SystemSid).Distinct(StringComparer.OrdinalIgnoreCase))
                    if (store.CreateEndOfLife(sid, package.FullName)) madeEndOfLife.Add((sid, package.FullName));
                if (ops.RemovePackage(package.FullName, package.Users) is { } failed) errors.Add(failed);
            }

            foreach (var staged in entry.Provisioned)
            {
                foreach (var sid in userSids.Append(SystemSid).Distinct(StringComparer.OrdinalIgnoreCase))
                    if (store.CreateEndOfLife(sid, staged.PackageName)) madeEndOfLife.Add((sid, staged.PackageName));
                if (ops.RemoveProvisioned(staged.PackageName) is { } failed) errors.Add(failed);
            }
        }
        finally
        {
            // Left behind, these marks can stop Windows Update from servicing the package, so they always go.
            foreach (var (sid, name) in madeEndOfLife) store.DeleteEndOfLife(sid, name);
        }

        var left = StillThere(entry);
        if (left.Count > 0)
        {
            // Only the marks for apps that are still installed are taken back; the others are gone for good and stay blocked.
            foreach (var family in madeDeprovisioned.Where(f => left.Contains(f, StringComparer.OrdinalIgnoreCase))) store.DeleteDeprovisioned(family);
            var why = errors.FirstOrDefault(x => x.Length > 0);
            return new(false, $"{left.Count} app{(left.Count == 1 ? "" : "s")} could not be removed.{(why is null ? "" : " " + Trim(why))}");
        }
        return new(true, null, entry.Provisioned.Count > 0 || entry.Packages.Any(x => x.NonRemovable) ? "Windows will not install it again for new accounts." : null, families);
    }

    /// <summary>A package's family is its name and its publisher id, which is the last part of its full name (Name_Version_Arch_Resource_Publisher).</summary>
    public static string FamilyOf(string fullName)
    {
        var parts = fullName.Split('_');
        return parts.Length >= 2 ? parts[0] + "_" + parts[^1] : fullName;
    }

    private static IEnumerable<string> StagedFamilies(DebloatEntry entry) => entry.Provisioned.Select(x => FamilyOf(x.PackageName));

    /// <summary>The names of what is still installed or staged after the removal.</summary>
    private List<string> StillThere(DebloatEntry entry)
    {
        var item = entry.Item;
        var names = entry.Packages.Select(x => x.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var left = system.Packages().Where(x => names.Contains(x.FullName) && x.Users.Count > 0).Select(x => x.FamilyName).ToList();
        left.AddRange(system.Provisioned().Where(x => DebloatGuard.Covers(item, x.DisplayName, false) && entry.Provisioned.Any(p => string.Equals(p.PackageName, x.PackageName, StringComparison.OrdinalIgnoreCase))).Select(x => x.DisplayName));
        return left.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string Trim(string text) => text.Length <= 220 ? text : text[..220] + "…";

    /// <summary>Lets Windows install the app for new accounts again. The app itself comes back from the Store.</summary>
    public DebloatOutcome Allow(DebloatItem item, IReadOnlyList<string> families)
    {
        var done = new List<string>();
        foreach (var family in families)
        {
            if (!DebloatGuard.IsToken(family) || !DebloatGuard.Covers(item, DebloatGuard.NameOf(family), false)) return new(false, "That app does not belong to this item.");
            store.DeleteDeprovisioned(family);
            done.Add(family);
        }
        return new(true, null, null, done);
    }
}

/// <summary>The steps for OneDrive, Edge and Recall. Each returns what went wrong, or null.</summary>
public interface IDebloatPlaybooks
{
    string? Remove(DebloatKind kind);
    string? Restore(DebloatKind kind);
}

/// <summary>
/// Makes one removal or put-back on this PC, after checking again that the item may be removed, and reads the PC back to be sure it worked.
/// This is the part that runs with administrator rights.
/// </summary>
public sealed class LocalDebloatChanger(IDebloatSystem system, AppxRemover appx, IDebloatPlaybooks playbooks, IReadOnlyList<DebloatItem> catalog) : IDebloatChanger
{
    public DebloatOutcome Change(DebloatRequest request)
    {
        var item = catalog.FirstOrDefault(x => string.Equals(x.Id, request.Id, StringComparison.OrdinalIgnoreCase));
        if (item is null) return new(false, "That item is not in the catalog.");
        return request.Restore ? Restore(item, request) : Remove(item);
    }

    private DebloatOutcome Remove(DebloatItem item)
    {
        var entry = DebloatScan.Find(system, catalog, item.Id);
        if (entry is null) return new(true, "It was already gone.");
        if (!entry.CanRemove) return new(false, entry.Lock);
        if (item.Kind == DebloatKind.Appx) return appx.Remove(entry);

        string? error;
        try { error = playbooks.Remove(item.Kind); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or System.Security.SecurityException) { error = ex.Message; }
        // Recall is a Windows feature that stays installed when it is switched off, so it cannot be told apart by looking again; the others can.
        var after = DebloatScan.Find(system, catalog, item.Id);
        if (item.Kind != DebloatKind.Recall && after is not null) return new(false, error ?? $"{item.Name} is still on this PC.");
        if (item.Kind == DebloatKind.Recall && error is not null) return new(false, error);
        return new(true, null, item.Kind == DebloatKind.Recall ? "Restart Windows to finish." : null, []);
    }

    private DebloatOutcome Restore(DebloatItem item, DebloatRequest request)
    {
        if (item.Kind == DebloatKind.Appx) return appx.Allow(item, request.Families ?? []);
        string? error;
        try { error = playbooks.Restore(item.Kind); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or System.Security.SecurityException) { error = ex.Message; }
        return error is null ? new(true) : new(false, error);
    }
}

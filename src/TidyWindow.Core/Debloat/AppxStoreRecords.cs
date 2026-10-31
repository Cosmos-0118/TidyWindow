namespace TidyWindow.Next.Core;

/// <summary>
/// Reads the package records Windows keeps in the registry (HKLM\...\Appx\AppxAllUserStore). Anyone may read them, so the page can see every app on the PC
/// without administrator rights: InboxApplications lists what Windows installs for new accounts, Applications what is installed for any account.
/// A record is a full package name and the path to its manifest.
/// </summary>
public static class AppxStoreRecords
{
    /// <summary>The folder that holds a package's manifest and logo. A bundle's manifest sits one folder deeper, in AppxMetadata.</summary>
    public static string FolderOf(string manifestPath)
    {
        if (string.IsNullOrWhiteSpace(manifestPath)) return "";
        var folder = Path.GetDirectoryName(manifestPath.Trim().Trim('"')) ?? "";
        return folder.EndsWith("AppxMetadata", StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(folder) ?? "" : folder;
    }

    /// <summary>The package a full name (Name_Version_Architecture_Resource_Publisher) stands for, or null for anything else, such as a language pack.</summary>
    public static AppxFact? Package(string fullName, string manifestPath)
    {
        if (!IsMainPackage(fullName)) return null;
        // Without administrator rights Windows does not say who has a package or whether it is locked, so the fact says neither.
        return new(DebloatGuard.NameOf(fullName), fullName, AppxRemover.FamilyOf(fullName), false, false, [], FolderOf(manifestPath));
    }

    public static ProvisionedFact? Staged(string fullName, string manifestPath) =>
        IsMainPackage(fullName) ? new(DebloatGuard.NameOf(fullName), fullName, FolderOf(manifestPath)) : null;

    // The resource part is empty or "~" for an app and names a language or scale for a resource package, which only belongs to an app that is listed itself.
    private static bool IsMainPackage(string fullName)
    {
        if (!DebloatGuard.IsToken(fullName)) return false;
        var parts = fullName.Split('_');
        return parts.Length >= 5 && parts[0].Length > 0 && (parts[3].Length == 0 || parts[3] == "~");
    }

    /// <summary>
    /// Adds the apps of the store that Get-AppxPackage did not list, such as those installed only for another account. They are told apart by family, not by full name:
    /// the store records a bundle (…_neutral_~_…) where Windows lists the package inside it (…_x64__…), and both are the same app.
    /// </summary>
    public static IReadOnlyList<AppxFact> Merge(IReadOnlyList<AppxFact> listed, IEnumerable<AppxFact> fromStore)
    {
        var known = listed.Select(x => x.FamilyName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return listed.Concat(fromStore.Where(x => known.Add(x.FamilyName))).ToList();
    }
}

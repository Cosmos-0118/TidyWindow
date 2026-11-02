namespace TidyWindow.Next.Core;

public static partial class InstallRepair
{
    /// <summary>Icon folders Windows Installer kept for programs it no longer has: nothing reads them, and they only clutter C:\Windows\Installer.</summary>
    private static IEnumerable<RepairFinding> OrphanedInstallerFindings(IInstallSystem system)
    {
        var folders = system.OrphanInstallerFolders();
        if (folders.Count == 0) yield break;
        yield return new("installer-files", RepairKind.InstallerFiles, folders.Count == 1 ? "Leftover installer folder of a removed program" : $"{folders.Count} leftover installer folders of removed programs",
            "Windows Installer keeps an icon folder for every program it installs. These belong to programs it has no record of any more, so nothing uses them. They are set aside, not deleted, so Undo can put them back.",
            string.Join(", ", folders.Take(3).Select(Path.GetFileName)) + (folders.Count > 3 ? $" and {folders.Count - 3} more" : ""),
            RepairLevel.Recommended, [], Folders: folders, MachineFolders: folders.Any(x => RepairFolders.IsMachine(x)));
    }

    /// <summary>The product codes a removed program was registered under: what its installer-cache folders are named after. A Burn bundle is named by its own entry key.</summary>
    public static IReadOnlyList<string> CodesOf(UninstallEntry? entry, MsiProduct? product, bool bundle)
    {
        var codes = new List<string>();
        if (product is not null) codes.Add(product.Code);
        if (entry is not null)
        {
            if (ProductCode(entry) is { } code) codes.Add(code);
            if (bundle && ProductGuid().IsMatch(entry.KeyName)) codes.Add(entry.KeyName.ToUpperInvariant());
        }
        return codes.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// A program's own folder, when it is one that may be set aside: directly under Program Files or the per-user Programs folder, and not the install location of any program still listed.
    /// </summary>
    public static IReadOnlyList<string> ProgramFolders(IInstallSystem system, UninstallEntry? entry, MsiProduct? product)
    {
        var marks = MarksOf(system, entry, product);
        var others = system.Entries().Where(x => entry is null || !string.Equals(x.Key, entry.Key, StringComparison.OrdinalIgnoreCase))
            .Select(x => system.Expand(x.InstallLocation.Trim().Trim('"')).TrimEnd('\\')).Where(x => x.Length > 0).ToList();
        return marks.Folders
            .Where(folder => RepairFolders.Classify(folder) == FolderKind.ProgramFolder && system.FolderExists(folder))
            .Where(folder => !others.Any(other => string.Equals(other, folder, StringComparison.OrdinalIgnoreCase) || ShortcutPlaces.Under(other, folder) || ShortcutPlaces.Under(folder, other)))
            .ToList();
    }
}

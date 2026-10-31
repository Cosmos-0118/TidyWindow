using System.Text.RegularExpressions;

namespace TidyWindow.Next.Core;

/// <summary>
/// What an installed app's identity already says about where its icon and its publisher can be found, so the icon and publisher finders
/// (built for startup items) can be asked about a package manager's row too.
/// </summary>
public static partial class InstalledHints
{
    [GeneratedRegex(@"^ARP\\(?:Machine|User)\\X(?:64|86)\\(\{[0-9A-Fa-f\-]{36}\})$")]
    private static partial Regex ProductKey();

    [GeneratedRegex(@"^MSIX\\([A-Za-z0-9.\-]+_[A-Za-z0-9.\-]+_[A-Za-z0-9]*_[A-Za-z0-9]*_[A-Za-z0-9]+)$")]
    private static partial Regex StorePackage();

    /// <summary>What is known about the app's icon: its name, the Windows Installer product when its id is one, and where Scoop keeps it.</summary>
    public static IconQuery IconFor(ManagedPackage package) => new(package.Name,
        InstallLocation: package.Manager == ManagerKind.Scoop ? ScoopFolder(package) : "",
        ProductCode: ProductKey().Match(package.Id) is { Success: true } product ? product.Groups[1].Value : "");

    /// <summary>
    /// A path inside the Store package, which is all the publisher finder needs to read the package's manifest; "" for anything that is not a Store app.
    /// The file itself need not exist.
    /// </summary>
    public static string PackagePath(ManagedPackage package) =>
        StorePackage().Match(package.Id) is { Success: true } store ? $@"%ProgramFiles%\WindowsApps\{store.Groups[1].Value}\app.exe" : "";

    private static string ScoopFolder(ManagedPackage package) =>
        package.Id.IndexOfAny(['\\', '/', ':']) >= 0 ? "" : $@"{(package.Global ? "%ProgramData%" : "%UserProfile%")}\scoop\apps\{package.Id}\current";
}

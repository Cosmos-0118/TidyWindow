using System.Text.RegularExpressions;

namespace TidyWindow.Next.Core;

/// <summary>A folder as it stood before a repair set it aside: where it was, and where it is kept so Undo can move it back.</summary>
public sealed record FolderBackup(string Original, string Stored);

/// <summary>
/// Sets folders aside and puts them back: the signed-in user's own, or the elevated helper. Setting aside is a move into the repair's own backup folder, not a delete,
/// so it costs no copy (on one drive) and Undo restores the folder exactly as it was, with its permissions.
/// </summary>
public interface IFolderEditor
{
    /// <summary>Moves a folder into <paramref name="vault"/> and says where it went; nothing when it is already gone.</summary>
    FolderBackup? SetAside(string path, string vault);
    /// <summary>Moves a set aside folder back to where it was. Refuses when something is there again.</summary>
    void PutBack(FolderBackup backup);
    /// <summary>Sets a folder aside again, to the very place it was kept before: what a failed undo does to the folders it already put back.</summary>
    void TakeAgain(FolderBackup backup);
    /// <summary>Deletes a set aside folder for good, once the person decided they do not want it back.</summary>
    void Discard(FolderBackup backup);
}

public enum FolderKind { None, InstallerCache, ProgramFolder, RollbackFolder }

/// <summary>
/// The only folders a repair ever sets aside: what Windows Installer and Burn leave in their own caches, the hidden Config.Msi, and (when the person asks)
/// a program's own folder directly under Program Files or the per-user Programs folder. Anything else is refused, however it is asked for.
/// </summary>
public static partial class RepairFolders
{
    [GeneratedRegex(@"^\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}$")]
    private static partial Regex Guid();
    [GeneratedRegex(@"^\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}(?:v[^\\/]+)?$")]
    private static partial Regex CacheName();

    // Folders under Program Files that belong to Windows or are shared by every program, so they are never a program's own.
    private static readonly string[] Shared =
    [
        "Common Files", "WindowsApps", "ModifiableWindowsApps", "Windows Defender", "Windows Defender Advanced Threat Protection", "Windows Mail", "Windows Media Player",
        "Windows NT", "Windows Photo Viewer", "Windows Portable Devices", "Windows Security", "Windows Sidebar", "WindowsPowerShell", "Internet Explorer", "Microsoft.NET",
        "dotnet", "Reference Assemblies", "MSBuild", "Uninstall Information", "Package Cache", "Microsoft Update Health Tools", "Windows Kits", "Application Verifier"
    ];

    private static string? Env(string name) => Environment.GetEnvironmentVariable(name);

    private static string? Clean(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path)) return null;
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); } catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    private static bool Is(string? a, string? b) => a is not null && b is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    public static FolderKind Classify(string path, Func<string, string?>? env = null)
    {
        env ??= Env;
        var full = Clean(path);
        if (full is null) return FolderKind.None;
        var name = Path.GetFileName(full);
        var parent = Path.GetDirectoryName(full);
        if (name.Length == 0 || parent is null) return FolderKind.None;

        var windows = Clean(env("SystemRoot") ?? env("WINDIR"));
        var programData = Clean(env("ProgramData"));
        var local = Clean(env("LOCALAPPDATA"));
        var roaming = Clean(env("APPDATA"));
        var drive = env("SystemDrive");

        if (CacheName().IsMatch(name) && ((programData is not null && Is(parent, Path.Combine(programData, "Package Cache"))) || (local is not null && Is(parent, Path.Combine(local, "Package Cache"))))) return FolderKind.InstallerCache;
        if (Guid().IsMatch(name) && ((windows is not null && Is(parent, Path.Combine(windows, "Installer"))) || (roaming is not null && Is(parent, Path.Combine(roaming, "Microsoft", "Installer"))))) return FolderKind.InstallerCache;
        if (!string.IsNullOrEmpty(drive) && name.Equals("Config.Msi", StringComparison.OrdinalIgnoreCase) && Is(parent, Clean(drive + "\\"))) return FolderKind.RollbackFolder;

        var bases = new[] { env("ProgramFiles"), env("ProgramFiles(x86)"), env("ProgramW6432"), local is null ? null : Path.Combine(local, "Programs") }.Select(Clean).Where(x => x is not null).ToList();
        foreach (var root in bases)
        {
            if (Is(parent, root)) return Shared.Contains(name, StringComparer.OrdinalIgnoreCase) ? FolderKind.None : FolderKind.ProgramFolder;
            // A vendor folder holding the program's own ("Geeks3D\FurMark2_x64"): the vendor folder may not be a shared one.
            if (Path.GetDirectoryName(parent) is { } grandparent && Is(grandparent, root) && !Shared.Contains(Path.GetFileName(parent), StringComparer.OrdinalIgnoreCase) && !Shared.Contains(name, StringComparer.OrdinalIgnoreCase)) return FolderKind.ProgramFolder;
        }
        return FolderKind.None;
    }

    /// <summary>Folders under the signed-in account's own profile are changed by the account itself; the rest need the administrator helper.</summary>
    public static bool IsMachine(string path, Func<string, string?>? env = null)
    {
        env ??= Env;
        var full = Clean(path);
        var profile = Clean(env("USERPROFILE"));
        return full is null || profile is null || !(full.StartsWith(profile + "\\", StringComparison.OrdinalIgnoreCase));
    }
}

public sealed class FileFolderEditor(Func<string, bool> allowed) : IFolderEditor
{
    private const string Vault = @"\TidyWindow.Next\RepairBackups\SetAside\";

    public static FileFolderEditor User() => new(path => RepairFolders.Classify(path) != FolderKind.None && !RepairFolders.IsMachine(path));
    public static FileFolderEditor Machine() => new(path => RepairFolders.Classify(path) != FolderKind.None && RepairFolders.IsMachine(path));

    public bool Allows(string path)
    {
        try { return allowed(Path.GetFullPath(path)); } catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    private string Checked(string path)
    {
        string full;
        try { full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); } catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { throw new IOException("That is not a path TidyWindow can change."); }
        if (!allowed(full)) throw new IOException("TidyWindow does not set that folder aside.");
        return full;
    }

    private static string InVault(string path)
    {
        var full = Path.GetFullPath(path);
        if (!full.Contains(Vault, StringComparison.OrdinalIgnoreCase)) throw new IOException("That is not where TidyWindow keeps what it sets aside.");
        return Path.TrimEndingDirectorySeparator(full);
    }

    public FolderBackup? SetAside(string path, string vault)
    {
        var full = Checked(path);
        if (!Directory.Exists(full)) return null;
        if ((new DirectoryInfo(full).Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("That folder is a link to somewhere else, so it was left alone.");
        var stored = InVault(Path.Combine(vault, System.Guid.NewGuid().ToString("N")[..8], Path.GetFileName(full)));
        Directory.CreateDirectory(Path.GetDirectoryName(stored)!);
        try { Move(full, stored); }
        catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows() && RepairFolders.Classify(full) == FolderKind.RollbackFolder)
        {
            WindowsInstallerOps.UnlockRollbackFolder(full);
            Move(full, stored);
        }
        return new(full, stored);
    }

    public void PutBack(FolderBackup backup)
    {
        var original = Checked(backup.Original);
        var stored = InVault(backup.Stored);
        if (!Directory.Exists(stored)) throw new IOException($"{Path.GetFileName(original)} is no longer where it was kept.");
        if (Directory.Exists(original)) throw new IOException($"A folder named {Path.GetFileName(original)} is there again, so it was not overwritten.");
        Directory.CreateDirectory(Path.GetDirectoryName(original)!);
        Move(stored, original);
    }

    public void TakeAgain(FolderBackup backup)
    {
        var original = Checked(backup.Original);
        var stored = InVault(backup.Stored);
        if (!Directory.Exists(original)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(stored)!);
        Move(original, stored);
    }

    public void Discard(FolderBackup backup)
    {
        // Both ends are checked: a stored folder only ever goes away for a backup whose original place this editor is allowed to change.
        Checked(backup.Original);
        var stored = InVault(backup.Stored);
        if (!Directory.Exists(stored)) return;
        // The folder keeps the permissions it had in Program Files, so files read-only for everyone are made writable first.
        foreach (var file in new DirectoryInfo(stored).EnumerateFiles("*", SearchOption.AllDirectories))
            if ((file.Attributes & FileAttributes.ReadOnly) != 0) file.Attributes &= ~FileAttributes.ReadOnly;
        Directory.Delete(stored, true);
        if (Path.GetDirectoryName(stored) is { } holder && Directory.Exists(holder) && !Directory.EnumerateFileSystemEntries(holder).Any()) Directory.Delete(holder);
    }

    /// <summary>A rename on one drive (permissions stay); a copy and delete across drives, where a folder cannot be renamed.</summary>
    private static void Move(string from, string to)
    {
        try { Directory.Move(from, to); return; }
        catch (IOException) when (!string.Equals(Path.GetPathRoot(from), Path.GetPathRoot(to), StringComparison.OrdinalIgnoreCase)) { }
        try { Copy(new DirectoryInfo(from), to); }
        catch { try { Directory.Delete(to, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } throw; }
        Directory.Delete(from, true);
    }

    private static void Copy(DirectoryInfo from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in from.EnumerateFiles()) file.CopyTo(Path.Combine(to, file.Name), false);
        foreach (var folder in from.EnumerateDirectories())
        {
            if ((folder.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException($"{folder.Name} is a link to somewhere else, so the folder cannot be moved to another drive.");
            Copy(folder, Path.Combine(to, folder.Name));
        }
        File.SetAttributes(to, from.Attributes);
    }
}

/// <summary>How big a folder in the quarantine is, for the list. Whatever cannot be read is left out, so the number is never more than what is really there.</summary>
public static class FolderSize
{
    public static long Of(string folder)
    {
        try
        {
            if (!Directory.Exists(folder)) return 0;
            return new DirectoryInfo(folder).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }).Sum(x => x.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }
}

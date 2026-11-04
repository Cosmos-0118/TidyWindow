namespace TidyWindow.Next.Core;

public enum ShortcutArea { StartMenu, Desktop, Taskbar, SendTo }

/// <summary>A folder Windows lists shortcuts from. <see cref="Machine"/> ones are shared by every account and only an administrator can change them.</summary>
public sealed record ShortcutRoot(string Path, ShortcutArea Area, bool Machine);

/// <summary>
/// What one .lnk or .url file says about itself, read without resolving it (resolving searches the disk and can show a dialog).
/// <see cref="Target"/> is the stored path with its variables still in it, so they are expanded for the signed-in account and not for whoever read the file.
/// </summary>
public sealed record ShortcutFile(string Path, string Target = "", string Arguments = "", string WorkingDirectory = "", string Icon = "", string AppId = "", string Product = "",
    bool ShellTarget = false, bool Unreadable = false, string Url = "", bool Unparsed = false)
{
    public bool IsUrl => Path.EndsWith(".url", StringComparison.OrdinalIgnoreCase);
}

/// <summary>A folder under a Start menu, with the full paths of what is directly in it.</summary>
public sealed record ShortcutFolder(string Path, IReadOnlyList<string> Files, IReadOnlyList<string> Folders);

/// <summary>A shortcut that does not work any more, and why. A companion works on its own but belongs to a program whose shortcuts are all gone (its web page, its help link).</summary>
public sealed record BrokenLink(ShortcutFile File, string Reason, bool Companion = false);

/// <summary>Everything stale that belongs together: one program's Start menu folder, or a single loose shortcut. <see cref="Paths"/> lists files first, then folders deepest first.</summary>
public sealed record ShortcutGroup(string Id, string Name, ShortcutArea Area, bool Machine, bool Review, IReadOnlyList<BrokenLink> Links, IReadOnlyList<string> Folders)
{
    public IReadOnlyList<string> Paths => [.. Links.Select(x => x.File.Path), .. Folders];
}

/// <summary>
/// A shortcut file or folder as it was before a repair removed it, kept so Undo can put it back. A null <see cref="Data"/> is a folder.
/// <see cref="Token"/> names the administrator helper's own protected copy, when the helper removed it: a put-back through the helper writes that copy, never this one.
/// </summary>
public sealed record ShortcutBackup(string Path, string? Data, int Attributes = 0, string? Token = null);

/// <summary>Whatever removes and restores shortcut files and the empty folders around them: the signed-in user's own, or the elevated helper.</summary>
public interface IShortcutEditor
{
    /// <summary>Removes a shortcut file or an empty folder and returns what it took to put it back (the folder's desktop.ini first, then the folder). Nothing when it was already gone.</summary>
    IReadOnlyList<ShortcutBackup> RemoveShortcut(string path);
    void RestoreShortcut(ShortcutBackup backup);
}

/// <summary>The places Windows lists shortcuts from, for this account.</summary>
public static class ShortcutPlaces
{
    public static IReadOnlyList<ShortcutRoot> Roots()
    {
        var roots = new List<ShortcutRoot>();
        void Add(string path, ShortcutArea area, bool machine)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try { path = Path.GetFullPath(path).TrimEnd('\\'); } catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return; }
            if (!Directory.Exists(path) || roots.Any(x => string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase))) return;
            roots.Add(new(path, area, machine));
        }

        Add(Environment.GetFolderPath(Environment.SpecialFolder.Programs), ShortcutArea.StartMenu, false);
        Add(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), ShortcutArea.StartMenu, true);
        Add(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), ShortcutArea.Desktop, false);
        Add(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), ShortcutArea.Desktop, true);
        // A OneDrive that was removed leaves its Desktop folder behind, still browsable and still indexed by Windows Search.
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        try { foreach (var folder in Directory.EnumerateDirectories(profile, "OneDrive*")) Add(Path.Combine(folder, "Desktop"), ShortcutArea.Desktop, false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar"), ShortcutArea.Taskbar, false);
        Add(Environment.GetFolderPath(Environment.SpecialFolder.SendTo), ShortcutArea.SendTo, false);
        return roots;
    }

    public static bool Under(string path, string root) =>
        path.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Removes shortcut files (.lnk, .url) and empty folders, only inside the places Windows lists shortcuts from, and keeps a byte-for-byte copy so each can be put back.
/// The user's own editor reaches the account's folders; the helper's reaches only the all-users Start menu and Public Desktop.
/// </summary>
public sealed class FileShortcutEditor(Func<string, bool> allowed) : IShortcutEditor
{
    private static readonly string[] Ignorable = ["desktop.ini", "thumbs.db"];

    public static FileShortcutEditor User() => new(path => ShortcutPlaces.Roots().Any(x => !x.Machine && ShortcutPlaces.Under(path, x.Path)));

    public static FileShortcutEditor Machine() => new(path => ShortcutPlaces.Roots().Any(x => x.Machine && ShortcutPlaces.Under(path, x.Path)));

    /// <summary>Whether this editor is allowed to change the path at all.</summary>
    public bool Allows(string path)
    {
        try { return allowed(Path.GetFullPath(path)); } catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    private string Checked(string path)
    {
        string full;
        try { full = Path.GetFullPath(path); } catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { throw new IOException("That is not a path TidyWindow can change."); }
        if (!allowed(full)) throw new IOException("TidyWindow does not change shortcuts in that place.");
        return full;
    }

    public IReadOnlyList<ShortcutBackup> RemoveShortcut(string path)
    {
        var full = Checked(path);
        if (Directory.Exists(full)) return RemoveFolder(full);
        if (!File.Exists(full)) return [];
        if (!full.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) && !full.EndsWith(".url", StringComparison.OrdinalIgnoreCase)) throw new IOException("Only shortcut files are removed here.");
        var backup = Read(full);
        Delete(full);
        return backup is null ? [] : [backup];
    }

    private List<ShortcutBackup> RemoveFolder(string full)
    {
        var info = new DirectoryInfo(full);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("That folder is a link to somewhere else, so it was left alone.");
        var found = Directory.EnumerateFileSystemEntries(full).ToList();
        if (found.Any(x => !Ignorable.Contains(Path.GetFileName(x), StringComparer.OrdinalIgnoreCase) || Directory.Exists(x))) throw new IOException($"{Path.GetFileName(full)} is not empty.");
        var backups = new List<ShortcutBackup>();
        foreach (var file in found)
        {
            if (Read(file) is { } copy) backups.Add(copy);
            Delete(file);
        }
        var attributes = (int)info.Attributes;
        Directory.Delete(full, false);
        backups.Add(new(full, null, attributes));
        return backups;
    }

    // A OneDrive placeholder that was cut off from its cloud cannot be read at all; it is removed with nothing to put back.
    private static ShortcutBackup? Read(string file)
    {
        try { return new(file, Convert.ToBase64String(File.ReadAllBytes(file)), (int)File.GetAttributes(file)); }
        catch (IOException) { return null; }
    }

    private static void Delete(string file)
    {
        File.SetAttributes(file, FileAttributes.Normal);
        File.Delete(file);
    }

    public void RestoreShortcut(ShortcutBackup backup)
    {
        var full = Checked(backup.Path);
        if (backup.Data is null)
        {
            Directory.CreateDirectory(full);
            File.SetAttributes(full, (FileAttributes)backup.Attributes);
            return;
        }
        var bytes = Convert.FromBase64String(backup.Data);
        if (File.Exists(full))
        {
            if (File.ReadAllBytes(full).AsSpan().SequenceEqual(bytes)) return;
            throw new IOException($"{Path.GetFileName(full)} has been replaced by a different file since, so it was not overwritten.");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, bytes);
        File.SetAttributes(full, (FileAttributes)backup.Attributes);
    }
}

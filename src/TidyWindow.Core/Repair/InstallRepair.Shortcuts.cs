using System.Text.RegularExpressions;

namespace TidyWindow.Next.Core;

/// <summary>What a removed program was known by, to tell which stale shortcuts were its own.</summary>
public sealed record ProgramMarks(IReadOnlyList<string> Folders, IReadOnlyList<string> Files, IReadOnlyList<string> Codes)
{
    public bool IsEmpty => Folders.Count == 0 && Files.Count == 0 && Codes.Count == 0;
}

public static partial class InstallRepair
{
    // Windows' own Start menu folders: they stay even when empty, and what is inside them is only ever offered, never ticked.
    private static readonly string[] ProtectedFolders =
    [
        "Startup", "Accessories", "Windows Accessories", "Administrative Tools", "Windows Administrative Tools", "Windows Tools", "System Tools",
        "Windows System", "Maintenance", "Accessibility", "Windows Ease of Access", "Windows PowerShell"
    ];

    private static readonly string[] Ignorable = ["desktop.ini", "thumbs.db"];

    // Store apps and the component store cannot be listed by a normal account, so a file in them that looks missing may only be out of sight.
    private static readonly string[] OutOfSight = [@"\WindowsApps\", @"\SystemApps\", @"\WinSxS\"];

    public static bool IsProtectedFolder(string name) => ProtectedFolders.Contains(name, StringComparer.OrdinalIgnoreCase);

    // ---- Judging one shortcut ----

    /// <summary>Why a shortcut does not work any more, or null when it works or cannot be judged. A shortcut is only called broken when everything it could rely on is gone.</summary>
    public static string? JudgeShortcut(IInstallSystem system, ShortcutFile file)
    {
        if (file.Unreadable) return "This copy is a OneDrive placeholder that Windows can no longer open.";
        if (file.Unparsed) return null;
        if (file.IsUrl)
        {
            // A web or app link says nothing about what is on this PC; only a link to a file can be broken.
            if (!file.Url.StartsWith("file:", StringComparison.OrdinalIgnoreCase) || !Uri.TryCreate(file.Url, UriKind.Absolute, out var uri) || !uri.IsFile) return null;
            return JudgeTarget(system, uri.LocalPath, "");
        }
        // A shortcut Windows Installer advertises has no real target until the program runs; it is broken when the program is gone.
        if (file.Product.Length > 0)
            return system.ProductState(file.Product) == Unknown ? "Windows Installer has no record of the program it starts." : null;

        var raw = file.Target.Trim().Trim('"');
        if (raw.Length == 0)
            // Shortcuts to File Explorer, the Control Panel or a Store app have no file path: they point into the shell or name an app id.
            return file.AppId.Length > 0 || file.ShellTarget ? null : "It does not point at anything.";
        return JudgeTarget(system, system.Expand(raw), file.Arguments);
    }

    private static string? JudgeTarget(IInstallSystem system, string target, string arguments)
    {
        if (target.Contains("::") || target.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) || IsUri(target) || target.StartsWith(@"\\")) return null;
        // A bare name is found through PATH or an app alias, which this cannot tell apart from a missing program.
        if (target.IndexOfAny(['\\', '/']) < 0) return null;
        if (OutOfSight.Any(x => target.Contains(x, StringComparison.OrdinalIgnoreCase))) return null;
        // A program on a drive that is not plugged in is not broken, only out of reach.
        if (!system.DriveAvailable(target)) return null;
        if (!Reachable(system, target)) return $"Its target, {target}, is gone.";
        return HostedTarget(system, target, arguments);
    }

    /// <summary>A file or folder that is there, also looking in Program Files (x86) for what a 32-bit program wrote under Program Files and the other way round.</summary>
    private static bool Reachable(IInstallSystem system, string path)
    {
        if (system.FileExists(path) || system.FolderExists(path)) return true;
        foreach (var (from, to) in new[] { (@"\Program Files\", @"\Program Files (x86)\"), (@"\Program Files (x86)\", @"\Program Files\") })
        {
            var at = path.IndexOf(from, StringComparison.OrdinalIgnoreCase);
            if (at < 0) continue;
            var other = path[..at] + to + path[(at + from.Length)..];
            if (system.FileExists(other) || system.FolderExists(other)) return true;
        }
        return false;
    }

    /// <summary>
    /// The system program a shortcut starts always exists, so what it hands that program is what counts:
    /// msiexec removing or repairing a product Windows Installer no longer has, or a command prompt, script or library in a program folder that is gone.
    /// </summary>
    private static string? HostedTarget(IInstallSystem system, string target, string arguments)
    {
        var name = Path.GetFileNameWithoutExtension(target).ToLowerInvariant();
        var command = $"\"{target}\" {arguments}".TrimEnd();
        if (name == "msiexec")
            return MsiCode().Match(command) is { Success: true } code && system.ProductState(code.Groups[1].Value.ToUpperInvariant()) == Unknown
                ? "Windows Installer no longer has the program this shortcut works on." : null;
        if (!Hosts.Contains(name)) return null;

        // "cmd /K path C:\Program Files\Rust\bin;%PATH%" opens a prompt set up for a toolchain; the folder it puts first is the program.
        if (name == "cmd" && PathFolder().Match(arguments) is { Success: true } folder)
        {
            var dir = system.Expand(folder.Groups[1].Value.Trim().Trim('"'));
            if (system.DriveAvailable(dir) && !system.FolderExists(dir)) return $"The program folder it opens in, {dir}, is gone.";
        }

        var launched = Launched(system, command);
        if (launched.SequenceEqual(Candidates(command))) return null;
        if (launched.Any(x => Exists(system, x) || IsUri(x) || !system.DriveAvailable(system.Expand(x)))) return null;
        return $"What it runs, {system.Expand(launched[0].Trim().Trim('"'))}, is gone.";
    }

    [GeneratedRegex(@"(?:^|[\s&""])path\s+([A-Za-z]:\\[^;&|""]+)", RegexOptions.IgnoreCase)]
    private static partial Regex PathFolder();

    // ---- Everything stale in the Start menu, desktops, taskbar and Send to menu ----

    /// <summary>
    /// Broken shortcuts, grouped the way a person thinks of them (one program's Start menu folder, one loose shortcut), with the companion links and the
    /// empty folders that go with them. Nothing here changes the PC.
    /// </summary>
    public static IReadOnlyList<ShortcutGroup> StaleShortcuts(IInstallSystem system)
    {
        var roots = system.ShortcutRoots();
        if (roots.Count == 0) return [];
        ShortcutRoot? RootOf(string path) => roots.Where(x => ShortcutPlacesUnder(path, x.Path)).OrderByDescending(x => x.Path.Length).FirstOrDefault();
        static string? Top(ShortcutRoot root, string path)
        {
            var relative = path[root.Path.TrimEnd('\\').Length..].TrimStart('\\');
            var slash = relative.IndexOf('\\');
            return slash < 0 ? null : relative[..slash];
        }

        var files = system.Shortcuts();
        var broken = new Dictionary<string, BrokenLink>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
            if (JudgeShortcut(system, file) is { } reason) broken[file.Path] = new(file, reason);

        // Once every program shortcut in a program's Start menu folder is broken, the links to its web page and help go with them.
        foreach (var tree in files.Where(x => RootOf(x.Path) is { Area: ShortcutArea.StartMenu } root && Top(root, x.Path) is not null)
                     .GroupBy(x => { var root = RootOf(x.Path)!; return (root.Path, Top: Top(root, x.Path)!); }))
        {
            if (IsProtectedFolder(tree.Key.Top)) continue;
            var healthy = tree.Where(x => !broken.ContainsKey(x.Path)).ToList();
            if (!tree.Any(x => broken.ContainsKey(x.Path)) || healthy.Any(x => !Companion(x))) continue;
            foreach (var link in healthy) broken[link.Path] = new(link, "It is left over from a program whose shortcuts are all gone.", Companion: true);
        }

        var removedFolders = new List<string>();
        var folderSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in system.ShortcutFolders().OrderByDescending(x => x.Path.Count(c => c == '\\')))
        {
            var root = RootOf(folder.Path);
            if (root is null || string.Equals(folder.Path.TrimEnd('\\'), root.Path, StringComparison.OrdinalIgnoreCase) || IsProtectedFolder(Path.GetFileName(folder.Path.TrimEnd('\\')))) continue;
            var filesGone = folder.Files.All(x => broken.ContainsKey(x) || Ignorable.Contains(Path.GetFileName(x), StringComparer.OrdinalIgnoreCase));
            if (filesGone && folder.Folders.All(folderSet.Contains)) { folderSet.Add(folder.Path); removedFolders.Add(folder.Path); }
        }

        var parts = new Dictionary<string, (ShortcutRoot Root, string Name, string? Top, List<BrokenLink> Links, List<string> Folders)>(StringComparer.OrdinalIgnoreCase);
        void Part(ShortcutRoot root, string path, string? top, BrokenLink? link, bool folder)
        {
            var key = top is null ? path : $"{root.Path}|{top}";
            if (!parts.TryGetValue(key, out var part)) parts[key] = part = (root, top ?? Path.GetFileNameWithoutExtension(path), top, [], []);
            if (link is not null) part.Links.Add(link);
            if (folder) part.Folders.Add(path);
        }
        foreach (var link in broken.Values)
            if (RootOf(link.File.Path) is { } root) Part(root, link.File.Path, Top(root, link.File.Path), link, false);
        foreach (var folder in removedFolders)
            if (RootOf(folder) is { } root) Part(root, folder, Top(root, folder) ?? Path.GetFileName(folder), null, true);

        return parts.Values
            .Select(x => new ShortcutGroup($"shortcuts:{(x.Top is null ? x.Links[0].File.Path : x.Root.Path + "|" + x.Top).ToLowerInvariant()}", x.Name, x.Root.Area, x.Root.Machine,
                x.Root.Area == ShortcutArea.Taskbar || (x.Top is not null && IsProtectedFolder(x.Top)) || x.Links.Any(y => y.File.Unreadable),
                x.Links.OrderBy(y => y.File.Path, StringComparer.OrdinalIgnoreCase).ToList(),
                x.Folders.OrderByDescending(y => y.Count(c => c == '\\')).ToList()))
            .OrderBy(x => x.Area).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static bool ShortcutPlacesUnder(string path, string root) => ShortcutPlaces.Under(path, root);

    // A web page or help link (a .url, or a .lnk whose target is an address and not a file) works on its own; it is a leftover only when the program's own shortcuts are all gone.
    private static bool Companion(ShortcutFile file) =>
        file.IsUrl ? !file.Url.StartsWith("file:", StringComparison.OrdinalIgnoreCase) : !file.Unparsed && file.Target.Length == 0 && file.ShellTarget && file.AppId.Length == 0 && file.Product.Length == 0;

    private static IEnumerable<RepairFinding> StaleShortcutFindings(IInstallSystem system)
    {
        foreach (var group in StaleShortcuts(system))
        {
            var place = group.Area switch { ShortcutArea.StartMenu => "in the Start menu", ShortcutArea.Desktop => "on the Desktop", ShortcutArea.Taskbar => "on the taskbar", _ => "in the Send to menu" };
            var count = group.Links.Count;
            var onlyFolders = count == 0;
            var title = onlyFolders ? $"{group.Name} — empty folder {place}"
                : count == 1 ? $"{group.Name} — broken shortcut {place}" : $"{group.Name} — {count} broken shortcuts {place}";
            static string List(IReadOnlyList<string> names) => names.Count <= 4 ? string.Join(", ", names) : $"{string.Join(", ", names.Take(4))} and {names.Count - 4} more";
            // A placeholder cannot be read at all, so nothing is known about the program it once started: it may well still be installed.
            var placeholders = group.Links.Where(x => x.File.Unreadable).Select(x => Path.GetFileNameWithoutExtension(x.File.Path)).ToList();
            var dead = group.Links.Where(x => !x.File.Unreadable).Select(x => Path.GetFileNameWithoutExtension(x.File.Path)).ToList();
            var detail = onlyFolders
                ? "The program is gone and left an empty folder behind, which Windows still shows in the Start menu and in search."
                : (dead.Count > 0 ? $"{List(dead)}: the program {(dead.Count == 1 ? "this starts is" : "these start is")} gone, but Windows still lists {(dead.Count == 1 ? "it" : "them")} in the Start menu and in search, and clicking one shows “Problem with Shortcut”." : "")
                  + (placeholders.Count > 0 ? $"{(dead.Count > 0 ? " " : "")}{List(placeholders)}: {(placeholders.Count == 1 ? "this is a leftover OneDrive copy" : "these are leftover OneDrive copies")} of a shortcut that Windows can no longer open, because OneDrive is not installed. That says nothing about the program itself, which is not touched and may still be installed. Undo cannot bring {(placeholders.Count == 1 ? "it" : "them")} back." : "")
                  + (group.Review && placeholders.Count == 0 ? " It is in a place Windows or you may have set up on purpose, so check before removing it." : "")
                  + (group.Folders.Count > 0 && count > 0 ? " The empty folder left behind goes with them." : "");
            var evidence = onlyFolders ? group.Folders[0] : group.Links[0].Reason;
            yield return new(group.Id, onlyFolders ? RepairKind.EmptyFolder : RepairKind.BrokenShortcut, title, detail, evidence,
                group.Review ? RepairLevel.Review : RepairLevel.Recommended, [], Shortcuts: group.Paths, MachineShortcuts: group.Machine);
        }
    }

    // ---- Shortcuts left by one removed program ----

    /// <summary>What the program a repair is removing is known by: its folders, its main files and its Windows Installer code.</summary>
    public static ProgramMarks MarksOf(IInstallSystem system, UninstallEntry? entry, MsiProduct? product)
    {
        var folders = new List<string>();
        var files = new List<string>();
        var codes = new List<string>();
        if (product is not null) codes.Add(product.Code);
        if (entry is not null)
        {
            if (ProductCode(entry) is { } code) codes.Add(code);
            if (system.Expand(entry.InstallLocation.Trim().Trim('"')) is { Length: > 0 } location) folders.Add(location);
            if (IconCommand.Location(entry.DisplayIcon) is { } icon) files.Add(system.Expand(icon.Path));
            foreach (var command in new[] { entry.UninstallString, entry.QuietUninstallString })
                foreach (var candidate in Candidates(command))
                    if (system.Expand(candidate.Trim().Trim('"')) is { } path && path.IndexOfAny(['\\', '/']) >= 0) { files.Add(path); break; }
        }
        // A program's own files live in a folder of their own; the folder an installer or a uninstaller sits in counts only when it is one.
        folders.AddRange(files.Select(x => Path.GetDirectoryName(x) ?? ""));
        return new([.. folders.Where(IsSpecificFolder).Distinct(StringComparer.OrdinalIgnoreCase)],
            [.. files.Where(x => x.Length > 0 && !Hosts.Contains(Path.GetFileNameWithoutExtension(x), StringComparer.OrdinalIgnoreCase) && !string.Equals(Path.GetFileNameWithoutExtension(x), "msiexec", StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase)],
            [.. codes.Distinct(StringComparer.OrdinalIgnoreCase)]);
    }

    private static readonly string[] Shared = ["Program Files", "Program Files (x86)", "ProgramData", "Users", "Windows", "AppData", "Local", "Roaming", "Programs", "System32", "SysWOW64", "Installer", "Package Cache", "Temp"];

    /// <summary>A folder that belongs to one program, not a drive, Program Files or another place every program shares.</summary>
    private static bool IsSpecificFolder(string folder)
    {
        folder = folder.Trim().TrimEnd('\\', '/');
        if (folder.Length == 0 || folder.IndexOfAny(['\\', '/']) < 0) return false;
        var parts = folder.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && !Shared.Contains(parts[^1], StringComparer.OrdinalIgnoreCase);
    }

    private static bool PointsInto(IInstallSystem system, ShortcutFile file, ProgramMarks marks)
    {
        if (file.Product.Length > 0 && marks.Codes.Contains(file.Product, StringComparer.OrdinalIgnoreCase)) return true;
        if (marks.Codes.Any(x => file.Arguments.Contains(x, StringComparison.OrdinalIgnoreCase))) return true;
        var places = new[] { file.Target, file.WorkingDirectory, IconCommand.Location(file.Icon)?.Path ?? "" }.Where(x => x.Trim().Length > 0).Select(x => system.Expand(x.Trim().Trim('"'))).ToList();
        if (places.Any(x => marks.Files.Contains(x, StringComparer.OrdinalIgnoreCase))) return true;
        return places.Any(x => marks.Folders.Any(folder => string.Equals(x.TrimEnd('\\'), folder, StringComparison.OrdinalIgnoreCase) || ShortcutPlaces.Under(x, folder)));
    }

    /// <summary>
    /// The stale shortcuts that belong to a removed program. When every broken shortcut in a group is the program's, the whole group goes
    /// (its companion links and empty folder too); when the group also holds another program's, only the program's own go.
    /// </summary>
    public static IReadOnlyList<(string Path, bool Machine)> SweepOf(IInstallSystem system, ProgramMarks marks)
    {
        if (marks.IsEmpty) return [];
        var picked = new List<(string, bool)>();
        foreach (var group in StaleShortcuts(system))
        {
            var program = group.Links.Where(x => !x.Companion && !x.File.Unreadable).ToList();
            var own = program.Where(x => PointsInto(system, x.File, marks)).ToList();
            if (own.Count == 0) continue;
            if (own.Count == program.Count && !group.Review) picked.AddRange(group.Paths.Select(x => (x, group.Machine)));
            else picked.AddRange(own.Select(x => (x.File.Path, group.Machine)));
        }
        return picked;
    }
}

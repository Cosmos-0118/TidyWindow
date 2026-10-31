using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace TidyWindow.Next.Core;

public enum IconSource { Package, Shortcut, Program, DisplayIcon, AppPaths, Folder, FileType }

/// <summary>One place to try for an icon: a file and, for programs and libraries, which of its icon resources (a negative index is a resource id).
/// For <see cref="IconSource.FileType"/> the file only supplies the type, so Windows' icon for that kind of file is used.</summary>
public sealed record IconRef(string Path, int Index, IconSource Source)
{
    public string Key => $"{Path.ToLowerInvariant()}|{Index}|{(Source == IconSource.FileType ? 'T' : 'F')}";
}

/// <summary>What is known about the thing that needs an icon. Any of it may be empty.</summary>
public sealed record IconQuery(string Name, string Command = "", string ExePath = "", string DisplayIcon = "", string InstallLocation = "", string UninstallCommand = "", string ProductCode = "")
{
    public string Key => string.Join('\u001f', Name, Command, ExePath, DisplayIcon, InstallLocation, UninstallCommand, ProductCode);
}

public sealed record UninstallIconFact(string Name, string DisplayIcon, string InstallLocation);

/// <summary>Everything the finder needs to know about the PC, so the search can be tested without touching a real one.</summary>
public interface IIconSystem
{
    string Expand(string text);
    bool FileExists(string path);
    string? FindOnPath(string fileName);
    /// <summary>The program Windows has registered under App Paths for this file name.</summary>
    string? AppPath(string fileName);
    /// <summary>Where a shortcut points, and its own icon location ("file,index"). Both empty when it cannot be read.</summary>
    (string Target, string Icon) Shortcut(string path);
    IReadOnlyList<string> Subfolders(string folder);
    /// <summary>Full paths of the files in a folder that match a pattern such as "*.exe".</summary>
    IReadOnlyList<string> Files(string folder, string pattern);
    /// <summary>Small text files only (a Store app's manifest); null when there is none.</summary>
    string? ReadText(string path);
    /// <summary>The "Installed apps" list, for the icon each program registered. Read once per finder.</summary>
    IReadOnlyList<UninstallIconFact> Programs();
    /// <summary>Every shortcut in the Start menu. Read once per finder.</summary>
    IReadOnlyList<string> StartMenu();
    /// <summary>The icon Windows Installer recorded for a product ("file,index"), given the product code in braces; empty when it has none.</summary>
    string ProductIcon(string productCode);
}

public sealed record LaunchTarget(string Path, string Arguments);

/// <summary>Works out which program a startup command line really starts, looking through the wrappers that only start something else.</summary>
public static partial class IconCommand
{
    private const string Types = "exe|lnk|bat|cmd|ico|msc|cpl|scr|vbs|ps1|jar|msi|dll";
    private static readonly string[] ScansArguments = ["powershell", "pwsh", "wscript", "cscript", "mshta", "conhost", "bash", "wsl"];
    private static readonly string[] Scripts = [".bat", ".cmd", ".vbs", ".ps1", ".jar", ".msi", ".js", ".wsf", ".py"];

    public static IReadOnlyList<LaunchTarget> Targets(string command, Func<string, string> expand)
    {
        var found = new List<LaunchTarget>();
        Collect(command, expand, found, 0);
        return found;
    }

    /// <summary>The icon location in a registry value such as <c>"C:\App\app.exe",1</c> or <c>C:\App\app.ico</c>.</summary>
    public static (string Path, int Index)? Location(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return null;
        var path = text; var index = 0;
        if (text[0] == '"')
        {
            var end = text.IndexOf('"', 1);
            if (end < 0) return null;
            path = text[1..end];
            var rest = text[(end + 1)..].Trim();
            if (rest.StartsWith(',') && int.TryParse(rest[1..].Trim(), out var quoted)) index = quoted;
        }
        else if (text.LastIndexOf(',') is > 0 and var comma && int.TryParse(text[(comma + 1)..].Trim(), out var plain))
        {
            path = text[..comma]; index = plain;
        }
        path = path.Trim().Trim('"');
        return path.Length == 0 ? null : (path, index);
    }

    private static void Collect(string command, Func<string, string> expand, List<LaunchTarget> found, int depth)
    {
        var (program, args) = Split(command.Trim());
        if (program.Length == 0 || depth > 3) return;
        var path = expand(program).Trim().Trim('"');
        var name = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
        if (name == "cmd") { Shell(args, expand, found, depth); return; }
        if (name == "rundll32")
        {
            var library = Split(args).Program;
            if (library.IndexOfAny(['\\', '/']) >= 0) found.Add(new(expand(library).Trim('"'), ""));
            return;
        }
        if (name == "explorer")
        {
            foreach (var token in Tokens(args, expand)) found.Add(new(token, ""));
            found.Add(new(path, ""));
            return;
        }
        if (Array.IndexOf(ScansArguments, name) >= 0)
        {
            foreach (var token in Tokens(args, expand)) found.Add(new(token, ""));
            return;
        }
        if (StartupGuard.IsLauncher(path)) return;
        found.Add(new(path, args));
    }

    // cmd /c "cd /d D:\App && start "" app.exe --tray": each part may name the program, and cd says where a bare name lives.
    private static void Shell(string args, Func<string, string> expand, List<LaunchTarget> found, int depth)
    {
        var switchMatch = ShellSwitch().Match(args);
        if (!switchMatch.Success) return;
        var inner = args[(switchMatch.Index + switchMatch.Length)..].Trim();
        // cmd drops the outer quotes of a whole command in quotes; a lone quoted path is left for the program to be read from.
        if (inner.Length > 2 && inner[0] == '"' && inner[^1] == '"' && (inner.IndexOf('"', 1) < inner.Length - 1 || inner.IndexOfAny(['&', '|', '<', '>', '^']) >= 0)) inner = inner[1..^1];
        var folders = new List<string>();
        foreach (var part in Chain().Split(inner).Select(x => x.Trim()).Where(x => x.Length > 0))
        {
            if (ChangeFolder().Match(part) is { Success: true } cd) { folders.Add(expand(cd.Groups[1].Value.Trim().Trim('"'))); continue; }
            var command = part.StartsWith("start", StringComparison.OrdinalIgnoreCase) && (part.Length == 5 || char.IsWhiteSpace(part[5])) ? WithoutStart(part) : part;
            var parts = new List<LaunchTarget>();
            Collect(command, expand, parts, depth + 1);
            foreach (var target in parts)
            {
                if (target.Path.IndexOfAny(['\\', '/']) < 0) foreach (var folder in folders) found.Add(target with { Path = Path.Combine(folder, target.Path) });
                found.Add(target);
            }
        }
    }

    private static string WithoutStart(string text)
    {
        var rest = text[5..].TrimStart();
        while (rest.StartsWith('/')) { var space = rest.IndexOf(' '); rest = space < 0 ? "" : rest[(space + 1)..].TrimStart(); }
        // start treats a first quoted word as the window title, unless it is plainly the program.
        if (rest.StartsWith('"') && rest.IndexOf('"', 1) is > 0 and var end && rest[(end + 1)..].Trim().Length > 0 && !LooksLikeProgram(rest[1..end]))
            rest = rest[(end + 1)..].TrimStart();
        return rest;
    }

    private static bool LooksLikeProgram(string text) => Extension().IsMatch(text);

    /// <summary>Program paths written out inside a script command, programs first and scripts after.</summary>
    private static List<string> Tokens(string text, Func<string, string> expand)
    {
        var all = new List<string>();
        foreach (Match match in PathToken().Matches(text))
            all.Add(expand(match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Value).Trim());
        return [.. all.Where(x => !IsScript(x)), .. all.Where(IsScript)];
    }

    private static bool IsScript(string path) => Array.IndexOf(Scripts, Path.GetExtension(path).ToLowerInvariant()) >= 0;

    /// <summary>Splits a command line into the program and what follows it. Handles quotes and unquoted paths that contain spaces.</summary>
    public static (string Program, string Arguments) Split(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return ("", "");
        if (text[0] == '"')
        {
            var end = text.IndexOf('"', 1);
            return end < 0 ? (text[1..], "") : (text[1..end], text[(end + 1)..].Trim());
        }
        var space = text.IndexOf(' ');
        var first = space < 0 ? text : text[..space];
        // Only a path can have spaces in it; a bare word such as "cmd" ends at the first space, whatever .exe comes later.
        if (first.IndexOfAny(['\\', '/']) >= 0 && Prefix().Match(text) is { Success: true } match) return (match.Groups[1].Value, text[match.Length..].TrimStart(',', ' '));
        return space < 0 ? (text, "") : (first, text[(space + 1)..].Trim());
    }

    [GeneratedRegex(@"^(.+?\.(?:exe|com|bat|cmd|lnk|msc|cpl|scr|vbs|ps1|msi|jar|dll|ico))(?=[\s,""]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex Prefix();

    [GeneratedRegex(@"(?:^|\s)/[ck](?=\s|""|$)", RegexOptions.IgnoreCase)]
    private static partial Regex ShellSwitch();

    [GeneratedRegex(@"\s*(?:&&|\|\||&|\|)\s*")]
    private static partial Regex Chain();

    [GeneratedRegex(@"^cd(?:\s+/d)?\s+(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex ChangeFolder();

    [GeneratedRegex(@"\.(?:" + Types + @")$", RegexOptions.IgnoreCase)]
    private static partial Regex Extension();

    [GeneratedRegex(@"""([^""]+?\.(?:" + Types + @"))""|'([^']+?\.(?:" + Types + @"))'|((?:[A-Za-z]:|%[^%\s]+%|\\\\[^\\\s]+)\\[^\s""'|<>*?&]*?\.(?:" + Types + @"))(?=[\s""',;)&|]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex PathToken();
}

/// <summary>
/// Lists the places an icon for a startup item or an installed program can come from, best first: the program the command really starts
/// (a Store app's own logo before its executable), what the program registered when it was installed, App Paths, and finally Windows'
/// icon for that kind of file. The caller tries them in order until one gives a picture; none left means a plain glyph.
/// </summary>
public sealed partial class IconFinder(IIconSystem system, Func<bool>? dark = null)
{
    private static readonly string[] Roots = ["%ProgramFiles%", "%ProgramFiles(x86)%", "%ProgramW6432%", "%CommonProgramFiles%", "%CommonProgramFiles(x86)%", "%LocalAppData%", @"%LocalAppData%\Programs", "%AppData%", "%ProgramData%", "%UserProfile%", "%SystemRoot%", @"%SystemRoot%\System32", @"%SystemRoot%\Installer", "%SystemDrive%", "%Temp%"];
    private readonly Lazy<IReadOnlyList<UninstallIconFact>> programs = new(system.Programs, LazyThreadSafetyMode.ExecutionAndPublication);
    private readonly Lazy<IReadOnlyList<string>> startMenu = new(system.StartMenu, LazyThreadSafetyMode.ExecutionAndPublication);
    private readonly Lazy<HashSet<string>> roots = new(() =>
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in Roots)
            if (system.Expand(root) is { Length: > 0 } expanded && expanded != root) set.Add(expanded.TrimEnd('\\', '/'));
        return set;
    }, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>True when the list holds something more specific than Windows' icon for the file type.</summary>
    public static bool IsSpecific(IReadOnlyList<IconRef> icons) => icons.Any(x => x.Source != IconSource.FileType);

    public IReadOnlyList<IconRef> Find(IconQuery query)
    {
        var found = new List<IconRef>();
        var seen = new HashSet<string>();
        var programsFound = new List<string>();
        string? fileType = null;
        void Add(IconRef icon) { if (seen.Add(icon.Key)) found.Add(icon); }
        IconRef? Registered(string raw, IconSource source)
        {
            if (IconCommand.Location(system.Expand(raw)) is not { } at) return null;
            return Locate(at.Path) is { } located ? new IconRef(located.Path, at.Index, source) : null;
        }

        if (Registered(query.DisplayIcon, IconSource.DisplayIcon) is { } own) Add(own);
        if (query.ProductCode.Length > 0 && Registered(system.ProductIcon(query.ProductCode), IconSource.DisplayIcon) is { } product) Add(product);

        var targets = IconCommand.Targets(query.Command, system.Expand).ToList();
        if (query.ExePath.Length > 0) targets.Add(new(system.Expand(query.ExePath), ""));
        foreach (var target in targets) FromTarget(target, 0);

        // A Store app's folder holds its manifest, and the logo in it is the app's own picture.
        if (query.InstallLocation.Length > 0 && FolderLogo(system.Expand(query.InstallLocation).Trim().Trim('"')) is { } folderLogo) Add(folderLogo);
        if (query.InstallLocation.Length > 0 && BestExecutable(system.Expand(query.InstallLocation).Trim().Trim('"'), query.Name) is { } installed) Add(new(installed, 0, IconSource.Folder));

        foreach (var (fact, _) in Matching(query, programsFound).Take(2))
        {
            if (Registered(fact.DisplayIcon, IconSource.DisplayIcon) is { } registered) Add(registered);
            else if (fact.InstallLocation.Length > 0 && BestExecutable(system.Expand(fact.InstallLocation).Trim().Trim('"'), query.Name) is { } there) Add(new(there, 0, IconSource.Folder));
        }

        if (query.UninstallCommand.Length > 0 && UninstallerFolder(query.UninstallCommand) is { } folder && BestExecutable(folder, query.Name) is { } beside) Add(new(beside, 0, IconSource.Folder));

        // The shortcut an installer put in the Start menu carries the app's icon even when nothing else says where the app is.
        if (StartMenuShortcut(query.Name) is { } shortcut) FromTarget(new(shortcut, ""), 0);

        // A name such as "Spotify" is often registered as spotify.exe even when the command line is no help.
        var guess = new string(query.Name.Where(char.IsLetterOrDigit).ToArray());
        if (guess.Length >= 3 && guess.Length == query.Name.Trim().Length && system.AppPath(guess + ".exe") is { } registeredApp && system.FileExists(registeredApp)) Add(new(registeredApp, 0, IconSource.AppPaths));

        if (fileType is not null) Add(new(fileType, 0, IconSource.FileType));
        return found;

        void FromTarget(LaunchTarget target, int depth)
        {
            var raw = target.Path.Trim().Trim('"');
            if (raw.Length == 0) return;
            if (Locate(raw) is not { } located)
            {
                if (Path.HasExtension(raw) && !StartupGuard.IsLauncher(raw)) fileType ??= raw;
                return;
            }
            var (path, via) = located;
            if (StartupGuard.IsLauncher(path)) return;
            var extension = Path.GetExtension(path).ToLowerInvariant();
            if (extension == ".lnk")
            {
                if (depth >= 2) return;
                var (to, icon) = system.Shortcut(path);
                if (Registered(icon, IconSource.Shortcut) is { } shortcutIcon) Add(shortcutIcon);
                if (to.Length > 0) FromTarget(new(system.Expand(to), ""), depth + 1);
                return;
            }
            fileType ??= path;
            if (Array.IndexOf([".bat", ".cmd", ".vbs", ".ps1", ".jar", ".msi", ".js", ".wsf", ".py"], extension) >= 0) return;
            if (PackageLogo(path) is { } logo) Add(logo);
            if (Squirrel(path, target.Arguments) is { } app) Add(new(app, 0, IconSource.Program));
            Add(new(path, 0, via));
            programsFound.Add(path);
        }
    }

    private (string Path, IconSource Source)? Locate(string path)
    {
        path = path.Trim().Trim('"');
        if (path.Length == 0) return null;
        var named = Path.HasExtension(path) ? path : path + ".exe";
        if (path.IndexOfAny(['\\', '/']) >= 0)
        {
            if (system.FileExists(path)) return (Tidy(path), IconSource.Program);
            if (!Path.HasExtension(path) && system.FileExists(named)) return (Tidy(named), IconSource.Program);
        }
        else if (system.FindOnPath(named) is { } onPath) return (Tidy(onPath), IconSource.Program);
        // The file moved or the command is a bare name: the program may have registered where it really is.
        var file = Path.GetFileName(named);
        return system.AppPath(file) is { Length: > 0 } registered && system.FileExists(registered) ? (Tidy(registered), IconSource.AppPaths) : null;
    }

    // The shell turns down a path with a doubled backslash, which registry values often have.
    private static string Tidy(string path)
    {
        try { return Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return path; }
    }

    private string? StartMenuShortcut(string name)
    {
        var wanted = Core(name);
        if (wanted.Length < 3) return null;
        string? best = null; var bestScore = 0;
        foreach (var shortcut in startMenu.Value)
        {
            var stem = Core(Path.GetFileNameWithoutExtension(shortcut));
            var folder = Core(Path.GetFileName(Path.GetDirectoryName(shortcut) ?? ""));
            var score = Math.Max(Related(wanted, stem), Related(wanted, folder) - 1);
            if (score > bestScore) { best = shortcut; bestScore = score; }
        }
        return best;
    }

    // ---- Store apps: the logo in the manifest is better than the executable's own icon ----

    private IconRef? PackageLogo(string exe)
    {
        if (!exe.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase)) return null;
        var folder = Path.GetDirectoryName(exe);
        for (var step = 0; step < 4 && !string.IsNullOrEmpty(folder) && !folder.EndsWith("WindowsApps", StringComparison.OrdinalIgnoreCase); step++, folder = Path.GetDirectoryName(folder))
            if (system.ReadText(Path.Combine(folder, "AppxManifest.xml")) is { } xml)
                return LogoOf(folder, xml) is { } logo ? new(logo, 0, IconSource.Package) : null;
        return null;
    }

    private IconRef? FolderLogo(string folder) =>
        folder.Length > 0 && system.ReadText(Path.Combine(folder, "AppxManifest.xml")) is { } xml && LogoOf(folder, xml) is { } logo ? new(logo, 0, IconSource.Package) : null;

    private string? LogoOf(string folder, string xml)
    {
        XDocument document;
        try { document = XDocument.Parse(xml); }
        catch (XmlException) { return null; }
        var visual = document.Descendants().FirstOrDefault(x => x.Name.LocalName == "VisualElements");
        var properties = document.Descendants().FirstOrDefault(x => x.Name.LocalName == "Properties");
        var names = new[] { "Square44x44Logo", "Square150x150Logo", "Square71x71Logo", "Logo" }.Select(x => visual?.Attribute(x)?.Value)
            .Append(properties?.Elements().FirstOrDefault(x => x.Name.LocalName == "Logo")?.Value);
        foreach (var name in names)
            if (!string.IsNullOrWhiteSpace(name) && PickLogo(folder, name.Trim()) is { } logo) return logo;
        return null;
    }

    private string? PickLogo(string folder, string relative)
    {
        var full = Path.Combine(folder, relative.Replace('/', '\\'));
        var directory = Path.GetDirectoryName(full);
        if (directory is null) return null;
        var best = system.Files(directory, Path.GetFileNameWithoutExtension(full) + "*" + Path.GetExtension(full))
            .Select(x => (File: x, Score: LogoScore(Path.GetFileName(x)))).Where(x => x.Score > 0).OrderByDescending(x => x.Score).FirstOrDefault();
        return best.File ?? (system.FileExists(full) ? full : null);
    }

    // Store apps ship a glyph for each theme (white for dark, black for light) next to a plated logo that suits both. The glyph for the current theme wins, the other theme's would vanish, and the plated logo is the safe middle.
    private int LogoScore(string file)
    {
        var name = file.ToLowerInvariant();
        if (name.Contains("contrast-")) return 0;
        var score = 1;
        var onDark = dark?.Invoke() ?? true;
        if (name.Contains("altform-lightunplated")) score += onDark ? -40 : 50;
        else if (name.Contains("altform-unplated")) score += onDark ? 50 : -40;
        if (name.Contains("theme-")) score -= 1;
        var size = TargetSize().Match(name);
        var scale = Scale().Match(name);
        if (size.Success) score += Math.Max(0, 40 - Math.Abs(int.Parse(size.Groups[1].Value) - 48));
        else if (scale.Success) score += int.Parse(scale.Groups[1].Value) switch { 200 => 20, 100 or 150 => 15, _ => 10 };
        else score += 10;
        return Math.Max(score, 1);
    }

    [GeneratedRegex(@"targetsize-(\d+)")]
    private static partial Regex TargetSize();

    [GeneratedRegex(@"scale-(\d+)")]
    private static partial Regex Scale();

    // Squirrel apps (Discord, Slack, GitHub Desktop) start through Update.exe, whose own icon says nothing; the app lives in the newest app-* folder.
    private string? Squirrel(string path, string arguments)
    {
        if (!string.Equals(Path.GetFileName(path), "Update.exe", StringComparison.OrdinalIgnoreCase)) return null;
        if (ProcessStart().Match(arguments) is not { Success: true } match || Path.GetDirectoryName(path) is not { } directory) return null;
        var exe = match.Groups[1].Value;
        foreach (var folder in system.Subfolders(directory).Where(x => Path.GetFileName(x).StartsWith("app-", StringComparison.OrdinalIgnoreCase)).OrderByDescending(x => x, StringComparer.OrdinalIgnoreCase))
        {
            var candidate = Path.Combine(folder, exe);
            if (system.FileExists(candidate)) return candidate;
        }
        return null;
    }

    [GeneratedRegex(@"--processStart(?:AndWait)?[ =]""?([^""\s]+)", RegexOptions.IgnoreCase)]
    private static partial Regex ProcessStart();

    // ---- What programs registered when they were installed ----

    /// <summary>Installed programs that look like the same thing: they live around the program found, or are named like it. Best first.</summary>
    private IEnumerable<(UninstallIconFact Fact, int Score)> Matching(IconQuery query, IReadOnlyList<string> found)
    {
        var name = Core(query.Name);
        var scored = new List<(UninstallIconFact, int)>();
        foreach (var fact in programs.Value)
        {
            if (fact.DisplayIcon.Length == 0 && fact.InstallLocation.Length == 0) continue;
            var score = 0;
            var location = system.Expand(fact.InstallLocation).Trim().Trim('"');
            var icon = IconCommand.Location(system.Expand(fact.DisplayIcon))?.Path;
            foreach (var exe in found)
            {
                if (location.Length > 0 && Shares(exe, location)) score = Math.Max(score, 3);
                if (icon is not null && Shares(exe, Path.GetDirectoryName(icon) ?? "")) score = Math.Max(score, 3);
            }
            score = Math.Max(score, Related(name, Core(fact.Name)));
            if (score > 0) scored.Add((fact, score));
        }
        return scored.OrderByDescending(x => x.Item2);
    }

    // The program sits in (or beside) the registered folder, which must be its own folder rather than Program Files itself.
    private bool Shares(string exe, string folder)
    {
        folder = folder.TrimEnd('\\', '/');
        if (folder.Length == 0 || roots.Value.Contains(folder) || Segments(folder) < 3) return false;
        return exe.StartsWith(folder + "\\", StringComparison.OrdinalIgnoreCase) || exe.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static int Segments(string path) => path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries).Length;
    private static string Plain(string text) => new(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    /// <summary>A name boiled down to what identifies the app: no brackets, version numbers or "x64", letters and digits only.</summary>
    private static string Core(string text) => Plain(Noise().Replace(text, " "));

    // 3 for the same name, 2 when one starts with the other ("7-Zip" and "7-Zip File Manager"), else 0.
    private static int Related(string a, string b)
    {
        if (a.Length < 3 || b.Length < 3) return 0;
        if (a == b) return 3;
        var (shorter, longer) = a.Length <= b.Length ? (a, b) : (b, a);
        return (shorter.Length >= 4 && longer.StartsWith(shorter, StringComparison.Ordinal)) || (shorter.Length >= 6 && longer.Contains(shorter, StringComparison.Ordinal)) ? 2 : 0;
    }

    [GeneratedRegex(@"\([^)]*\)|\[[^\]]*\]|\bv?\d+(?:[.\-_]\d+)+\b|\b(?:x64|x86|64-bit|32-bit|amd64|win64|win32|edition|setup|installer)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Noise();

    // ---- Looking inside an install folder ----

    private static readonly string[] SkipPrefixes = ["unins", "uninst", "setup", "install", "update", "upgrade", "crash", "report", "elevate", "vc_redist", "vcredist", "dxsetup", "notification", "diagnos", "cleanup", "repair", "dotnet", "msiexec"];

    /// <summary>The program in a folder that most likely is the app: one named like it, or the only candidate left once installers and helpers are set aside.</summary>
    private string? BestExecutable(string folder, string name)
    {
        folder = folder.TrimEnd('\\', '/');
        if (folder.Length == 0 || roots.Value.Contains(folder)) return null;
        var wanted = Core(name);
        var places = new List<string> { folder, Path.Combine(folder, "bin"), Path.Combine(folder, "app") };
        places.AddRange(system.Subfolders(folder).Take(12));
        foreach (var place in places)
        {
            var candidates = system.Files(place, "*.exe")
                .Where(x => !Skipped(Path.GetFileNameWithoutExtension(x))).ToList();
            if (candidates.Count == 0) continue;
            var named = candidates.Select(x => (File: x, Stem: Plain(Path.GetFileNameWithoutExtension(x))))
                .Where(x => x.Stem.Length >= 2 && wanted.Length >= 3 && (wanted.StartsWith(x.Stem, StringComparison.Ordinal) || (x.Stem.Length >= 3 && (wanted.Contains(x.Stem) || x.Stem.Contains(wanted)))))
                .OrderBy(x => Math.Abs(x.Stem.Length - wanted.Length)).ToList();
            if (named.Count > 0) return named[0].File;
            if (candidates.Count == 1) return candidates[0];
        }
        return null;
    }

    private static bool Skipped(string stem) =>
        Array.Exists(SkipPrefixes, x => stem.StartsWith(x, StringComparison.OrdinalIgnoreCase)) || stem.Contains("helper", StringComparison.OrdinalIgnoreCase) || stem.Contains("crashpad", StringComparison.OrdinalIgnoreCase);

    // An uninstaller usually sits in the program's own folder, but not when Windows Installer or a setup cache keeps it.
    private string? UninstallerFolder(string command)
    {
        foreach (var target in IconCommand.Targets(command, system.Expand))
        {
            if (Locate(target.Path) is not { } located || Path.GetDirectoryName(located.Path) is not { } folder) continue;
            if (folder.Contains(@"\Installer", StringComparison.OrdinalIgnoreCase) || folder.Contains("Package Cache", StringComparison.OrdinalIgnoreCase) || folder.Contains(@"\Temp", StringComparison.OrdinalIgnoreCase)) continue;
            if (roots.Value.Contains(folder.TrimEnd('\\')) || Segments(folder) < 3) continue;
            return folder;
        }
        return null;
    }
}

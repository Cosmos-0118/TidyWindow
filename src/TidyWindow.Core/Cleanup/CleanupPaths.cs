using System.Text.RegularExpressions;

namespace TidyWindow.Next.Core;

/// <summary>Turns catalog paths into real folders and refuses any that could harm Windows or the user's own files.</summary>
public static partial class CleanupPaths
{
    [GeneratedRegex("%([^%]+)%")]
    private static partial Regex Variables();

    private static readonly EnumerationOptions Folders = new() { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint, MatchType = MatchType.Win32 };

    /// <summary>Expands variables and wildcard folders. Missing folders, unknown variables and unsafe results are dropped.</summary>
    public static string[] Roots(CleanupRule rule, CleanupContext context) => Roots(rule, context, Protected(context));

    private static string[] Roots(CleanupRule rule, CleanupContext context, IReadOnlyList<string> protectedPaths)
    {
        var found = new List<string>();
        void Add(string folder)
        {
            if (IsSafeRoot(folder, rule, protectedPaths) && IsPlainFolder(folder)) found.Add(Normalize(folder));
        }
        foreach (var pattern in rule.Paths)
        {
            var expanded = Expand(pattern, context);
            if (expanded is null) continue;
            foreach (var folder in Wildcards(expanded)) Add(folder);
        }
        // Folders Windows lists for its own cleanup handlers. They only count inside the places Windows keeps its temporary data.
        foreach (var handler in rule.Handlers)
            foreach (var pattern in context.HandlerFolders(handler) ?? [])
            {
                var expanded = Expand(pattern, context);
                if (expanded is not null && IsWindowsDataFolder(expanded, context)) Add(expanded);
            }
        if (string.Equals(rule.Discover, "chromium", StringComparison.OrdinalIgnoreCase))
            foreach (var folder in DiscoverChromium(context)) Add(folder);
        return found.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool IsWindowsDataFolder(string path, CleanupContext context)
    {
        string full;
        try { full = Normalize(path); } catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
        foreach (var name in new[] { "WINDIR", "PROGRAMDATA", "LOCALAPPDATA" })
            if (context.Variable(name) is { Length: > 0 } baseFolder && Path.IsPathRooted(baseFolder) && IsInside(full, Normalize(baseFolder))) return true;
        return false;
    }

    private static readonly string[] ChromiumCaches =
    [
        "Cache", "Code Cache", "GPUCache", "DawnCache", "DawnGraphiteCache", "DawnWebGPUCache", "GraphiteDawnCache", "GrShaderCache", "ShaderCache",
        "component_crx_cache", "extensions_crx_cache", "Crashpad", @"Service Worker\CacheStorage", @"Service Worker\ScriptCache"
    ];
    private static readonly HashSet<string> DiscoverSkips = new(StringComparer.OrdinalIgnoreCase) { "Temp", "node_modules", ".git", "Programs", "WinGet", "Windows" };
    private const int DiscoverDepth = 9;

    /// <summary>
    /// Finds the cache folders of every Chromium based app in the profile. A folder counts as an app's data folder only when it holds
    /// a "Local State" file, which Chromium, Electron, WebView2 and CEF all write; only well-known cache folders directly inside it
    /// or inside one of its profiles are returned, never the profiles themselves, so sign-ins, history and settings are not reachable.
    /// </summary>
    internal static IReadOnlyList<string> DiscoverChromium(CleanupContext context)
    {
        var found = new System.Collections.Concurrent.ConcurrentBag<string>();
        var tops = new List<string>();
        foreach (var name in new[] { "LOCALAPPDATA", "APPDATA" })
            if (context.Variable(name) is { Length: > 0 } start && Path.IsPathRooted(start) && Directory.Exists(start)) tops.Add(Normalize(start));

        void AddCaches(string folder)
        {
            foreach (var cache in ChromiumCaches)
            {
                var path = Path.Combine(folder, cache);
                if (Directory.Exists(path)) found.Add(path);
            }
        }

        void Walk(string folder, int depth)
        {
            try
            {
                if (File.Exists(Path.Combine(folder, "Local State")))
                {
                    AddCaches(folder);
                    foreach (var profile in Directory.EnumerateDirectories(folder, "*", Folders)) AddCaches(profile);
                    return;
                }
                if (depth >= DiscoverDepth) return;
                foreach (var child in Directory.EnumerateDirectories(folder, "*", Folders))
                    if (!DiscoverSkips.Contains(Path.GetFileName(child))) Walk(child, depth + 1);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        var work = new List<string>();
        foreach (var top in tops)
        {
            if (File.Exists(Path.Combine(top, "Local State"))) continue;
            try { work.AddRange(Directory.EnumerateDirectories(top, "*", Folders).Where(x => !DiscoverSkips.Contains(Path.GetFileName(x)))); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        Parallel.ForEach(work, new ParallelOptions { MaxDegreeOfParallelism = context.Parallelism }, folder => Walk(folder, 1));
        return found.ToArray();
    }

    /// <summary>Roots for every rule, in rule order. A folder that another rule already cleans whole, or that sits inside one, is dropped, so no file is counted or removed twice.</summary>
    public static string[][] Plan(IReadOnlyList<CleanupRule> rules, CleanupContext context)
    {
        var roots = new string[rules.Count][];
        var protectedPaths = Protected(context);
        // Wildcard folders (browser profiles) make this the slowest of the quick steps, so rules are resolved side by side.
        Parallel.For(0, rules.Count, new ParallelOptions { MaxDegreeOfParallelism = context.Parallelism }, i =>
            roots[i] = rules[i].Action is CleanupAction.Files or CleanupAction.PreviousWindows ? Roots(rules[i], context, protectedPaths) : []);
        var whole = new List<(int Rule, string Root)>();
        for (var i = 0; i < rules.Count; i++)
            if (rules[i].Action == CleanupAction.Files && rules[i].Masks.Length == 0 && rules[i].Recurse) whole.AddRange(roots[i].Select(root => (i, root)));
        var kept = new List<string>();
        var drop = new HashSet<(int, string)>();
        foreach (var (rule, root) in whole.OrderBy(x => x.Root.Length))
        {
            if (kept.Any(parent => root.Equals(parent, StringComparison.OrdinalIgnoreCase) || IsInside(root, parent))) drop.Add((rule, root));
            else kept.Add(root);
        }
        for (var i = 0; i < roots.Length; i++)
            if (drop.Count > 0) roots[i] = roots[i].Where(root => !drop.Contains((i, root))).ToArray();
        return roots;
    }

    public static bool IsInside(string path, string parent) =>
        path.Length > parent.Length && path.StartsWith(parent, StringComparison.OrdinalIgnoreCase) && path[parent.Length] == Path.DirectorySeparatorChar;

    public static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    internal static string? Expand(string pattern, CleanupContext context)
    {
        var missing = false;
        var text = Variables().Replace(pattern, match =>
        {
            var value = context.Variable(match.Groups[1].Value);
            if (string.IsNullOrWhiteSpace(value)) { missing = true; return ""; }
            return value;
        });
        return missing || !Path.IsPathRooted(text) ? null : text;
    }

    private static IEnumerable<string> Wildcards(string path)
    {
        var root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root)) yield break;
        var segments = path[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        var current = new List<string> { root };
        foreach (var segment in segments)
        {
            var next = new List<string>(current.Count);
            if (segment.AsSpan().IndexOfAny('*', '?') >= 0)
            {
                foreach (var parent in current)
                {
                    try { foreach (var child in Directory.EnumerateDirectories(parent, segment, Folders)) next.Add(child); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
            }
            else foreach (var parent in current) next.Add(Path.Combine(parent, segment));
            current = next;
            if (current.Count == 0) yield break;
        }
        foreach (var folder in current) yield return Path.TrimEndingDirectorySeparator(folder);
    }

    internal static bool IsPlainFolder(string folder)
    {
        try
        {
            var info = new DirectoryInfo(folder);
            return info.Exists && !info.Attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>A root is unsafe when it is a drive, a protected folder or something that contains one.
    /// Only a rule that matches specific file names, without recursing, may sit directly in a protected folder (old installers in Downloads, the memory dump in Windows).</summary>
    internal static bool IsSafeRoot(string folder, CleanupRule rule, IReadOnlyList<string> protectedPaths)
    {
        string full;
        try { full = Normalize(folder); } catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
        var root = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(root) || string.Equals(full, Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase)) return false;
        var filesOnly = !rule.Recurse && rule.Masks.Length > 0 && rule.Masks.All(mask => mask != "*" && mask != "*.*");
        // Such a rule only ever touches matching files directly in the folder, never anything below it.
        if (filesOnly) return true;
        foreach (var guarded in protectedPaths)
            if (full.Equals(guarded, StringComparison.OrdinalIgnoreCase) || IsInside(guarded, full)) return false;
        return true;
    }

    /// <summary>Folders that must never be cleaned as a whole, and never contain a cleanable folder's parent.</summary>
    internal static IReadOnlyList<string> Protected(CleanupContext context)
    {
        var list = new List<string>();
        void Add(string? path) { if (!string.IsNullOrWhiteSpace(path) && Path.IsPathRooted(path)) list.Add(Normalize(path)); }
        var profile = context.Variable("USERPROFILE");
        var windows = context.Variable("WINDIR") ?? context.Variable("SystemRoot");
        Add(profile); Add(windows); Add(context.Variable("LOCALAPPDATA")); Add(context.Variable("APPDATA")); Add(context.Variable("PROGRAMDATA"));
        Add(context.Variable("ProgramFiles")); Add(context.Variable("ProgramFiles(x86)")); Add(context.Variable("ProgramW6432"));
        if (!string.IsNullOrWhiteSpace(profile) && Path.IsPathRooted(profile))
        {
            Add(Path.GetDirectoryName(Normalize(profile)));
            foreach (var name in new[] { "Desktop", "Documents", "Pictures", "Music", "Videos", "Downloads", "OneDrive", "AppData" }) Add(Path.Combine(profile, name));
        }
        if (!string.IsNullOrWhiteSpace(windows) && Path.IsPathRooted(windows))
            foreach (var name in new[] { "System32", "SysWOW64", "WinSxS", "Installer", "Fonts", "assembly", "Boot" }) Add(Path.Combine(windows, name));
        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}

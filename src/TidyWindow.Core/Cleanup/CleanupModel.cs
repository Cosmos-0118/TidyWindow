using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace TidyWindow.Next.Core;

public enum CleanupLevel { Recommended, Review }
public enum CleanupAction { Files, RecycleBin, ComponentStore, PreviousWindows, Hibernation, DiskCleanup }

/// <summary>One thing the cleaner knows how to clean. Rules are data (cleanup-catalog.json); nothing outside a rule's paths is ever touched.</summary>
public sealed class CleanupRule
{
    public required string Id { get; init; }
    public required string Group { get; init; }
    public required string Name { get; init; }
    public string Detail { get; init; } = "";
    public CleanupLevel Level { get; init; } = CleanupLevel.Recommended;
    public CleanupAction Action { get; init; } = CleanupAction.Files;
    /// <summary>Folders whose contents are cleaned. %variables% are expanded; a segment may contain * or ? to match several folders (browser profiles).</summary>
    public string[] Paths { get; init; } = [];
    /// <summary>Data folders of a Chromium based app (the folder holding "Local State", or a pattern for several). Every well-known cache folder in it and in each of its profiles is cleaned, so one list of cache names serves every browser and Electron app.</summary>
    public string[] ChromiumData { get; init; } = [];
    /// <summary>Windows Disk Cleanup handlers (VolumeCaches in the registry). Files rules add the folders Windows itself lists for them, so the list follows the installed Windows version; DiskCleanup rules hand the work to those handlers.</summary>
    public string[] Handlers { get; init; } = [];
    /// <summary>"chromium" adds the cache folders of every Chromium based app found in the profile (Electron, WebView2, CEF), whatever its name.</summary>
    public string? Discover { get; init; }
    /// <summary>Optional ';'-separated file masks. Without it every file in the folder is cleaned.</summary>
    public string? Files { get; init; }
    public int MinAgeDays { get; init; }
    public bool Recurse { get; init; } = true;
    public bool Admin { get; init; }
    /// <summary>Files are moved to the undo folder instead of deleted.</summary>
    public bool Quarantine { get; init; }
    /// <summary>Running apps that keep these files open. Their files are skipped, not forced.</summary>
    public string[] Processes { get; init; } = [];

    /// <summary>Handled by Windows' own tools in an elevated helper instead of by deleting files.</summary>
    public bool IsSystemAction => Action is CleanupAction.ComponentStore or CleanupAction.PreviousWindows or CleanupAction.Hibernation or CleanupAction.DiskCleanup;

    public string[] Masks => string.IsNullOrWhiteSpace(Files) ? [] : Files.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>What a rule found. Sizes cover exactly the files a clean would remove.</summary>
/// <param name="NeedsAdmin">Measured without elevation; cleaning it needs administrator rights.</param>
/// <param name="Unmeasured">Worth offering, but its size cannot be known before it is cleaned.</param>
public sealed record CleanupFinding(CleanupRule Rule, string[] Roots, long Bytes, int Files, int Unreadable, bool AppRunning, string? Skipped = null, bool NeedsAdmin = false, bool Unmeasured = false)
{
    public bool HasContent => Bytes > 0 || Files > 0 || Unmeasured;
}

/// <param name="Stopped">The clean was stopped before it finished. The counts cover what was done up to then, and all of it stays done.</param>
public sealed record CleanupResult(long FreedBytes, int Removed, int InUse, int Denied, int Failed, string? UndoId, long HeldBytes, TimeSpan Elapsed, bool Stopped = false)
{
    public int Skipped => InUse + Denied + Failed;
}

public sealed record CleanupProgress(string Name, long FreedBytes, int Removed, bool Starting = false);

/// <summary>Machine facts the engine depends on, replaceable in tests.</summary>
public sealed class CleanupContext
{
    public static CleanupContext Current { get; } = new();
    /// <summary>Environment variables, plus %DOWNLOADS%, %DOCUMENTS%, %DESKTOP%, %PICTURES%, %MUSIC% and %VIDEOS%, which follow the folder if the user moved it.</summary>
    public Func<string, string?> Variable { get; init; } = CleanupNative.Variable;
    public bool IsAdministrator { get; init; } = CleanupNative.IsAdministrator();
    public Func<DateTime> Now { get; init; } = () => DateTime.UtcNow;
    /// <summary>Names of running processes, read once per scan; asking per app costs far more than listing them all.</summary>
    public Func<IReadOnlySet<string>> RunningProcesses { get; init; } = CleanupNative.RunningProcesses;
    /// <summary>The folders a Disk Cleanup handler lists in the registry; null when this Windows has no such handler.</summary>
    public Func<string, string[]?> HandlerFolders { get; init; } = CleanupNative.VolumeCacheFolders;
    /// <summary>Parallel folder operations. Directory work is latency bound, so more than the core count helps.</summary>
    public int Parallelism { get; init; } = Math.Clamp(Environment.ProcessorCount * 2, 4, 32);
}

public static class CleanupCatalog
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };

    public static IReadOnlyList<CleanupRule> Load()
    {
        using var stream = typeof(CleanupCatalog).Assembly.GetManifestResourceStream("cleanup-catalog.json") ?? throw new InvalidOperationException("The cleanup catalog is missing from the app.");
        var catalog = JsonSerializer.Deserialize<CatalogFile>(stream, Options) ?? throw new InvalidOperationException("The cleanup catalog is empty.");
        // A catalog that breaks the rules is a bug in the app, so it is refused as a whole before anything can be scanned or cleaned.
        var problems = CleanupGuard.Validate(catalog.Rules);
        if (problems.Count > 0) throw new InvalidOperationException("The cleanup catalog is not valid: " + string.Join(" ", problems));
        return catalog.Rules;
    }

    private sealed class CatalogFile { public List<CleanupRule> Rules { get; init; } = []; }
}

/// <summary>
/// The rules every catalog entry must follow before it is trusted. They catch the mistakes a hand-written catalog can make (a rule listed twice, a fixed drive letter,
/// a path that climbs out of its folder, a Windows tool offered without administrator rights); where a folder is allowed to be is judged at scan time by <see cref="CleanupPaths"/>.
/// </summary>
public static partial class CleanupGuard
{
    [GeneratedRegex(@"^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex Slug();

    /// <summary>Everything wrong with a catalog, as sentences. Empty when it is fine.</summary>
    public static List<string> Validate(IReadOnlyList<CleanupRule> rules)
    {
        var problems = new List<string>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in rules)
        {
            var who = $"“{rule.Id}”";
            if (!Slug().IsMatch(rule.Id)) problems.Add($"{who} needs a lowercase id with dashes.");
            if (!ids.Add(rule.Id)) problems.Add($"{who} is listed twice.");
            if (string.IsNullOrWhiteSpace(rule.Group) || string.IsNullOrWhiteSpace(rule.Name) || string.IsNullOrWhiteSpace(rule.Detail)) problems.Add($"{who} needs a group, a name and a detail.");
            if (rule.MinAgeDays < 0) problems.Add($"{who} has a negative age.");
            if (rule.Discover is not null && !string.Equals(rule.Discover, "chromium", StringComparison.OrdinalIgnoreCase)) problems.Add($"{who} discovers something unknown: “{rule.Discover}”.");
            foreach (var path in rule.Paths.Concat(rule.ChromiumData))
            {
                if (string.IsNullOrWhiteSpace(path)) problems.Add($"{who} has an empty path.");
                else if (!path.StartsWith('%')) problems.Add($"{who}: “{path}” must start with a %variable%, never with a fixed drive or folder.");
                else if (path.Contains("..")) problems.Add($"{who}: “{path}” climbs out of its folder.");
            }
            foreach (var handler in rule.Handlers)
                if (string.IsNullOrWhiteSpace(handler) || handler.AsSpan().IndexOfAny('\\', '/') >= 0) problems.Add($"{who}: “{handler}” is not the name of a Disk Cleanup handler.");
            if (rule.Files is not null && rule.Masks.Length == 0) problems.Add($"{who} names no file masks.");
            if (rule.Masks.Any(mask => mask.AsSpan().IndexOfAny('\\', '/') >= 0 || mask.Contains(".."))) problems.Add($"{who}: a file mask may name files, not paths.");
            if (rule.Quarantine && rule.Level != CleanupLevel.Review) problems.Add($"{who} holds files for undo, so it must be marked review.");
            if (rule.Quarantine && rule.Action != CleanupAction.Files) problems.Add($"{who} can only hold files, not run a Windows tool.");
            if (rule.IsSystemAction && !rule.Admin) problems.Add($"{who} runs a Windows tool, which needs administrator rights.");
            if (rule.Action is CleanupAction.DiskCleanup or CleanupAction.PreviousWindows or CleanupAction.Hibernation && rule.Level != CleanupLevel.Review) problems.Add($"{who} cannot be undone, so it must be marked review.");
            switch (rule.Action)
            {
                case CleanupAction.Files:
                    if (rule.Paths.Length == 0 && rule.ChromiumData.Length == 0 && rule.Handlers.Length == 0 && rule.Discover is null) problems.Add($"{who} cleans nothing.");
                    break;
                case CleanupAction.DiskCleanup:
                    if (rule.Handlers.Length == 0) problems.Add($"{who} names no Disk Cleanup handler.");
                    if (rule.Paths.Length > 0 || rule.ChromiumData.Length > 0 || rule.Discover is not null) problems.Add($"{who} hands its work to Windows and cannot name folders of its own.");
                    break;
                case CleanupAction.PreviousWindows:
                    if (rule.Paths.Length == 0) problems.Add($"{who} must name the previous installation's folder.");
                    if (rule.ChromiumData.Length > 0 || rule.Handlers.Length > 0 || rule.Discover is not null) problems.Add($"{who} hands its work to Windows and can only name the folder it removes.");
                    break;
                default:
                    if (rule.Paths.Length > 0 || rule.ChromiumData.Length > 0 || rule.Handlers.Length > 0 || rule.Discover is not null) problems.Add($"{who} is handled by Windows and cannot name folders.");
                    break;
            }
        }
        return problems;
    }
}

using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TidyWindow.Next.Core;

public enum CleanupLevel { Recommended, Review }
public enum CleanupAction { Files, RecycleBin, ComponentStore, PreviousWindows, Hibernation }

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
    public bool IsSystemAction => Action is CleanupAction.ComponentStore or CleanupAction.PreviousWindows or CleanupAction.Hibernation;

    public string[] Masks => string.IsNullOrWhiteSpace(Files) ? [] : Files.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>What a rule found. Sizes cover exactly the files a clean would remove.</summary>
/// <param name="NeedsAdmin">Measured without elevation; cleaning it needs administrator rights.</param>
/// <param name="Unmeasured">Worth offering, but its size cannot be known before it is cleaned.</param>
public sealed record CleanupFinding(CleanupRule Rule, string[] Roots, long Bytes, int Files, int Unreadable, bool AppRunning, string? Skipped = null, bool NeedsAdmin = false, bool Unmeasured = false)
{
    public bool HasContent => Bytes > 0 || Files > 0 || Unmeasured;
}

public sealed record CleanupResult(long FreedBytes, int Removed, int InUse, int Denied, int Failed, string? UndoId, long HeldBytes, TimeSpan Elapsed)
{
    public int Skipped => InUse + Denied + Failed;
}

public sealed record CleanupProgress(string Name, long FreedBytes, int Removed);

/// <summary>Machine facts the engine depends on, replaceable in tests.</summary>
public sealed class CleanupContext
{
    public static CleanupContext Current { get; } = new();
    public Func<string, string?> Variable { get; init; } = name => Environment.GetEnvironmentVariable(name);
    public bool IsAdministrator { get; init; } = CleanupNative.IsAdministrator();
    public Func<DateTime> Now { get; init; } = () => DateTime.UtcNow;
    /// <summary>Names of running processes, read once per scan; asking per app costs far more than listing them all.</summary>
    public Func<IReadOnlySet<string>> RunningProcesses { get; init; } = CleanupNative.RunningProcesses;
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
        return catalog.Rules;
    }

    private sealed class CatalogFile { public List<CleanupRule> Rules { get; init; } = []; }
}

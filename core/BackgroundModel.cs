using System.Text.Json;
using System.Text.Json.Serialization;

namespace TidyWindow.Next.Core;

public enum BackgroundGroup { Telemetry, Ai, Optional }
public enum BackgroundRisk { Safe, Careful }
public enum BackgroundTargetKind { Service, Task, Policy }

/// <summary>One thing an item switches: a Windows service, a scheduled task, or a Windows policy value. Names come from the catalog, never from a caller.</summary>
public sealed class BackgroundTarget
{
    public required BackgroundTargetKind Kind { get; init; }
    /// <summary>The service name, the task's full path, or the policy value name.</summary>
    public required string Name { get; init; }
    /// <summary>Policies only: the key under HKEY_LOCAL_MACHINE.</summary>
    public string Key { get; init; } = "";
    /// <summary>Services only. "disabled" stops it from starting at all; "manual" keeps it available but not running all the time.</summary>
    public string Off { get; init; } = "disabled";
    /// <summary>Services only: how Windows normally starts it (2 automatic, 3 manual). Used when a saved record is not available.</summary>
    public int DefaultStart { get; init; } = 3;
    public bool DefaultDelayed { get; init; }
    /// <summary>Services only: the folder the program lives in when it is not in the Windows folder, such as the Windows Media Player folder under Program Files. Environment variables are expanded.</summary>
    public string Image { get; init; } = "";
    /// <summary>Policies only: the value that blocks the feature.</summary>
    public int Block { get; init; } = 1;
    /// <summary>Policies only: Windows Home ignores this setting.</summary>
    public bool NeedsPro { get; init; }
}

/// <summary>One thing that may run in the background, described for a person rather than for an administrator. Items are data (background-catalog.json).</summary>
public sealed class BackgroundItem
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required BackgroundGroup Group { get; init; }
    /// <summary>A few words on what area it belongs to, such as "Error reports" or "Xbox".</summary>
    public string Topic { get; init; } = "";
    public string Icon { get; init; } = "";
    public required string Summary { get; init; }
    /// <summary>What a person gives up by turning it off.</summary>
    public required string Effect { get; init; }
    public BackgroundRisk Risk { get; init; } = BackgroundRisk.Safe;
    /// <summary>Names a person may see in Task Manager, so they can search for what they noticed.</summary>
    public string[] Processes { get; init; } = [];
    public required BackgroundTarget[] Targets { get; init; }
}

// What the scanner reads from the PC. Each record is one fact, so tests can describe a PC without touching the registry.
public sealed record ServiceFact(string Name, string DisplayName, int Start, bool Delayed, bool Running, bool HasTrigger, string ImagePath, int Type, IReadOnlyList<string> DependsOn);
public sealed record TaskFact(string Path, bool Enabled, bool Running);

public interface IBackgroundSystem
{
    string WindowsFolder { get; }
    /// <summary>The Windows edition id, such as "Professional" or "Core" (Home).</summary>
    string Edition { get; }
    string Expand(string text);
    /// <summary>Reads the service list again. Called once at the start of every scan.</summary>
    void Refresh();
    ServiceFact? Service(string name);
    /// <summary>Other services that need this one and are not switched off.</summary>
    IReadOnlyList<ServiceFact> Dependents(string name);
    TaskFact? Task(string path);
    int? Policy(string key, string name);
}

/// <summary>The few writes the page may make. Nothing here deletes a service or a task, or edits what a program installed.</summary>
public interface IBackgroundWriter
{
    void SetServiceStart(string name, int start, bool delayed);
    /// <summary>Stops a running service and waits for it. False when it would not stop in time.</summary>
    bool StopService(string name);
    void StartService(string name);
    /// <summary>Turns a task on or off, and ends it if it is running.</summary>
    void SetTask(string path, bool enabled);
    /// <summary>Sets a policy value, or removes it when <paramref name="value"/> is null.</summary>
    void SetPolicy(string key, string name, int? value);
}

/// <summary>How one target looked before it was changed, so it can be put back exactly.</summary>
public sealed record BackgroundSaved(BackgroundTargetKind Kind, string Name, bool WasOn, int Start = 0, bool Delayed = false, int? Value = null);

public sealed record BackgroundRequest(string Id, bool Enable, IReadOnlyList<BackgroundSaved>? Saved = null);
public sealed record BackgroundOutcome(bool Ok, string? Error = null, string? Note = null, IReadOnlyList<BackgroundSaved>? Before = null);

/// <summary>Applies one change and checks it took. Implemented on this PC and, with administrator rights, by the helper.</summary>
public interface IBackgroundChanger
{
    BackgroundOutcome Change(BackgroundRequest request);
}

/// <summary>What TidyWindow turned off, so it can all be turned back on later, even if the app was closed in between.</summary>
public sealed record BackgroundChange(string Id, string Name, DateTimeOffset When, IReadOnlyList<BackgroundSaved> Saved);

public static class BackgroundCatalog
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };
    private static IReadOnlyList<BackgroundItem>? loaded;

    public static IReadOnlyList<BackgroundItem> Load() => loaded ??= Read();

    private static IReadOnlyList<BackgroundItem> Read()
    {
        using var stream = typeof(BackgroundCatalog).Assembly.GetManifestResourceStream("background-catalog.json") ?? throw new InvalidOperationException("The background catalog is missing from the app.");
        var file = JsonSerializer.Deserialize<CatalogFile>(stream, Options) ?? throw new InvalidOperationException("The background catalog is empty.");
        // A catalog that breaks the rules is a bug in the app, so it is refused before anything can be switched.
        var problems = BackgroundGuard.Validate(file.Items);
        if (problems.Count > 0) throw new InvalidOperationException("The background catalog is not valid: " + string.Join(" ", problems));
        return file.Items;
    }

    private sealed class CatalogFile { public List<BackgroundItem> Items { get; init; } = []; }
}

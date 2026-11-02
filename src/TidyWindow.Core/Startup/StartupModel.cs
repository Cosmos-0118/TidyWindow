namespace TidyWindow.Next.Core;

public enum StartupKind { RunKey, StartupFolder, ScheduledTask, Service, StoreApp, WindowsSetting }
public enum StartupScope { User, Machine }

/// <summary>How careful to be: Optional items only add to startup time, Careful ones can take a feature with them, Protected ones are never changed.</summary>
public enum StartupRisk { Optional, Careful, Protected }
public enum StartupTrust { Unknown, Verified, Unsigned, Invalid }

public sealed record StartupEntry(
    string Id, StartupKind Kind, StartupScope Scope, string Name, string Publisher, string Description,
    string Command, string ExePath, string Location, string Source, string Key,
    bool Enabled, StartupRisk Risk, string Reason, StartupTrust Trust, bool Running, bool FileMissing,
    int ServiceStart = 0, bool ServiceDelayed = false, PublisherSource PublisherFrom = PublisherSource.None,
    bool ServiceTriggered = false, string FolderPath = "")
{
    public bool CanChange => Risk != StartupRisk.Protected;
    /// <summary>Explorer keeps no on/off record for some Run-style keys, so those items can only be taken out, never switched.</summary>
    public bool CanToggle => CanChange && Array.IndexOf(StartupLocations.NoSwitchSources, Source) < 0;
    /// <summary>Items that live somewhere TidyWindow can take them out of and put back exactly as they were.</summary>
    public bool CanRemove => CanChange && Kind is StartupKind.RunKey or StartupKind.StartupFolder or StartupKind.ScheduledTask;
}

// What the scanner reads from the PC. Each record is one fact, so tests can describe a PC without touching the registry.
/// <param name="Source">Which Run-style key it came from (see <see cref="StartupLocations"/>). Empty means "Run", or "Run32" when <paramref name="Wow64"/>.</param>
/// <param name="Expandable">The value is stored with %variables% to expand, which a restore has to keep.</param>
public sealed record RunValue(StartupScope Scope, bool Wow64, string Name, string Command, string Source = "", bool Expandable = false)
{
    public string Where => Source.Length > 0 ? Source : Wow64 ? "Run32" : "Run";
}
public sealed record FolderItem(StartupScope Scope, string FileName, string FullPath, string Target, string Arguments);
public sealed record ScheduledStartup(string Path, string Name, string Description, string Author, bool Enabled, string Program, string Arguments, bool AtLogon, bool AtBoot, string RunAs);
/// <param name="Triggered">Windows can also start it when an event happens (a network arriving, a device plugged in), whatever its start type says.</param>
/// <param name="ServiceDll">For a service that runs inside a shared Windows host process, the library that actually holds it.</param>
public sealed record ServiceFacts(string Name, string DisplayName, string Description, string ImagePath, int Type, int Start, bool Delayed, string Account, IReadOnlyList<string> DependsOn,
    bool Triggered = false, string ServiceDll = "");
public sealed record StoreStartup(string Family, string TaskId, int State, string PackageFullName, string DisplayName, string Publisher, string Executable);
public sealed record WindowsSetting(string Id, string Name, string Value, string Expected, string Location, StartupScope Scope = StartupScope.Machine);
/// <summary><see cref="Signer"/> is the company named by the file's digital signature, and is only filled in when that signature checks out.</summary>
public sealed record FileFacts(bool Exists, string Company, string Description, string Product, StartupTrust Trust, string Signer = "");

public sealed record StartupPlaces(string Windows, string UserProfile, IReadOnlyList<string> Scratch)
{
    // The corners of the Windows folder that any program can write to. A file there is no more part of Windows than one in Temp.
    private static readonly string[] OpenUnderWindows =
    [
        "Temp", "Tasks", "tracing", @"System32\Tasks", @"SysWOW64\Tasks", @"System32\spool\drivers\color", @"System32\spool\PRINTERS", @"System32\spool\SERVERS",
        @"Registration\CRMLog", @"System32\FxsTmp", @"SysWOW64\FxsTmp", @"System32\com\dmp", @"SysWOW64\com\dmp", @"System32\Microsoft\Crypto\RSA\MachineKeys", @"debug\WIA", "PLA"
    ];

    public bool InWindows(string path) => Under(path, Windows);
    /// <summary>Temporary and download folders, and the few places under Windows any program can write to: nothing is trusted for living in one of them.</summary>
    public bool InScratch(string path) => Scratch.Any(x => Under(path, x)) || (Windows.Length > 0 && OpenUnderWindows.Any(x => Under(path, Path.Combine(Windows, x))));

    private static bool Under(string path, string folder)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(folder)) return false;
        var root = folder.TrimEnd('\\') + "\\";
        return path.Replace('/', '\\').StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }
}

public interface IStartupSystem
{
    StartupPlaces Places { get; }
    IReadOnlyList<RunValue> RunValues();
    /// <summary>Explorer's own on/off record for one startup item (what Task Manager and Settings write), or null if there is none.</summary>
    byte[]? Approved(StartupScope scope, string subKey, string name);
    IReadOnlyList<FolderItem> FolderItems();
    /// <summary>Scheduled tasks that run at sign-in or boot and are not part of Windows, plus how many Windows-owned tasks were left out.</summary>
    (IReadOnlyList<ScheduledStartup> Tasks, int WindowsOwned) Tasks();
    IReadOnlyList<ServiceFacts> Services();
    IReadOnlyList<StoreStartup> StoreTasks();
    IReadOnlyList<WindowsSetting> WindowsSettings();
    FileFacts Describe(string path);
    /// <summary>The programs listed under "Installed apps", used to name the company behind a file that does not say.</summary>
    IReadOnlyList<UninstallEntry> InstalledPrograms();
    /// <summary>The publisher a Store package names in its manifest, for a file inside that package, or "" if it is not in one.</summary>
    string PackagePublisher(string exePath);
    string Expand(string text);
    bool FileExists(string path);
    string? FindOnPath(string name);
    ISet<string> RunningImages();
    /// <summary>The folders directly inside a folder (a Squirrel app keeps each version in its own app-* folder).</summary>
    IReadOnlyList<string> Subfolders(string folder) => [];
    /// <summary>Turns a resource reference such as "@%SystemRoot%\x.dll,-101" into the text it points to, or returns the text unchanged when it cannot.</summary>
    string Localize(string text) => text;
}

/// <summary>
/// The writes the manager is allowed to make. Switching an item off only flips a flag. Taking one out of startup is the only thing that deletes,
/// and it hands back a copy (the returned text or bytes) so the item can be put back exactly as it was.
/// </summary>
public interface IStartupWriter
{
    void SetApproved(StartupScope scope, string subKey, string name, bool enabled);
    void SetTask(string path, bool enabled);
    void SetService(string name, int start, bool delayed);
    void SetStoreTask(string family, string taskId, bool enabled);
    void RemoveRun(StartupScope scope, string source, string name);
    void RestoreRun(StartupScope scope, string source, string name, string command, bool expandable);
    /// <returns>The file's contents, read before it was deleted.</returns>
    byte[] RemoveFolderItem(StartupScope scope, string fileName);
    void RestoreFolderItem(StartupScope scope, string fileName, byte[] contents);
    /// <returns>The task's definition (XML), read before it was deleted.</returns>
    string RemoveTask(string path);
    void RestoreTask(string path, string xml);
}

/// <summary>Everything needed to put a removed startup item back. <see cref="Data"/> is the shortcut's bytes (base64) or the task's XML.</summary>
/// <param name="Command">For a Run value, the command to write back. For the other kinds, the command the item was listed with, so a put-back can tell the same item from a newcomer with its name.</param>
public sealed record StartupVault(StartupKind Kind, StartupScope Scope, string Source, string Key, string Command = "", bool Expandable = false, string Data = "");


public enum StartupAction { Toggle, Remove, Restore }

public sealed record StartupRequest(string Id, bool Enable, int RestoreStart = 2, bool RestoreDelayed = false, StartupAction Action = StartupAction.Toggle, StartupVault? Vault = null);
public sealed record StartupOutcome(bool Ok, string? Error = null, StartupEntry? Entry = null, StartupVault? Vault = null);

/// <summary>Applies one change and checks it took. Implemented on this PC and, with administrator rights, by the helper.</summary>
public interface IStartupChanger
{
    StartupOutcome Change(StartupRequest request);
}

/// <summary>What TidyWindow turned off, so it can all be turned back on later, even if the app was closed in between.</summary>
public sealed record StartupChange(string Id, string Name, StartupKind Kind, StartupScope Scope, DateTimeOffset When, int ServiceStart = 2, bool ServiceDelayed = false,
    bool Removed = false, StartupVault? Vault = null);

/// <summary>The Run-style registry keys Windows reads at sign-in, by the name TidyWindow gives each. Explorer keeps an on/off record for the first two only.</summary>
public static class StartupLocations
{
    public const string Run = "Run", Run32 = "Run32", RunOnce = "RunOnce", RunOnce32 = "RunOnce32", PolicyRun = "PolicyRun";

    /// <summary>Where Explorer's own on/off record cannot reach: these can only be taken out.</summary>
    public static readonly string[] NoSwitchSources = [RunOnce, RunOnce32, PolicyRun];

    /// <summary>The key under the hive for a source, or null if it is not one TidyWindow reads.</summary>
    public static string? PathFor(string source) => source switch
    {
        Run => @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run",
        Run32 => @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run",
        RunOnce => @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce",
        RunOnce32 => @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\RunOnce",
        PolicyRun => @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer\Run",
        _ => null
    };

    /// <summary>The Explorer on/off record that goes with a source, or null when Explorer has none.</summary>
    public static string? ApprovedKey(string source) => source switch { Run => "Run", Run32 => "Run32", _ => null };
}

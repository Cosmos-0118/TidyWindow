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
    int ServiceStart = 0, bool ServiceDelayed = false, PublisherSource PublisherFrom = PublisherSource.None)
{
    public bool CanChange => Risk != StartupRisk.Protected;
}

// What the scanner reads from the PC. Each record is one fact, so tests can describe a PC without touching the registry.
public sealed record RunValue(StartupScope Scope, bool Wow64, string Name, string Command);
public sealed record FolderItem(StartupScope Scope, string FileName, string FullPath, string Target, string Arguments);
public sealed record ScheduledStartup(string Path, string Name, string Description, string Author, bool Enabled, string Program, string Arguments, bool AtLogon, bool AtBoot, string RunAs);
public sealed record ServiceFacts(string Name, string DisplayName, string Description, string ImagePath, int Type, int Start, bool Delayed, string Account, IReadOnlyList<string> DependsOn);
public sealed record StoreStartup(string Family, string TaskId, int State, string PackageFullName, string DisplayName, string Publisher, string Executable);
public sealed record WindowsSetting(string Id, string Name, string Value, string Expected, string Location);
/// <summary><see cref="Signer"/> is the company named by the file's digital signature, and is only filled in when that signature checks out.</summary>
public sealed record FileFacts(bool Exists, string Company, string Description, string Product, StartupTrust Trust, string Signer = "");

public sealed record StartupPlaces(string Windows, string UserProfile, IReadOnlyList<string> Scratch)
{
    public bool InWindows(string path) => Under(path, Windows);
    public bool InScratch(string path) => Scratch.Any(x => Under(path, x));

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
}

/// <summary>The few writes the manager is allowed to make. Nothing here deletes or edits what a program installed.</summary>
public interface IStartupWriter
{
    void SetApproved(StartupScope scope, string subKey, string name, bool enabled);
    void SetTask(string path, bool enabled);
    void SetService(string name, int start, bool delayed);
    void SetStoreTask(string family, string taskId, bool enabled);
}

public sealed record StartupRequest(string Id, bool Enable, int RestoreStart = 2, bool RestoreDelayed = false);
public sealed record StartupOutcome(bool Ok, string? Error = null, StartupEntry? Entry = null);

/// <summary>Applies one change and checks it took. Implemented on this PC and, with administrator rights, by the helper.</summary>
public interface IStartupChanger
{
    StartupOutcome Change(StartupRequest request);
}

/// <summary>What TidyWindow turned off, so it can all be turned back on later, even if the app was closed in between.</summary>
public sealed record StartupChange(string Id, string Name, StartupKind Kind, StartupScope Scope, DateTimeOffset When, int ServiceStart = 2, bool ServiceDelayed = false);

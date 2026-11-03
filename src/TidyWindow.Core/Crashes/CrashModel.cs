using System.Text.Json.Serialization;

namespace TidyWindow.Next.Core;

/// <summary>What went wrong. Apps are split from Windows itself because they are read and fixed differently.</summary>
public enum CrashKind
{
    AppCrash, AppHang,
    BlueScreen, UnexpectedShutdown, ServiceFailure, DriverReset, HardwareError, StorageError, LowMemory,
    /// <summary>A Windows Error Reporting record that only carries the report folder for another event. Never shown.</summary>
    Report
}

public static class CrashKinds
{
    public static bool IsApp(this CrashKind kind) => kind is CrashKind.AppCrash or CrashKind.AppHang;
    public static bool IsWindows(this CrashKind kind) => kind is not (CrashKind.AppCrash or CrashKind.AppHang or CrashKind.Report);

    public static string Label(this CrashKind kind) => kind switch
    {
        CrashKind.AppCrash => "Crash",
        CrashKind.AppHang => "Not responding",
        CrashKind.BlueScreen => "Blue screen",
        CrashKind.UnexpectedShutdown => "Unexpected shutdown",
        CrashKind.ServiceFailure => "Service failure",
        CrashKind.DriverReset => "Driver reset",
        CrashKind.HardwareError => "Hardware error",
        CrashKind.StorageError => "Disk error",
        CrashKind.LowMemory => "Low memory",
        _ => "Report"
    };

    /// <summary>Extra words a person might type for this kind, so "freeze" finds a hang and "bsod" finds a blue screen.</summary>
    internal static string Synonyms(this CrashKind kind) => kind switch
    {
        CrashKind.AppCrash => "crash crashed closed stopped working terminated",
        CrashKind.AppHang => "hang hung freeze frozen freezing not responding stuck",
        CrashKind.BlueScreen => "bsod blue screen bugcheck stop error kernel",
        CrashKind.UnexpectedShutdown => "shutdown restart reboot power loss kernel-power",
        CrashKind.ServiceFailure => "service background",
        CrashKind.DriverReset => "driver gpu graphics display video tdr",
        CrashKind.HardwareError => "hardware whea cpu memory ram machine check",
        CrashKind.StorageError => "disk drive storage ssd hdd ntfs file system",
        CrashKind.LowMemory => "memory ram out of memory resource",
        _ => ""
    };
}

/// <summary>One event as Windows wrote it, before anything is made of it. <see cref="Data"/> keeps the order Windows used; an unnamed value has an empty name.</summary>
public sealed record RawEvent(string Log, long RecordId, int EventId, string Provider, DateTimeOffset Time, IReadOnlyList<KeyValuePair<string, string>> Data)
{
    public string? Get(string name) => Data.FirstOrDefault(x => string.Equals(x.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
    public string? At(int index) => index >= 0 && index < Data.Count ? Data[index].Value : null;
    /// <summary>The named value, or the one in that position when the event carries no names.</summary>
    public string Field(string name, int index) => (Get(name) ?? At(index) ?? "").Trim();
}

/// <summary>A crash, freeze or Windows problem, in the terms a person would use. Saved to disk as one JSON line.</summary>
public sealed record CrashEvent
{
    public required string Id { get; init; }
    public required DateTimeOffset Time { get; init; }
    public required CrashKind Kind { get; init; }
    /// <summary>The Windows component that wrote it (for example "Application Error").</summary>
    public string Source { get; init; } = "";
    public int EventId { get; init; }
    /// <summary>The program's file name (chrome.exe), or for a service, driver or device the name it goes by.</summary>
    public string App { get; init; } = "";
    public string AppPath { get; init; } = "";
    public string AppVersion { get; init; } = "";
    public string Module { get; init; } = "";
    public string ModulePath { get; init; } = "";
    public string ModuleVersion { get; init; } = "";
    /// <summary>The exception code, bugcheck code or kernel event code, as 0x…</summary>
    public string Code { get; init; } = "";
    public string Offset { get; init; } = "";
    public string ProcessId { get; init; } = "";
    public string ReportId { get; init; } = "";
    /// <summary>The Windows Error Reporting folder for this event, when Windows still keeps one.</summary>
    public string ReportFolder { get; init; } = "";
    public string DumpFile { get; init; } = "";
    /// <summary>The managed (.NET) exception, first line, when the program was a .NET one.</summary>
    public string Managed { get; init; } = "";
    /// <summary>Everything else worth reading: a stack trace, the failing service, the raw values.</summary>
    public string Detail { get; init; } = "";

    /// <summary>How many times this happened within a few minutes of each other, folded into this one entry. Worked out when the list is built, never saved.</summary>
    [JsonIgnore] public int Repeats { get; init; } = 1;
    [JsonIgnore] public string FileName => AppPath.Length > 0 ? System.IO.Path.GetFileName(AppPath.Replace('/', '\\')) : App;
    /// <summary>Groups every event from the same program. Two programs with the same file name are one app here.</summary>
    [JsonIgnore] public string AppKey => (App.Length > 0 ? App : FileName).Trim().ToLowerInvariant();
    [JsonIgnore] public string AppName => CrashNames.Friendly(App.Length > 0 ? App : FileName);
    [JsonIgnore] public string CodeName => Kind switch
    {
        CrashKind.BlueScreen => CrashKnowledge.BugcheckName(Code),
        CrashKind.DriverReset or CrashKind.HardwareError when Source == "Windows Error Reporting" => CrashKnowledge.LiveKernelName(Code),
        _ => CrashKnowledge.ExceptionName(Code)
    };
    /// <summary>One line saying what happened.</summary>
    [JsonIgnore] public string Headline => CrashKnowledge.Headline(this);
    /// <summary>A plain-language reading of the cause, or empty when there is nothing useful to add.</summary>
    [JsonIgnore] public string Explanation => CrashKnowledge.Explain(this);
}

public static class CrashNames
{
    /// <summary>"chrome.exe" becomes "chrome". The file name is the one thing every event has, so it stays the identity; a friendlier name is the page's job.</summary>
    public static string Friendly(string name)
    {
        name = name.Trim();
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && name.Length > 4 ? name[..^4] : name;
    }
}

/// <summary>How a program has been doing: every crash and freeze, grouped the way a person asks about it.</summary>
public sealed record CrashApp(string Key, string Name, string Path, int Crashes, int Hangs, DateTimeOffset First, DateTimeOffset Last, IReadOnlyList<CrashEvent> Events, IReadOnlyList<CrashSignature> Signatures, IReadOnlyList<string> Versions, IReadOnlyList<int> Daily)
{
    public int Total => Crashes + Hangs;
}

/// <summary>The same failure happening again: the same file, failing the same way.</summary>
public sealed record CrashSignature(string Module, string Code, string CodeName, int Count, DateTimeOffset Last)
{
    public string Label => Module.Length > 0 && CodeName.Length > 0 ? $"{Module} · {CodeName}" : Module.Length > 0 ? Module : CodeName.Length > 0 ? CodeName : "Unknown cause";
}

/// <summary>Something the whole list says that no single row does.</summary>
public sealed record CrashInsight(string Title, string Detail);

public sealed record CrashScan(int Added, IReadOnlyList<string> Problems, TimeSpan Elapsed);

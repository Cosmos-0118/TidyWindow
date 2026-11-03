using System.Globalization;
using System.Text;

namespace TidyWindow.Next.Core;

/// <summary>Where crash records live. The scanner asks for exactly these and the parser understands exactly these; one list keeps them agreeing.</summary>
internal sealed record CrashSource(string Log, string Provider, int[] Ids, string[]? EventNames = null);

internal static class CrashSources
{
    // Matching on the component as well as the number matters: other software reuses 1000 and 1002 for its own routine messages.
    public static readonly CrashSource[] All =
    [
        new("Application", "Application Error", [1000]),
        new("Application", "Application Hang", [1002]),
        new("Application", ".NET Runtime", [1026]),
        new("Application", "Windows Error Reporting", [1001], ["APPCRASH", "CLR20r3", "BEX", "BEX64", "AppHangB1", "AppHangXProcB1", "AppHangTransient", "MoAppCrash", "MoAppHang", "MoBEX", "LiveKernelEvent"]),
        new("System", "Microsoft-Windows-WER-SystemErrorReporting", [1001]),
        new("System", "Microsoft-Windows-Kernel-Power", [41]),
        new("System", "EventLog", [6008]),
        new("System", "Service Control Manager", [7000, 7009, 7031, 7034]),
        new("System", "Display", [4101]),
        new("System", "Microsoft-Windows-WHEA-Logger", [1, 17, 18, 19, 20]),
        new("System", "disk", [7, 11, 51, 153]),
        new("System", "Microsoft-Windows-Ntfs", [55]),
        new("System", "Microsoft-Windows-Resource-Exhaustion-Detector", [2004])
    ];
}

/// <summary>Turns what Windows wrote into <see cref="CrashEvent"/>. Pure: the same event always reads the same way, and an event it does not understand is skipped, never guessed.</summary>
public static class CrashParser
{
    public static string IdOf(RawEvent raw) => $"{raw.Log}/{raw.RecordId}/{raw.Time.UtcTicks:x}";

    public static CrashEvent? Parse(RawEvent raw)
    {
        try { return Dispatch(raw); }
        // One record with an unexpected shape must never stop the rest of the scan.
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException or IndexOutOfRangeException or OverflowException) { return null; }
    }

    private static CrashEvent? Dispatch(RawEvent raw)
    {
        var id = raw.EventId;
        return raw.Provider.ToLowerInvariant() switch
        {
            "application error" when id == 1000 => AppCrash(raw),
            "application hang" when id == 1002 => AppHang(raw),
            ".net runtime" when id == 1026 => DotNet(raw),
            "windows error reporting" when id == 1001 => Report(raw),
            "microsoft-windows-wer-systemerrorreporting" when id == 1001 => BlueScreen(raw),
            "microsoft-windows-kernel-power" when id == 41 => KernelPower(raw),
            "eventlog" when id == 6008 => Make(raw, CrashKind.UnexpectedShutdown),
            "service control manager" => Service(raw),
            "display" when id == 4101 => Make(raw, CrashKind.DriverReset, app: raw.At(0)?.Trim() ?? "", detail: raw.At(1) is { Length: > 0 } device ? $"Device: {device.Trim()}" : ""),
            "microsoft-windows-whea-logger" => Make(raw, CrashKind.HardwareError, detail: Whea(id)),
            "disk" => Make(raw, CrashKind.StorageError, detail: Join(Disk(id), DataText(raw))),
            "microsoft-windows-ntfs" when id == 55 => Make(raw, CrashKind.StorageError, detail: Join("The file system on a disk is damaged", DataText(raw))),
            "microsoft-windows-resource-exhaustion-detector" when id == 2004 => Make(raw, CrashKind.LowMemory, detail: DataText(raw)),
            _ => null
        };
    }

    private static CrashEvent Make(RawEvent raw, CrashKind kind, string app = "", string detail = "", string code = "") => new()
    {
        Id = IdOf(raw), Time = raw.Time, Kind = kind, Source = raw.Provider, EventId = raw.EventId, App = app, Code = code, Detail = detail
    };

    private static CrashEvent AppCrash(RawEvent raw)
    {
        var package = raw.Field("PackageFullName", 13);
        return new()
        {
            Id = IdOf(raw), Time = raw.Time, Kind = CrashKind.AppCrash, Source = raw.Provider, EventId = raw.EventId,
            App = raw.Field("AppName", 0), AppVersion = raw.Field("AppVersion", 1),
            Module = raw.Field("ModuleName", 3), ModuleVersion = raw.Field("ModuleVersion", 4),
            Code = CrashKnowledge.NormalizeCode(raw.Field("ExceptionCode", 6)), Offset = raw.Field("FaultingOffset", 7),
            ProcessId = ProcessId(raw.Field("ProcessId", 8)), AppPath = raw.Field("AppPath", 10), ModulePath = raw.Field("ModulePath", 11),
            ReportId = raw.Field("IntegratorReportId", 12), Detail = package.Length > 0 ? $"Store app package: {package}" : ""
        };
    }

    private static CrashEvent AppHang(RawEvent raw)
    {
        var type = raw.Field("HangType", 9);
        var package = raw.Field("PackageFullName", 7);
        return new()
        {
            Id = IdOf(raw), Time = raw.Time, Kind = CrashKind.AppHang, Source = raw.Provider, EventId = raw.EventId,
            App = raw.Field("AppName", 0), AppVersion = raw.Field("AppVersion", 1), ProcessId = ProcessId(raw.Field("ProcessId", 2)),
            AppPath = raw.Field("ExeFileName", 5), ReportId = raw.Field("ReportId", 6),
            Detail = Join(type.Length > 0 && !type.Equals("Unknown", StringComparison.OrdinalIgnoreCase) ? $"Hang type: {type}" : "", package.Length > 0 ? $"Store app package: {package}" : "")
        };
    }

    // The .NET runtime writes one text block: "Application: x.exe", then the exception and its stack.
    private static CrashEvent? DotNet(RawEvent raw)
    {
        var text = (raw.At(0) ?? "").Replace("\r\n", "\n").Trim();
        var lines = text.Split('\n');
        string App() => lines.FirstOrDefault(x => x.StartsWith("Application:", StringComparison.OrdinalIgnoreCase)) is { } line ? line["Application:".Length..].Trim() : "";
        var app = App();
        var at = Array.FindIndex(lines, x => x.StartsWith("Exception Info:", StringComparison.OrdinalIgnoreCase));
        if (app.Length == 0 || at < 0) return null;
        var detail = string.Join('\n', lines.Skip(at).Take(24).Select(x => x.TrimEnd()));
        detail = detail["Exception Info:".Length..].Trim();
        if (detail.Length > 3000) detail = detail[..3000] + "\n…";
        var first = detail.Split('\n')[0].Trim();
        return new()
        {
            Id = IdOf(raw), Time = raw.Time, Kind = CrashKind.AppCrash, Source = raw.Provider, EventId = raw.EventId,
            App = app, Code = "0xE0434352", Managed = first.Length > 300 ? first[..300] + "…" : first, Detail = detail
        };
    }

    // Reports for crashes and freezes only carry the folder Windows keeps; the crash itself is the other event with the same report number.
    // A kernel event report is a problem of its own, because nothing else records it.
    private static CrashEvent? Report(RawEvent raw)
    {
        var name = raw.Field("EventName", 2);
        var report = raw.Field("ReportId", 19);
        var folder = Unprefixed(raw.Field("StorePath", 16));
        if (name.Equals("LiveKernelEvent", StringComparison.OrdinalIgnoreCase))
        {
            var code = CrashKnowledge.NormalizeCode(raw.Field("P1", 5));
            var known = CrashKnowledge.LiveKernelName(code);
            // Windows writes hundreds of these for harmless self-recoveries. Only the ones with a known meaning are worth a person's attention.
            if (known.Length == 0) return null;
            var kind = known.Contains("isplay", StringComparison.Ordinal) || known.Contains("raphics", StringComparison.Ordinal) ? CrashKind.DriverReset : CrashKind.HardwareError;
            return new() { Id = IdOf(raw), Time = raw.Time, Kind = kind, Source = raw.Provider, EventId = raw.EventId, Code = code, ReportId = report, ReportFolder = folder, Detail = known };
        }
        if (report.Length == 0 && folder.Length == 0) return null;
        return new() { Id = IdOf(raw), Time = raw.Time, Kind = CrashKind.Report, Source = raw.Provider, EventId = raw.EventId, ReportId = report, ReportFolder = folder };
    }

    // "The bugcheck was: 0x000000d1 (0x…, 0x…, 0x…, 0x…). A dump was saved in: C:\Windows\MEMORY.DMP."
    private static CrashEvent BlueScreen(RawEvent raw)
    {
        var text = raw.Field("param1", 0);
        var split = text.IndexOf(' ');
        var code = CrashKnowledge.NormalizeCode(split > 0 ? text[..split] : text);
        var parameters = split > 0 ? text[split..].Trim().Trim('(', ')') : "";
        return new()
        {
            Id = IdOf(raw), Time = raw.Time, Kind = CrashKind.BlueScreen, Source = raw.Provider, EventId = raw.EventId, Code = code,
            DumpFile = raw.Field("param2", 1), ReportId = raw.Field("param3", 2),
            Detail = parameters.Length > 0 ? $"Stop code parameters: {parameters}" : ""
        };
    }

    // Written when Windows starts again after it did not shut down properly. A stop code makes it a blue screen.
    private static CrashEvent KernelPower(RawEvent raw)
    {
        var stop = int.TryParse(raw.Get("BugcheckCode"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : 0;
        var notes = new List<string>();
        if (stop > 0)
        {
            var parts = new[] { "BugcheckParameter1", "BugcheckParameter2", "BugcheckParameter3", "BugcheckParameter4" }.Select(x => raw.Get(x)).Where(x => !string.IsNullOrEmpty(x));
            notes.Add($"Stop code parameters: {string.Join(", ", parts)}");
        }
        if (NonZero(raw.Get("PowerButtonTimestamp"))) notes.Add("The power button was held down before the restart.");
        if (NonZero(raw.Get("SleepInProgress"))) notes.Add("It happened while the PC was going to sleep or waking up.");
        return Make(raw, stop > 0 ? CrashKind.BlueScreen : CrashKind.UnexpectedShutdown, detail: string.Join('\n', notes), code: stop > 0 ? $"0x{(uint)stop:X8}" : "");
    }

    private static CrashEvent? Service(RawEvent raw) => raw.EventId switch
    {
        7031 => Make(raw, CrashKind.ServiceFailure, app: raw.Field("param1", 0), detail: raw.Field("param5", 4) is { Length: > 0 } action ? $"Windows is set to: {action}" : ""),
        7034 => Make(raw, CrashKind.ServiceFailure, app: raw.Field("param1", 0)),
        7000 => Make(raw, CrashKind.ServiceFailure, app: raw.Field("param1", 0), detail: $"failed to start: {Win32Message(raw.Field("param2", 1))}".TrimEnd(' ', ':')),
        7009 => Make(raw, CrashKind.ServiceFailure, app: raw.Field("param2", 1), detail: int.TryParse(raw.Field("param1", 0), out var ms) ? $"timed out while starting (waited {ms / 1000} s)" : "timed out while starting"),
        _ => null
    };

    private static string Whea(int id) => id switch
    {
        18 => "Fatal hardware error (machine check)",
        19 => "Corrected hardware error (processor)",
        17 => "Corrected hardware error (PCI Express)",
        20 => "Hardware error (PCI Express)",
        _ => "Hardware error reported by the processor or a device"
    };

    private static string Disk(int id) => id switch
    {
        7 => "A disk has a bad block",
        11 => "The disk controller reported an error",
        51 => "A disk error happened during a paging operation",
        _ => "A disk read or write had to be retried"
    };

    // Windows often leaves the reason as an unresolved "%%1053" (a Win32 error number). The common ones are spelled out; an unknown one is left out, not shown raw.
    private static string Win32Message(string text) => text.StartsWith("%%", StringComparison.Ordinal) && int.TryParse(text[2..], out var code)
        ? code switch
        {
            2 => "a file it needs was not found",
            5 => "access was denied",
            193 => "its program file is not valid",
            1053 => "it did not respond in time",
            1058 => "it is turned off",
            1068 => "a service it depends on failed",
            1069 => "its sign-in failed",
            1275 => "a driver was blocked",
            _ => ""
        }
        : text;

    private static bool NonZero(string? value) => !string.IsNullOrWhiteSpace(value) && value != "0" && !value.Equals("0x0", StringComparison.OrdinalIgnoreCase);
    private static string ProcessId(string value) => CrashKnowledge.TryParse(value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value : "", out var pid) ? pid.ToString(CultureInfo.InvariantCulture) : value;
    private static string Unprefixed(string path) => path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
    private static string Join(params string[] parts) => string.Join('\n', parts.Where(x => x.Length > 0));

    // Values this parser has no meaning for, kept readable rather than dropped.
    private static string DataText(RawEvent raw)
    {
        var text = new StringBuilder();
        foreach (var (name, value) in raw.Data)
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            text.Append(name.Length > 0 ? $"{name}: {value.Trim()}" : value.Trim()).Append('\n');
            if (text.Length > 800) break;
        }
        return text.ToString().Trim();
    }
}

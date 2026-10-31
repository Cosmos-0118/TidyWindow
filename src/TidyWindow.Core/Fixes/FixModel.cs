using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace TidyWindow.Next.Core;

public enum FixGroup { Windows, Network, Devices, Apps }
/// <summary>Whose settings and files a fix touches. User fixes run in the app; machine fixes need administrator rights.</summary>
public enum FixScope { User, Machine }
public enum FixRisk { Safe, Careful }
public enum FixRestart { Never, Maybe, Always }
public enum FixStepKind { Run, DeleteFiles, RenameFolder, StopExplorer, StartExplorer, RestartExplorer }
/// <summary>How a program writes its text. Most Windows tools use the console's OEM code page; sfc writes UTF-16.</summary>
public enum FixEncoding { Oem, Utf16, Utf8 }

/// <summary>A choice a person makes before running a fix. Options only switch catalog steps on or off; they never carry text into a command.</summary>
public sealed class FixOption
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public string Detail { get; init; } = "";
    public bool Default { get; init; }
}

/// <summary>One thing a fix does. Steps are data (fix-catalog.json); nothing outside the catalog can be run.</summary>
public sealed class FixStep
{
    public required string Title { get; init; }
    public FixStepKind Kind { get; init; } = FixStepKind.Run;
    /// <summary>Run: a program from the allow-list, such as "sfc.exe", "powershell.exe" or "onedrive".</summary>
    public string File { get; init; } = "";
    public string[] Args { get; init; } = [];
    /// <summary>How long a program may run before it is stopped.</summary>
    public int Minutes { get; init; } = 5;
    public int[] Ok { get; init; } = [0];
    /// <summary>Exit codes that are reported as a warning but do not fail the step.</summary>
    public int[] Warn { get; init; } = [];
    /// <summary>Exit codes that mean Windows needs a restart to finish (such as 3010).</summary>
    public int[] Restart { get; init; } = [];
    /// <summary>A step that may fail without stopping the fix.</summary>
    public bool Optional { get; init; }
    /// <summary>A step that runs even after an earlier step failed, to put things back (for example starting a service that was stopped).</summary>
    public bool Always { get; init; }
    /// <summary>An option id that must be on for this step to run, or "!id" when it must be off.</summary>
    public string When { get; init; } = "";
    public FixEncoding Encoding { get; init; } = FixEncoding.Oem;
    /// <summary>DeleteFiles and RenameFolder: the folder. Environment variables are expanded.</summary>
    public string Path { get; init; } = "";
    /// <summary>DeleteFiles: the file name patterns inside the folder.</summary>
    public string[] Patterns { get; init; } = [];
}

/// <summary>One repair, described for a person. Items are data (fix-catalog.json).</summary>
public sealed class FixItem
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required FixGroup Group { get; init; }
    public required FixScope Scope { get; init; }
    public string Icon { get; init; } = "";
    /// <summary>What problem this is for, in a sentence.</summary>
    public required string Summary { get; init; }
    /// <summary>What it changes, and what you give up or need to know.</summary>
    public required string Effect { get; init; }
    public FixRisk Risk { get; init; } = FixRisk.Safe;
    public FixRestart Restart { get; init; } = FixRestart.Never;
    /// <summary>How long it usually takes, for example "A few minutes".</summary>
    public string Duration { get; init; } = "";
    public string[] Sources { get; init; } = [];
    public FixOption[] Options { get; init; } = [];
    public required FixStep[] Steps { get; init; }
}

/// <summary>A fix a person asked to run, with the options they switched on.</summary>
public sealed record FixRun(string Id, IReadOnlyList<string> Options);

public static class FixCatalog
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };
    private static IReadOnlyList<FixItem>? loaded;

    public static IReadOnlyList<FixItem> Load() => loaded ??= Read();

    private static IReadOnlyList<FixItem> Read()
    {
        using var stream = typeof(FixCatalog).Assembly.GetManifestResourceStream("fix-catalog.json") ?? throw new InvalidOperationException("The fix catalog is missing from the app.");
        var file = JsonSerializer.Deserialize<CatalogFile>(stream, Options) ?? throw new InvalidOperationException("The fix catalog is empty.");
        // A catalog that breaks the rules is a bug in the app, so it is refused before anything can run.
        var problems = FixGuard.Validate(file.Items);
        if (problems.Count > 0) throw new InvalidOperationException("The fix catalog is not valid: " + string.Join(" ", problems));
        return file.Items;
    }

    private sealed class CatalogFile { public List<FixItem> Items { get; init; } = []; }
}

/// <summary>
/// The rules for what a fix may run. A fix can only start programs on a short allow-list, with arguments from a short allow-list, and can only
/// delete or rename the few folders named here. The same rules run again in the administrator helper, which only ever runs a step of the
/// catalog by its position, so no request can ask it to run anything else.
/// </summary>
public static partial class FixGuard
{
    private static readonly HashSet<string> System32Programs = new(StringComparer.OrdinalIgnoreCase)
    {
        "dism.exe", "sfc.exe", "netsh.exe", "ipconfig.exe", "net.exe", "w32tm.exe", "pnputil.exe", "wsreset.exe"
    };

    // Services a fix may stop and start. Only restarts: nothing here changes how a service starts.
    private static readonly HashSet<string> Services = new(StringComparer.OrdinalIgnoreCase)
    {
        "spooler", "audiosrv", "AudioEndpointBuilder", "bthserv", "w32time", "WSearch", "bits", "wuauserv", "cryptsvc"
    };

    private static readonly string[][] AllowedNetsh =
    [
        ["winsock", "reset"], ["int", "ip", "reset"], ["interface", "ip", "delete", "arpcache"], ["winhttp", "reset", "proxy"]
    ];

    private static readonly HashSet<string> AllowedIpconfig = new(StringComparer.OrdinalIgnoreCase) { "/flushdns", "/release", "/renew", "/registerdns" };

    private static readonly string[][] AllowedDism =
    [
        ["/Online", "/Cleanup-Image", "/RestoreHealth"], ["/Online", "/Cleanup-Image", "/StartComponentCleanup"]
    ];

    private static readonly string[][] AllowedW32tm = [["/resync", "/rediscover"], ["/config", "/update"]];

    // Folders a fix may clear or rename, with what it may remove from them.
    private static readonly (FixScope Scope, string Path, string[] Patterns)[] Deletable =
    [
        (FixScope.Machine, @"%ALLUSERSPROFILE%\Microsoft\Network\Downloader", ["qmgr*.dat"]),
        (FixScope.Machine, @"%SystemRoot%\System32\spool\PRINTERS", ["*"]),
        (FixScope.Machine, @"%ProgramData%\Microsoft\Search\Data\Applications\Windows", ["Windows.edb"]),
        (FixScope.User, @"%LOCALAPPDATA%", ["IconCache.db"]),
        (FixScope.User, @"%LOCALAPPDATA%\Microsoft\Windows\Explorer", ["iconcache_*.db", "thumbcache_*.db"])
    ];

    private static readonly (FixScope Scope, string Path)[] Renamable =
    [
        (FixScope.Machine, @"%SystemRoot%\SoftwareDistribution\Download"),
        (FixScope.Machine, @"%SystemRoot%\SoftwareDistribution\DataStore"),
        (FixScope.Machine, @"%SystemRoot%\System32\catroot2")
    ];

    private static readonly string[] Forbidden =
    [
        "Invoke-Expression", "iex ", "iex(", "DownloadString", "DownloadFile", "Invoke-WebRequest", "Invoke-RestMethod", "Start-BitsTransfer", "-EncodedCommand", "WebClient",
        "Set-ExecutionPolicy", "Enable-PSRemoting", "Clear-Tpm", "Remove-Item", "Format-Volume", "Set-ItemProperty", "reg ", "bootrec", "bcdedit", "slmgr", "manage-bde"
    ];

    private static readonly string[] UserOnlyVariables = ["%LOCALAPPDATA%", "%APPDATA%", "%USERPROFILE%", "$env:LOCALAPPDATA", "$env:APPDATA", "$env:USERPROFILE", "HKCU"];

    [GeneratedRegex(@"^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex Slug();

    /// <summary>The full path of an allowed program, or null when it is not one.</summary>
    public static string? Resolve(string file)
    {
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        if (System32Programs.Contains(file)) return Path.Combine(system, file);
        if (string.Equals(file, "powershell.exe", StringComparison.OrdinalIgnoreCase)) return Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe");
        if (string.Equals(file, "onedrive", StringComparison.OrdinalIgnoreCase))
            return new[]
            {
                @"%LOCALAPPDATA%\Microsoft\OneDrive\OneDrive.exe", @"%ProgramFiles%\Microsoft OneDrive\OneDrive.exe", @"%ProgramFiles(x86)%\Microsoft OneDrive\OneDrive.exe"
            }.Select(Environment.ExpandEnvironmentVariables).FirstOrDefault(System.IO.File.Exists);
        return null;
    }

    public static bool IsKnownProgram(string file) => System32Programs.Contains(file) || file.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase) || file.Equals("onedrive", StringComparison.OrdinalIgnoreCase);

    public static bool AllowsDelete(FixScope scope, string path, string pattern) =>
        Deletable.Any(x => x.Scope == scope && string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase) && x.Patterns.Contains(pattern, StringComparer.OrdinalIgnoreCase));

    public static bool AllowsRename(FixScope scope, string path) =>
        Renamable.Any(x => x.Scope == scope && string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase));

    /// <summary>Everything wrong with a catalog, as sentences. Empty when it is fine.</summary>
    public static List<string> Validate(IReadOnlyList<FixItem> items)
    {
        var problems = new List<string>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            var who = $"“{item.Id}”";
            if (!Slug().IsMatch(item.Id)) problems.Add($"{who} needs a lowercase id with dashes.");
            if (!ids.Add(item.Id)) problems.Add($"{who} is listed twice.");
            if (string.IsNullOrWhiteSpace(item.Name) || string.IsNullOrWhiteSpace(item.Summary) || string.IsNullOrWhiteSpace(item.Effect)) problems.Add($"{who} needs a name, a summary and an effect.");
            if (item.Steps.Length == 0) problems.Add($"{who} does nothing.");
            var options = item.Options.Select(x => x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (options.Count != item.Options.Length || item.Options.Any(x => !Slug().IsMatch(x.Id))) problems.Add($"{who} has options with missing, repeated or badly written ids.");
            foreach (var step in item.Steps)
            {
                var at = $"{who}, “{step.Title}”";
                if (string.IsNullOrWhiteSpace(step.Title)) problems.Add($"{who} has a step without a title.");
                if (step.Minutes is < 1 or > 180) problems.Add($"{at} must time out between 1 and 180 minutes.");
                if (step.Ok.Length == 0) problems.Add($"{at} needs at least one exit code that counts as success.");
                if (step.When.Length > 0 && !options.Contains(step.When.TrimStart('!'))) problems.Add($"{at} depends on an option that does not exist.");
                switch (step.Kind)
                {
                    case FixStepKind.Run: ValidateRun(item, step, at, problems); break;
                    case FixStepKind.DeleteFiles:
                        if (step.Patterns.Length == 0) problems.Add($"{at} does not say which files to delete.");
                        foreach (var pattern in step.Patterns)
                            if (!AllowsDelete(item.Scope, step.Path, pattern)) problems.Add($"{at}: deleting {pattern} in {step.Path} is not allowed here.");
                        break;
                    case FixStepKind.RenameFolder:
                        if (!AllowsRename(item.Scope, step.Path)) problems.Add($"{at}: renaming {step.Path} is not allowed here.");
                        break;
                    case FixStepKind.StopExplorer:
                    case FixStepKind.StartExplorer:
                    case FixStepKind.RestartExplorer:
                        if (item.Scope != FixScope.User) problems.Add($"{at}: only a fix for the current user may restart File Explorer.");
                        break;
                }
            }
        }
        return problems;
    }

    private static void ValidateRun(FixItem item, FixStep step, string at, List<string> problems)
    {
        var file = step.File;
        if (!IsKnownProgram(file)) { problems.Add($"{at}: {file} is not a program fixes may run."); return; }
        var text = string.Join(' ', step.Args);
        if (item.Scope == FixScope.Machine && UserOnlyVariables.Any(x => text.Contains(x, StringComparison.OrdinalIgnoreCase)))
            problems.Add($"{at} reaches into a user's profile, which an administrator helper must never do.");
        if (item.Scope == FixScope.User && file.Equals("net.exe", StringComparison.OrdinalIgnoreCase)) problems.Add($"{at}: a fix for the current user cannot control services.");
        bool Is(string[][] allowed) => allowed.Any(x => x.SequenceEqual(step.Args, StringComparer.OrdinalIgnoreCase));
        switch (file.ToLowerInvariant())
        {
            case "net.exe":
                var stop = step.Args.Length == 3 && step.Args[0].Equals("stop", StringComparison.OrdinalIgnoreCase) && step.Args[2].Equals("/y", StringComparison.OrdinalIgnoreCase);
                var start = step.Args.Length == 2 && step.Args[0].Equals("start", StringComparison.OrdinalIgnoreCase);
                if (!(stop || start) || !Services.Contains(step.Args[1])) problems.Add($"{at}: net may only stop or start one of the services this page restarts.");
                break;
            case "netsh.exe": if (!Is(AllowedNetsh)) problems.Add($"{at}: that netsh command is not allowed."); break;
            case "ipconfig.exe": if (step.Args.Length != 1 || !AllowedIpconfig.Contains(step.Args[0])) problems.Add($"{at}: that ipconfig command is not allowed."); break;
            case "dism.exe": if (!Is(AllowedDism)) problems.Add($"{at}: that DISM command is not allowed."); break;
            case "sfc.exe": if (step.Args.Length != 1 || step.Args[0] != "/scannow") problems.Add($"{at}: sfc may only run /scannow."); break;
            case "w32tm.exe": if (!Is(AllowedW32tm)) problems.Add($"{at}: that w32tm command is not allowed."); break;
            case "pnputil.exe": if (step.Args.Length != 1 || step.Args[0] != "/scan-devices") problems.Add($"{at}: pnputil may only rescan devices."); break;
            case "wsreset.exe": if (step.Args.Length != 0) problems.Add($"{at}: wsreset takes no arguments."); break;
            case "onedrive": if (step.Args.Length != 1 || step.Args[0] != "/reset") problems.Add($"{at}: OneDrive may only be reset."); break;
            case "powershell.exe":
                if (step.Args.Length != 4 || step.Args[0] != "-NoProfile" || step.Args[1] != "-NonInteractive" || step.Args[2] != "-Command") { problems.Add($"{at}: PowerShell must run one fixed -Command."); break; }
                foreach (var bad in Forbidden)
                    if (step.Args[3].Contains(bad, StringComparison.OrdinalIgnoreCase)) problems.Add($"{at}: the PowerShell command uses {bad.Trim()}, which fixes must not.");
                break;
        }
    }
}

/// <summary>Turns a catalog fix and the options a person chose into the steps that will run.</summary>
public static class FixPlan
{
    /// <summary>The option ids a fix starts with.</summary>
    public static IReadOnlyList<string> DefaultOptions(FixItem item) => item.Options.Where(x => x.Default).Select(x => x.Id).ToList();

    public static IReadOnlyList<FixStep> Build(FixItem item, IEnumerable<string> options)
    {
        var on = options.Where(x => item.Options.Any(o => o.Id.Equals(x, StringComparison.OrdinalIgnoreCase))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return item.Steps.Where(step => step.When.Length == 0 || (step.When[0] == '!' ? !on.Contains(step.When[1..]) : on.Contains(step.When))).ToList();
    }
}

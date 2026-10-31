using System.Text.RegularExpressions;

namespace TidyWindow.Next.Core;

public sealed record StartupScanResult(IReadOnlyList<StartupEntry> Entries, int WindowsTasks, int WindowsServices);

/// <summary>Explorer's 12-byte on/off record for startup items: the first byte is even when the item runs and odd when it is off, then a timestamp.</summary>
public static class StartupApproved
{
    public static bool IsEnabled(byte[]? data) => data is not { Length: > 0 } || (data[0] & 1) == 0;

    public static byte[] Make(bool enabled, DateTimeOffset now)
    {
        var data = new byte[12];
        data[0] = enabled ? (byte)2 : (byte)3;
        if (!enabled) BitConverter.GetBytes(now.UtcDateTime.ToFileTimeUtc()).CopyTo(data, 4);
        return data;
    }
}

/// <summary>
/// The rules for what may be changed. They look at facts read from the PC, never at what a caller says about an item, and they run again
/// in the administrator helper, so a request cannot talk its way past them.
/// </summary>
public static partial class StartupGuard
{
    // Programs that start other programs. Their own location says nothing about what they launch, so they never count as part of Windows.
    private static readonly string[] Launchers = ["cmd", "powershell", "pwsh", "wscript", "cscript", "mshta", "rundll32", "regsvr32", "msiexec", "conhost", "wmic", "bash", "wsl"];

    [GeneratedRegex(@"\b(defender|windows ?security|security ?health|antivirus|anti-virus|antimalware|msmpeng|mpdefender|kaspersky|norton|mcafee|avast|avg|bitdefender|eset|malwarebytes|sophos|trend ?micro|webroot|crowdstrike|sentinelone|cylance|carbon ?black|f-secure|avira|comodo|symantec|cortex ?xdr|bullguard|panda ?security)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SecurityMarkers();

    [GeneratedRegex(@"\b(intel|realtek|nvidia|advanced micro devices|amd|synaptics|elan|alps|qualcomm|mediatek|broadcom|logitech|razer|corsair|steelseries|asus|asustek|acer|dell|hewlett|hp|lenovo|micro-star|msi|gigabyte|wacom|tobii|nahimic|killer|thunderbolt)\b", RegexOptions.IgnoreCase)]
    private static partial Regex HardwareMarkers();

    /// <summary>Files Windows itself ships: they live in the Windows folder and are not from another company.</summary>
    public static bool IsWindowsOwned(string exePath, string publisher, StartupPlaces places) =>
        !IsLauncher(exePath) && places.InWindows(exePath) && (publisher.Length == 0 || publisher.Contains("Microsoft", StringComparison.OrdinalIgnoreCase));

    /// <summary>The wording for an item with nothing special about it, so a screen can skip repeating it on every row.</summary>
    public const string OrdinaryReason = "Starting with Windows is optional. The app still works when you open it yourself.";

    public static bool IsLauncher(string exePath) =>
        !string.IsNullOrWhiteSpace(exePath) && Array.Exists(Launchers, x => string.Equals(Path.GetFileNameWithoutExtension(exePath), x, StringComparison.OrdinalIgnoreCase));

    public static (StartupRisk Risk, string Reason) Judge(StartupKind kind, string name, string exePath, string command, string publisher, StartupTrust trust, bool missing,
        StartupPlaces places, ServiceFacts? service = null, bool dependents = false, bool policy = false, bool bootAsSystem = false)
    {
        if (kind == StartupKind.WindowsSetting)
            return (StartupRisk.Protected, "A core Windows sign-in setting. It is shown so you can see it looks normal, and it cannot be changed here.");
        if (policy)
            return (StartupRisk.Protected, "Your organization controls this setting.");

        var launcher = IsLauncher(exePath);
        if (!launcher && places.InWindows(exePath))
            return (StartupRisk.Protected, IsWindowsOwned(exePath, publisher, places)
                ? "Part of Windows. Windows needs it, so it cannot be turned off here."
                : $"Installed in the Windows folder by {publisher}, usually a driver or system helper. It cannot be turned off here.");

        var text = $"{name} {publisher} {exePath}";
        if (SecurityMarkers().IsMatch(text))
            return (StartupRisk.Protected, "Security software. Turning it off would leave this PC less protected, so it cannot be turned off here.");

        var microsoft = publisher.Contains("Microsoft", StringComparison.OrdinalIgnoreCase);
        if (kind == StartupKind.Service)
        {
            if (microsoft) return (StartupRisk.Protected, "A Microsoft service. Parts of Windows can depend on it, so it cannot be changed here.");
            if (dependents) return (StartupRisk.Protected, "Other services depend on it, so changing it could stop them from working.");
            return (StartupRisk.Careful, "A background service. Turning it off makes it start only when something needs it, instead of switching it off for good.");
        }

        if (launcher)
            return (StartupRisk.Careful, "Runs a script or command instead of a program. Check what it starts before you change it.");
        if (microsoft && trust is StartupTrust.Unsigned or StartupTrust.Invalid && !missing)
            return (StartupRisk.Careful, "Says it is from Microsoft, but its signature could not be verified. Check it before you change anything.");
        if (trust is StartupTrust.Unsigned or StartupTrust.Invalid && places.InScratch(exePath))
            return (StartupRisk.Careful, "Not signed and starts from a temporary folder. That is a common sign of unwanted software, so check what it is.");
        if (HardwareMarkers().IsMatch(publisher) || HardwareMarkers().IsMatch(exePath.Replace('\\', ' ')))
            return (StartupRisk.Careful, "Supports your hardware, such as keys, lights, audio or the touchpad. The hardware keeps working, but extras may stop.");
        if (bootAsSystem)
            return (StartupRisk.Careful, "Runs with full system rights before you sign in. Turn it off only if you know what it does.");
        if (missing)
            return (StartupRisk.Optional, "The file this points to is gone, so it does nothing now. Safe to turn off.");
        if (UpdaterWords().IsMatch(name) || UpdaterWords().IsMatch(command))
            return (StartupRisk.Optional, "Keeps an app up to date in the background. If you turn it off, that app may stop updating by itself.");
        return (StartupRisk.Optional, OrdinaryReason);
    }

    [GeneratedRegex(@"updat|upgrad", RegexOptions.IgnoreCase)]
    private static partial Regex UpdaterWords();
}

/// <summary>Reads every place Windows starts things from and sorts the findings by how safe they are to change.</summary>
public static partial class StartupScan
{
    private static readonly string[] RunSubKeys = ["Run", "Run32"];

    public static StartupScanResult Run(IStartupSystem system, IReadOnlyCollection<string>? includeServices = null)
    {
        var running = system.RunningImages();
        var facts = new Dictionary<string, FileFacts>(StringComparer.OrdinalIgnoreCase);
        FileFacts Describe(string path)
        {
            if (path.Length == 0) return new(false, "", "", "", StartupTrust.Unknown);
            if (!facts.TryGetValue(path, out var found)) facts[path] = found = system.Describe(path);
            return found;
        }

        var entries = new List<StartupEntry>();
        var finder = new PublisherFinder(system);

        StartupEntry Build(StartupKind kind, StartupScope scope, string id, string name, string description, string command, string exe, string location, string source, string key,
            bool enabled, string publisherHint = "", ServiceFacts? service = null, bool dependents = false, bool policy = false, bool bootAsSystem = false, int start = 0, bool delayed = false, bool checkFile = true)
        {
            var file = Describe(exe);
            var missing = checkFile && exe.Length > 0 && !file.Exists;
            var (publisher, from) = finder.Find(name, exe, file, publisherHint, hintIsPackage: kind == StartupKind.StoreApp);
            var (risk, reason) = StartupGuard.Judge(kind, name, exe, command, publisher, file.Trust, missing, system.Places, service, dependents, policy, bootAsSystem);
            var about = description.Length > 0 ? description : file.Description;
            var isRunning = exe.Length > 0 && running.Contains(exe);
            return new(id, kind, scope, name, publisher, about, command, exe, location, source, key, enabled, risk, reason, file.Trust, isRunning, missing, start, delayed, from);
        }

        foreach (var run in system.RunValues())
        {
            var source = run.Wow64 ? "Run32" : "Run";
            var enabled = StartupApproved.IsEnabled(system.Approved(run.Scope, source, run.Name));
            var exe = ResolveExe(system, run.Command);
            var hive = run.Scope == StartupScope.User ? "HKCU" : "HKLM";
            var location = $@"{hive}\...\{(run.Wow64 ? @"WOW6432Node\...\" : "")}Run";
            var name = CleanName(run.Name);
            entries.Add(Build(StartupKind.RunKey, run.Scope, $"run|{ScopeKey(run.Scope)}|{source}|{run.Name}", name, "", run.Command, exe, location, source, run.Name, enabled));
        }

        foreach (var item in system.FolderItems())
        {
            var enabled = StartupApproved.IsEnabled(system.Approved(item.Scope, "StartupFolder", item.FileName));
            var command = item.Target.Length > 0 ? (item.Arguments.Length > 0 ? $"\"{item.Target}\" {item.Arguments}" : item.Target) : item.FullPath;
            var exe = item.Target.Length > 0 ? ResolveExe(system, item.Target) : item.FullPath;
            var name = Path.GetFileNameWithoutExtension(item.FileName);
            var location = item.Scope == StartupScope.User ? "Your Startup folder" : "The Startup folder for everyone";
            entries.Add(Build(StartupKind.StartupFolder, item.Scope, $"folder|{ScopeKey(item.Scope)}|{item.FileName}", name, "", command, exe, location, "StartupFolder", item.FileName, enabled));
        }

        var (tasks, windowsTasks) = system.Tasks();
        foreach (var task in tasks.Where(x => x.AtLogon || x.AtBoot))
        {
            var exe = ResolveExe(system, task.Program);
            var command = task.Arguments.Length > 0 ? $"{task.Program} {task.Arguments}".Trim() : task.Program;
            var asSystem = task.RunAs.Contains("SYSTEM", StringComparison.OrdinalIgnoreCase);
            var scope = asSystem ? StartupScope.Machine : StartupScope.User;
            var when = task.AtBoot && task.AtLogon ? "at startup and sign-in" : task.AtBoot ? "at startup" : "at sign-in";
            entries.Add(Build(StartupKind.ScheduledTask, scope, $"task|{ScopeKey(scope)}|{task.Path}", TaskName(task.Name), task.Description, command, exe, $"Scheduled task, runs {when}", "Task", task.Path, task.Enabled,
                publisherHint: task.Author, bootAsSystem: asSystem && task.AtBoot));
        }

        var windowsServices = 0;
        var all = system.Services();
        var started = all.Where(x => x.Start <= 2).ToList();
        foreach (var service in all)
        {
            var own = (service.Type & 0x30) != 0 && (service.Type & 0xC0) == 0;
            if (!own) continue;
            var wanted = includeServices?.Contains(service.Name, StringComparer.OrdinalIgnoreCase) == true;
            if (service.Start != 2 && !wanted) continue;
            var exe = ResolveExe(system, service.ImagePath);
            if (StartupGuard.IsWindowsOwned(exe, Describe(exe).Company, system.Places)) { windowsServices++; continue; }
            var dependents = started.Any(x => !ReferenceEquals(x, service) && x.DependsOn.Contains(service.Name, StringComparer.OrdinalIgnoreCase));
            var name = service.DisplayName.Length > 0 && !service.DisplayName.StartsWith('@') ? service.DisplayName : service.Name;
            var about = service.Description.StartsWith('@') ? "" : service.Description;
            var how = service.Delayed ? "Starts shortly after Windows" : "Starts with Windows";
            entries.Add(Build(StartupKind.Service, StartupScope.Machine, $"service|machine|{service.Name}", name, about, service.ImagePath, exe, $"Background service. {how}", "Service", service.Name,
                service.Start == 2, service: service, dependents: dependents, start: service.Start, delayed: service.Delayed));
        }

        foreach (var store in system.StoreTasks())
        {
            var policy = store.State is 3 or 4;
            var name = store.DisplayName.Length > 0 ? store.DisplayName : StoreName(store.Family);
            var exe = store.Executable.Length > 0 ? store.Executable : "";
            var location = "App from the Microsoft Store";
            entries.Add(Build(StartupKind.StoreApp, StartupScope.User, $"store|user|{store.Family}|{store.TaskId}", name, "", exe.Length > 0 ? exe : $"Startup task {store.TaskId}", exe, location, "Store", $"{store.Family}|{store.TaskId}",
                store.State is 2 or 4, publisherHint: store.Publisher, policy: policy, checkFile: false));
        }

        foreach (var setting in system.WindowsSettings())
        {
            var normal = string.Equals(setting.Value.Trim(), setting.Expected.Trim(), StringComparison.OrdinalIgnoreCase);
            var note = normal ? "" : "This is not the usual value. If you did not change it, run a security scan.";
            var entry = Build(StartupKind.WindowsSetting, StartupScope.Machine, $"setting|machine|{setting.Id}", setting.Name, note, setting.Value, "", setting.Location, "Setting", setting.Id, true);
            entries.Add(normal ? entry : entry with { Reason = "Not the usual value. If you did not change it, run a security scan. It cannot be changed here." });
        }

        var ordered = entries
            .OrderBy(x => x.Risk == StartupRisk.Protected)
            .ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        return new(ordered, windowsTasks, windowsServices);
    }

    public static StartupEntry? Find(IStartupSystem system, string id) =>
        Run(system, ServiceNameOf(id) is { } service ? [service] : null).Entries.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));

    private static string? ServiceNameOf(string id)
    {
        var parts = id.Split('|');
        return parts.Length == 3 && parts[0] == "service" ? parts[2] : null;
    }

    private static string ScopeKey(StartupScope scope) => scope == StartupScope.User ? "user" : "machine";

    /// <summary>The program a command line starts: the first candidate that exists, or the likeliest one if none does.</summary>
    public static string ResolveExe(IStartupSystem system, string command)
    {
        string? first = null;
        foreach (var candidate in InstallRepair.Candidates(command))
        {
            var path = system.Expand(candidate.Trim().Trim('"'));
            if (path.Length == 0) continue;
            first ??= path;
            if (path.IndexOfAny(['\\', '/']) < 0)
            {
                var found = system.FindOnPath(path) ?? (!Path.HasExtension(path) ? system.FindOnPath(path + ".exe") : null);
                if (found is not null) return found;
                continue;
            }
            if (system.FileExists(path)) return path;
            if (!Path.HasExtension(path) && system.FileExists(path + ".exe")) return path + ".exe";
        }
        return first ?? "";
    }

    [GeneratedRegex(@"[_-][0-9A-Fa-f]{16,}$")]
    private static partial Regex HashTail();

    [GeneratedRegex(@"-S-1-5-21-[\d-]+$", RegexOptions.IgnoreCase)]
    private static partial Regex SidTail();

    public static string CleanName(string valueName) => HashTail().Replace(valueName, "").Trim() is { Length: > 0 } cleaned ? cleaned : valueName;
    public static string TaskName(string name) => SidTail().Replace(name, "").Trim();

    /// <summary>A readable name from a package family name ("5319275A.WhatsAppDesktop_cv1g1gvanyjgm" becomes "WhatsAppDesktop").</summary>
    public static string StoreName(string family)
    {
        var name = family.Split('_')[0];
        return name[(name.LastIndexOf('.') + 1)..];
    }
}

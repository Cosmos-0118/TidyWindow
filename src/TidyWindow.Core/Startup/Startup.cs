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
    public static bool IsWindowsOwned(string exePath, string publisher, StartupPlaces places) => IsWindowsOwned(exePath, () => publisher, places);

    /// <summary>The same, asking for the company only when the file is in the Windows folder, since that is the only case where it matters.</summary>
    public static bool IsWindowsOwned(string exePath, Func<string> publisher, StartupPlaces places) =>
        !IsLauncher(exePath) && places.InWindows(exePath) && publisher() is var company && (company.Length == 0 || company.Contains("Microsoft", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Whether a task in Windows' own task folder really is Windows'. It is unless there is proof it runs something else: a task that names no program
    /// (a COM handler) or only a launcher with nothing found behind it proves nothing, and a Microsoft-signed program outside the Windows folder is still Microsoft's.
    /// </summary>
    public static bool IsWindowsTask(string program, string exePath, Func<string> company, Func<FileFacts> file, StartupPlaces places) =>
        string.IsNullOrWhiteSpace(program) || string.IsNullOrWhiteSpace(exePath) || IsLauncher(exePath)
        || IsWindowsOwned(exePath, company, places)
        || (file() is { Trust: StartupTrust.Verified } signed && signed.Signer.Contains("Microsoft", StringComparison.OrdinalIgnoreCase));

    public const string UnusualTaskReason = "Sits among Windows' own scheduled tasks but runs a program that is not part of Windows. Drivers and system helpers do this, and so does unwanted software that wants to blend in, so check what it is before you change it.";

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

        // A command that goes through cmd, PowerShell or rundll32 is a launcher even when the program it starts could be found and is no launcher itself.
        var launcher = IsLauncher(exePath) || IsLauncher(IconCommand.Split(command).Program);
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
            return (StartupRisk.Careful, service?.Triggered == true
                ? "A background service. Turning it off makes it start only when something needs it. Windows also starts this one when certain events happen, such as a network connecting, so it can still start with Windows."
                : "A background service. Turning it off makes it start only when something needs it, instead of switching it off for good.");
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
        // "Update.exe --processStart Discord.exe" only starts the app: the word "update" there is Squirrel's launcher, not an updater job.
        if (UpdaterWords().IsMatch(name) || (!IconCommand.IsSquirrelLaunch(command) && UpdaterWords().IsMatch(command)))
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

    public static StartupScanResult Run(IStartupSystem system, IReadOnlyCollection<string>? includeServices = null) => Scan(system, includeServices, null);

    private static readonly string[] IdKinds = ["run", "folder", "task", "service", "store", "setting"];

    /// <summary>
    /// Reads the places Windows starts things from. With <paramref name="only"/> it reads just what is needed to rebuild that one item, using the same code
    /// and the same rules as the full scan, so the entry it gives back is identical to the one the full scan lists under that id.
    /// </summary>
    private static StartupScanResult Scan(IStartupSystem system, IReadOnlyCollection<string>? includeServices, string? only)
    {
        // Only the kind of place the id names is read; an id that names no known kind reads everything and simply finds nothing.
        var kind = only?.Split('|', 2)[0];
        var everything = only is null || Array.IndexOf(IdKinds, kind?.ToLowerInvariant()) < 0;
        bool Reads(string section) => everything || string.Equals(kind, section, StringComparison.OrdinalIgnoreCase);
        bool NotWanted(string id) => only is not null && !string.Equals(id, only, StringComparison.OrdinalIgnoreCase);
        string[] Parts(int count) => only!.Split('|', count);

        ISet<string>? running = null;
        // The whole list of running programs is read once for a full scan; for one item it is enough to ask about that program.
        bool IsRunning(string exe) => only is null ? (running ??= system.RunningImages()).Contains(exe) : system.IsRunning(exe);

        var facts = new Dictionary<string, FileFacts>(StringComparer.OrdinalIgnoreCase);
        var companies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        FileFacts Describe(string path)
        {
            if (path.Length == 0) return new(false, "", "", "", StartupTrust.Unknown);
            if (!facts.TryGetValue(path, out var found)) facts[path] = found = system.Describe(path);
            return found;
        }
        // Deciding that something belongs to Windows only needs the company a file names, which is far cheaper than checking its signature.
        string Company(string path)
        {
            if (path.Length == 0) return "";
            if (facts.TryGetValue(path, out var known)) return known.Company;
            if (!companies.TryGetValue(path, out var company)) companies[path] = company = system.Company(path);
            return company;
        }

        // Entries are built after everything has been read: the slow part of building one is checking its program's signature, and once all the programs are known
        // that can be done for all of them side by side. The entries come out in the order they were found.
        var entries = new List<StartupEntry>();
        var pending = new List<(string Exe, Func<StartupEntry> Make)>();
        void Defer(string exe, Func<StartupEntry> make) => pending.Add((exe, make));
        var finder = new PublisherFinder(system);

        StartupEntry Build(StartupKind kind, StartupScope scope, string id, string name, string description, string command, string exe, string location, string source, string key,
            bool enabled, string publisherHint = "", ServiceFacts? service = null, bool dependents = false, bool policy = false, bool bootAsSystem = false, int start = 0, bool delayed = false, bool checkFile = true,
            string folderPath = "")
        {
            // When nothing behind a launcher (cmd, PowerShell, rundll32...) could be found, the launcher's own file says nothing about the item:
            // it is Microsoft's, signed and running whenever any terminal is open. None of that is lent to the entry.
            var bare = StartupGuard.IsLauncher(exe);
            var file = bare ? new FileFacts(true, "", "", "", StartupTrust.Unknown) : Describe(exe);
            var missing = checkFile && exe.Length > 0 && !file.Exists;
            var (publisher, from) = finder.Find(name, exe, file, publisherHint, hintIsPackage: kind == StartupKind.StoreApp);
            var (risk, reason) = StartupGuard.Judge(kind, name, exe, command, publisher, file.Trust, missing, system.Places, service, dependents, policy, bootAsSystem);
            var about = description.Length > 0 ? description : file.Description;
            var isRunning = !bare && exe.Length > 0 && IsRunning(exe);
            return new(id, kind, scope, name, publisher, about, command, exe, location, source, key, enabled, risk, reason, file.Trust, isRunning, missing, start, delayed, from, service?.Triggered ?? false, folderPath);
        }

        if (Reads("run"))
            foreach (var run in system.RunValues())
            {
                var source = run.Where;
                var id = $"run|{ScopeKey(run.Scope)}|{source}|{run.Name}";
                if (NotWanted(id)) continue;
                // Explorer keeps no on/off record for RunOnce or the Policies Run key, so those are always "on" until they are taken out.
                var enabled = StartupLocations.ApprovedKey(source) is not { } approved || StartupApproved.IsEnabled(system.Approved(run.Scope, approved, run.Name));
                var exe = ResolveExe(system, run.Command);
                var name = CleanName(run.Name);
                Defer(exe, () => NoSwitchNote(Build(StartupKind.RunKey, run.Scope, id, name, "", run.Command, exe, RunLocation(run.Scope, source), source, run.Name, enabled)));
            }

        if (Reads("folder"))
        {
            // The shortcuts in a Startup folder each cost a COM call to read, so for one item only that file is read.
            IEnumerable<FolderItem> folderItems = !everything && Parts(3) is [_, var folderScope, var folderFile] && ScopeOf(folderScope) is { } scopeWanted
                ? system.FolderItemFor(scopeWanted, folderFile) is { } one ? [one] : []
                : system.FolderItems();
            foreach (var item in folderItems)
            {
                var id = $"folder|{ScopeKey(item.Scope)}|{item.FileName}";
                if (NotWanted(id)) continue;
                var enabled = StartupApproved.IsEnabled(system.Approved(item.Scope, "StartupFolder", item.FileName));
                var command = item.Target.Length > 0 ? (item.Arguments.Length > 0 ? $"\"{item.Target}\" {item.Arguments}" : item.Target) : item.FullPath;
                var exe = item.Target.Length > 0 ? ResolveExe(system, Quoted(item.Target, item.Arguments)) : item.FullPath;
                var name = Path.GetFileNameWithoutExtension(item.FileName);
                var location = item.Scope == StartupScope.User ? "Your Startup folder" : "The Startup folder for everyone";
                Defer(exe, () => Build(StartupKind.StartupFolder, item.Scope, id, name, "", command, exe, location, "StartupFolder", item.FileName, enabled, folderPath: item.FullPath));
            }
        }

        var windowsTasks = 0;
        IReadOnlyList<ScheduledStartup> tasks = [];
        if (Reads("task"))
        {
            // Reading every task in Task Scheduler is the slowest read there is, so for one item only that task is read.
            if (!everything && Parts(3) is [_, _, var taskPath]) tasks = system.TaskAt(taskPath) is { } oneTask ? [oneTask] : [];
            else (tasks, windowsTasks) = system.Tasks();
        }
        foreach (var task in tasks.Where(x => x.AtLogon || x.AtBoot))
        {
            var asSystem = task.RunAs.Contains("SYSTEM", StringComparison.OrdinalIgnoreCase);
            var scope = asSystem ? StartupScope.Machine : StartupScope.User;
            var id = $"task|{ScopeKey(scope)}|{task.Path}";
            if (NotWanted(id)) continue;
            // The program and its arguments are read together: "cmd.exe" or "Update.exe" alone says nothing about what the task starts.
            var exe = ResolveExe(system, Quoted(task.Program, task.Arguments));
            // Office, Visual Studio and others keep their tasks in \Microsoft too. Only the ones that are really Windows' own are left out.
            var inWindowsFolder = task.Path.StartsWith(StartupLocations.WindowsTaskFolder + @"\", StringComparison.OrdinalIgnoreCase);
            if (inWindowsFolder ? StartupGuard.IsWindowsTask(task.Program, exe, () => Company(exe), () => Describe(exe), system.Places)
                : task.Path.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase) && StartupGuard.IsWindowsOwned(exe, () => Company(exe), system.Places)) { windowsTasks++; continue; }
            var command = task.Arguments.Length > 0 ? $"{task.Program} {task.Arguments}".Trim() : task.Program;
            var when = task.AtBoot && task.AtLogon ? "at startup and sign-in" : task.AtBoot ? "at startup" : "at sign-in";
            Defer(exe, () =>
            {
                var entry = Build(StartupKind.ScheduledTask, scope, id, TaskName(task.Name), task.Description, command, exe, $"Scheduled task, runs {when}", "Task", task.Path, task.Enabled,
                    publisherHint: task.Author, bootAsSystem: asSystem && task.AtBoot);
                // Being among Windows' tasks is itself worth a warning; an ordinary "optional" verdict would undersell it.
                return inWindowsFolder && entry.Risk == StartupRisk.Optional && !entry.FileMissing ? entry with { Risk = StartupRisk.Careful, Reason = StartupGuard.UnusualTaskReason } : entry;
            });
        }

        var windowsServices = 0;
        // Every service is read even for one item, because whether other services depend on it is part of the answer; that read is cheap.
        IReadOnlyList<ServiceFacts> all = Reads("service") ? system.Services() : Array.Empty<ServiceFacts>();
        var started = new Lazy<List<ServiceFacts>>(() => all.Where(x => x.Start <= 2).ToList());
        foreach (var service in all)
        {
            if (NotWanted($"service|machine|{service.Name}")) continue;
            var own = (service.Type & 0x30) != 0 && (service.Type & 0xC0) == 0;
            if (!own) continue;
            var wanted = includeServices?.Contains(service.Name, StringComparer.OrdinalIgnoreCase) == true;
            if (service.Start != 2 && !wanted) continue;
            var exe = ResolveExe(system, service.ImagePath);
            // A third-party service inside a shared svchost is known by its library, not by Windows' host program.
            if (service.ServiceDll.Length > 0 && string.Equals(Path.GetFileNameWithoutExtension(exe), "svchost", StringComparison.OrdinalIgnoreCase)
                && ResolveExe(system, Quoted(service.ServiceDll, "")) is { Length: > 0 } library && !system.Places.InWindows(library))
                exe = library;
            if (StartupGuard.IsWindowsOwned(exe, () => Company(exe), system.Places)) { windowsServices++; continue; }
            var dependents = started.Value.Any(x => !ReferenceEquals(x, service) && x.DependsOn.Contains(service.Name, StringComparer.OrdinalIgnoreCase));
            // Names and descriptions that point into a resource file ("@dll,-101") are read here, only for the services that are listed.
            var display = system.Localize(service.DisplayName);
            var name = display.Length > 0 && !display.StartsWith('@') ? display : service.Name;
            var about = system.Localize(service.Description);
            if (about.StartsWith('@')) about = "";
            var how = service.Delayed ? "Starts shortly after Windows" : "Starts with Windows";
            var listed = exe;
            Defer(exe, () => Build(StartupKind.Service, StartupScope.Machine, $"service|machine|{service.Name}", name, about, service.ImagePath, listed, $"Background service. {how}", "Service", service.Name,
                service.Start == 2, service: service, dependents: dependents, start: service.Start, delayed: service.Delayed));
        }

        IEnumerable<StoreStartup> storeTasks = [];
        if (Reads("store"))
            storeTasks = !everything && Parts(4) is [_, _, var family, var taskId]
                ? system.StoreTaskFor(family, taskId) is { } oneStore ? [oneStore] : []
                : system.StoreTasks();
        foreach (var store in storeTasks)
        {
            var id = $"store|user|{store.Family}|{store.TaskId}";
            if (NotWanted(id)) continue;
            var policy = store.State is 3 or 4;
            var name = store.DisplayName.Length > 0 ? store.DisplayName : StoreName(store.Family);
            var exe = store.Executable.Length > 0 ? store.Executable : "";
            var location = "App from the Microsoft Store";
            Defer(exe, () => Build(StartupKind.StoreApp, StartupScope.User, id, name, "", exe.Length > 0 ? exe : $"Startup task {store.TaskId}", exe, location, "Store", $"{store.Family}|{store.TaskId}",
                store.State is 2 or 4, publisherHint: store.Publisher, policy: policy, checkFile: false));
        }

        IReadOnlyList<WindowsSetting> settings = Reads("setting") ? system.WindowsSettings() : Array.Empty<WindowsSetting>();
        foreach (var setting in settings)
        {
            if (NotWanted($"setting|{ScopeKey(setting.Scope)}|{setting.Id}")) continue;
            var normal = string.Equals(setting.Value.Trim(), setting.Expected.Trim(), StringComparison.OrdinalIgnoreCase);
            var note = normal ? "" : "This is not the usual value. If you did not change it, run a security scan.";
            // A setting of this user that is empty on a healthy PC can be cleared, and the program it names is looked at like any other startup item.
            var clearable = setting.Scope == StartupScope.User && !normal && StartupLocations.SettingFor(setting.Id) is not null;
            var settingExe = clearable ? ResolveExe(system, setting.Value) : "";
            Defer(settingExe, () =>
            {
                var entry = Build(StartupKind.WindowsSetting, setting.Scope, $"setting|{ScopeKey(setting.Scope)}|{setting.Id}", setting.Name, note, setting.Value, settingExe, setting.Location, "Setting", setting.Id, true);
                return clearable ? entry with { Risk = StartupRisk.Careful, Reason = ClearableReason(setting.Id) }
                    : normal ? entry : entry with { Reason = "Not the usual value. If you did not change it, run a security scan. It cannot be changed here." };
            });
        }

        // One item has nothing to share, so only a full scan checks the programs side by side first.
        if (only is null)
            system.Warm(pending.Select(x => x.Exe).Where(x => x.Length > 0 && !StartupGuard.IsLauncher(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
        foreach (var (_, make) in pending) entries.Add(make());

        var ordered = entries
            .OrderBy(x => x.Risk == StartupRisk.Protected)
            .ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        return new(ordered, windowsTasks, windowsServices);
    }

    private static string ClearableReason(string id) => id switch
    {
        "User.Winlogon.Shell" => "Replaces the Windows desktop for your account only. Clearing it makes Windows use the usual one. If you did not set this up, run a security scan. TidyWindow keeps a copy so you can put it back.",
        "User.Winlogon.Userinit" => "Replaces the sign-in program for your account only. Clearing it makes Windows use the usual one. If you did not set this up, run a security scan. TidyWindow keeps a copy so you can put it back.",
        _ => "Starts a program every time you sign in, from a setting that is empty on most PCs and that Task Manager does not show. Only clear it if you recognize the program. TidyWindow keeps a copy so you can put it back."
    };

    /// <summary>The entry the full scan lists under this id, or null, found by reading only what that one item needs.</summary>
    public static StartupEntry? Find(IStartupSystem system, string id) =>
        Scan(system, ServiceNameOf(id) is { } service ? [service] : null, id).Entries.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));

    private static StartupScope? ScopeOf(string key) => key.ToLowerInvariant() switch { "user" => StartupScope.User, "machine" => StartupScope.Machine, _ => null };

    private static string? ServiceNameOf(string id)
    {
        var parts = id.Split('|');
        return parts.Length == 3 && parts[0] == "service" ? parts[2] : null;
    }

    private static string ScopeKey(StartupScope scope) => scope == StartupScope.User ? "user" : "machine";

    /// <summary>The id an item has before and after it is taken out of startup, so a restore can check it is putting back the item it was asked for.</summary>
    public static string IdFor(StartupVault vault) => vault.Kind switch
    {
        StartupKind.RunKey => $"run|{ScopeKey(vault.Scope)}|{vault.Source}|{vault.Key}",
        StartupKind.StartupFolder => $"folder|{ScopeKey(vault.Scope)}|{vault.Key}",
        StartupKind.ScheduledTask => $"task|{ScopeKey(vault.Scope)}|{vault.Key}",
        StartupKind.WindowsSetting => $"setting|{ScopeKey(vault.Scope)}|{vault.Key}",
        _ => ""
    };

    private static string RunLocation(StartupScope scope, string source)
    {
        var hive = scope == StartupScope.User ? "HKCU" : "HKLM";
        return source switch
        {
            StartupLocations.Run32 => $@"{hive}\...\WOW6432Node\...\Run",
            StartupLocations.RunOnce => $@"{hive}\...\RunOnce",
            StartupLocations.RunOnce32 => $@"{hive}\...\WOW6432Node\...\RunOnce",
            StartupLocations.PolicyRun => $@"{hive}\...\Policies\Explorer\Run",
            _ => $@"{hive}\...\Run"
        };
    }

    // Windows has no switch for these, so the row says what the person can do instead.
    private static StartupEntry NoSwitchNote(StartupEntry entry)
    {
        if (entry.Kind != StartupKind.RunKey || entry.Risk == StartupRisk.Protected || Array.IndexOf(StartupLocations.NoSwitchSources, entry.Source) < 0) return entry;
        if (entry.Source == StartupLocations.PolicyRun)
            return entry with { Risk = StartupRisk.Careful, Reason = "Listed under the Policies Run key. Task Manager has no switch for it, and a company's policy can put things here, so remove it only if you recognize it. TidyWindow keeps a copy so you can put it back." };
        return entry.Risk == StartupRisk.Optional
            ? entry with { Reason = "Runs once at your next sign-in. Windows has no switch for this kind of item, so the only way to stop it is to remove it. TidyWindow keeps a copy so you can put it back." }
            : entry;
    }

    /// <summary>A program path and its arguments as one command line, quoted when the path has spaces (a task's program is a bare path).</summary>
    private static string Quoted(string program, string arguments)
    {
        program = program.Trim();
        var quoted = program.Length > 0 && program[0] != '"' && program.Contains(' ') ? $"\"{program}\"" : program;
        return arguments.Length > 0 ? $"{quoted} {arguments}".Trim() : quoted;
    }

    /// <summary>
    /// The program a command line really starts. A launcher such as cmd, PowerShell or rundll32 is looked through to the script or library it runs,
    /// and Squirrel's Update.exe to the app it starts. Only when nothing can be seen behind a launcher is the launcher itself returned.
    /// </summary>
    public static string ResolveExe(IStartupSystem system, string command)
    {
        var (program, arguments) = IconCommand.Split(command.Trim());
        if (StartupGuard.IsLauncher(program)) return ResolveBehindLauncher(system, command, program);
        var found = ResolveProgram(system, command);
        return found.Length > 0 && IconCommand.SquirrelApp(found, arguments, system.Subfolders, system.FileExists) is { } app ? app : found;
    }

    private static readonly HashSet<string> ShellBuiltins = new(StringComparer.OrdinalIgnoreCase)
    {
        "echo", "start", "call", "set", "rem", "cd", "chdir", "if", "for", "del", "erase", "copy", "move", "type", "title", "pause", "exit", "goto", "md", "mkdir", "rd", "rmdir",
        "ren", "rename", "setlocal", "endlocal", "pushd", "popd", "cls", "color", "date", "time", "dir", "mklink", "path", "prompt", "ver", "vol", "assoc", "ftype", "break", "verify"
    };

    private static string ResolveBehindLauncher(IStartupSystem system, string command, string program)
    {
        string? first = null;
        var bare = false;
        foreach (var target in IconCommand.Targets(command, system.Expand))
        {
            var path = system.Expand(target.Path.Trim().Trim('"'));
            if (path.Length == 0) continue;
            if (path.IndexOfAny(['\\', '/']) < 0)
            {
                bare = true;
                // "echo" or "start" typed after cmd /c is part of cmd, even if a program of that name (Git's echo.exe) happens to be on the PATH.
                if (ShellBuiltins.Contains(path)) continue;
                var onPath = system.FindOnPath(path) ?? (!Path.HasExtension(path) ? system.FindOnPath(path + ".exe") : null);
                if (onPath is not null && !StartupGuard.IsLauncher(onPath)) return onPath;
                continue;
            }
            first ??= path;
            if (system.FileExists(path)) return path;
        }
        // A bare name that cannot be found (a command such as "start notepad") proves nothing, so the item is not called missing on its account.
        return !bare && first is not null ? first : ResolveProgram(system, program);
    }

    /// <summary>The first candidate that exists, or the likeliest one if none does.</summary>
    private static string ResolveProgram(IStartupSystem system, string command)
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

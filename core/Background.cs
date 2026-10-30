using System.Text.RegularExpressions;

namespace TidyWindow.Next.Core;

public sealed record BackgroundTargetState(BackgroundTarget Target, bool Present, bool On, bool Running, string? Lock);

/// <summary>An item as it is on this PC right now. <see cref="On"/> means it is allowed to run; <see cref="Running"/> means something is running this minute.</summary>
public sealed record BackgroundEntry(BackgroundItem Item, bool Present, bool On, bool FullyOn, bool Running, string? Lock, IReadOnlyList<BackgroundTargetState> Targets)
{
    public string Id => Item.Id;
    /// <summary>It can always be turned back on; it can be turned off unless a rule says to leave it alone.</summary>
    public bool CanChange => Present && (Lock is null || !On);
}

public sealed record BackgroundScanResult(IReadOnlyList<BackgroundEntry> Entries, int NotOnThisPc);

/// <summary>
/// The rules for what may be changed. They look at facts read from the PC, never at what a caller says about an item, and they run again
/// in the administrator helper, so a request cannot talk its way past them.
/// </summary>
public static partial class BackgroundGuard
{
    // Things other programs lean on. Switching any of these off is how an app such as WhatsApp stops starting or stops showing notifications,
    // so they can never be in the catalog, whatever a list somewhere on the internet says.
    public static readonly IReadOnlySet<string> NeverServices = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "WpnService", "AppXSvc", "StateRepository", "ClipSVC", "InstallService", "BITS", "wuauserv", "UsoSvc", "WaaSMedicSvc", "TrustedInstaller", "CryptSvc",
        "WinDefend", "Sense", "SecurityHealthService", "wscsvc", "MpsSvc", "BFE", "webthreatdefsvc", "EventLog", "EventSystem", "RpcSs", "RpcEptMapper", "DcomLaunch",
        "Winmgmt", "Schedule", "ProfSvc", "UserManager", "gpsvc", "Themes", "SENS", "BrokerInfrastructure", "CoreMessaging", "TimeBrokerSvc", "StorSvc", "camsvc",
        "Audiosrv", "AudioEndpointBuilder", "Dhcp", "Dnscache", "NlaSvc", "nsi", "netprofm", "LanmanWorkstation", "LanmanServer", "WlanSvc", "Wcmsvc", "W32Time",
        "TokenBroker", "WebClient", "WSearch", "DoSvc", "Spooler", "PlugPlay", "Power", "LSM", "SamSs", "lsass", "CDPUserSvc", "WpnUserService", "cbdhsvc",
        "edgeupdate", "edgeupdatem", "MicrosoftEdgeElevationService", "ClickToRunSvc", "OneDrive Updater Service"
    };

    private static readonly string[] NeverTaskFolders =
    [
        @"\Microsoft\Windows\Windows Defender\", @"\Microsoft\Windows\WindowsUpdate\", @"\Microsoft\Windows\UpdateOrchestrator\", @"\Microsoft\Windows\WaaSMedic\",
        @"\Microsoft\Windows\Servicing\", @"\Microsoft\Windows\Defrag\", @"\Microsoft\Windows\TaskScheduler\", @"\Microsoft\Windows\Registry\", @"\Microsoft\Windows\Time Synchronization\",
        @"\Microsoft\Windows\Windows Filtering Platform\", @"\Microsoft\Windows\AppxDeploymentClient\", @"\Microsoft\Windows\Maintenance\", @"\Microsoft\Windows\SoftwareProtectionPlatform\"
    ];

    private static readonly string[] AllowedPolicyKeys = [@"SOFTWARE\Policies\Microsoft\Windows\WindowsAI", @"SOFTWARE\Policies\Microsoft\Dsh"];

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_.\- ]{0,127}$")]
    private static partial Regex ServiceName();

    [GeneratedRegex(@"_[0-9a-fA-F]{4,}$")]
    private static partial Regex UserServiceSuffix();

    [GeneratedRegex(@"^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex Slug();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]{0,63}$")]
    private static partial Regex ValueName();

    /// <summary>Everything wrong with a catalog, as sentences. Empty when it is fine.</summary>
    public static List<string> Validate(IReadOnlyList<BackgroundItem> items)
    {
        var problems = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            var who = $"“{item.Id}”";
            if (!Slug().IsMatch(item.Id)) problems.Add($"{who} needs a lowercase id with dashes.");
            if (!seen.Add(item.Id)) problems.Add($"{who} is listed twice.");
            if (string.IsNullOrWhiteSpace(item.Name) || string.IsNullOrWhiteSpace(item.Summary) || string.IsNullOrWhiteSpace(item.Effect)) problems.Add($"{who} needs a name, a summary and an effect.");
            if (item.Targets.Length == 0) problems.Add($"{who} switches nothing.");
            foreach (var target in item.Targets)
            {
                switch (target.Kind)
                {
                    case BackgroundTargetKind.Service:
                        if (!ServiceName().IsMatch(target.Name) || target.Name.Contains('*')) problems.Add($"{who}: “{target.Name}” is not a service name.");
                        else if (NeverServices.Contains(target.Name) || NeverServices.Any(x => target.Name.StartsWith(x + "_", StringComparison.OrdinalIgnoreCase))) problems.Add($"{who}: {target.Name} must never be switched off.");
                        if (UserServiceSuffix().IsMatch(target.Name)) problems.Add($"{who}: {target.Name} is a per-user service.");
                        if (target.Off is not ("disabled" or "manual")) problems.Add($"{who}: {target.Name} must turn off as “disabled” or “manual”.");
                        if (target.DefaultStart is not (2 or 3)) problems.Add($"{who}: {target.Name} must normally start automatically (2) or manually (3).");
                        if (target.Image.Length > 0 && (!target.Image.StartsWith(@"%ProgramFiles%\", StringComparison.OrdinalIgnoreCase) || target.Image.Contains(".."))) problems.Add($"{who}: {target.Name} may only live in a folder under Program Files.");
                        break;
                    case BackgroundTargetKind.Task:
                        if (!target.Name.StartsWith('\\') || target.Name.Count(c => c == '\\') < 2 || target.Name.EndsWith('\\') || target.Name.IndexOfAny(['*', '?']) >= 0) problems.Add($"{who}: “{target.Name}” is not the full path of one task.");
                        else if (NeverTaskFolders.Any(x => target.Name.StartsWith(x, StringComparison.OrdinalIgnoreCase))) problems.Add($"{who}: {target.Name} must never be switched off.");
                        break;
                    case BackgroundTargetKind.Policy:
                        if (!AllowedPolicyKeys.Contains(target.Key, StringComparer.OrdinalIgnoreCase)) problems.Add($"{who}: policy key “{target.Key}” is not one this page may write to.");
                        if (!ValueName().IsMatch(target.Name)) problems.Add($"{who}: “{target.Name}” is not a policy value name.");
                        break;
                }
            }
        }
        return problems;
    }

    /// <summary>True when a service's program is inside <paramref name="folder"/>.</summary>
    public static bool IsInFolder(string imagePath, string folder)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || string.IsNullOrWhiteSpace(folder)) return false;
        return imagePath.Trim().Trim('"').Replace('/', '\\').StartsWith(folder.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when a service's program is one Windows ships.</summary>
    public static bool IsInWindows(string imagePath, string windowsFolder)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || string.IsNullOrWhiteSpace(windowsFolder)) return false;
        var path = imagePath.Trim().Trim('"').Replace('/', '\\');
        foreach (var token in new[] { "%SystemRoot%", "%windir%", @"\SystemRoot" })
            if (path.StartsWith(token, StringComparison.OrdinalIgnoreCase)) { path = windowsFolder.TrimEnd('\\') + path[token.Length..]; break; }
        return path.StartsWith(windowsFolder.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Reads what is on this PC and works out, for each catalog item, whether it is present, allowed to run, running, and whether it is safe to turn off.</summary>
public static class BackgroundScan
{
    public static BackgroundScanResult Run(IBackgroundSystem system, IReadOnlyList<BackgroundItem> catalog)
    {
        system.Refresh();
        var entries = new List<BackgroundEntry>();
        var missing = 0;
        foreach (var item in catalog)
        {
            var entry = Evaluate(system, item);
            if (entry.Present) entries.Add(entry); else missing++;
        }
        return new(entries, missing);
    }

    public static BackgroundEntry? Find(IBackgroundSystem system, IReadOnlyList<BackgroundItem> catalog, string id)
    {
        system.Refresh();
        return catalog.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase)) is { } item && Evaluate(system, item) is { Present: true } entry ? entry : null;
    }

    private static BackgroundEntry Evaluate(IBackgroundSystem system, BackgroundItem item)
    {
        var sameItem = item.Targets.Where(x => x.Kind == BackgroundTargetKind.Service).Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var states = item.Targets.Select(target => target.Kind switch
        {
            BackgroundTargetKind.Service => ServiceState(system, target, sameItem),
            BackgroundTargetKind.Task => TaskState(system, target),
            _ => PolicyState(system, target)
        }).ToList();
        var present = states.Where(x => x.Present).ToList();
        return new(item, present.Count > 0, present.Any(x => x.On), present.Count > 0 && present.All(x => x.On), present.Any(x => x.Running),
            present.FirstOrDefault(x => x.Lock is not null)?.Lock, states);
    }

    private static BackgroundTargetState ServiceState(IBackgroundSystem system, BackgroundTarget target, HashSet<string> sameItem)
    {
        if (system.Service(target.Name) is not { } service) return new(target, false, false, false, null);
        var on = target.Off == "manual" ? service.Start <= 2 : service.Start != 4;
        // The registry keeps variables such as %ProgramFiles% unexpanded.
        var image = system.Expand(service.ImagePath);
        string? rule = null;
        if ((service.Type & 0xC0) != 0) rule = "A per-user service, which is left alone.";
        else if (!BackgroundGuard.IsInWindows(image, system.WindowsFolder) && !BackgroundGuard.IsInFolder(image, target.Image.Length > 0 ? system.Expand(target.Image) : "")) rule = "This was not installed by Windows, so it is left alone. Turn it off from the Startup page.";
        else if (on)
        {
            var needs = system.Dependents(target.Name).Where(x => !sameItem.Contains(x.Name) && (target.Off == "disabled" || x.Running)).ToList();
            if (needs.Count > 0) rule = $"{Name(needs[0])} relies on it, so turning it off could stop that from working.";
        }
        return new(target, true, on, service.Running, rule);
    }

    private static string Name(ServiceFact service) => service.DisplayName.Length > 0 && !service.DisplayName.StartsWith('@') ? service.DisplayName : service.Name;

    private static BackgroundTargetState TaskState(IBackgroundSystem system, BackgroundTarget target) =>
        system.Task(target.Name) is { } task ? new(target, true, task.Enabled, task.Running, null) : new(target, false, false, false, null);

    private static BackgroundTargetState PolicyState(IBackgroundSystem system, BackgroundTarget target)
    {
        var value = system.Policy(target.Key, target.Name);
        var on = value != target.Block;
        var rule = target.NeedsPro && system.Edition.StartsWith("Core", StringComparison.OrdinalIgnoreCase) ? "Windows Home does not support this setting." : null;
        return new(target, true, on, false, rule);
    }
}

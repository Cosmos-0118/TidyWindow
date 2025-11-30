using System;
using System.Collections.Immutable;

namespace TidyWindow.Core.Maintenance;

/// <summary>
/// Provides the curated list of essentials automation tasks exposed in the UI.
/// </summary>
public sealed class EssentialsTaskCatalog
{
    private readonly ImmutableArray<EssentialsTaskDefinition> _tasks;

    public EssentialsTaskCatalog()
    {
        _tasks = ImmutableArray.Create(
            new EssentialsTaskDefinition(
                "network-reset",
                "Network reset & cache flush",
                "Network",
                "Flushes DNS/ARP/TCP caches and restarts adapters for fresh connectivity.",
                ImmutableArray.Create(
                    "Flushes DNS, ARP, and Winsock stacks",
                    "Optionally restarts adapters and renews DHCP"),
                "automation/essentials/network-reset-and-cache-flush.ps1",
                DurationHint: "Approx. 3-6 minutes (adapter refresh adds a couple more)",
                DetailedDescription: "Flushes DNS, ARP, and TCP caches, resets Winsock and IP stacks, can restart adapters, renew DHCP leases, and tracks which actions succeeded with reboot guidance.",
                DocumentationLink: "docs/essentials-overview.md#1-network-reset--cache-flush",
                Options: ImmutableArray.Create(
                    new EssentialsTaskOptionDefinition(
                        id: "adapter-refresh",
                        label: "Restart adapters after resets",
                        parameterName: "IncludeAdapterRefresh",
                        defaultValue: false,
                        description: "Disables and re-enables active physical adapters."),
                    new EssentialsTaskOptionDefinition(
                        id: "dhcp-renew",
                        label: "Force DHCP release/renew",
                        parameterName: "IncludeDhcpRenew",
                        defaultValue: false,
                        description: "Runs ipconfig /release and /renew."),
                    new EssentialsTaskOptionDefinition(
                        id: "winsock-reset",
                        label: "Reset Winsock catalog",
                        parameterName: "SkipWinsockReset",
                        mode: EssentialsTaskOptionMode.EmitWhenFalse,
                        description: "Skips the wsreset-style Winsock reset when unchecked."),
                    new EssentialsTaskOptionDefinition(
                        id: "ip-reset",
                        label: "Reset IP stack",
                        parameterName: "SkipIpReset",
                        mode: EssentialsTaskOptionMode.EmitWhenFalse,
                        description: "Skips netsh int ip reset when unchecked."))),

            new EssentialsTaskDefinition(
                "system-health",
                "System health scanner",
                "Integrity",
                "Runs SFC and DISM passes to repair core Windows components.",
                ImmutableArray.Create(
                    "Runs SFC /scannow and DISM health checks",
                    "Optional component cleanup to reclaim space"),
                "automation/essentials/system-health-scanner.ps1",
                DurationHint: "Approx. 30-60 minutes (SFC/DISM phases vary by corruption)",
                DetailedDescription: "Automates a full SFC scan followed by DISM CheckHealth, ScanHealth, and RestoreHealth repairs with optional StartComponentCleanup, component store analysis, restore point creation, and transcript logging.",
                DocumentationLink: "docs/essentials-overview.md#2-system-health-scanner-sfc--dism",
                Options: ImmutableArray.Create(
                    new EssentialsTaskOptionDefinition(
                        id: "run-sfc",
                        label: "Run SFC /scannow",
                        parameterName: "SkipSfc",
                        mode: EssentialsTaskOptionMode.EmitWhenFalse),
                    new EssentialsTaskOptionDefinition(
                        id: "run-dism",
                        label: "Run DISM Check/Scan",
                        parameterName: "SkipDism",
                        mode: EssentialsTaskOptionMode.EmitWhenFalse),
                    new EssentialsTaskOptionDefinition(
                        id: "run-restorehealth",
                        label: "Run DISM RestoreHealth",
                        parameterName: "SkipRestoreHealth",
                        mode: EssentialsTaskOptionMode.EmitWhenFalse),
                    new EssentialsTaskOptionDefinition(
                        id: "component-cleanup",
                        label: "Run component cleanup",
                        parameterName: "ComponentCleanup",
                        defaultValue: false),
                    new EssentialsTaskOptionDefinition(
                        id: "analyze-store",
                        label: "Analyze component store",
                        parameterName: "AnalyzeComponentStore",
                        defaultValue: false),
                    new EssentialsTaskOptionDefinition(
                        id: "post-restore-point",
                        label: "Create restore point after repairs",
                        parameterName: "CreateSystemRestorePoint",
                        defaultValue: false))),

            new EssentialsTaskDefinition(
                "disk-check",
                "Disk checkup & repair",
                "Storage",
                "Schedules CHKDSK scans, repairs volumes, and collects SMART data.",
                ImmutableArray.Create(
                    "Detects dirty volumes and queues boot-time repairs",
                    "Captures SMART telemetry for context"),
                "automation/essentials/disk-checkup-and-fix.ps1",
                DurationHint: "Approx. 8-18 minutes to scan; offline repairs after reboot can add 30+ minutes",
                DetailedDescription: "Identifies the target volume, runs CHKDSK in scan or repair modes, schedules offline repairs when the drive is busy, and aggregates SMART telemetry and findings into a concise summary.",
                DocumentationLink: "docs/essentials-overview.md#3-disk-checkup-and-fix-chkdsk--smart",
                Options: ImmutableArray.Create(
                    new EssentialsTaskOptionDefinition(
                        id: "perform-repair",
                        label: "Attempt repairs (/f)",
                        parameterName: "PerformRepair",
                        defaultValue: false,
                        description: "Adds /f to repair logical errors and may require reboot."),
                    new EssentialsTaskOptionDefinition(
                        id: "surface-scan",
                        label: "Include surface scan (/r)",
                        parameterName: "IncludeSurfaceScan",
                        defaultValue: false,
                        description: "Adds /r to scan for bad sectors (implies /f and can take much longer)."),
                    new EssentialsTaskOptionDefinition(
                        id: "schedule-if-busy",
                        label: "Schedule repair if volume is busy",
                        parameterName: "ScheduleIfBusy",
                        defaultValue: false),
                    new EssentialsTaskOptionDefinition(
                        id: "collect-smart",
                        label: "Collect SMART telemetry",
                        parameterName: "SkipSmart",
                        mode: EssentialsTaskOptionMode.EmitWhenFalse))),

            new EssentialsTaskDefinition(
                "ram-purge",
                "RAM purge",
                "Performance",
                "Frees standby memory, trims working sets, and manages SysMain for headroom.",
                ImmutableArray.Create(
                    "Downloads EmptyStandbyList if required",
                    "Trims heavy process working sets safely"),
                "automation/essentials/ram-purge.ps1",
                DurationHint: "Approx. 2-4 minutes (download adds ~1 minute on first run)",
                DetailedDescription: "Fetches EmptyStandbyList when missing, clears standby lists, trims memory-heavy processes, optionally pauses SysMain, and restores it once headroom is reclaimed.",
                DocumentationLink: "docs/essentials-overview.md#4-ram-purge",
                Options: ImmutableArray.Create(
                    new EssentialsTaskOptionDefinition(
                        id: "clear-standby",
                        label: "Clear standby memory lists",
                        parameterName: "SkipStandbyClear",
                        mode: EssentialsTaskOptionMode.EmitWhenFalse),
                    new EssentialsTaskOptionDefinition(
                        id: "trim-working-sets",
                        label: "Trim heavy working sets",
                        parameterName: "SkipWorkingSetTrim",
                        mode: EssentialsTaskOptionMode.EmitWhenFalse),
                    new EssentialsTaskOptionDefinition(
                        id: "toggle-sysmain",
                        label: "Pause SysMain during purge",
                        parameterName: "SkipSysMainToggle",
                        mode: EssentialsTaskOptionMode.EmitWhenFalse))),

            new EssentialsTaskDefinition(
                "restore-manager",
                "System Restore manager",
                "Recovery",
                "Creates, lists, and prunes restore points to safeguard maintenance flows.",
                ImmutableArray.Create(
                    "Enables restore across targeted drives",
                    "Prunes aged or excess checkpoints"),
                "automation/essentials/system-restore-manager.ps1",
                DurationHint: "Approx. 4-8 minutes when creating a new restore point",
                DetailedDescription: "Validates System Restore configuration, enables protection per drive, creates fresh checkpoints, lists historical restore points, and prunes by age or quota in one run.",
                DocumentationLink: "docs/essentials-overview.md#5-system-restore-snapshot-manager"),

            new EssentialsTaskDefinition(
                "network-fix",
                "Network fix suite",
                "Network",
                "Runs advanced adapter resets alongside diagnostics like traceroute and pathping.",
                ImmutableArray.Create(
                    "Resets ARP/NBT/TCP heuristics and re-registers DNS",
                    "Captures latency samples and adapter stats"),
                "automation/essentials/network-fix-suite.ps1",
                DurationHint: "Approx. 6-12 minutes (pathping plus diagnostics)",
                DetailedDescription: "Resets network heuristics, re-registers DNS, runs adapter restarts, traceroute, ping sweeps, and pathping loss analysis, and captures adapter statistics for troubleshooting.",
                DocumentationLink: "docs/essentials-overview.md#6-network-fix-suite-advanced",
                Options: ImmutableArray.Create(
                    new EssentialsTaskOptionDefinition(
                        id: "diagnostics-only",
                        label: "Diagnostics only (skip remediation)",
                        parameterName: "DiagnosticsOnly",
                        defaultValue: false),
                    new EssentialsTaskOptionDefinition(
                        id: "run-traceroute",
                        label: "Run traceroute",
                        parameterName: "SkipTraceroute",
                        mode: EssentialsTaskOptionMode.EmitWhenFalse),
                    new EssentialsTaskOptionDefinition(
                        id: "run-pathping",
                        label: "Run pathping",
                        parameterName: "SkipPathPing",
                        mode: EssentialsTaskOptionMode.EmitWhenFalse),
                    new EssentialsTaskOptionDefinition(
                        id: "dns-registration",
                        label: "Re-register DNS",
                        parameterName: "SkipDnsRegistration",
                        mode: EssentialsTaskOptionMode.EmitWhenFalse))),

            new EssentialsTaskDefinition(
                "app-repair",
                "App repair helper",
                "Apps",
                "Resets Microsoft Store infrastructure and re-registers critical AppX packages.",
                ImmutableArray.Create(
                    "Clears Store cache and restarts dependent services",
                    "Re-registers App Installer and framework packages"),
                "automation/essentials/app-repair-helper.ps1",
                DurationHint: "Approx. 7-12 minutes depending on package re-registration",
                DetailedDescription: "Flushes the Microsoft Store cache, restarts licensing services, re-registers App Installer, Store, and supporting AppX frameworks for all users, and verifies provisioning state.",
                DocumentationLink: "docs/essentials-overview.md#7-app-repair-helper-storeuwp",
                Options: ImmutableArray.Create(
                    new EssentialsTaskOptionDefinition(
                        id: "reset-store-cache",
                        label: "Reset Microsoft Store cache",
                        parameterName: "ResetStoreCache"),
                    new EssentialsTaskOptionDefinition(
                        id: "re-register-store",
                        label: "Re-register Store components",
                        parameterName: "ReRegisterStore"),
                    new EssentialsTaskOptionDefinition(
                        id: "repair-app-installer",
                        label: "Repair App Installer",
                        parameterName: "ReRegisterAppInstaller"),
                    new EssentialsTaskOptionDefinition(
                        id: "re-register-builtins",
                        label: "Re-register built-in apps",
                        parameterName: "ReRegisterPackages"),
                    new EssentialsTaskOptionDefinition(
                        id: "refresh-frameworks",
                        label: "Refresh AppX frameworks",
                        parameterName: "IncludeFrameworks"),
                    new EssentialsTaskOptionDefinition(
                        id: "repair-licensing",
                        label: "Repair licensing services",
                        parameterName: "ConfigureLicensingServices"),
                    new EssentialsTaskOptionDefinition(
                        id: "current-user-only",
                        label: "Limit repairs to current user",
                        parameterName: "CurrentUserOnly",
                        defaultValue: false))),

            new EssentialsTaskDefinition(
                "windows-update-repair",
                "Windows Update repair toolkit",
                "Updates",
                "Resets Windows Update services, caches, and supporting components in one pass.",
                ImmutableArray.Create(
                    "Stops services, resets SoftwareDistribution and Catroot2",
                    "Re-registers DLLs and can trigger fresh scans"),
                "automation/essentials/windows-update-repair-toolkit.ps1",
                DurationHint: "Approx. 25-45 minutes (cache reset plus optional DISM/SFC)",
                DetailedDescription: "Stops the Windows Update service stack, clears SoftwareDistribution and Catroot2, re-registers core DLLs, removes stuck policies, optionally runs DISM/SFC, and kicks off a new update scan.",
                DocumentationLink: "docs/essentials-overview.md#9-windows-update-repair-toolkit",
                Options: ImmutableArray.Create(
                    new EssentialsTaskOptionDefinition(
                        id: "reset-services",
                        label: "Reset update services",
                        parameterName: "ResetServices"),
                    new EssentialsTaskOptionDefinition(
                        id: "reset-components",
                        label: "Reset update components",
                        parameterName: "ResetComponents"),
                    new EssentialsTaskOptionDefinition(
                        id: "reregister-libraries",
                        label: "Re-register update DLLs",
                        parameterName: "ReRegisterLibraries"),
                    new EssentialsTaskOptionDefinition(
                        id: "run-dism-restorehealth",
                        label: "Run DISM RestoreHealth",
                        parameterName: "RunDismRestoreHealth"),
                    new EssentialsTaskOptionDefinition(
                        id: "run-sfc",
                        label: "Run SFC /scannow",
                        parameterName: "RunSfc"),
                    new EssentialsTaskOptionDefinition(
                        id: "trigger-scan",
                        label: "Trigger Windows Update scan",
                        parameterName: "TriggerScan"),
                    new EssentialsTaskOptionDefinition(
                        id: "reset-policies",
                        label: "Reset WU policies",
                        parameterName: "ResetPolicies"),
                    new EssentialsTaskOptionDefinition(
                        id: "reset-network",
                        label: "Reset network stack",
                        parameterName: "ResetNetwork"))),

            new EssentialsTaskDefinition(
                "defender-repair",
                "Windows Defender repair & deep scan",
                "Security",
                "Restores Microsoft Defender services, forces signature updates, and runs targeted scans.",
                ImmutableArray.Create(
                    "Restarts WinDefend, WdNisSvc, and SecurityHealthService with safe defaults",
                    "Updates signatures and supports quick, full, or custom scans with dry-run logging"),
                "automation/essentials/windows-defender-repair-and-deep-scan.ps1",
                DurationHint: "Approx. 15-30 minutes (full scans or large path sets extend runtime)",
                DetailedDescription: "Heals Microsoft Defender by restarting critical services, forcing signature and engine refreshes, optionally re-enabling real-time protection, and executing quick, full, or custom scans while producing transcripts and JSON run summaries for the UI.",
                DocumentationLink: "docs/essentials-overview.md#10-windows-defender-repair--deep-scan"),

            new EssentialsTaskDefinition(
                "print-spooler-recovery",
                "Print spooler recovery suite",
                "Printing",
                "Clears jammed queues, rebuilds spooler services, and restores DLL registrations.",
                ImmutableArray.Create(
                    "Stops Spooler/PrintNotify, purges %SystemRoot%\\System32\\spool\\PRINTERS",
                    "Optional DLL re-registration, stale driver cleanup, and isolation policy reset"),
                "automation/essentials/print-spooler-recovery-suite.ps1",
                DurationHint: "Approx. 5-12 minutes (driver cleanup may extend runtime)",
                DetailedDescription: "Automates end-to-end spooler remediation by restarting services, flushing stuck print jobs, optionally removing stale drivers, re-registering spooler DLLs, and resetting isolation policies with dry-run visibility for support teams.",
                DocumentationLink: "docs/essentials-overview.md#12-print-spooler-recovery-suite",
                Options: ImmutableArray.Create(
                    new EssentialsTaskOptionDefinition(
                        id: "service-reset",
                        label: "Stop & restart spooler services",
                        parameterName: "SkipServiceReset",
                        mode: EssentialsTaskOptionMode.EmitWhenFalse),
                    new EssentialsTaskOptionDefinition(
                        id: "queue-purge",
                        label: "Clear print queue",
                        parameterName: "SkipSpoolPurge",
                        mode: EssentialsTaskOptionMode.EmitWhenFalse),
                    new EssentialsTaskOptionDefinition(
                        id: "driver-cleanup",
                        label: "Remove stale printer drivers",
                        parameterName: "SkipDriverRefresh",
                        mode: EssentialsTaskOptionMode.EmitWhenFalse),
                    new EssentialsTaskOptionDefinition(
                        id: "dll-registration",
                        label: "Re-register spooler DLLs",
                        parameterName: "SkipDllRegistration",
                        mode: EssentialsTaskOptionMode.EmitWhenFalse),
                    new EssentialsTaskOptionDefinition(
                        id: "isolation-reset",
                        label: "Reset print isolation policies",
                        parameterName: "SkipPrintIsolationPolicy",
                        mode: EssentialsTaskOptionMode.EmitWhenFalse))));
    }

    public ImmutableArray<EssentialsTaskDefinition> Tasks => _tasks;

    public EssentialsTaskDefinition GetTask(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("Task id must be provided.", nameof(id));
        }

        foreach (var task in _tasks)
        {
            if (string.Equals(task.Id, id, StringComparison.OrdinalIgnoreCase))
            {
                return task;
            }
        }

        throw new InvalidOperationException($"Unknown essentials task '{id}'.");
    }
}

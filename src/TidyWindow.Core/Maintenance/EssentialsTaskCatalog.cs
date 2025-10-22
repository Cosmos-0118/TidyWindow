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
                DocumentationLink: "docs/essentials-overview.md#1-network-reset--cache-flush"),

            new EssentialsTaskDefinition(
                "system-health",
                "System health scanner",
                "Integrity",
                "Runs SFC and DISM passes to repair core Windows components.",
                ImmutableArray.Create(
                    "Runs SFC /scannow and DISM health checks",
                    "Optional component cleanup to reclaim space"),
                "automation/essentials/system-health-scanner.ps1",
                DocumentationLink: "docs/essentials-overview.md#2-system-health-scanner-sfc--dism"),

            new EssentialsTaskDefinition(
                "disk-check",
                "Disk checkup & repair",
                "Storage",
                "Schedules CHKDSK scans, repairs volumes, and collects SMART data.",
                ImmutableArray.Create(
                    "Detects dirty volumes and queues boot-time repairs",
                    "Captures SMART telemetry for context"),
                "automation/essentials/disk-checkup-and-fix.ps1",
                DocumentationLink: "docs/essentials-overview.md#3-disk-checkup-and-fix-chkdsk--smart"),

            new EssentialsTaskDefinition(
                "ram-purge",
                "RAM purge",
                "Performance",
                "Frees standby memory, trims working sets, and manages SysMain for headroom.",
                ImmutableArray.Create(
                    "Downloads EmptyStandbyList if required",
                    "Trims heavy process working sets safely"),
                "automation/essentials/ram-purge.ps1",
                DocumentationLink: "docs/essentials-overview.md#4-ram-purge"),

            new EssentialsTaskDefinition(
                "restore-manager",
                "System Restore manager",
                "Recovery",
                "Creates, lists, and prunes restore points to safeguard maintenance flows.",
                ImmutableArray.Create(
                    "Enables restore across targeted drives",
                    "Prunes aged or excess checkpoints"),
                "automation/essentials/system-restore-manager.ps1",
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
                DocumentationLink: "docs/essentials-overview.md#6-network-fix-suite-advanced"),

            new EssentialsTaskDefinition(
                "app-repair",
                "App repair helper",
                "Apps",
                "Resets Microsoft Store infrastructure and re-registers critical AppX packages.",
                ImmutableArray.Create(
                    "Clears Store cache and restarts dependent services",
                    "Re-registers App Installer and framework packages"),
                "automation/essentials/app-repair-helper.ps1",
                DocumentationLink: "docs/essentials-overview.md#7-app-repair-helper-storeuwp"),

            new EssentialsTaskDefinition(
                "startup-analyzer",
                "Startup impact analyzer",
                "Diagnostics",
                "Inventories startup hooks, services, and boot history to surface heavy hitters.",
                ImmutableArray.Create(
                    "Lists registry, scheduled task, and service startup entries",
                    "Exports optional CSV or JSON inventories"),
                "automation/essentials/startup-impact-analyzer.ps1",
                DocumentationLink: "docs/essentials-overview.md#8-startup-impact-analyzer"),

            new EssentialsTaskDefinition(
                "windows-update-repair",
                "Windows Update repair toolkit",
                "Updates",
                "Resets Windows Update services, caches, and supporting components in one pass.",
                ImmutableArray.Create(
                    "Stops services, resets SoftwareDistribution and Catroot2",
                    "Re-registers DLLs and can trigger fresh scans"),
                "automation/essentials/windows-update-repair-toolkit.ps1",
                DocumentationLink: "docs/essentials-overview.md#9-windows-update-repair-toolkit"));
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

# Essentials Automation Overview

The `automation/essentials` suite packages the highest impact maintenance flows into dedicated PowerShell scripts. Each script is designed to run elevated (unless noted), emit transcript-friendly output, and optionally persist results via the shared JSON contract (`-ResultPath`).

| #   | Script                                                            | Primary Focus                                             |
| --- | ----------------------------------------------------------------- | --------------------------------------------------------- |
| 1   | `automation/essentials/network-reset-and-cache-flush.ps1`         | Resets network stacks, caches, and adapters               |
| 2   | `automation/essentials/system-health-scanner.ps1`                 | Runs SFC/DISM integrity checks and cleanup                |
| 3   | `automation/essentials/disk-checkup-and-fix.ps1`                  | Drives CHKDSK scans, repairs, and SMART telemetry         |
| 4   | `automation/essentials/ram-purge.ps1`                             | Frees standby memory, trims working sets, toggles SysMain |
| 5   | `automation/essentials/system-restore-manager.ps1`                | Manages System Restore snapshots and policies             |
| 6   | `automation/essentials/network-fix-suite.ps1`                     | Runs advanced connectivity diagnostics and resets         |
| 7   | `automation/essentials/app-repair-helper.ps1`                     | Repairs Microsoft Store/AppX registrations and services   |
| 8   | `automation/essentials/startup-impact-analyzer.ps1`               | Inventories startup entries, services, and boot history   |
| 9   | `automation/essentials/windows-update-repair-toolkit.ps1`         | Resets Windows Update services, caches, and components    |
| 10  | `automation/essentials/windows-defender-repair-and-deep-scan.ps1` | Repairs Defender services, updates signatures, and scans  |
| 11  | `automation/essentials/storage-declutter-and-temp-cleanup.ps1`    | Cleans temp caches and reclaims component store space     |
| 12  | `automation/essentials/print-spooler-recovery-suite.ps1`          | Repairs the print spooler pipeline end-to-end             |

## 1. Network Reset & Cache Flush

-   Flushes DNS, ARP, and TCP cache data, with toggles for IP/Winsock resets.
-   Optional adapter restarts and DHCP renewal for stubborn link issues.
-   Emits a reboot reminder if Winsock was reset.

## 2. System Health Scanner (SFC + DISM)

-   Runs `sfc /scannow`, `DISM /CheckHealth`, `DISM /ScanHealth`, and optional `RestoreHealth`.
-   Supports optional component cleanup and `AnalyzeComponentStore` for disk reclamation.
-   Ideal pre-flight for deeper repair runs.

## 3. Disk Checkup and Fix (CHKDSK + SMART)

-   Executes `chkdsk` in scan, repair, or surface-scan modes.
-   Detects busy volumes and can schedule repairs for the next boot.
-   Collects SMART status via WMI for health context.

## 4. RAM Purge

-   Downloads Sysinternals `EmptyStandbyList` when absent and clears standby lists.
-   Uses native `EmptyWorkingSet` interop to trim process working sets.
-   Temporarily pauses and restores SysMain for additional cache release.

## 5. System Restore Snapshot Manager

-   Enables/disables System Restore per drive, creates snapshots, lists history.
-   Prunes excess points (`-KeepLatest`) or aged ones (`-PurgeOlderThanDays`).
-   Safe guardrail before running aggressive maintenance flows.

## 6. Network Fix Suite (Advanced)

-   Resets ARP/NBT/TCP heuristics and optionally registers DNS records.
-   Collects adapter stats, DNS resolution, traceroute, `pathping`, and latency samples.
-   Provides a single transcript with both remediation and diagnostics.

## 7. App Repair Helper (Store/UWP)

-   Resets the Microsoft Store cache, re-registers Store/App Installer/AppX packages.
-   Refreshes framework dependencies and restarts licensing services when requested.
-   Supports multi-user repairs (default) or current-user only.

## 8. Startup Impact Analyzer

-   Enumerates registry, WMI, scheduled task, and service startup hooks.
-   Surfaces size, signatures, and recent write times to flag heavy hitters.
-   Optional export (`-ExportPath`) to CSV or JSON, plus boot-time event summary.

## 9. Windows Update Repair Toolkit

-   Stops/starts the Windows Update service set while resetting caches.
-   Renames `SoftwareDistribution` and `Catroot2`, re-registers key DLLs.
-   Offers optional DISM/SFC passes, policy cleanup, network resets, and scan triggers.

## 10. Windows Defender Repair & Deep Scan

-   Restarts core Microsoft Defender services (`WinDefend`, `WdNisSvc`, and `SecurityHealthService`) to recover when protection components are stuck or disabled.
-   Updates signature and engine payloads through `Update-MpSignature`, capturing transcript and JSON summaries for UI consumption.
-   Offers quick, full, or custom path scans via `Start-MpScan`, with dry-run support and post-run status snapshots to verify real-time protection is restored.

## 11. Storage Declutter & Temp Cleanup

-   Clears user and system temp directories with built-in dry-run mode and safe directory guards to avoid active cache removal.
-   Optionally purges Delivery Optimization, Windows Update downloads, error report queues, and recycle bin contents for deeper reclamation.
-   Chains `DISM /StartComponentCleanup` (and optional `/ResetBase`) so WinSxS stays lean while transcripts and JSON summaries capture reclaimed space.

## 12. Print Spooler Recovery Suite

-   Stops and restarts `Spooler`/`PrintNotify`, purges stuck jobs in `%SystemRoot%\System32\spool\PRINTERS`, and tracks each phase for clear audit trails.
-   Optionally re-registers core DLLs (`spoolss.dll`, `win32spl.dll`, `localspl.dll`, `printui.dll`), cleans stale drivers, and resets isolation policies to defaults.
-   Dry-run mode previews every action so support teams can confirm impact before touching production queues.

## Running the Scripts Manually

```powershell
# Recommended pattern: elevated PowerShell session
powershell.exe -ExecutionPolicy Bypass -File automation/essentials/<script>.ps1 -ResultPath C:\Temp\tidy-result.json
```

Each script can be wired into the WPF UI or invoked headlessly by reading the JSON payload (`Success`, `Output`, and `Errors`).

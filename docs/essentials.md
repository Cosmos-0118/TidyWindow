# Essentials Page

Essentials is the one-stop shelf for high-impact Windows repairs and maintenance. Every task is curated, parameterized, and executed sequentially with transcripts and status updates so operators can see exactly what happened and why.

## Principles

-   **Safety first**: Sequential execution, admin checks, guardrails around services/paths, and restore-point guidance via System Restore Manager. Browser Reset offers dry-run preview; expanding opt-in dry-run to other risky tasks is on the roadmap.
-   **Transparency**: Each run streams output/errors into the Queue view and Activity Log. Scripts emit structured JSON summaries when `ResultPath` is provided.
-   **Control**: Tasks expose clear options (emit-on-true/emit-on-false). Riskier toggles (e.g., display adapter resets) default off unless you explicitly allow them.
-   **Recoverability**: System Health and System Restore Manager support checkpoints; other tasks remain revertible through Windows built-ins where possible.

## How the page works

-   **Task shelf**: Browse tiles, open details, configure options, queue a run.
-   **Queue + output**: Single-reader channel runs one task at a time; progress, transcripts, and errors surface live. Cancel or retry failed operations in-place.
-   **Automation settings**: (when enabled) pick tasks and schedules; automation still respects sequential execution.

Key implementation points:

-   Definitions live in [src/TidyWindow.Core/Maintenance/EssentialsTaskCatalog.cs](src/TidyWindow.Core/Maintenance/EssentialsTaskCatalog.cs). Options map 1:1 to PowerShell switches/parameters.
-   PowerShell lives under `automation/essentials/*.ps1`; scripts enforce `Set-StrictMode -Version Latest` and `$ErrorActionPreference = 'Stop'`, with consistent logging helpers.
-   Queue processing is single-flight; state persists so pending/finished work restores after app relaunch (running items revert to pending for safety).

## Safety controls

-   **Elevation checks**: Tasks that touch services/registry validate admin context before proceeding.
-   **Dry-run (today)**: Browser Reset supports preview-only. Other tasks execute live; add a restore point first for invasive actions.
-   **Restore points**: Use System Restore Manager or the System Health toggle before heavy repairs (Updates, Defender, activation/licensing, etc.).
-   **Cancellation**: Active operations can be cancelled; the queue will honor cancellation tokens and surface partial output.
-   **Logging**: Output, errors, JSON summaries (when requested), and Activity Log entries for every run.

## Task catalog (current)

-   **Network & connectivity**
    -   Network reset & cache flush: DNS/ARP/TCP flush, Winsock/IP reset, optional adapter restart, DHCP renew.
    -   Network fix suite (advanced): diagnostics + adapter refresh, traceroute/pathping, DNS re-register, optional remediation.
-   **Integrity & recovery**
    -   System health scanner (SFC/DISM with optional cleanup/restore point).
    -   Disk checkup & repair (CHKDSK scheduling, SMART capture, surface scan optional).
    -   System Restore manager (create/list/prune checkpoints).
    -   Recovery & boot repair (safeboot clear, bootrec, offline DISM guidance, testsigning off, time sync, WMI salvage/reset, dump + driver inventory).
-   **Performance & storage**
    -   Performance & storage repair (SysMain disable, pagefile policy, temp/prefetch cleanup, event-log trim, power plans reset/HP plan).
    -   RAM purge (standby clear, working set trim, optional SysMain pause).
-   **Devices & peripherals**
    -   Audio & peripheral repair (Audio stack restart, endpoint rescan, Bluetooth AVCTP reset, USB hub refresh, mic/camera enable).
    -   Graphics & display repair (adapter toggle, display services restart, HDR/night light refresh, resolution reapply, EDID/PnP rescan, optional DWM/color/GPU panel resets with safety opt-in).
    -   Device drivers & PnP repair (pnputil rescan, non-Microsoft oem\*.inf cleanup, PlugPlay/DPS/WudfSvc restart, USB selective suspend disable).
-   **Shell & user experience**
    -   Shell & UI repair (ShellExperienceHost/StartMenu re-register, search reset, explorer recycle, Settings re-register, tray refresh).
    -   File Explorer & context repair (stale shell extension block, .exe/.lnk association repair, default library restore, double-click/Explorer policy reset).
    -   PowerShell environment repair (execution policy to RemoteSigned, profile reset, PSRemoting/WinRM enable, optional PSModulePath/system profile repair).
-   **Accounts & profile**
    -   Profile & logon repair (startup audit, ProfileImagePath repair, ProfSvc restart + Userinit fix, stale profile cleanup).
-   **Security & platform**
    -   Security & credential repair (firewall reset, Security app re-register, credential vault rebuild, EnableLUA enforcement).
    -   Activation & licensing repair (activation DLL re-register, Software Protection refresh, slmgr /ato, optional /rearm).
    -   TPM, BitLocker & Secure Boot repair (BitLocker suspend/resume, TPM clear request, Secure Boot guidance, device encryption prerequisites, optional status outputs).
    -   Windows Defender repair & deep scan (service heal, signatures, quick/full/custom scans, optional real-time heal).
-   **Apps & store**
    -   App repair helper (Store/AppX resets and provisioning, App Installer repair, licensing services, capability access reset, optional reinstall if missing).
    -   Browser reset & cache cleanup (Edge/Chrome/Brave/Firefox/Opera caches, policies, WebView, optional Edge repair; dry-run supported).
    -   OneDrive & cloud sync repair (OneDrive reset/restart, sync service restart, KFM mapping repair, autorun/task recreate).
-   **Updates & automation**
    -   Windows Update repair toolkit (service/component reset, DLL re-register, optional DISM/SFC, policy reset, scan trigger, network reset option).
    -   Task Scheduler repair (TaskCache rebuild, USO/Windows Update tasks re-enable/rebuild, Schedule restart).
-   **Time & region**
    -   Time & region repair (time zone + NTP resync, locale/language reset, Windows Time repair, optional fallback peers and offset report).
-   **Printing**
    -   Print spooler recovery (spooler reset, queue purge, optional stale driver cleanup, DLL re-register, isolation policy reset).

For mapping between catalog items and issue coverage, see [essentialsaddition.md](essentialsaddition.md).

## Operator guidance

-   Queue one high-impact task at a time; avoid stacking heavy operations (e.g., Windows Update repair immediately after System Health) without a restore point.
-   Use System Restore Manager first when running activation/licensing, Update repair, Defender deep scans, or recovery/boot fixes on production machines.
-   Prefer Browser Reset dry-run when cleaning multiple browsers to preview impact; run live once confirmed.
-   If a task fails, review the transcript in Queue → Details, then retry once. Persistent failures likely need manual remediation.

## Developer notes

-   Add new tasks in [src/TidyWindow.Core/Maintenance/EssentialsTaskCatalog.cs](src/TidyWindow.Core/Maintenance/EssentialsTaskCatalog.cs) and point to a script under `automation/essentials`.
-   Use the shared logging helpers in the automation module; keep `Set-StrictMode -Version Latest` and `$ErrorActionPreference = 'Stop'` at the top of scripts.
-   Emit clear, user-facing output; avoid silent skips. Prefer idempotent changes and guard risky steps with toggles named `Skip*` or explicit opt-in flags.
-   When adding risky actions, default them off and document expected side effects (reboot prompts, display flicker, service restarts).

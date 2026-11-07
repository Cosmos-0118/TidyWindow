# Fixes: repairs for common Windows problems

Research and implementation decision, 30 October 2026. This replaces the old Essentials page. The old page was about 26 PowerShell scripts of 450 to 1,450 lines each, run from a queue that could not show their output. Fixes keeps what worked, drops what was risky or pointless, and shows every step live in the Queue.

## Decision

A fix is a short list of steps in data (`src/TidyWindow.Core/Fixes/fix-catalog.json`): a standard Windows program with exact arguments, or one of four file and Explorer steps. Each step says which exit codes mean success, a warning, or "restart needed", and whether the fix may carry on after it fails. A runner executes them one at a time and reports as it goes. Nothing is a script.

This keeps the useful part of the old page (a shelf of repairs, options, restart hints) without 17,000 lines of wrapper code, and makes every fix readable on the page before it runs.

## Why output was missing before, and how it shows now

- The old scripts collected their output into lists and result files, and printed it afterwards. The app also started administrator work in a separate window whose output cannot be read (the Queue says so for Chocolatey).
- Fixes read each program's output as it arrives and pass it to the same Queue the installs use, so the Queue page, its colors and its collapsing rows all work for fixes.
- For fixes that need administrator rights, the existing helper (`TidyWindow.CleanHost.exe --fix`) runs one step at a time and sends each line back over a private pipe. The helper is never given a command: it is given a fix id, the chosen options and a step position, and rebuilds the step from the catalog. It refuses a fix for the current user, a step the options turned off, and anything not in its catalog.
- Programs that redraw one progress line (DISM, chkdsk) would flood the output, so redraws are shown about every ten percent. `sfc` writes UTF-16 (checked on this PC; its line endings are `\r\r\n`), so it is read as UTF-16. Other tools are read in the console code page, and Windows PowerShell is told to write UTF-8.
- The Fixes page also shows the newest line of output on each row while it runs.

## What was audited

All 26 old scripts parse without errors, so the problem was not broken syntax. The audit looked at what each one runs:

| Old script | What happens to it |
| --- | --- |
| Network reset and cache flush, Network fix suite | **Refresh the network connection** and **Reset the network stack**. Dropped the TCP "heuristics" and auto-tuning changes (a tuning side effect nobody asked for), and the long traceroute/pathping diagnostics. |
| System health scanner | **Repair Windows system files**. The old script ran SFC before DISM. Microsoft says DISM first, because it supplies the files SFC uses. |
| Disk checkup and fix | **Check the drive for errors**, read-only. It no longer schedules repairs by itself. |
| System restore manager | **Create a restore point**. Listing and pruning were dropped. |
| Windows Update repair toolkit | **Reset Windows Update**, following Microsoft's steps: stop the services, remove the queue files, rename the download folder, start the services. Dropped the 36 DLL re-registrations (Windows XP era), the service permission rewrite, and WinUtil-style deletion of whole policy keys, which can wipe settings an administrator set. |
| Print spooler recovery suite | **Clear stuck print jobs**. Dropped stale driver removal and DLL re-registration. |
| Audio and peripheral repair | **Restart sound services**, with Bluetooth as an option. |
| Device drivers and PnP repair | **Rescan hardware**. Dropped removing `oem*.inf` driver packages and turning off USB power saving. |
| Shell and UI repair | **Fix Start menu and taskbar**. |
| Explorer and context repair | **Fix wrong or blank icons**. Dropped resetting file associations and libraries, which rewrites defaults. |
| App repair helper | **Reset Microsoft Store**. Dropped licensing service changes and all-users re-registration. The re-register option was also dropped: `WSReset.exe` needs administrator rights on current Windows 11 builds, so this is now a machine fix, and registering apps is a per-user job an administrator helper must not do. |
| OneDrive and cloud repair | **Reset OneDrive**. Dropped folder remapping and Run-key edits. |
| Time and region repair | **Fix the clock**. Dropped language and locale resets. |
| Windows Defender repair and deep scan | **Update Defender and run a quick scan**. |
| Browser reset and cache cleanup | Not carried over. The Cleanup page handles browser caches. |
| Performance and storage repair | Not carried over: turning off SysMain slows hard drives, pagefile policy and event-log trimming have no clear benefit, power plan resets change settings. |
| RAM purge | Not carried over: clearing the standby list makes Windows re-read files from disk, and the old catalog described downloading a third-party tool to do it. |
| Recovery and boot repair | Not carried over: `bootrec /fixmbr` and `/fixboot` only work from the recovery environment, not on a running Windows; also reset the WMI repository and toggled test signing. |
| TPM, BitLocker and Secure Boot repair | Not carried over: requested a TPM clear and suspended BitLocker protection. |
| Activation and licensing repair | Not carried over: `slmgr /rearm` uses a limited count, and rebuilding the licensing store can lose activation. |
| Security and credential repair | Not carried over: reset the firewall, forced UAC on, rebuilt the credential vault. |
| PowerShell environment repair | Not carried over: changed the execution policy and could enable PowerShell remoting and WinRM. |
| Profile and logon repair | Not carried over: edited the Winlogon `Userinit` and `ProfileImagePath` values. |
| Task Scheduler repair | Not carried over: rebuilt the TaskCache registry and rewrote permissions. |
| Graphics and display repair | Not carried over: disabled and re-enabled the display adapter and restarted the desktop compositor. |

Where Microsoft has a built-in tool that is better for the job (Memory Diagnostic, Settings > Troubleshoot, Reset this PC), it is not duplicated.

## Safety rules

- Only catalog steps can run. The guard allows a short list of programs with exact arguments (for example `net` may only stop or start one of nine named services, and `netsh` only four commands), PowerShell only with one fixed command and without anything that downloads, deletes or changes execution policy, and file steps only for named folders. An invalid catalog is refused as a whole. The runner checks the same rules again before starting anything.
- A fix is for the current user or for the machine. A machine fix may not touch a user's profile, and a user fix may not control services, because the administrator helper may belong to a different account.
- A step that stops a service has a matching step that starts it again, and it runs even if an earlier step failed. A test checks that for every fix.
- Folders are renamed to `.bak`, not deleted, where Microsoft's steps allow it.
- Options only switch catalog steps on or off. No text from the page ever reaches a command.
- Fixes with a trade-off (resetting the network stack, rebuilding the search index, stopping stuck apps at shutdown) ask first and say what you give up. Fixes that need a restart say so before and after.
- Queued fixes run one at a time, and the same fix cannot be queued twice. A running fix cannot be cancelled from the Queue, the same as installs, because stopping `sfc` or `dism` half way is worse than waiting.

## Sources

| Source | License | Used for |
| --- | --- | --- |
| [Microsoft Support: System File Checker](https://support.microsoft.com/en-us/topic/use-the-system-file-checker-tool-to-repair-missing-or-corrupted-system-files-79aa86cb-ca52-166a-92a3-966e85d4094e) | Documentation | DISM first, then SFC; what the results mean |
| [Microsoft Learn: reset Windows Update components](https://learn.microsoft.com/en-us/troubleshoot/windows-client/installing-updates-features-roles/additional-resources-for-windows-update) | Documentation | The manual reset steps, and which are only for a second attempt |
| [WinUtil](https://github.com/ChrisTitusTech/winutil) `functions/public` (Fixes) | MIT | Cross-check of network, update and repair commands; its policy deletion was not copied |
| Microsoft Support and Q&A pages named in each catalog entry | Documentation | Print queue, Store reset, OneDrive reset, search index, Start menu, icon cache |

Nothing is copied from these projects. Catalog entries and descriptions are written for this app.

## First real run (30 October 2026, Windows 11 build 26300)

All 16 fixes were run once. 11 finished, 1 finished with a warning and 3 failed (the log keeps only the summary, not the live output).

- **Reset Microsoft Store** failed because `WSReset.exe` has a manifest that demands administrator rights (started without them, Windows answers "The requested operation requires elevation"). It was a user fix, so it never asked. Now a machine fix. Run again by hand since the change; the Fixes page works (4 November 2026).
- **Reset OneDrive** failed with exit code 0xC0000005. Reproduced outside the app: `OneDrive.exe /reset` (version 26.189.928.9, in `C:\Program Files\Microsoft OneDrive`) crashes in `FileSyncClient.dll` however it is started, with or without redirected output. That is a fault in this OneDrive build, not in the command, which is the one Microsoft documents. The step is left as it is and the summary now says the program crashed. Starting `OneDrive.App.exe /reset` instead only started OneDrive and left its settings untouched, so it is not a substitute.
- **Check the drive for errors** failed because `chkdsk.exe` cannot be started on this PC at all. Reproduced with a genuinely elevated window (confirmed High integrity): `chkdsk C: /scan` printed "Access is denied" and exited 5, and PowerShell reported that the program "failed to run". Something on this PC blocks that executable, so it is not our command. `Repair-Volume -DriveLetter C -Scan`, the same read-only online scan, ran fine elevated and returned `NoErrorsFound`, so the fix now uses it and `chkdsk.exe` was removed from the allowed programs. The step exits 0 when no errors are found, 1 (a warning) when it reports any, and 2 (a failure) if the scan cannot run. Scan progress is no longer shown, only the result. The failure summary now keeps the last line a failed step printed.
- **Reset the network stack** warned because `netsh int ip reset` returned 1, which is expected when some keys cannot be reset, and is already treated as a warning.

## Second run: through the real service and helper

Fixes were then run on the development PC through `FixService` and the elevated `--fix` helper (one permission prompt, one connection for every machine fix), with every output line time-stamped.

- **Live output works**: DISM, SFC, PnP, `net` and `w32tm` lines arrived while the steps ran, from the elevated helper.
- **Finished, with the result checked**: Check the drive (Repair-Volume returned `NoErrorsFound` after 89 s), Reset Microsoft Store (now as an administrator fix), Rescan hardware, Fix the clock, Repair Windows system files (DISM 6 min, SFC 52 s, no integrity violations), Clear stuck print jobs, Restart sound services, Fix wrong or blank icons (Explorer closed and started again by the fix, 30 thumbnail and icon cache files removed).
- **Reset OneDrive** still fails with the OneDrive crash, now reported plainly.
- **Bug found and fixed**: DISM ended every update of its progress bar with a full line ending (`\r[====  4.9%  ] \r\n`, captured from a real `dism /ScanHealth`), so the splitter treated each as a normal line and showed 223 lines in one step. Lines that look like a bar are now throttled to about every ten percent. SFC was already handled correctly.
- Harmless messages were shown in red: `net start` says "already started" on stderr and exits 2, which the fix accepts. Steps that accept a non-zero code now show their stderr as a warning. The "approve the permission prompt" notice only shows when a prompt is really coming.
- SFC output arrives in bursts (about every 30 seconds), because SFC buffers its own output when it is not writing to a console. That is SFC's behaviour, not a delay in the app.

## Verification

Covered by tests and by running real, harmless programs: the catalog and its guard, the runner (success, warnings, failure, restart codes, timeouts, steps that always run), the helper protocol (output streaming, cancel, refusals, one connection for several steps), line handling for DISM-style progress, UTF-16 from `sfc`, UTF-8 from PowerShell, code-page output from `ipconfig`, a real program's output appearing in the Queue while it is still running, and the real helper program answering `--fix` and refusing without administrator rights.

Checked by hand in the app on 4 November 2026 (Windows 11 Pro, build 26300): the Fixes page works, the Queue page shows each fix's output correctly, and stopping and starting services behaves as intended. That covers the fixes that had not been run since the second round (Reset Windows Update, Fix Start menu and taskbar, Reset the network stack, Refresh the network connection, Rebuild the search index, Update Defender, Create a restore point), how the Queue page shows the output, and how the Fixes page looks.

Also checked on 4 November 2026: when `explorer.exe` is closed, Windows starts a new one by itself (a new process within 0.2 seconds), so the icon fix's wait-and-start step is only a fallback.

Still from documentation only: `net stop` and `net start` return 2 when the service is already stopped or running, and the fix accepts that as success. (Replaced on 7 November 2026 for the Windows tab, see the last section: 2 also means "turned off" or "broken".)

## Third round: catalog growth (31 October 2026)

An outside review of the page was checked against the code and against this PC (Windows 11 build 26300) before anything was taken from it. The catalog went from 16 to 23 fixes.

### Changed in existing fixes

- **Create a restore point / Repair Windows system files.** `Checkpoint-Computer` only prints a warning when Windows refuses a second restore point within 24 hours, so the old step printed "Restore point created." when none was. The step now compares the list of restore points before and after, says what happened, and exits 1 (a warning) when nothing new exists. The 24-hour limit itself is not bypassed: that means writing `SystemRestorePointCreationFrequency` in the registry, which fixes do not do.
- **Reset Windows Update.** Also stops and starts the Update Orchestrator (`UsoSvc`), which can keep the download folder open, and empties the Delivery Optimization cache (`Delete-DeliveryOptimizationCache`, optional step). `usoclient StartInteractiveScan` was not added: Microsoft no longer supports it, and the effect text already sends people to Settings.
- **Refresh the network connection.** The guard already allowed `ipconfig /registerdns` but no fix used it. It now runs after the new address is obtained (optional, with the "new IP address" option).
- **Fix Start menu and taskbar.** Also registers `Microsoft.UI.Xaml.CBS`, `MicrosoftWindows.Client.Core` and `MicrosoftWindows.Client.CBS` (present on this PC; they host Search and the Windows 11 shell). Only the original three packages can fail the fix, so a package that cannot be registered while in use no longer turns a working fix into a failure. `Microsoft.Windows.Search` is not a package on this build and was left out.
- **Reset OneDrive.** Closes OneDrive first (`/shutdown`, optional), resets it, then, whether or not the reset worked, waits 20 seconds and starts OneDrive if it is not running. Microsoft's page says OneDrive restarts by itself, so the final step only acts when it did not. **Not tested:** OneDrive is not installed on this PC, so whether closing it first avoids the `0xC0000005` crash from the first run is unknown.

### New fixes

| Fix | Group, scope | Notes |
| --- | --- | --- |
| Rebuild the font cache | Windows, machine | Stops `FontCache` and `FontCache3.0.0.0`, deletes `*.dat` in the LocalService FontCache folder and `System32\FNTCACHE.DAT` (present here), starts both again. Careful. |
| Check and repair WMI | Windows, machine | `winmgmt /verifyrepository`, then (option, on by default) `/salvagerepository`. `/resetrepository` stays forbidden. The old test that banned the word "salvagerepository" was narrowed to `resetrepository`: salvage keeps what it can read, reset wipes. Careful. `winmgmt.exe` lives in `System32\wbem`, which the guard now knows. |
| Fix other PCs not showing in Network | Network, machine | Restarts `fdPHost` and `FDResPub`. It does not touch firewall rules or service start types. |
| Restart the camera service | Devices, machine | Restarts `FrameServer`, then rescans hardware. |
| Find devices with problems | Devices, user | Read-only list of devices with an error code. Run on this PC it found none. |
| Fix a blank Windows Security window | Apps, user | Registers `Microsoft.SecHealthUI` again, then `Reset-AppxPackage` (optional). Registering apps is per user, so this is a user fix. |
| Repair WinGet and App Installer | Apps, user | Registers `Microsoft.DesktopAppInstaller` again and runs `winget source update`. `winget source reset --force` needs administrator rights and the helper may be a different account, so the effect text points to it instead. |

### Added later: Restart Bluetooth (1 November 2026)

The catalog is now 24 fixes. **Restart Bluetooth** (Devices, machine) stops and starts `bthserv` and nothing else, for a headset, mouse or keyboard that shows as connected but does nothing. Before this, the only way to restart Bluetooth was the option inside *Restart sound services*, which also restarted audio. `bthserv` was already on the guard's list of services a fix may restart, so the guard did not change. It was added for the Unstick page ([unstick-research.md](unstick-research.md)), which runs it, and four other fixes already here, without the queue. It is listed on this page too. It was checked by the guard and the tests, and run by hand from the Unstick page on 1 November 2026, where it worked.

### Looked at and not added

- **Reset display driver / restart the compositor.** The first round dropped this on purpose (disabling the display adapter can leave a black screen on a one-GPU PC) and nothing has changed.
- **Fix clipboard history (Win+V).** The service that matters, `cbdhsvc_xxxxx`, has a per-user random suffix, and a user fix may not control services. Nothing here could be verified.
- **Refresh group policy.** `gpupdate /force` does not clear "managed by your organization" left by local registry policy, and deleting policy caches is what round one removed.
- **Repair devices by disabling and enabling them.** Restarting a USB controller can cut off the keyboard or mouse in use, so the page lists problem devices and leaves the repair to Device Manager.

### Page changes

- Options now update the page: ticking one rebuilds the **Steps** list so it shows only the steps that will run.
- A new **Commands** row shows the exact program and arguments (or PowerShell script, or folder action) of each of those steps, which is what the README promises ("inspect the commands first").
- The last run's step-by-step summary is kept in `last-runs.json` (`Detail`; older files still load) and shown as **Last run** in the details, so the step that failed is still visible after a restart.
- A fix whose last run needed a restart shows **Restart now** until Windows has restarted. It asks first and runs `shutdown /r /t 30`; the toast can cancel with `shutdown /a`.

## Hardening round (3 November 2026)

A security and robustness audit of the page was checked against the code and fixed where it held up.

- **Elevated PowerShell no longer loads a module from a profile folder.** Windows PowerShell searches `Documents\WindowsPowerShell\Modules` first when a cmdlet it does not know is used, and a UAC-elevated helper of the same account inherits that account's environment, so a module planted there would have run as administrator (`-NoProfile` does not stop module auto-loading). The fix is that every cmdlet in the catalog names its module (`Appx\Get-AppxPackage`, `Storage\Repair-Volume`, `Microsoft.PowerShell.Core\Where-Object`), so command discovery never searches the path, and the guard refuses a step that uses a cmdlet without its module or a module outside the allow-list. The executor also sets `PSModulePath` to the Program Files and `System32\WindowsPowerShell\v1.0` module folders; this is only defence in depth, because Windows PowerShell prepends the user's Documents module folder on startup whatever `PSModulePath` is set to (measured on build 26300) — which is exactly why the module-qualified names, not the path, are the protection. The Defender cmdlets are written as `Defender\…`: Windows 11 reports them from `ConfigDefender`, but the `Defender` module exists on both Windows 10 and 11 and resolves them there too (checked on build 26300). Programs start in `System32`, never in a folder the account can write to.
- **DISM, SFC, the WMI salvage and the update-folder renames are never interrupted.** Those steps are marked `uninterruptible` in the catalog: a stop (the window closing, the queue being stopped, the helper's pipe closing) and the time limit are only said in the output, and the program runs to the end. Before, closing the app mid-`/RestoreHealth` killed DISM and could leave the component store with a half-applied servicing transaction. The `always` steps that start services again now run even after a stop, with a token that cannot be cancelled, so a stopped "Reset Windows Update" still starts BITS, Windows Update, the orchestrator and Cryptographic Services. A stopped fix is reported as "Stopped …" with the step summary.
- **The helper's idle exit no longer fails the next fix.** `FixSession.OpenAsync` greets an existing connection first and starts a new helper (one more prompt) when the old one has left; the greeting has a time limit, and nonsense from a helper is an IO problem, not a crash. One fix runs at a time whatever page asked for it, so Queue and Unstick fixes can no longer interleave their steps.
- **A warning no longer hides a needed restart**: a fix that needs one ends as "finished with warnings; restart required", so the Restart now button still appears.
- **Pending restart.** Repair Windows system files and Reset Windows Update are marked `restartFirst`: when Windows has a restart pending (Component Based Servicing, Windows Update or pending file renames), the page says so before they run and the output and summary say so when they ran anyway, because SFC and DISM then report damage they cannot repair.
- **Updates.** The SHA-256 GitHub lists for a release asset (`digest`) is read and the download is refused when it does not match; a release without one is still accepted and logged as checked by size and type only. Redirects are followed by hand so each hop is held to the GitHub-only rule, the release list is capped at 2 MB, and a download that stops arriving for a minute is given up on instead of showing a frozen bar.
- Not done here, for the shell: the close dialog should say when a fix is on a step that must not be interrupted (`FixService.Running`, `FixService.Busy`, `FixService.WhenIdleAsync`) and wait for its restore steps before closing the helper's pipe.

### Not verified here

Machine fixes (font cache, WMI, camera, discovery, Update Orchestrator, restore point) were validated by the guard and by tests, and then used by hand in the app on 4 November 2026 with the Fixes page working as intended. The new user scripts were run from the catalog text on this PC: *Find devices with problems* (no problems found; the table branch was exercised with a loosened filter) and the `winget source update` step of *Repair WinGet* (exit 0). The Fixes page was reported working on 4 November 2026; the re-register steps (Windows Security, App Installer, shell packages) and the Restart now button were not itemized in that report.

## Windows tab review (7 November 2026)

An outside review of the nine Windows fixes was checked claim by claim, on this PC (Windows 11 build 26300) where it could be. What held up was built; what did not was left out and is listed below with the reason. The catalog is now 26 fixes.

### Built

- **`net start` with `ok: [0, 2]` hid services that never started.** `net.exe` ends with 2 for "already started" and for "turned off" (error 1058) or broken. Four Windows fixes (Reset Windows Update, Fix the clock, Rebuild the search index, Rebuild the font cache) now use two new step kinds, `startService` and `stopService`. The executor runs a fixed PowerShell command for them with the service name taken from the guard's list: already in the wanted state is fine, a service whose startup type is Disabled ends with exit code 3 and says so, a service this PC does not have ends with 4, anything else that does not start is exit 1. Fixes still never change how a service starts. Stopping uses `Stop-Service -Force` (dependents too, as `net stop /y` did) and waits for the service to be stopped. The other pages (printing, sound, Bluetooth, camera, network discovery) still use `net.exe`; the same change fits them and is not made here.
- **`RenameFolder` failed on a `.bak` that held read-only files.** Reproduced: `Directory.Delete(recursive)` throws `UnauthorizedAccessException` for a read-only file. The older copy is now cleared of read-only and system marks first (links are not followed), and the rename itself tries up to three times, two seconds apart, because a service that was just stopped can still be letting go, and Windows may restart one by itself.
- **Reset Windows Update** clears the Delivery Optimization cache first, so the folders are renamed straight after the services stop.
- **Create a restore point** has an option, on by default, that runs `Enable-ComputerRestore` for the system drive (System Protection is off by default on many Windows 11 PCs). When Windows makes no new point it now says which of two things happened: a point from the last 24 hours exists (named, with its time; exit 0, you can go back to it) or System Protection is probably off (exit 1). Repair Windows system files only offers the restore point and never switches System Protection on by itself.
- **Repair Windows system files** ends with a step that reads the end of `CBS.log` and prints the `[SR]` lines of the last SFC scan: files repaired, files it could not repair. The log is opened with `ReadWrite` sharing because the servicing stack writes to it.
- **Check the drive** adds wear, temperature and uncorrected errors (`Get-StorageReliabilityCounter`) and the system volume's health (`Get-Volume`). Both are optional, so a drive that reports nothing does not stop the scan.
- **Fix the clock** ends with `w32tm /query /status`, and has an option (off) to reset the time servers to `time.windows.com` and `pool.ntp.org`, which does nothing on a PC joined to a domain.
- **Rebuild the search index** has an option (off) that sets `HKLM\SOFTWARE\Microsoft\Windows Search\SetupCompletedSuccessfully` to 0 while the service is stopped, which makes Windows put the indexed folders back to its defaults. The guard allows exactly that one value. Windows also forgets folders you *excluded* (this PC has dozens), which the option text says.
- **Update Defender and run a quick scan** first reads `Get-MpComputerStatus`, and stops with a plain sentence when another antivirus has turned Defender off, instead of a raw HRESULT. It ends by saying "No threats were found." or listing the threats and the latest detections (time, whether the action succeeded, files). A full scan is an option.
- **Rebuild the font cache** also deletes `*.log` in the FontCache folder. The WPF cache service is allowed to be missing (exit 4).
- **Check and repair WMI** now verifies again after the salvage and ends by asking WMI for `Win32_OperatingSystem`, because `/verifyrepository` only checks the files on disk.
- **New: Fix blank Task Manager graphs and counters** (`lodctr /R` for 64-bit and, from SysWOW64, 32-bit programs; `winmgmt /resyncperf`; `wmiadap /f`; then it counts the counter sets Windows lists). **New: Find out why the PC will not sleep** (`powercfg /availablesleepstates`, `/requests`, `/lastwake`, `/devicequery wake_armed`; options, off, to rebuild the hibernation file only when hibernation is on, and to restore the default power plans).

### Checked and not built

- **Create `WinSxS\Temp\PendingDeletes` and `PendingRenames`.** Both exist on this PC, the folders are owned by TrustedInstaller, and the review gave no evidence that creating them fixes the SFC error. Not added.
- **`DISM /CheckHealth` first, to skip `/RestoreHealth`.** `/CheckHealth` only reads a flag that an earlier scan set; it can say healthy about a damaged store. The repair stays DISM first, then SFC.
- **Online `Repair-Volume -SpotFix` / `Optimize-Volume -ReTrim`.** `-SpotFix` takes the volume offline, which the system drive cannot do while Windows runs, so it would not be the few-seconds repair the review describes. The effect text still points to `chkdsk C: /f`.
- **`w32tm /resync /force` for large offsets.** `/force` is not a documented switch of `/resync`, and the phase correction limits are not bypassed by it.
- **`UsoClient StartInteractiveScan`.** Undocumented and unreliable on current builds (also dropped in the third round).
- **Refresh group policy (`gpupdate /force`)** and **Windows activation (`slmgr`)**. The first does not clear "managed by your organization" left by registry policy (already decided in the third round). The second asks for `slmgr`, which the guard forbids on purpose, and a read-only `/dlv` repairs nothing; error 0x80070422 is the update service being turned off, which the new service check now reports.

### What running them for real found (7 November 2026, as administrator, through the real runner and executor)

- **A typographic apostrophe in a PowerShell string.** The new drive-wear step said "an SSD’s life"; PowerShell treats ’ as a quote, so the step died with a parser error (exit 1, shown as a warning). Tests that read the catalog could not see it. Now fixed, and two tests guard it: no PowerShell step may contain ‘ ’ “ ”, and every PowerShell step (and both service scripts) is parsed with PowerShell's own parser.
- **Windows Search fails its first start after its database is deleted.** Event 7031/7010: `0x80040d06 The index cannot be initialized`. Windows restarts the service by itself about 30 seconds later and then builds a new index. The old `net start WSearch` returned 2 and the fix accepted it, which is how this stayed unnoticed. The service step now tries to start up to three times and then waits up to 75 seconds for the service to be running, so the fix ends by telling the truth: it did start, or it did not.
- **`Start-Service` writes "Waiting for service … to start" as warnings**, which showed up as output lines; they are switched off.
- **`w32time` stops itself right after a resync** when `time.windows.com` gives no answer (this PC: source `Local CMOS Clock`, not synchronized, before and after), so `w32tm /query /status` failed with 0x80070426. A start step now comes before it. With the option *Also reset the time server list* the PC synchronized with `pool.ntp.org` at once (stratum 4); the original setting (`time.windows.com,0x9`) was put back afterwards.
- **The review's claim that `FNTCACHE.DAT` is always locked was not true here.** It was deleted in both runs (`Removed 1 file`), no warning. The warning handling stays for PCs where it is in use.
- **The review's claim that restore points are throttled to one per 24 hours** did not hold here either: this PC has `SystemRestorePointCreationFrequency = 0`, and two points were created a few seconds apart. The "a recent point already exists" branch was tested by setting the value to 1440 for one run and putting it back.
- System Protection was already on here, so `Enable-ComputerRestore` was a no-op (checked: shadow storage and restore points unchanged apart from the two new points).

### Not run on this PC

Restoring the default power plans (this PC's active plan is a custom Ultimate Performance plan, which the command deletes), the search folder reset (it would forget the dozens of folders excluded from indexing), a full Defender scan (hours), and Reset Windows Update's *update database and certificate store* option (it empties the list of past updates).

## Network tab review (7 November 2026)

An outside review of the three Network fixes was checked claim by claim on this PC (Windows 11 build 26300, Wi-Fi only, Tailscale installed) and every fix was then run for real as administrator. The Network tab now has seven fixes; the catalog has 30.

### Built

- **Refresh the network connection: the adapters are restarted before the lease is renewed.** The review was right: restarting after `ipconfig /renew` threw the new address away. The restart step now comes first, restarts each adapter on its own (one failure does not hide the others), waits up to 30 seconds for the ones that were up to come back, and includes adapters whose status is `Disconnected` (a Wi-Fi adapter that is stuck is not `Up`). Adapters that are switched off are left off. `ipconfig /renew` stays `always`, so a stop never leaves the PC without an address; checked live by asking for a stop as the release step began.
- **Refresh ends by checking that the router answered**, instead of trusting `ipconfig`: it waits up to 25 seconds for every physical adapter that is up to hold a real IPv4 address and prints it with the router. Only physical adapters count. On this PC Tailscale and the unplugged Ethernet port both hold `169.254` addresses all the time, so a check over all adapters would have warned on a healthy PC.
- **Refresh also clears the IPv6 neighbor table** (`netsh interface ipv6 delete neighbors`) and the **NetBIOS name cache** (`nbtstat -R`). Needs administrator rights; `nbtstat` is on the guard's list with exactly one argument.
- **Fix other PCs not showing in Network** restarts all four services in dependency order (stop `upnphost`, `SSDPSRV`, `FDResPub`, `fdPHost`; start in the reverse order), through the native service steps, so a service that is turned off fails or warns instead of looking fine behind `net.exe` exit code 2. The two device services (`SSDPSRV`, `upnphost`) are an option on by default; a turned-off one is a warning, an essential one (`fdPHost`, `FDResPub`) is a failure. Tested live by turning each off.
- **That fix first checks the two things that most often hide other PCs**: the network type (`Public` networks hide the PC and do not look for others) and whether the Windows Firewall has Network discovery on for Private networks (read only; the rule group is found by its language-neutral name). Two options, both off: *Make a Public network Private* (`Set-NetConnectionProfile`, only for networks that are Public) and *Turn the services on if something switched them off* (`Set-Service ... -StartupType Manual`, only for a service that is `Disabled`, only for those four services). A test makes sure no other step in the catalog changes a startup type. The Quiet page turns `SSDPSRV` and `upnphost` off, which is why the second option exists. Tested live with the Wi-Fi network set to Public and with `upnphost` disabled; both were put back.
- **New: Turn off a stuck proxy** (for the current user, no administrator rights). `netsh winhttp reset proxy` only clears the proxy of Windows services; browsers and apps use the one in `HKCU\...\Internet Settings`. The fix lists `ProxyEnable`, `ProxyServer`, `AutoConfigURL` and the `HTTP_PROXY`-type environment variables, sets `ProxyEnable` to 0, removes the proxy address and the setup script address (their old values are written to the output), and then asks Windows which proxy apps would use. This needed a new step kind, `deleteRegistry`, and the registry step now accepts `HKCU` for fixes of the current user. The guard allows exactly `ProxyEnable` = 0 and removing those two values. After the change the executor sends the two WinINET notices Settings sends, so open programs notice. Checked live with a manual proxy and a proxy script (served from a local server) planted: apps went through `127.0.0.1:1081` before the fix and connected directly after it; the original settings were put back.
- **New: Fix Wi-Fi list and "no internet" icon** restarts `WlanSvc` and `NlaSvc` (neither has services that depend on it), waits up to 40 seconds for Windows to report internet, and shows `netsh wlan show interfaces`. Stopping it half way still starts both again (tested).
- **New: Disconnect stuck network folders** (for the current user). Uses `Get-SmbMapping` and `Remove-SmbMapping` without `-UpdateProfile`, so a mapped drive is disconnected but not forgotten. Checked live against a temporary local share: a UNC connection and a persistent mapped drive `Z:` were disconnected; `Z:` stayed in the list as *Unavailable* with its saved entry. Files open on the share are closed by force, which the effect text says.
- **New: Restore normal download speed** shows `netsh int tcp show global` and the heuristics, sets Receive Window Auto-Tuning to `normal`, window scaling heuristics to `disabled` and Receive-Side Scaling to `enabled`, and shows the settings again. The guard allows only those three values; a test refuses the slow ones.
- The proxy option of **Reset the network stack** now says that it is the proxy for Windows services and points to the new fix.

### Checked and not built

- **`netsh int ipv6 reset` next to `netsh int ip reset`.** Run both on this PC: the output is the same line for line (the same IPv6-only object types, such as anycast addresses, prefix policy and site prefix, and the same single "Access is denied" and exit code 1), and both help texts are the same. `int ip reset` already resets both stacks, so the second command would repeat the first. The review's other point was right: that exit code 1 is the protected key, which is why the step already reports 1 as a warning.
- **Restarting `netprofm` (Network List Service) as the review suggested.** On this PC Tailscale and `NcdAutoSetup` depend on it, so stopping it stops the VPN service and nothing would start it again. Only `NlaSvc` is restarted (it decides the "no internet" icon).
- **`nbtstat -RR`.** It sends name-release packets to a WINS server; a home network has none. Not added and refused by the guard.
- **`net use * /delete /y`.** It also removes the saved mapped drives. The new fix disconnects them without forgetting them.
- **A firewall reset (`netsh advfirewall reset`)** and a **TCP "booster" list beyond the three defaults.** A reset wipes the user's and apps' rules. Settings such as RSC and ECN were left alone because turning them off is a deliberate choice some people make.
- **Putting the TCP settings into the network reset.** `netsh int ip reset` does not reset them, but a reset needs a restart and removes address settings; the speed fix needs neither, so it is its own fix.
- **A warning for `169.254` after `ipconfig /renew` via its exit code.** `/renew` exits 0 here even with an adapter whose media is disconnected, so the exit code cannot tell; the check at the end of the refresh does.

### What running them for real found (7 November 2026, as administrator, through the real runner and executor)

- **`nbtstat -R` reports "Failed to Purge the NBT Remote Cache Table" and still exits 0 without administrator rights.** As administrator it prints "Successful purge and preload". The refresh is a whole-PC fix, so it always runs elevated; the output is where to look.
- **`netsh interface ipv6 delete neighbors` also needs administrator rights** ("The requested operation requires elevation").
- **A step with several lines of PowerShell.** The new scripts are written over several lines in the catalog; a first version joined the lines with spaces and would not parse. The test that parses every PowerShell step with PowerShell's own parser exists for this.
- After the adapters restart, Wi-Fi came back in a few seconds, and the new IP address then took 43 seconds to arrive on this router.
- A stopped fix is reported as failed with "Stopped ...", as for the other tabs; the services and the address were back afterwards in every stop test.
- **The registry alone is enough for the proxy.** With only `ProxyEnable`, `ProxyServer` and `AutoConfigURL` written, .NET's system proxy followed them at once. A proxy script that cannot be reached is ignored by the system proxy, so the test used a reachable local one.

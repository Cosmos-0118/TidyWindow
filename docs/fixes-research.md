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

- **Reset Microsoft Store** failed because `WSReset.exe` has a manifest that demands administrator rights (started without them, Windows answers "The requested operation requires elevation"). It was a user fix, so it never asked. Now a machine fix. Not run again since the change.
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

## Not verified

Covered by tests and by running real, harmless programs: the catalog and its guard, the runner (success, warnings, failure, restart codes, timeouts, steps that always run), the helper protocol (output streaming, cancel, refusals, one connection for several steps), line handling for DISM-style progress, UTF-16 from `sfc`, UTF-8 from PowerShell, code-page output from `ipconfig`, a real program's output appearing in the Queue while it is still running, and the real helper program answering `--fix` and refusing without administrator rights.

Not done yet, and needing a person at the PC:

- Not run for real since the changes: Reset Windows Update, Fix Start menu and taskbar, Reset the network stack, Refresh the network connection, Rebuild the search index, Update Defender, Create a restore point (these ran once from the app and finished, before the second round of fixes).
- How the Queue page itself shows all this. The service was driven by a console program, not the Queue window.
- Exit codes: `net stop` and `net start` return 2 when the service is already stopped or running. This comes from documentation and is accepted as success.
- Whether Windows restarts File Explorer by itself after it is closed for the icon fix. The fix waits a few seconds and starts it if not.
- How the Fixes page looks. It was built and loaded, but not seen on screen.

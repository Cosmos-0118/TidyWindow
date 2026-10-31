# Quiet Windows: background services, tasks and settings

Research and implementation decision, 30 October 2026. The page lists known Windows background items (telemetry, features that watch the screen, optional services) and lets the person turn each one off and back on.

## Decision

Ship a small, reviewed catalog instead of scanning for "anything suspicious". Every item names exactly the services, scheduled tasks and Windows policy values it changes, and nothing outside the catalog can be changed, whatever a caller sends. The previous app stopped services that other programs depend on (WhatsApp stopped starting), so safety rules come before the list of items.

## Safety rules

- Services that other things lean on can never be in the catalog: notifications (`WpnService`), Store and app plumbing (`AppXSvc`, `StateRepository`, `ClipSVC`, `InstallService`), Windows Update and `BITS`, Search, Delivery Optimization, printing, audio, networking, security and Edge/Office/OneDrive updaters. The list is checked when the catalog loads; an invalid catalog is refused as a whole.
- Per-user services, services not installed by Windows, and wildcard names are refused.
- A service that another running service depends on shows a lock, not a switch. User-service helpers for the same feature (for example `BluetoothUserService`) do not count as other programs.
- Services that normally start automatically are turned to Manual, not Disabled, so Windows can still start them for something that asks. Only `DiagTrack` and `SysMain` are disabled outright.
- Scheduled tasks are matched by full path; Defender, Windows Update, servicing, maintenance and similar folders are refused. The one exception is `\Microsoft\Windows\Maintenance\WinSAT` (the idle-time hardware benchmark), named in full; the rest of Maintenance stays off limits.
- Policy writes are limited to named values in named keys, checked when the catalog loads and again when the value is written. Several keys also hold settings this page must not touch (`Windows Search\DisableSearch`, `WindowsAI\AllowRecallEnablement`), so a key alone is not enough. Allowed: `WindowsAI` (`DisableAIDataAnalysis`, `DisableClickToDo`), `Dsh`, `GameDVR`, `DeliveryOptimization\DODownloadMode`, `Windows Search\EnableDynamicContentInWSB`, `Power\PowerSettings\f15576e8-…\ACSettingIndex/DCSettingIndex`, `FindMyDevice\AllowFindMyDevice`, `WindowsNotepad\DisableAIFeatures`, and the three Paint values.
- "Turn off safe items" only ever switches off items marked Safe in the open tab, after showing the list. Careful items are never part of a batch.
- The original state of each target is saved before a change, so turning back on restores it exactly. Changes are verified by reading them back, and a partial failure is rolled back.
- Changes that need administrator rights go through the same elevated helper and Windows permission prompt as the other pages. Nothing runs in the background and nothing is re-applied automatically.

## Sources

| Source | License | Used for |
| --- | --- | --- |
| [Microsoft: security guidelines for system services](https://learn.microsoft.com/en-us/windows-server/security/windows-services/security-guidelines-for-disabling-system-services-in-windows-server) | Documentation | Which services Microsoft marks "OK to disable". Written for Windows Server, so each entry was re-checked for a client PC. |
| [Microsoft: WindowsAI policy CSP](https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-windowsai) | Documentation | Recall snapshot and Click to Do policies |
| [WinUtil](https://github.com/ChrisTitusTech/winutil) | MIT | Telemetry, location and Game DVR tweaks (cross-check) |
| [Win11Debloat](https://github.com/Raphire/Win11Debloat) | MIT | Widgets and Copilot-era settings (cross-check) |
| [Sophia Script](https://github.com/farag2/Sophia-Script-for-Windows) | MIT | `DiagTrack` and error reporting handling (cross-check) |
| [Win10-Initial-Setup-Script](https://github.com/Disassembler0/Win10-Initial-Setup-Script) | MIT | Service names for telemetry, `SysMain` |
| [WindowsSpyBlocker](https://github.com/crazy-max/WindowsSpyBlocker) | MIT | Which components talk to Microsoft |
| [privacy.sexy](https://github.com/undergroundwires/privacy.sexy) | AGPL-3.0 | Read as a reference only (for example that `devicecensus.exe` sends device data); no text or code copied |

Nothing was copied from AGPL sources. The catalog entries and descriptions are written for this app.

## What is in the catalog

- **Telemetry:** Windows telemetry, error reporting, app compatibility telemetry, Customer Experience Improvement Program, disk reporting, feedback requests, app list backup, Office telemetry, device census, Program Compatibility Assistant, recommended troubleshooting.
- **Windows AI:** Recall snapshots, Click to Do, Windows AI components, Notepad AI, Paint AI.
- **Optional:** Xbox, Phone Link, Location, offline maps, Wallet/NFC, retail demo, hotspot and connection sharing, media sharing, Superfetch, startup reminders, Widgets, Bluetooth, network device discovery, remote management (WinRM), Remote Registry, scanners and camera import, sensors, smart cards, Offline Files and Work Folders, Family Safety, Windows Insider, game recording, sharing updates with other PCs (Delivery Optimization peer-to-peer, not the service), network while the PC sleeps, search highlights, hardware benchmark (WinSAT), speech model downloads, remote install from the Store, Find My Device, fax and printer notifications, GameInput.

Policy keys, value names and editions for the newer items were checked against Microsoft's Policy CSP pages (WindowsAI, Search, DeliveryOptimization, ADMX_Power, Experience) and "Manage AI features in Notepad". The Delivery Optimization, Search highlights, Find My Device, standby network and Paint policies are documented for Pro and above only, so they are locked on Windows Home like the Recall policy. The Notepad page does not state editions, so it is not locked.

Items that do not exist on a PC are not shown; the footer counts them.

## Deliberately left out

| Item | Why |
| --- | --- |
| `dmwappushservice` (WAP push) | Microsoft says it is required for Intune and MDM management. |
| Remote Desktop (`TermService`) | Could not confirm what depends on it locally (fast user switching is reported to), so it is not offered. |
| `WbioSrvc`, `NgcSvc` | Needed for Windows Hello. |
| `FrameServer` | Breaks webcams in many apps. |
| `RmSvc` | Controls airplane mode. |
| `DPS`, `diagsvc`, Diagnostics Hub | Used by troubleshooters and Visual Studio profiling. |
| `LanmanServer` | Windows file sharing. |
| `TrkWks`, `lmhosts`, `Dnscache` and similar | No clear benefit, and other things rely on them. |
| Flighting `UsageData*` tasks, Mixed Reality services | Could not find a reliable source for exactly what they do or their names, so they are not listed. |
| Copilot legacy policy, `AllowRecallEnablement` | Removes Windows components instead of turning a feature off. |
| Third-party background apps | Handled on the Startup page. |
| Settings AI agent (`DisableSettingsAgent`) | Microsoft documents it for Enterprise and Education on Insider builds only, so on Home and Pro it would show as off while doing nothing. Add it when it reaches Pro. |
| Disabling `DoSvc`, `Spooler`, `WSearch`, `WpnService`, `DusmSvc`, `NcbService` | Other things rely on them (Store and WinGet downloads, printing, file search, notifications, metered-network awareness, Store/WinRT connections). Delivery Optimization sharing is handled by policy instead. |
| Disabling Modern Standby (`PlatformAoAcOverride`) | Breaks sleep on modern firmware. "Network while the PC sleeps" uses Microsoft's connectivity-in-standby policy instead. |
| `CrossDeviceResume.exe` | Started by `sihost.exe` whatever Phone Link is set to, so no item here stops it. Blocking it with Image File Execution Options makes Windows respawn it in a loop. Resume can be turned off in Settings > Apps > Resume. |

## Not verified

Reading and the safety rules are covered by tests, with a read-only scan of a real Windows 11 PC. Writing to services, scheduled tasks and policy values needs administrator rights and is checked by hand in the app. The Game recording policy is documented for Windows 10 and 11 (it is also in the DISA STIGs); whether Windows Home honors it was not confirmed.

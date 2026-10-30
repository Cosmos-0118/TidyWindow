# Tune: registry settings that make a real difference

Research and implementation decision, 30 October 2026. Status: implemented (`core/TuneModel.cs`, `core/TuneManager.cs`, `core/WindowsTuneSystem.cs`, `core/tune-catalog.json`, `app/TuneView.xaml`). The old Registry Optimizer offered about 50 scripts, and most were folklore. This page offers a short, reviewed list where each setting has a reason you can point to, shows what is set on this PC right now, and can put every value back exactly.

## Decision

Same shape as Quiet: a small catalog of data (`tune-catalog.json`), a guard that refuses anything outside it, a scan that reads the PC, a changer that writes and reads back, and a history that remembers the original values. Every item says in one line what you will notice. Anything that only has forum posts behind it is left out (see below).

A setting that is already applied on this PC shows as applied and offers nothing. A read-only scan of the development PC (Windows build 26300) found the shutdown settings, advertising ID, mouse acceleration, file extensions and long paths already set, and the suggestion and Sticky Keys items partly set, so a good page often has little to do. That is correct, not a gap.

## What counts as "does something"

An item is only listed if it passes one of these:

1. **You notice it within a minute.** An extra click removed, a delay shortened, an ad or prompt gone.
2. **It fixes a limit.** Long paths removes the 260-character limit.
3. **It saves resources that can be measured in Task Manager.** Edge background processes.

Each item also carries a plain label: **Speed**, **Cleaner** or **Comfort**, plus when it takes effect (now, sign out, restart Explorer, restart).

## Catalog (15 items)

| Item | Scope | What changes | You notice | Source |
| --- | --- | --- | --- | --- |
| Big folders open faster | User | `Shell\Bags\AllFolders\Shell\FolderType = NotSpecified` (one value; the Bags tree is not deleted) | Explorer stops scanning a folder's files to guess its view. Trade-off: no automatic picture/video view | WinUtil, Windows Latest, Neowin |
| Stuck apps do not hold up shutdown | User | `AutoEndTasks = 1`, `WaitToKillAppTimeout` at most 5000 | Shutdown and sign-out stop waiting 20 s on a stuck app. Careful: an app with unsaved work is closed. `HungAppTimeout` and `WaitToKillServiceTimeout` are not offered: Windows 10 and 11 already use 5000 ms for both when nothing is set (guides and an ITmedia history; the 20000 in old Microsoft docs is out of date), so writing 5000 changes nothing. The old Registry Optimizer set the service timeout to 7000, which is slower than the default | Microsoft TechNet (AutoEndTasks), Tom's Hardware |
| Start search without web results | User, Windows 11 26H2 build 26300.8697 and later | `SearchSettings\IsWebSuggestionsEnabled = 0` | Start and taskbar search show only your PC, no Bing results. This is the real "Web Searches" switch in Settings > Privacy & security > Search, so it has no "managed" notice | Windows Settings (tested), KapilArya |
| Menus and windows respond sooner | User | `MenuShowDelay` at most 200 (WinUtil's value; Windows uses 400), `MinAnimate`, `TaskbarAnimations` | Menus open without the 400 ms pause; fewer animations | WinUtil, Win11Debloat; Microsoft says default effects cost performance |
| Edge stays closed when closed | Machine | `Edge\StartupBoostEnabled = 0`, `BackgroundModeEnabled = 0` | Fewer `msedge.exe` processes after closing Edge. Careful: Edge shows "managed by your organization" | Microsoft Learn (both policies) |
| No suggestions or promoted apps | User | 14 `ContentDeliveryManager` values | No Start suggestions, lock screen tips, silently installed apps | WinUtil, Win11Debloat |
| No promoted apps on new accounts | Machine, Pro and up | `CloudContent\DisableWindowsConsumerFeatures = 1` | Windows stops installing promoted apps for every user. Locked on Home | WinUtil, Microsoft Learn |
| Advertising ID and tailored tips off | User | `AdvertisingInfo\Enabled`, `TailoredExperiencesWithDiagnosticDataEnabled` | No personalized ads in apps | WinUtil, Win11Debloat |
| Activity history off | Machine | `System\PublishUserActivities`, `UploadUserActivities`, `EnableActivityFeed` | Timeline data stops being collected and sent | WinUtil |
| Classic right-click menu | User | Empty `InprocServer32` under CLSID `{86ca1aa0-...}` | "Show more options" click disappears. Restart Explorer to apply | WinUtil, Win11Debloat |
| Mouse acceleration off | User | `MouseSpeed`, `MouseThreshold1`, `MouseThreshold2 = 0` | Pointer distance matches hand distance | WinUtil, Win11Debloat |
| Sticky Keys shortcut off | User | Clears only the "shortcut is active" bit (4) in `Flags` for Sticky, Toggle and Filter Keys; other bits are kept | Five Shifts no longer opens a dialog | WinUtil, Win11Debloat |
| Show file extensions | User | `Advanced\HideFileExt = 0` | `invoice.pdf.exe` cannot hide its real type | WinUtil, Win11Debloat |
| End task from the taskbar | User | `TaskbarDeveloperSettings\TaskbarEndTask = 1` | Right-click an app on the taskbar, End task | WinUtil, Win11Debloat |
| Long file paths | Machine | `FileSystem\LongPathsEnabled = 1` | Paths over 260 characters stop failing in apps that opt in | Microsoft Learn |

## Deliberately left out

These appear in almost every "speed up Windows" list. The evidence for them is thin, so they are not offered.

| Setting | Why not |
| --- | --- |
| `SystemResponsiveness = 0` | Microsoft documents that values below 10 are clamped to 20, so "0" does nothing. 100 turns the multimedia scheduler off. |
| `NetworkThrottlingIndex = ffffffff` | Only forum anecdotes and vendor blogs; no benchmark found. |
| `Win32PrioritySeparation`, core parking, HPET, `DisablePagingExecutive`, `LargeSystemCache`, `IRPStackSize` | The best-researched tuning guide (valleyofdoom/PC-Tuning) says to benchmark each on your own system and that results vary; none is a safe default. |
| Disable SysMain/Prefetch | Slows apps on hard drives. |
| Nagle / `TcpAckFrequency` | Matters only for rare latency-sensitive protocols. |
| Disable IPv6, Teredo | Breaks things for no speed gain. |
| Hardware GPU scheduling, Multiplane Overlay | Hardware and driver dependent. Windows Settings already has the HAGS switch. |
| Registry cleaners, "clean the registry" | No measurable effect, real risk. |
| Disable Defender, SmartScreen, UAC | Security loss. |
| Telemetry services and policies, Recall, Game DVR | Already on the Quiet page. |
| Windows Update restart rules | The registry rule only applies to scheduled installs (`AUOptions = 4`), which a normal PC does not use. Not reliable on 11. |
| Explorer startup delay (`StartupDelayInMSec`) | Sources date from Windows 10; not confirmed on 11. |
| Start search without web results on older Windows 11 (`Policies\...\Explorer\DisableSearchBoxSuggestions`) | Microsoft documents it only for File Explorer's search box. Win11Debloat and forum posts say it also hides web results in Start on 24H2/25H2, but one report says it was blocked, and it could not be checked on a machine without the new setting. Not offered; the item above covers 26H2. |

## Safety rules

- Only catalog values can be written. The guard allows named registry keys and value names, in the right hive, with the right type. Anything else is refused whatever a caller sends. An invalid catalog is refused as a whole.
- Each item is wholly **user** (current user's registry) or wholly **machine** (needs administrator). An elevated helper may belong to a different account, so it never writes to the user's registry. A mixed item is split in two.
- The original value of every target, including "was not set", is saved before a change. Turning an item off restores exactly that, so a setting that did not exist is deleted again rather than set to a guessed default.
- Changes are read back. A partial failure is rolled back. Running a change twice does the same as running it once.
- Nothing is re-applied in the background. Nothing outside the catalog is touched. There is no "apply all" button; each item is a switch.
- Edition and build are checked: the consumer-features policy is Pro/Enterprise only, the classic menu is Windows 11 only.
- If something else (a Windows update, Group Policy) changes a value, the page says "changed by something else" instead of claiming it is still applied.

## Page design

Follows Quiet. A list of switches with a one-line effect each, the small print behind a chevron (values before and after, "applies after you restart Explorer"), tabs **Speed · Cleaner · Comfort · Changes**, a search box and an On/Off filter. The Changes tab lists what TidyWindow changed, with **Put everything back**. Items that need Explorer restarted offer a **Restart Explorer** button after the change. Careful items show a one-time confirmation that states the trade-off. Toasts and the Logs page report outcomes like the other pages.

## Implementation outline

- `core/TuneModel.cs`: item/target types, `TuneCatalog`, `TuneGuard` (validation), `TuneScan`.
- `core/TuneManager.cs`: local changer, history (`%LOCALAPPDATA%\TidyWindow.Next\Tune\changed.json`), manager, elevated host command `--tune` in the existing `CleanHost`.
- `core/WindowsTuneSystem.cs`: registry reader and writer, and an Explorer restart helper.
- `core/tune-catalog.json`: the catalog above.
- `app/TuneView.xaml(.cs)`, `app/TuneViewModel.cs`, a sidebar entry and page in `MainWindow`.
- `tests/TidyWindow.Tests/TuneTests.cs`: catalog validation, scan states, exact restore (including "not set"), rollback, edition gating, refusal of out-of-catalog writes.
- README entry and this document updated to "implemented" with what was verified by hand.

## Not verified

Reading, the guard, the rules for applying and putting back, rollback, and the helper protocol are covered by tests, plus a read-only scan of a real Windows PC. Writing to the real registry, the administrator helper with a real permission prompt, and how the page looks were not checked by hand yet.

- Whether each setting does what the sources say on the newest Windows builds. Several values (`ContentDeliveryManager`, `TaskbarEndTask`) are community-documented, not Microsoft-documented.
- Whether the Edge policies make Edge show a "managed by your organization" notice. The item says so up front; Edge's behavior with these policies was not confirmed here.
- `WaitToKillAppTimeout` is written as "at most 5000". Its Windows default of 20000 comes from guides, not from a fresh install (the system default profile has no value, and loading the default user hive needs administrator rights). The page reads the real value and only offers a change when the current one is higher.
- Web search: checked by hand on build 26300.9818. Setting `IsWebSuggestionsEnabled = 0` made Settings show Web Searches Off, Microsoft Store suggestions stayed On, and removing the value put it back to On. What the Start menu itself then shows was not looked at, because screenshots of the Start menu are masked. Microsoft's own wording for this setting is not documented yet; the value comes from a preview-build article and the Settings test.
- Whether `MenuShowDelay` and the Sticky Keys flags apply without signing in again. The page says "After sign-in" to be safe.

## Sources

| Source | License | Used for |
| --- | --- | --- |
| [WinUtil](https://github.com/ChrisTitusTech/winutil) `config/tweaks.json` | MIT | Registry paths, values and original values for most items (cross-check) |
| [Win11Debloat](https://github.com/Raphire/Win11Debloat) | MIT | Which options people actually use; context-menu, mouse, sticky keys, taskbar (cross-check) |
| [valleyofdoom/PC-Tuning](https://github.com/valleyofdoom/PC-Tuning) | MIT | "Benchmark, do not assume" stance; why the folklore list is excluded |
| [Microsoft: Multimedia Class Scheduler Service](https://learn.microsoft.com/en-us/windows/win32/procthread/multimedia-class-scheduler-service) | Documentation | `SystemResponsiveness` clamping (reason for exclusion) |
| [Microsoft Edge: StartupBoostEnabled](https://learn.microsoft.com/DeployEdge/microsoft-edge-browser-policies/startupboostenabled), [BackgroundModeEnabled](https://learn.microsoft.com/DeployEdge/microsoft-edge-browser-policies/backgroundmodeenabled) | Documentation | Edge policies and their registry location |
| [Windows Latest on folder discovery](https://windowslatest.com/2024/03/04/windows-11-can-open-big-folders-in-file-explorer-faster-if-you-turn-off-folder-discovery) | Article | Folder discovery cause and the single-value fix |
| [Microsoft TechNet: AutoEndTasks](https://technet.microsoft.com/en-us/library/cc978624.aspx) | Documentation | AutoEndTasks meaning |

Nothing is copied from these projects. Catalog entries and descriptions are written for this app.

## Added later: memory integrity

"Memory integrity off" (`HypervisorEnforcedCodeIntegrity\Enabled`, machine, needs a restart, marked Careful) was added with the Lab. It is the only setting in the old Performance Lab with a measured effect on speed (about 4 to 8% in games, depending on CPU and game), and it is a security feature, so it asks first and says what is given up. Research and the evidence are in [lab-research.md](lab-research.md). The Lab shows its state and links here to change it.

# Tune: registry settings that make a real difference

Research and implementation decision, 30 October 2026. Status: implemented (`src/TidyWindow.Core/Tune/TuneModel.cs`, `src/TidyWindow.Core/Tune/TuneManager.cs`, `src/TidyWindow.Core/Tune/WindowsTuneSystem.cs`, `src/TidyWindow.Core/Tune/tune-catalog.json`, `src/TidyWindow.Desktop/Features/Tune/TuneView.xaml`). The old Registry Optimizer offered about 50 scripts, and most were folklore. This page offers a short, reviewed list where each setting has a reason you can point to, shows what is set on this PC right now, and can put every value back exactly.

## Decision

Same shape as Quiet: a small catalog of data (`tune-catalog.json`), a guard that refuses anything outside it, a scan that reads the PC, a changer that writes and reads back, and a history that remembers the original values. Every item says in one line what you will notice. Anything that only has forum posts behind it is left out (see below).

A setting that is already applied on this PC shows as applied and offers nothing. A read-only scan of the development PC (Windows build 26300) found the shutdown settings, advertising ID, mouse acceleration, file extensions and long paths already set, and the suggestion and Sticky Keys items partly set, so a good page often has little to do. That is correct, not a gap.

## What counts as "does something"

An item is only listed if it passes one of these:

1. **You notice it within a minute.** An extra click removed, a delay shortened, an ad or prompt gone.
2. **It fixes a limit.** Long paths removes the 260-character limit.
3. **It saves resources that can be measured in Task Manager.** Edge background processes.

Each item also carries a plain label: **Speed**, **Cleaner** or **Comfort**, plus when it takes effect (now, sign out, restart Explorer, restart).

## Catalog (38 items)

The nine rows after "Long file paths" were added in the second round and the thirteen after "Shut down really shuts down" in the third (31 October 2026, from a deeper pass through Sophia Script, Win11Debloat and Winhance, with each value re-checked before it went in). The memory integrity item, added with the Lab, is described at the end.

| Item | Scope | What changes | You notice | Source |
| --- | --- | --- | --- | --- |
| Big folders open faster | User | `Shell\Bags\AllFolders\Shell\FolderType = NotSpecified` (one value; the Bags tree is not deleted) | Explorer stops scanning a folder's files to guess its view. Trade-off: no automatic picture/video view | WinUtil, Windows Latest, Neowin |
| Stuck apps do not hold up shutdown | User | `AutoEndTasks = 1`, `WaitToKillAppTimeout` at most 5000 | Shutdown and sign-out stop waiting 20 s on a stuck app. Careful: an app with unsaved work is closed. `HungAppTimeout` and `WaitToKillServiceTimeout` are not offered: Windows 10 and 11 already use 5000 ms for both when nothing is set (guides and an ITmedia history; the 20000 in old Microsoft docs is out of date), so writing 5000 changes nothing. The old Registry Optimizer set the service timeout to 7000, which is slower than the default | Microsoft TechNet (AutoEndTasks), Tom's Hardware |
| Start search without web results | User, Windows 11 26H2 build 26300.8697 and later, 24H2 from 26100.9278 and 25H2 from 26200.9278 (`alsoOn` in the catalog) | `SearchSettings\IsWebSuggestionsEnabled = 0` | Start and taskbar search show only your PC, no Bing results. This is the real "Web Searches" switch in Settings > Privacy & security > Search, so it has no "managed" notice | Windows Settings (tested), KapilArya |
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
| Taskbar click opens the last window | User | `Explorer\Advanced\LastActiveClick = 1` | One click on a multi-window app goes to its last window instead of showing previews. Undocumented by Microsoft; community guides report it works on Windows 11 | Windows Eleven Forum, NinjaOne |
| File Explorer opens to This PC | User | `Explorer\Advanced\LaunchTo = 1` | Win+E shows drives instead of Home. Put back by removing the value | Win11Debloat, Windows Eleven Forum |
| File copies show the full details | User | `Explorer\OperationStatusManager\EnthusiastMode = 1` | The copy window opens with the speed graph. It is the value Explorer saves for "More details" | Explorer itself (already set on the development PC) |
| No Game Bar pop-ups | User | `GameBar\UseNexusForGameBarEnabled`, `GameDVR\AppCaptureEnabled`, `GameConfigStore\GameDVR_Enabled` = 0 | The controller button and game launch stop opening Game Bar, or the "get an app for ms-gamebar" prompt. Separate from Quiet's machine-wide `AllowGameDVR` policy; no URL protocol handlers are touched | Microsoft Q&A, Technipages |
| Full-quality wallpaper | User | `Control Panel\Desktop\JPEGImportQuality = 100` | A JPEG wallpaper set afterwards is not compressed again. Applies to pictures set from then on | Winaero |
| No "finish setting up" pages or Explorer banners | User | `Explorer\Advanced\ShowSyncProviderNotifications = 0`, `UserProfileEngagement\ScoobeSystemSettingEnabled = 0` | The full-screen setup page after updates and the OneDrive/Microsoft 365 banners in Explorer are gone | Windows Eleven Forum, Winaero |
| Start keeps no recent files or app lists | User, Windows 11 | `Explorer\Advanced\Start_TrackDocs`, `Start_IrisRecommendations`, `Start\ShowRecentList`, `ShowFrequentList` = 0 | Start's Recommended section empties. These are the Settings > Personalization > Start switches, so they work on Home and Pro. Trade-off: recent files also leave Jump Lists and Explorer's Recent | Windows Eleven Forum, Windows Settings |
| No maker apps for new devices | Machine | `Device Metadata\PreventDeviceMetadataFromNetwork = 1` | Windows stops fetching the maker's companion app and icons for new mice, keyboards, headsets and monitors. The driver still installs | Windows Device installation settings, Microsoft Learn |
| Shut down really shuts down | Machine, Careful | `Session Manager\Power\HiberbootEnabled = 0` | Shut down is a full shutdown (uptime resets). Trade-off: a slightly slower start. Listed under Comfort, not Speed, because it does not make anything faster | Windows Settings (Fast startup) |
| Startup apps start at once | User, Speed | `Explorer\Serialize\StartupDelayInMSec = 0`, `WaitForIdleState = 0` | Startup-folder and Run apps start with the desktop instead of after the delay and the wait for an idle PC. Community-documented only (Microsoft Q&A, Eleven Forum); the key is missing on some builds and results differ. The `Serialize` key is never removed, only the two values | Microsoft Q&A, Windows Eleven Forum |
| Search without highlights or cloud results | User, Windows 11 22H2+ | `SearchSettings\IsDynamicSearchBoxEnabled`, `IsMSACloudSearchEnabled`, `IsAADCloudSearchEnabled = 0` | No search highlights picture; Start search stops reading Microsoft-account and work-account cloud content. Switches in Settings > Search permissions | Windows Settings |
| No accidental work or school enrollment | Machine, Careful | `Policies\Microsoft\Windows\WorkplaceJoin\BlockAADWorkplaceJoin = 1` | The "Allow my organization to manage my device" prompt can no longer enroll a personal PC. Trade-off: no single sign-on for those accounts; leave off on managed PCs | Microsoft Q&A, MSEndpointMgr |
| Edge without first-run, shopping and feed pop-ups | Machine, Careful | Seven `Policies\Microsoft\Edge` values: `HideFirstRunExperience`, `NewTabPageContentEnabled`, `EdgeShoppingAssistantEnabled`, `ShowRecommendationsEnabled`, `SpotlightExperiencesAndRecommendationsEnabled`, `DefaultBrowserSettingEnabled`, `DefaultBrowserSettingsCampaignEnabled` | Fewer pop-ups; Edge says "managed by your organization", as for the other Edge item | Microsoft Learn (Edge policy reference) |
| OneDrive stops asking to back up your folders | Machine | `Policies\Microsoft\OneDrive\KFMBlockOptIn = 1` | The "back up your folders" prompt and button go away. Folders already moved stay | Microsoft Learn |
| Windows keeps no typing profile | User | `Input\TIPC\Enabled`, `InputPersonalization\RestrictImplicitTextCollection`, `RestrictImplicitInkCollection`, `TrainedDataStore\HarvestContacts` | Privacy only; nothing visible changes. Listed under Cleaner next to the advertising ID, which is the same kind of item | Windows Settings, Sophia Script |
| Websites cannot read your language list | User | `Control Panel\International\User Profile\HttpAcceptLanguageOptOut = 1` | Privacy only | Windows Settings, Sophia Script |
| No Gallery in File Explorer | User, 22H2+ | `Classes\CLSID\{e88865ea-...}\System.IsPinnedToNameSpaceTree = 0` | Gallery leaves the sidebar | Win11Debloat |
| Closer rows in File Explorer | User, Windows 11 | `Explorer\Advanced\UseCompactMode = 1` | About a third more rows on screen | Explorer's own Compact view switch |
| Alt+Tab shows windows, not browser tabs | User, 22H2+ | `Explorer\Advanced\MultiTaskingAltTabFilter = 3` | Alt+Tab lists windows only | Windows Settings (Multitasking) |
| Shaking a window does nothing | User | `Explorer\Advanced\DisallowShaking = 1` | A window shake no longer minimizes the others | Windows Settings (Multitasking) |
| F1 does not open a web search | User | Empty `Typelib\{8cec5860-...}\1.0\0\win64` default; the key is removed again when put back | Accidental F1 stops opening Edge. Apps with their own help still do | Sophia Script |
| Detailed messages at start-up and shutdown | Machine | `Policies\System\VerboseStatus = 1` | "Installing update 2 of 3" instead of "Please wait" | Microsoft Learn |

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
| Drag tray off (`CDP\DragTrayEnabled`) | Microsoft has been pulling and renaming the drag tray ("Drop Tray") in recent builds, and the development PC (26300) does not have the value at all. A switch that may do nothing is what this page avoids. |
| "- Shortcut" off (`NamingTemplates\ShortcutNameTemplate`) | The value needs literal quotes (`"%s.lnk"`), reports on current builds disagree (one says nothing changed), and it could not be checked by hand. |
| Crash screen details (`CrashControl\DisplayParameters`) | You would only see it at the next crash, so it fails "notice it within a minute", and the simplified Windows 11 crash screen was not confirmed to honor it. |
| Do not reopen apps (`Winlogon\RestartApps = 0`) | "Automatically save my restartable apps" is already off by default on Windows 11, so writing 0 changes nothing for most people. The after-update sign-in value (`ARSOUserConsent`) is a different, machine-wide setting about passwords. |
| Restart-apps off, auto-reboot rules, BitLocker auto-encryption (`PreventDeviceEncryption`), "Store" in Open with (`NoUseStoreOpenWith`), indexed-only File Explorer search (`WholeFileSystem`) | Each was re-checked in the third round. `RestartApps` is off by default on Windows 11. `NoAutoRebootWithLoggedOnUsers` and `IsExpedited` still do not stop restarts the way the guides say (`IsExpedited` is the "Get me up to date" switch, off by default). Turning off device encryption lowers protection and Settings already has the switch. `NoUseStoreOpenWith` is reported as ignored on some builds. `WholeFileSystem` has no source showing it is slow on, and its Windows default was not found. |
| Older Windows 11 web search (`DisableSearchBoxSuggestions`, `BingSearchEnabled`) | Sources still disagree on whether either works on 24H2 and 25H2. Those builds now have the real Settings switch from the August 2026 update, so the main item covers them. Earlier builds get no web-search item. |
| Settings ads (`CloudContent\DisableConsumerAccountStateContent`, `SettingsPageVisibility = hide:home`) | The policy exists, but nothing found shows it removes the Settings home cards. `hide:home` would overwrite a person's own page-hiding list. |
| Extra nag values (`Start_AccountNotifications`, `EnableAccountNotifications`, `Windows.SystemToast.BackupReminder\Enabled`, `SubscribedContent-353698Enabled`) | No source found that confirms them. The one Microsoft Q&A thread that tried the Backup reminder key reported it did not work. The two nag values that are confirmed are in "No finish setting up pages" and the Start tips value is in "Start keeps no recent files or app lists". |
| `HideRecommendedSection` policy | Honored only on Windows 11 Education and Enterprise. The per-user Start switches above work on Home and Pro without changing the reported edition. |
| Snap Assist off, taskbar alignment, dark mode, combine taskbar buttons | Personal taste, and already in Settings. |
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

Follows Quiet. A list of switches with a one-line effect each, the small print behind a chevron (values before and after, "applies after you restart Explorer"), tabs **Speed · Cleaner · Comfort · Changes**, a search box and an On/Off filter. The Changes tab lists what TidyWindow changed, with **Put everything back**. Rows that are off are dimmed, like Quiet. Items that need Explorer restarted offer a **Restart Explorer** button in the toast, and a banner at the top of the list stays until Explorer has been restarted, so several shell tweaks can be changed first. When something such as a Windows update puts tweaks back, a banner and the toast offer **Turn on again** for all of them at once. Search also looks at the registry key and value names and at the effect text (for example `ContentDeliveryManager` or `HVCI`). Careful items show a one-time confirmation that states the trade-off. Toasts and the Logs page report outcomes like the other pages.

## Implementation outline

- `src/TidyWindow.Core/Tune/TuneModel.cs`: item/target types, `TuneCatalog`, `TuneGuard` (validation), `TuneScan`.
- `src/TidyWindow.Core/Tune/TuneManager.cs`: local changer, history (`%LOCALAPPDATA%\TidyWindow.Next\Tune\changed.json`), manager, elevated host command `--tune` in the existing `CleanHost`.
- `src/TidyWindow.Core/Tune/WindowsTuneSystem.cs`: registry reader and writer, and an Explorer restart helper.
- `src/TidyWindow.Core/Tune/tune-catalog.json`: the catalog above.
- `src/TidyWindow.Desktop/Features/Tune/TuneView.xaml(.cs)`, `src/TidyWindow.Desktop/Features/Tune/TuneViewModel.cs`, a sidebar entry and page in `MainWindow`.
- `tests/TidyWindow.Tests/TuneTests.cs`: catalog validation, scan states, exact restore (including "not set"), rollback, edition gating, refusal of out-of-catalog writes.
- README entry and this document updated to "implemented" with what was verified by hand.

## Not verified

Reading, the guard, the rules for applying and putting back, rollback, and the helper protocol are covered by tests, plus a read-only scan of a real Windows PC. Writing to the real registry, the administrator helper with a real permission prompt, and how the page looks were not checked by hand yet.

- Whether each setting does what the sources say on the newest Windows builds. Several values (`ContentDeliveryManager`, `TaskbarEndTask`) are community-documented, not Microsoft-documented.
- Whether the Edge policies make Edge show a "managed by your organization" notice. The item says so up front; Edge's behavior with these policies was not confirmed here.
- `WaitToKillAppTimeout` is written as "at most 5000". Its Windows default of 20000 comes from guides, not from a fresh install (the system default profile has no value, and loading the default user hive needs administrator rights). The page reads the real value and only offers a change when the current one is higher.
- Web search: checked by hand on build 26300.9818. Setting `IsWebSuggestionsEnabled = 0` made Settings show Web Searches Off, Microsoft Store suggestions stayed On, and removing the value put it back to On. What the Start menu itself then shows was not looked at, because screenshots of the Start menu are masked. Microsoft's own wording for this setting is not documented yet; the value comes from a preview-build article and the Settings test.
- The items added in the second and third rounds were checked by reading this PC (a read-only scan: values and their "partly on" states came out as expected) and against community guides, not by switching each one on and watching the result. In particular `LastActiveClick` on the newest Windows 11 builds, `Start_IrisRecommendations`, `ShowRecentList`/`ShowFrequentList` (no written source; the two Settings switches they match, and `ShowFrequentList` exists on the development PC) and `PreventDeviceMetadataFromNetwork` blocking a real companion app were not seen working. The new page behavior (dimming, Explorer banner, Turn on again) was built and compiled but not looked at in the running app.
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

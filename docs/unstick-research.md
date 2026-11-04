# Unstick: instant fixes and one-press restarts

Research and implementation decision, 1 November 2026. Unstick is a page of buttons for everyday Windows 10 and 11 glitches that need a process or service restarted, not a repair. Each button runs at once and finishes in about 0.2 to 3 seconds. Fifteen run as the signed-in user with no permission prompt; five more, in a separate section marked Admin, restart a Windows service and ask for administrator permission once.

## Why it is not part of Fixes

[Fixes](fixes-research.md) is built for slow, multi-step repairs, and every fix goes through the same one-at-a-time queue as installs.

- A fix waits behind any install that is running. A frozen taskbar should not wait for a package download.
- Many fixes are long on purpose: *Fix Start menu and taskbar* re-registers six system packages (one to three minutes), *Fix wrong or blank icons* deletes the icon and thumbnail caches, and *Rebuild the search index* makes Windows index again for up to an hour.
- Fixes has no plain "restart File Explorer" button. `ExplorerShell.RestartAsync()` exists only as a helper for Tune.

Unstick runs the work in place, with no queue, and shows `Done in 0.4 s · 7:02 PM` on the row. Nothing is reset and nothing needs a restart. The only thing any action deletes is the waiting print jobs, in *Clear stuck print jobs*. If an action would be slow or heavy, it belongs in Fixes.

## Decision

Twenty actions in four groups. Fifteen need no administrator rights; the fourth group needs them. Every row says when to use it, what it does (the exact programs or calls), what the person will notice, and how long it takes.

| Group | Action | Closes or calls | What it fixes |
| --- | --- | --- | --- |
| Desktop and taskbar | Restart File Explorer | `explorer.exe` for this session, then waits for Windows to start it, or starts it | Frozen taskbar, missing tray icons, stuck previews |
| | Restart Start and taskbar flyouts | `StartMenuExperienceHost.exe`, `ShellExperienceHost.exe` | Start, Quick Settings, calendar and volume pop-ups that will not open, without closing folder windows |
| | Restart Windows search | `SearchHost.exe` (and `SearchApp.exe`, `SearchUI.exe` on Windows 10) | Search box blank or spinning while the index is fine. The index is not touched. |
| | Refresh the display | Presses Win+Ctrl+Shift+B with `SendInput` | Black windows, flicker after sleep or a full-screen game, a monitor that will not redraw |
| | Unstick folder previews | `prevhost.exe`, and only the `dllhost.exe` started with `{AB8902B4-09CA-4BB6-B78D-A8F59079A8D5}` | Endless green bar in a folder of photos or videos; files locked by COM Surrogate or Preview Host |
| | Refresh icons and variables | `SHChangeNotify(SHCNE_ASSOCCHANGED)`, then `WM_SETTINGCHANGE "Environment"` | Icons stale after a default app changed; PATH or variables not seen by running programs |
| Typing, clipboard and pop-ups | Unstick the emoji panel and typing | `TextInputHost.exe`, `TabTip.exe`; starts `ctfmon.exe` if it is not running | Win+. , Win+V or Win+H leaves a frozen pop-up that blocks clicks; no typing in Start, Search or Settings |
| | Restart the clipboard | Opens and closes the clipboard to see if it is held, closes `TextInputHost.exe`, restarts `rdpclip.exe` if running, and closes the single-service `svchost.exe` of `cbdhsvc_*` | Copy and paste stopped, Win+V not recording, Remote Desktop copy failing |
| | Unstick screenshots | `ScreenClippingHost.exe`, `SnippingTool.exe` | Win+Shift+S works once, then stops |
| | Restart notifications | Stops and starts `WpnUserService_*` | Toasts stop, or apps freeze when they raise one |
| | Unstick media keys | Closes the single-service `svchost.exe` of `NPSMSvc_*` | Play/Pause/Next keys dead; old media card stuck in Quick Settings |
| | Restart Widgets | `Widgets.exe`, `WidgetService.exe` | Widgets board blank or stuck; high memory use |
| | Clear stuck sign-in prompts | `CredentialUIBroker.exe`, `Microsoft.AAD.BrokerPlugin.exe` | Hello, passkey and work or school sign-in stuck on "a remote procedure call is already in progress" |
| Network and developer | Flush the DNS cache | `DnsFlushResolverCache()` in `dnsapi.dll` | A site opens an old address after a Wi-Fi or VPN change |
| | Restart WSL | `wsl.exe --shutdown` (shown only when WSL is installed) | `vmmemWSL` holding memory, a terminal that will not open, broken localhost forwarding |
| Hardware and services (Admin) | Restart sound | Fix `restart-audio`: `audiosrv`, `AudioEndpointBuilder` | No sound, crackling, a headset that disappeared |
| | Restart Bluetooth | Fix `restart-bluetooth`: `bthserv` | Devices show as connected but do nothing, or will not pair |
| | Restart the camera | Fix `restart-camera`: `FrameServer`, then `pnputil /scan-devices` | "In use by another app" or no camera found after a call |
| | Clear stuck print jobs | Fix `fix-printing`: stops `spooler`, empties the print queue folder, starts it | A printer stuck on Printing or Deleting |
| | Rescan hardware | Fix `rescan-devices`: `pnputil /scan-devices` | A USB drive, dock or monitor that did not show up |

The Admin group runs fixes that already exist in the Fixes catalog, by id, through the same `FixService` and the same elevated helper. That means the helper still rebuilds each step from the catalog and the guard (an allow-list of programs, arguments and services) still applies; Unstick cannot ask it to run anything else. The only addition to that catalog is *Restart Bluetooth*, a two-step `bthserv` restart (see [fixes-research.md](fixes-research.md)). The five rows show an **Admin** badge and a "Needs" line in their details. Windows asks for permission the first time; the helper then stays open until TidyWindow closes, and Fixes shares it, so a later Fixes run or Unstick button does not ask again. Declining leaves everything unchanged and the row says so.

## Measured on this PC

Windows 11 build 26300, signed in as a standard user (not elevated), checked on 1 November 2026. Only reads and harmless calls were run; no shell process was closed to test.

- **Processes.** `explorer`, `SearchHost`, `TextInputHost` and `ctfmon` are all in this user's session, so they can be listed and closed without elevation.
- **Per-user services.** `cbdhsvc_ddf08`, `NPSMSvc_ddf08` and `WpnUserService_ddf08` are listed by `EnumServicesStatusEx` with their process ids. Each of the three has its own `svchost.exe` in this session (`-k ClipboardSvcGroup -p -s cbdhsvc`, `-k LocalService -p -s NPSMSvc`, `-k UnistackSvcGroup -s WpnUserService`).
- **Reading a command line.** `NtQueryInformationProcess` (class 60) with `PROCESS_QUERY_LIMITED_INFORMATION` reads the command line of those `svchost.exe` and `dllhost.exe` processes without elevation. That is how the thumbnail `dllhost.exe` is told apart from the others.
- **DNS.** `ipconfig /flushdns` fails here with "The requested operation requires elevation" (exit code 1). `DnsFlushResolverCache()` returns true in the same session.
- **Notifications service.** Research on the same build shows `WpnUserService` grants Start and Stop to the Interactive User (`IU`), so a signed-in user can restart it with the service control manager. `cbdhsvc` and `NPSMSvc` have a failure action that restarts them after three seconds, which is why closing their own `svchost.exe` is enough.
- **`TextInputManagementService`.** Its descriptor is `D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCLCSWRPLOCRRC;;;BA)(A;;CCLCSWRPLOCRRC;;;IU)...`. Only `SY` has Stop (`WP`); Administrators have Start only. The advice to restart that service therefore cannot work, and closing `TextInputHost.exe` is used instead.

The runner is checked against a fake Windows (see Tests below), and the real calls were exercised as described above. On 4 November 2026 the developer used the Unstick page by hand on Windows 11 Pro (build 26300) and reports that it works well.

## How it stays safe

- **Own session only.** Programs are matched by name and by session id; another signed-in user's processes are never touched.
- **A process that hosts several services is never closed.** For a per-user service, the host is closed only if its command line contains `-s <service>`, meaning it hosts that one service. A shared `svchost.exe` would take other services with it.
- **Only the thumbnail `dllhost.exe`.** Every other `dllhost.exe` is left alone, including ones that hold open files for other programs.
- **Nothing outside the catalog runs.** `UnstickRunner` refuses an id that is not on the page, and a test checks that no action names `dwm`, `sihost`, `winlogon`, `csrss`, `lsass`, `svchost`, `ApplicationFrameHost` or `RuntimeBroker`.
- **Honest results.** A program that was not running is reported as "not running, nothing was stuck", not as a fix. A close that Windows refuses is reported as a failure, with what to do next.
- **Administrator actions are the reviewed fixes, nothing new.** The first fifteen never use CleanHost, and if Windows refuses one, the row says so rather than asking for elevation. The five Admin actions go only through `FixService`, so the helper receives a fix id, never a command. If the helper is not available they are hidden and refused.
- **One Admin action at a time.** They share one helper and one permission prompt, so a second press waits for the first. Time shown on an Admin row starts after that wait.

## Left out, and why

| Idea | Why it is not here | Instead |
| --- | --- | --- |
| End `dwm.exe` | `winlogon` treats it as a critical desktop failure. On multi-monitor or hybrid-GPU PCs it can give a black screen or sign the session out. | Win+Ctrl+Shift+B, which asks DWM for a fresh surface |
| End `sihost.exe` | Breaks the session's WinRT activation, leaving Start, Action Center and notifications dead until sign-out. | Restart `explorer.exe` and the two flyout hosts |
| Restart `TextInputManagementService` | Stop is denied even to Administrators on current builds (see above). | End `TextInputHost.exe` and make sure `ctfmon.exe` runs |
| End `ApplicationFrameHost.exe` or every `RuntimeBroker.exe` | `ApplicationFrameHost` holds the frame of every open Store app, so all of them close without saving. | Close only the specific stuck host |
| Disable and enable the display adapter | On a single GPU or a MUX laptop it can leave a black screen until power-off, and it crashes WinUI and DirectX apps, including this one. | Win+Ctrl+Shift+B |
| Empty working sets, flush the standby list | Already rejected in [fixes-research.md](fixes-research.md) and [lab-research.md](lab-research.md): Windows reads the same data from disk again, causing stutter. | Nothing |
| End `CrossDeviceResume.exe` | Already noted in [quiet-research.md](quiet-research.md): `sihost.exe` restarts it at once and it has no stuck state to clear. | Nothing |

## Why the Admin section exists

Five items already in Fixes are really two-to-three-second service restarts: audio, Bluetooth, the camera, the print spooler, and a hardware rescan. They needed the queue, so a headset that stopped working could wait behind an install. The choice was between keeping the page free of permission prompts or giving these their own section. The page now has the section, clearly marked and last, so everything above it still never asks. The Fixes entries are unchanged and still work as before.

Checked by hand: the five Admin actions were run by the maintainer on 1 November 2026 and worked, including the permission prompt. The code was written without being able to approve that prompt, so the automated checks cover the logic only. The runner is tested against a fake Fixes service (the right fix id runs, a declined prompt and a failed step are reported, a warning is still a success, the actions are hidden without the helper), and the catalog test checks that each one names a machine-scope fix that exists.

## Where the code is

| Part | File |
| --- | --- |
| Catalog text, `IUnstickSystem`, the runner and its messages | [`UnstickModel.cs`](../src/TidyWindow.Core/Unstick/UnstickModel.cs) |
| The Windows calls: processes, services, `SendInput`, clipboard, DNS (the Admin actions call `FixService` instead) | [`WindowsUnstickSystem.cs`](../src/TidyWindow.Core/Unstick/WindowsUnstickSystem.cs) |
| Page, rows, timing, toasts and log entries | [`Features/Unstick/`](../src/TidyWindow.Desktop/Features/Unstick/) |
| Tests | [`UnstickTests.cs`](../tests/TidyWindow.Tests/Unstick/UnstickTests.cs) |

The catalog is in code, not JSON like Fixes or Tune, because each action runs its own logic rather than a list of commands. The text on each row comes from the same records the runner uses, so what the page says is what runs.

## Tests

`UnstickTests.cs` uses a fake Windows that records calls, so no program on the machine is closed:

- every action is described, has a unique id and a valid icon, and is handled by the runner;
- an id that is not on the page is refused and does nothing;
- no action names a protected Windows process;
- only the thumbnail `dllhost.exe` is closed, with its CLSID;
- `ctfmon.exe` is started only when missing, and `rdpclip.exe` is restarted only when running;
- a program that is not running is a success with a clear message, and one Windows refuses is a failure;
- service outcomes, the clipboard holder message, the WSL exit codes and the "WSL installed" gate;
- an action reports itself active only while its programs are running;
- each Admin action runs exactly the fix it names, a failed step or a declined prompt is reported with its reason, a warning is a success that says where to look, and without the helper the Admin actions are hidden and refused;
- every Admin action points at a machine-scope fix that exists in the Fixes catalog.

## Hardening, 3 November 2026

When TidyWindow itself runs as administrator, Unstick starts nothing: a program started from an elevated app keeps those rights for the rest of the session, with no prompt, and so does everything opened from a shell started that way. *Restart File Explorer* is refused with a message, *Unstick the emoji panel and typing* leaves a missing `ctfmon.exe` to Task Manager, and *Restart the clipboard* leaves `rdpclip.exe` running and says so. `IUnstickSystem.Elevated` carries the fact and the runner tests cover both modes. `ctfmon.exe` and `rdpclip.exe` were already started by their full System32 path. Still open, in Tune's `ExplorerShell`: it starts `explorer.exe` by bare name through ShellExecute and inherits the caller's token; it should use `%WINDIR%\explorer.exe` and refuse, or drop elevation, when elevated.

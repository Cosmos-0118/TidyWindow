# Debloat: removing Windows default apps and bloatware

Research and implementation decision, 31 October 2026. The Debloat page lists Windows default apps, promoted apps, AI features, manufacturer software and two built-in components. It removes the ones you pick and can put them back.

## Decision

Ship a reviewed catalog and a removal pipeline that checks its own result, not a "remove everything matching `*Microsoft*`" script. Plain `Remove-AppxPackage` does not do the job on a modern PC. It removes the app for you only, leaves the staged copy that Windows installs for every new user, and refuses outright for some inbox apps. Apps then return after a feature update or for the next account. The pipeline below handles each of those cases, and it only reports success after it rescans and confirms the app is gone.

The page never removes anything itself. It asks the elevated helper, which loads the same catalog and guard and re-checks every request.

## What the page offers

Items are grouped as tabs, plus **Removed**, which is the way back.

| Tab | Contents |
| --- | --- |
| **Microsoft** | Inbox apps such as Clipchamp, News and Weather, Get Help, Feedback Hub, Solitaire, Office hub, To Do, Power Automate, Phone Link, Dev Home, Quick Assist, Xbox app and Game Bar. |
| **Promoted** | Games and sponsored apps that Windows installs for new accounts. |
| **AI** | The Copilot app and Recall. |
| **Manufacturer** | Pre-installed software from HP, Dell, Lenovo, ASUS, Acer, MSI, Samsung and LG. Shown only if present. |
| **Components** | OneDrive and Microsoft Edge, with extra checks (see below). |
| **Removed** | What this app removed, with **Put back**. |

Each item says what it is, what removing it changes, and how risky it is. Items marked **Careful** default to the safe button in the confirmation dialog. Items that are not on the PC are not shown. **Select suggested** ticks the apps almost nobody uses, and never anything marked Careful.

## How an app is removed

For each package family the helper does the following, in order:

1. Create the `Deprovisioned\<family>` registry key, so Windows does not install the app again for new accounts or after an upgrade.
2. If Windows marks the package non-removable, clear that with `dism /Online /Set-NonRemovableAppPolicy`.
3. Delete the `InboxApplications` registry key for the package.
4. Create temporary `EndOfLife` keys for every user and for the system account. Windows refuses to remove some inbox apps unless it sees these.
5. Run `Remove-AppxPackage -User <sid>` for each user, then `-AllUsers`.
6. For the staged copy, run `Remove-AppxProvisionedPackage -Online`.
7. Delete the temporary keys, even if something above failed.
8. Rescan. If the app is still installed, the `Deprovisioned` key from step 1 is rolled back and the item is reported as failed.

PowerShell 7 cannot load the Appx module (it fails with `0x80131539`), so every step runs in Windows PowerShell 5.1 by its full path with an encoded command. A removed package is recorded only after the rescan confirms it.

## Safety rules

- **Catalog and guard.** Only catalog entries can be removed. A protected list stays out of reach: .NET and VC runtimes, WebView2, the Store, App Installer (WinGet), Xbox sign-in, shell and start components, `MicrosoftWindows.Client.*` packages and GUID-named packages. The catalog is refused as a whole if it names one of them.
- **Strict names.** Package patterns must match `^[A-Za-z0-9][A-Za-z0-9._-]*\*?$`. A wildcard needs a prefix of at least six characters. Tokens and SIDs are regex-checked before they reach the registry or a command line.
- **Checked twice.** The page and the elevated helper both validate. A request that names anything outside the catalog is rejected in the helper whatever the page sent.
- **Scan without admin.** The page lists the current user's packages without elevation. The helper rescans all users once you approve the Windows prompt.
- **Nothing in the background.** Nothing is removed or re-applied automatically. The helper exits after five minutes of inactivity.

## OneDrive and Edge

These are not packages, so they have their own steps and extra refusals.

**OneDrive**

- Removal is refused while Desktop, Documents or Pictures are redirected to OneDrive, which would break your folders. The page shows a lock and says why.
- It stops OneDrive, runs OneDrive's own `OneDriveSetup.exe /uninstall`, removes its startup entry, and removes the setup entry from the default user profile so new accounts do not get it. It never deletes your files.
- OneDrive counts as installed only if `OneDrive.exe` exists in a Program Files or LocalAppData install folder, or the uninstall registry entry exists. The stub `System32\OneDriveSetup.exe` is always present and a leftover folder remains after uninstall, so neither counts. Before this was fixed, the check wrongly reported "OneDrive is still on this PC" after a successful uninstall.
- An Explorer shell extension (`FileSyncShell64.dll`) may stay locked until Explorer restarts or the PC reboots.

**Microsoft Edge**

- Removal is refused if no other browser is registered. WebView2 is never touched, because other apps depend on it.
- Windows blocks Edge's own uninstaller on purpose. The pipeline sets `AllowUninstall` for Edge Update, adds a temporary stub file Windows looks for, runs Edge's installer with `--uninstall --system-level --force-uninstall`, and then removes the stub, the value and the shortcuts.
- It also sets the Edge Update policy that stops Edge installing itself again. That step is best-effort.

**Recall**

- Removal turns the Windows feature off with DISM and does not use `/Remove`, so it can be turned on again. A PC without Recall is not an error, and the page hides Recall once it has been handled.

## Putting things back

The **Removed** tab remembers what was removed, in `%LOCALAPPDATA%\TidyWindow.Next\Debloat\removed.json`.

| Item | What Put back does |
| --- | --- |
| Apps | Deletes the `Deprovisioned` key for the item's families only, then installs from the Microsoft Store (`winget install --id <id> --source msstore`). A 12-character store ID means the Store; other IDs use WinGet. |
| Recall | Turns the Windows feature back on. |
| Edge | Removes the install-blocking policy, then `winget install Microsoft.Edge`. |
| OneDrive | `winget install Microsoft.OneDrive`. |

Put back reinstalls the app, not its data. Settings and files kept inside an app's own data are gone along with the app.

If a removal fails, it is not added to the list. To bring that app back, install it with WinGet or the Store as usual.

## Will removed apps come back?

Usually not, with some honest limits:

- **New accounts and staged copies.** The `Deprovisioned` key and the removed staged package are what stop Windows installing the app for new users. This is the same mechanism Windows uses for itself.
- **Store updates.** An app that is not installed is not updated or reinstalled by the Store.
- **Windows feature updates.** The key is meant to survive upgrades, but Microsoft can add new inbox apps with a new release, or change how Edge and OneDrive are delivered. Open the page after a big update: anything that returned is listed again and can be removed again. This was not tested across a feature update.
- **Recall and Edge.** Recall stays off unless a Windows update or you turn it on. Edge is blocked from reinstalling by policy, but a Microsoft update can still ship it again.
- **Manufacturer software.** OEM tools may reinstall themselves from their own updater or a driver package if it is still installed. Removing the OEM app does not remove that.

## Sources

Windows has no single documented "remove this inbox app everywhere" command, so the pipeline is assembled from several documented pieces and then checked on a real PC.

| Source | Used for |
| --- | --- |
| Microsoft Learn: DISM app package servicing, `Remove-AppxPackage`, `Remove-AppxProvisionedPackage` | What each command removes and its scope |
| Microsoft Learn: Edge and WebView2 documentation | Edge Update policy, and why WebView2 must stay |
| [Win11Debloat](https://github.com/Raphire/Win11Debloat) (MIT) | Which default apps people remove, and which to leave alone (cross-check) |
| [WinUtil](https://github.com/ChrisTitusTech/winutil) (MIT) | App lists and the OneDrive removal approach (cross-check) |

The catalog entries, descriptions and code are written for this app. Each item's `sources` field in `debloat-catalog.json` names where its package names were checked.

## Deliberately left out

- Runtimes, WebView2, the Store, WinGet, shell components, drivers and security components.
- Windows features unrelated to apps (Defender, Windows Update, search, widgets and similar). Quiet and Tune handle those.
- Wildcard sweeps such as "remove every Microsoft app".
- Third-party apps you installed yourself. Use Installed for those.

## Tests and limits

- The unit tests (`tests/TidyWindow.Tests/Debloat/`) use a fake PC and cover the guard, the scan and its locks, each step of the removal pipeline and its rollback, OneDrive detection, Edge, Recall, put back, and the helper's validation.
- One opt-in live test (`TIDYWINDOW_LIVE_TESTS=1`) does a read-only scan of the real PC.
- Writing to the registry, DISM, the Appx commands and the OneDrive and Edge uninstallers needs administrator rights and was run by hand on one Windows 11 PC with the apps listed above. Other Windows builds, other editions and PCs with manufacturer software were not tested.
- Windows Home, Pro and Enterprise were not compared, and policy-managed PCs can block any of these steps. A blocked step is reported as a failure and rolled back.

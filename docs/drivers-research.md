# Drivers: Windows Update only

Research and implementation decision, 5 November 2026. Drivers lists the drivers that Windows Update offers for this PC, sets each beside the driver in use, and installs one at a time with the old driver saved first. It is not a driver scanner: it never looks anywhere but Windows Update.

## Decision

| It does | It does not |
| --- | --- |
| Ask Windows Update for drivers that are not installed (`IsInstalled=0 and Type='Driver'`), the same list as Settings, Windows Update, Optional updates | Keep its own driver database, or scan for "outdated" drivers |
| Show each offer beside the installed driver: version, date, maker, and a verdict (New, Newer, Same, Older, Not compared) | Offer a button for a driver older than the one in use |
| Put a device that Windows reports a problem with (a non-zero problem code) first | Update everything in one click |
| Install one driver at a time through the administrator helper, after saving the driver in use | Download a driver from anywhere else, or accept a file, link or package from the app |
| Point to the maker's own tool for graphics and laptop-specific drivers | Accept licence terms for you, or work around a policy that limits drivers |
| Put the earlier driver back from the page | Update BIOS or firmware |

## Why Windows Update

- **Microsoft only offers a driver to hardware that matches it, after the maker submitted it.** TidyWindow makes no guess of its own; it shows Microsoft's list.
- **Driver-updater tools have a bad record.** Users report wrong drivers for their hardware, drivers from unknown sources, and bulk updates where nobody can tell which driver broke the PC. A restore point often does not undo a driver.
- **It keeps the page inside TidyWindow's promise** that every change is visible and reviewable before it runs.

## What can go wrong, and what the page does

| Problem | What the page does |
| --- | --- |
| Windows Update sometimes offers a driver **older** than the one installed. Microsoft's documentation says Windows Update offers "the best matching driver package, which is not necessarily the most recent", and a news report from May 2026 (Windows Latest, not independently checked) says Windows 11 had been replacing newer graphics drivers with older ones. | Every offer is compared with the installed driver. The offered version is read from the end of the update's title (Windows Update gives a date but no version field); when both versions can be read they decide, otherwise the dates do. An older driver is shown with the reason and no button, and the helper refuses it again. |
| Graphics and laptop-specific drivers are often behind. | A **From the maker** card shows the tool that fits this PC: chosen from the graphics driver's maker (NVIDIA, AMD, Intel) and the PC's maker (Dell, Lenovo, HP). |
| A managed PC may get nothing: an update server (WSUS), an Intune or Group Policy setting that excludes drivers, blocked Windows Update access, or device installation set to never search. | The page reads those settings and says why the list may be short. It does not try to get around them. |
| A driver may come with licence terms. | The helper refuses it and points to Settings. TidyWindow does not accept terms on someone's behalf. |
| Windows reports success but the device still has the old driver. | The helper reads the PC again. A result the records do not show is reported as unverified, not as a success. |

## How it is built

**Reading Windows Update.** The Windows Update Agent is created by name at run time (`Type.GetTypeFromProgID("Microsoft.Update.Session")`). It is not a project reference, because a compile-time reference needs a type library that a plain `dotnet build` on CI does not have.

**A search program the app can end.** The agent's asynchronous calls need a COM callback object. Passing none does not work from either PowerShell or C#: `BeginSearch(criteria, $null, $null)` fails with "Could not convert argument 1" (checked on a real PC). A synchronous `Search` cannot be interrupted, and on a slow connection it can take a minute or more. So the search runs in a separate program, `TidyWindow.CleanHost.exe --driver-scan`, started as the ordinary account (no permission prompt). It prints one line of JSON, and Cancel ends the process. This is the one use of the helper executable without administrator rights. It only reads.

**Installed drivers from the registry.** Windows' own device records (`HKLM\SYSTEM\CurrentControlSet\Enum`, and the class keys under `Control\Class`) carry every hardware and compatible id, the driver's version, date, maker and INF name, and any standard account may read them. 229 devices read in about 0.1 s on the test PC. `Win32_PnPSignedDriver`, the usual WMI source, has only one hardware id per device, which is not enough to match an offer. A device's class is read from its class key because most devices do not carry it.

**Matching an offer to a device.** An offer carries the hardware id it matched on, in lower case, sometimes a shorter form than the device lists. A device matches when it lists that id, or a longer one that begins with it followed by `&`. With several matches (two identical network cards) the newest installed driver is the one the offer is judged against, so an offer is never judged only against the oldest.

**The helper boundary.** Drivers cannot come from a fixed catalog, so the boundary is kept this way:

- The app sends one id: an update id (a GUID) or the id of a saved driver (a GUID without dashes). Anything else is refused before it is used.
- The helper runs its own search and installs only an update that is in its own results. It never accepts a file path, link or driver package.
- It checks the same rules as the page: no downgrade, no unaccepted licence.
- It reads the installed driver itself, saves it, installs, and reads the PC again.
- The helper is `DriverHost` in `TidyWindow.Core`, so it is tested without a permission prompt, over the same private pipe as the other helpers.

**Administrator rights.** Searching works as an ordinary user. Installing does not: Windows refuses with `0x80240044` (`WU_E_PER_MACHINE_UPDATE_ACCESS_DENIED`), so installs go through the helper.

**Install order.** Search again; check the update is in the results; compare with the installed driver; refuse a downgrade or unaccepted licence; save the driver in use; download and install; read the PC again; record the new driver's package name next to the saved copy. If the save fails, nothing is installed. If the install fails, the saved copy is deleted.

**The way back.** Before installing, the driver in use is copied out of Windows' driver store with `pnputil /export-driver`. The copy and its record live under `%ProgramData%\TidyWindow.Next\drivers`, a folder only administrators can change (see [`ProtectedStore`](../src/TidyWindow.Core/Infrastructure/ProtectedStore.cs)), so a program running as the ordinary account cannot plant a driver for the helper to put back with administrator rights. The app keeps its own list under `%LOCALAPPDATA%\TidyWindow.Next\Drivers` only to show; the helper trusts its own copy and is given the id of a saved driver, nothing else.

- A driver that comes with Windows (`netrasa.inf` and similar) cannot be exported. Windows keeps it, so there is nothing to save, and the page says so. Only a vendor driver in the store (`oem12.inf`) is ever named to `pnputil`.
- **Put the earlier driver back** stages the saved package with `pnputil /add-driver`, then removes the update's package with `pnputil /delete-driver ... /uninstall /force`, then rescans devices. Windows then serves the device from the best package left. It is refused if the device has a different driver than the one TidyWindow installed, so it cannot undo a later change, and if the update's package cannot be told apart from the old one.
- `DiRollbackDriver`, the documented rollback call, was left out. It is a call into `newdev.dll` that depends on the previous driver Windows itself kept for that device, which TidyWindow cannot see or check beforehand. The saved copy is TidyWindow's own and does not depend on that. Device Manager's Roll Back Driver is always still there.

**Stopping.** A stop is honoured while the helper searches and saves, before anything on the PC has changed. Once the download and install are under way they cannot be stopped, because the agent's call waits and ending a driver install half way can leave a device without a working driver. The Stop button says so. Closing the window while a driver installs asks first, and says the install carries on in the helper but will not be recorded.

## Checked on a real PC

On Windows 11 (build 26300) with MSI hardware and two AMD display adapters:

- The search returned one offer (a Microsoft "Voice Clarity" audio component) in 8 to 56 seconds, depending on Windows Update's mood. It matched the installed driver by hardware id and was judged **Newer** (1.0.4.55794 against 1.0.4.7057).
- Cancelling the search ended the process; none was left behind.
- The AMD display adapters were read with their class, version, date and INF names, and the AMD tool was chosen. No policy that limits drivers was set.
- WinGet: `Intel.IntelDriverAndSupportAssistant`, `Dell.CommandUpdate`, `Lenovo.SystemUpdate` and `HP.SupportAssistant` exist. The NVIDIA app and AMD Software are **not** in WinGet, so they get a link to the maker's page and no Install Hub button. The Intel, Dell and Lenovo pages refused an automated check (HTTP 403); the others opened.

**Not checked on a real PC:** installing a driver and putting one back. Both would change the PC, so they are covered by tests with stand-ins for Windows Update, the installed drivers and `pnputil`, not by a real run. Treat the first real install as unverified until someone has done one. The `pnputil` behaviour (that `/delete-driver ... /uninstall` makes Windows fall back to the staged package) comes from its documentation.

## Left out

- Scanning for drivers outside Windows Update, or any list of "outdated" drivers.
- "Update all". One driver at a time keeps a failure traceable to one change.
- Hiding or blocking an update.
- Accepting licence terms, and any workaround for a policy.
- BIOS and firmware, which belong to the maker's tool.

## Sources

- [Windows Update Agent object model](https://learn.microsoft.com/en-us/windows/win32/wua_sdk/windows-update-agent-object-model)
- [IUpdateSearcher::Search](https://learn.microsoft.com/en-us/windows/win32/api/wuapi/nf-wuapi-iupdatesearcher-search)
- [IWindowsDriverUpdate](https://learn.microsoft.com/en-us/windows/win32/api/wuapi/nn-wuapi-iwindowsdriverupdate)
- [IUpdateInstaller](https://learn.microsoft.com/en-us/windows/win32/api/wuapi/nn-wuapi-iupdateinstaller)
- [WUA success and error codes](https://learn.microsoft.com/en-us/windows/win32/wua_sdk/wua-success-and-error-codes-)
- [How Windows selects a driver package](https://learn.microsoft.com/en-us/windows-hardware/drivers/install/how-setup-selects-drivers)
- [Intune Windows Update settings](https://learn.microsoft.com/mem/intune/protect/windows-update-settings)
- [PnPUtil command syntax](https://github.com/MicrosoftDocs/windows-driver-docs/blob/staging/windows-driver-docs-pr/devtest/pnputil-command-syntax.md)
- [DiRollbackDriver](https://learn.microsoft.com/windows/win32/api/newdev/nf-newdev-dirollbackdriver)
- [Microsoft Support: automatically get drivers](https://support.microsoft.com/en-us/help/15046)
- [Windows Latest: Windows 11 downgrading graphics drivers](https://www.windowslatest.com/2026/05/13/microsoft-admits-windows-11-has-been-downgrading-graphics-drivers-reveals-when-a-fix-is-coming/)

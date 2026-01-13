# TidyWindow Essentials Repair Catalog - Gap Additions

## Windows 10/11 Issues (67 Fixes)

**Version**: 1.2  
**Date**: January 12, 2026  
**Author**: Cosmos-0118  
**Scope**: Items not already covered by current Essentials tasks (network reset and cache flush, network fix suite, system health scanner, disk checkup and fix, RAM purge, system restore manager, app repair helper, browser reset and cache cleanup, Windows Update repair toolkit, Windows Defender repair and deep scan, print spooler recovery suite).

_Priority uses 1-5 (5 is highest). Risk is a qualitative assessment._

## Implementation Roadmap (bundle -> script)

-   [x] Performance and Storage Repair Pack — `automation/essentials/performance-and-storage-repair.ps1`
    -   SysMain disable, pagefile sizing, temp/prefetch purge, event-log trim, power plan reset
-   [x] Audio and Peripheral Repair Pack — `automation/essentials/audio-and-peripheral-repair.ps1`
    -   Audio stack restart, endpoint rescan, Bluetooth AVCTP reset, USB hub reset, mic/camera enable
-   [x] Shell and UI Repair Pack — `automation/essentials/shell-and-ui-repair.ps1`
    -   ShellExperienceHost/StartMenu re-register, search indexer reset, explorer recycle, settings re-register, tray refresh
-   [ ] Security and Credentials Repair Pack — `automation/essentials/security-and-credential-repair.ps1`
    -   Firewall reset, SecurityHealth service/UI re-register, credential vault rebuild, EnableLUA enforcement
-   [ ] Profile and Logon Repair Pack — `automation/essentials/profile-and-logon-repair.ps1`
    -   Startup audit/trim, ProfileImagePath repair, ProfSvc restart/userinit check, stale profile cleanup
-   [ ] Recovery and Boot Repair Pack — `automation/essentials/recovery-and-boot-repair.ps1`
    -   Safe mode exit, bootrec fixes, DISM from recovery guidance, testsigning toggle, time sync repair, WMI salvage/reset, dump + driver scan helper
-   [ ] Graphics and Display Repair Pack — `automation/essentials/graphics-and-display-repair.ps1`
    -   Display adapter disable/enable, display services restart, HDR/night light toggle, resolution/refresh apply, EDID/stack refresh
-   [ ] OneDrive and Cloud Sync Repair Pack — `automation/essentials/onedrive-and-cloud-repair.ps1`
    -   OneDrive reset, sync services restart, KFM mapping repair, autorun/task recreate
-   [ ] Activation and Licensing Repair Pack — `automation/essentials/activation-and-licensing-repair.ps1`
    -   slmgr activation/rearm helpers, activation DLL re-registration
-   [ ] TPM, BitLocker, Secure Boot Repair Pack — `automation/essentials/tpm-bitlocker-secureboot-repair.ps1`
    -   TPM clear, BitLocker suspend/resume, Secure Boot key reset guidance, device encryption prerequisites
-   [ ] PowerShell Environment Repair Pack — `automation/essentials/powershell-environment-repair.ps1`
    -   Execution policy set, profile reset, WinRM/PSRemoting enable
-   [ ] Store and AppX Repair Pack (extended) — extend `automation/essentials/app-repair-helper.ps1` (add Store/AppX block; no new script file)
    -   wsreset + UwpSvc restart, AppX re-register, Store reinstall, capability access reset (extends App Repair Helper)
-   [ ] Task Scheduler and Automation Repair Pack — `automation/essentials/task-scheduler-repair.ps1`
    -   Task DB rebuild, USO task re-enable, trigger reset + Schedule service restart
-   [ ] Time and Region Repair Pack — `automation/essentials/time-and-region-repair.ps1`
    -   Time zone + NTP resync, locale and language reset
-   [ ] File Explorer and Context Menu Repair Pack — `automation/essentials/explorer-and-context-repair.ps1`
    -   Shell extension cleanup, file association repair, library restore, mouse double-click and explorer tweaks
-   [ ] Device Drivers and PnP Repair Pack — `automation/essentials/device-drivers-and-pnp-repair.ps1`
    -   PnP rescan, stale oem\*.inf cleanup, Plug and Play stack restart, USB selective suspend disable
-   [ ] Per-pack UI/catalog wiring — add each script to EssentialsTaskCatalog as it lands (no final batch step)
-   [ ] Add docs links from Essentials page to this gap catalog and new task help cards
-   [ ] Add automated tests/stubs under `tests/TidyWindow.Automation.Tests/` for each pack (argument validation, dry-run if applicable)

_Coverage trace: Roadmap packs map 1:1 to every issue group below (Performance/Storage, Audio/Peripherals, Shell/UI, Security/Services, Profile/Logon, Recovery/Boot, Graphics/Display, OneDrive/Cloud, Activation/Licensing, TPM/BitLocker/Secure Boot, PowerShell Environment, Store/AppX, Task Scheduler/Automation, Time/Region/NTP, File Explorer/Context, Device Drivers/PnP)._

## Performance and Storage (6 Issues)

| Issue                            | PowerShell/C# Fix                                                                                                                                                                                             | Priority (1-5) | Risk   | Restart Required |
| -------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | -------------- | ------ | ---------------- |
| 100 percent disk usage (SysMain) | Stop SysMain and disable startup: `Stop-Service -Name SysMain -Force; Set-Service -Name SysMain -StartupType Disabled`                                                                                        | 5              | Low    | No               |
| Low virtual memory warnings      | Set managed pagefile: `wmic pagefileset where name="C:\\pagefile.sys" set InitialSize=1024,MaximumSize=4096` or `Set-CimInstance -ClassName Win32_ComputerSystem -Property @{AutomaticManagedPagefile=$true}` | 4              | Medium | Yes              |
| Temp files filling disk          | Run `cleanmgr /sagerun:1` after `cleanmgr /sageset:1`, purge `%TEMP%`                                                                                                                                         | 3              | Low    | No               |
| Prefetch folder bloated          | `Remove-Item C:\\Windows\\Prefetch\\* -Force` (safe delete; Windows rebuilds)                                                                                                                                 | 2              | Low    | No               |
| Event logs consuming space       | Clear and cap sizes: `wevtutil cl System; wevtutil sl System /ms:32768` (repeat for Application/Setup)                                                                                                        | 2              | Low    | No               |
| Power throttling conflicts       | Restore defaults: `powercfg /restoredefaultschemes`; optionally `powercfg /setactive scheme_min`                                                                                                              | 2              | Low    | No               |

## Audio and Peripherals (6 Issues)

| Issue                      | PowerShell/C# Fix                                                                                                   | Priority (1-5) | Risk   | Restart Required |
| -------------------------- | ------------------------------------------------------------------------------------------------------------------- | -------------- | ------ | ---------------- |
| No system sound            | Restart audio stack: `Restart-Service -Name AudioSrv,AudioEndpointBuilder -Force`                                   | 5              | Low    | No               |
| Audio devices missing      | Rescan endpoints: `pnputil /enum-devices /class AudioEndpoint` then `pnputil /scan-devices`                         | 4              | Low    | No               |
| Bluetooth audio drops      | Restart Bluetooth AVCTP: `Restart-Service -Name BthAvctpSvc`; remove and re-add device via `Remove-BluetoothDevice` | 3              | Medium | No               |
| USB devices not recognized | Reset USB hubs: `Get-PnpDevice -Class USB -Status Error; Enable-PnpDevice -Confirm:$false; pnputil /scan-devices`   | 3              | Low    | No               |
| Microphone not detected    | Enable disabled endpoints: `Get-PnpDevice -Class AudioEndpoint -Status Error; Enable-PnpDevice -Confirm:$false`     | 2              | Low    | No               |
| Camera not detected        | Restart camera service and rescan: `Restart-Service -Name FrameServer -Force; pnputil /scan-devices`                | 2              | Low    | No               |

## Shell and UI Issues (6 Issues)

| Issue                           | PowerShell/C# Fix                                                                                                                                                                                      | Priority (1-5) | Risk   | Restart Required |
| ------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ | -------------- | ------ | ---------------- |
| Taskbar missing or unresponsive | Re-register ShellExperienceHost: `Get-AppxPackage -AllUsers Microsoft.Windows.ShellExperienceHost; Add-AppxPackage -DisableDevelopmentMode -Register "$($_.InstallLocation)\AppXManifest.xml"`         | 5              | Medium | Yes              |
| Start Menu broken or empty      | Re-register StartMenuExperienceHost: `Get-AppxPackage -AllUsers Microsoft.Windows.StartMenuExperienceHost; Add-AppxPackage -DisableDevelopmentMode -Register "$($_.InstallLocation)\AppXManifest.xml"` | 5              | Medium | Yes              |
| Search not finding files        | Restart indexer: `Restart-Service -Name WSearch -Force`; rebuild index via `Dism /Online /Cleanup-Image /RestoreHealth` if needed                                                                      | 4              | Low    | No               |
| Explorer.exe keeps crashing     | Reset explorer: `Stop-Process -Name explorer -Force; Start-Process explorer.exe`                                                                                                                       | 4              | Low    | No               |
| Settings app crashes            | Re-register ImmersiveControlPanel: `Get-AppxPackage -AllUsers windows.immersivecontrolpanel; Add-AppxPackage -DisableDevelopmentMode -Register "$($_.InstallLocation)\AppXManifest.xml"`               | 3              | Medium | Yes              |
| Notification area blank         | Restart ShellExperienceHost: `Stop-Process -Name ShellExperienceHost -Force; Start-Process ShellExperienceHost.exe`                                                                                    | 2              | Low    | No               |

## Security and Services (4 Issues)

| Issue                             | PowerShell/C# Fix                                                                                                                                                                                                                            | Priority (1-5) | Risk   | Restart Required |
| --------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | -------------- | ------ | ---------------- |
| Firewall blocking legitimate apps | Reset firewall: `netsh advfirewall reset`, re-enable profiles, re-add rules as needed                                                                                                                                                        | 3              | Low    | No               |
| Windows Security app blank        | Restart Security Center: `Restart-Service -Name SecurityHealthService -Force`; re-register UI: `Get-AppxPackage Microsoft.SecHealthUI -AllUsers; Add-AppxPackage -DisableDevelopmentMode -Register "$($_.InstallLocation)\AppXManifest.xml"` | 2              | Medium | Yes              |
| Credential Manager empty          | Restart vault services: `Restart-Service -Name VaultSvc,Schedule -Force`; rebuild credential cache by renaming `%LOCALAPPDATA%\Microsoft\Credentials`                                                                                        | 2              | High   | Yes              |
| UAC prompts not appearing         | Enforce LUA: `Set-ItemProperty -Path "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System" -Name EnableLUA -Value 1`                                                                                                             | 1              | Medium | Yes              |

## Login and Profile (4 Issues)

| Issue                      | PowerShell/C# Fix                                                                                                                                    | Priority (1-5) | Risk   | Restart Required |
| -------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------- | -------------- | ------ | ---------------- |
| Slow login (>60s)          | Disable non-essential startup items: audit HKLM/HKCU Run keys and Scheduled Tasks, then set critical services back to Automatic start                | 4              | Low    | No               |
| User Profile Service fails | Repair ProfileImagePath: set correct path under `HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\<SID>`; ensure `ProfSvc` is Automatic | 3              | High   | Yes              |
| Welcome screen hangs       | Restart ProfSvc: `Restart-Service -Name ProfSvc -Force`; verify `Userinit` registry points to `userinit.exe`                                         | 2              | Medium | Yes              |
| Cannot switch user         | Clear stale profile cache: `quser` to identify sessions, `logoff <id>` then delete temp profiles under `C:\\Users` ending in `.000`                  | 1              | Medium | No               |

## Mission-Critical Recovery (7 Issues)

| Issue                              | PowerShell/C# Fix                                                                                                                               | Priority (1-5) | Risk   | Restart Required |
| ---------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------- | -------------- | ------ | ---------------- |
| BSOD loop or recovery screen       | Collect dump: analyze `C:\\Windows\\Minidump` with WinDbg; run `driverquery /v` to spot third-party drivers                                     | 5              | High   | Yes              |
| Stuck in Safe Mode                 | Return to normal boot: `bcdedit /deletevalue {current} safeboot` then reboot                                                                    | 5              | High   | Yes              |
| Bootrec needed (MBR/BCD)           | `bootrec /fixmbr`, `bootrec /fixboot`, `bootrec /rebuildbcd` from recovery                                                                      | 4              | High   | Yes              |
| Winload.exe errors                 | Run DISM from recovery: `dism /Image:C:\\ /Cleanup-Image /RestoreHealth`                                                                        | 3              | High   | Yes              |
| Driver signature enforcement block | Temporarily allow unsigned: `bcdedit /set testsigning on` then reboot; revert with `bcdedit /set testsigning off`                               | 2              | High   | Yes              |
| Time sync broken                   | `w32tm /config /update /manualpeerlist:"time.windows.com,0x9" /syncfromflags:manual; net stop w32time; net start w32time; w32tm /resync /force` | 2              | Low    | No               |
| WMI repository inconsistent        | `winmgmt /salvagerepository` then `winmgmt /resetrepository`; restart Winmgmt service                                                           | 2              | Medium | Yes              |

## Graphics and Display (5 Issues)

| Issue                                | PowerShell/C# Fix                                                                                                                       | Priority (1-5) | Risk   | Restart Required |
| ------------------------------------ | --------------------------------------------------------------------------------------------------------------------------------------- | -------------- | ------ | ---------------- |
| Screen freezes or black screen       | Disable and enable display adapter: `Get-PnpDevice -Class Display; Disable-PnpDevice -Confirm:$false; Enable-PnpDevice -Confirm:$false` | 4              | Low    | No               |
| GPU driver crash recovery            | Restart display services: `Restart-Service -Name DisplayEnhancementService,UdkUserSvc -Force`                                           | 4              | Low    | No               |
| Night light or HDR not turning on    | Restart DisplayEnhancementService and toggle registry flag for night light/HDR, then refresh display policies                           | 3              | Low    | No               |
| Resolution or refresh rate incorrect | Apply using WMI/CIM: set `Win32_VideoController` current mode to desired resolution/refresh                                             | 3              | Medium | No               |
| Display not waking after sleep       | Reset graphics stack (disable/enable) and force EDID rebuild via PnP rescan                                                             | 3              | Medium | Yes              |

## OneDrive and Cloud Sync (4 Issues)

| Issue                                           | Fix                                                                 | Priority (1-5) | Risk   | Restart Required |
| ----------------------------------------------- | ------------------------------------------------------------------- | -------------- | ------ | ---------------- |
| OneDrive stuck processing changes               | `OneDrive.exe /reset`; restart `FileSyncSvc`                        | 4              | Low    | No               |
| Sync stalls after sleep                         | Restart `OneSyncSvc` and `FileSyncProvider` services                | 3              | Low    | No               |
| Known Folder Move stuck (Documents to OneDrive) | Clear KFM registry mappings under User Shell Folders and retry      | 3              | Medium | Yes              |
| OneDrive not starting at boot                   | Re-register OneDrive client and recreate scheduled task for startup | 2              | Low    | No               |

## Activation and Licensing (3 Issues)

| Issue                           | Fix                                              | Priority (1-5) | Risk | Restart Required |
| ------------------------------- | ------------------------------------------------ | -------------- | ---- | ---------------- |
| Windows will not activate       | `slmgr /ato`                                     | 4              | Low  | No               |
| "Windows not genuine" watermark | Re-register activation DLLs and retry activation | 3              | Low  | No               |
| Evaluation expired              | `slmgr /rearm`                                   | 2              | Low  | Yes              |

## TPM, BitLocker, Secure Boot (4 Issues)

| Issue                               | Fix                                                                                 | Priority (1-5) | Risk   | Restart Required |
| ----------------------------------- | ----------------------------------------------------------------------------------- | -------------- | ------ | ---------------- |
| TPM locked or bad state             | `Clear-Tpm` (requires reboot and owner consent)                                     | 4              | High   | Yes              |
| BitLocker keeps asking recovery key | Suspend and resume protectors: `manage-bde -protectors -disable C:` then re-enable  | 4              | Medium | Yes              |
| Secure Boot validation error        | Reset boot keys with `bcdedit` and bootmgr repair sequence                          | 3              | Medium | Yes              |
| Device Encryption toggle missing    | Enable required registry keys and services (DeviceInstall, BitLocker prerequisites) | 2              | Low    | Yes              |

## PowerShell Environment (3 Issues)

| Issue                                 | Fix                                                                                          | Priority (1-5) | Risk   | Restart Required |
| ------------------------------------- | -------------------------------------------------------------------------------------------- | -------------- | ------ | ---------------- |
| "Scripts are disabled on this system" | Set execution policy to RemoteSigned: `Set-ExecutionPolicy RemoteSigned -Scope LocalMachine` | 4              | Medium | No               |
| Broken PowerShell profile             | Delete and recreate profile under Documents PS path                                          | 2              | Low    | No               |
| Remote or WinRM malfunction           | Enable remoting: `Enable-PSRemoting -Force`                                                  | 2              | Low    | No               |

## Windows Store and AppX (4 Issues)

_Implementation: extend `automation/essentials/app-repair-helper.ps1` with a Store/AppX block instead of creating a new script._

| Issue                         | Fix                                                  | Priority (1-5) | Risk   | Restart Required |
| ----------------------------- | ---------------------------------------------------- | -------------- | ------ | ---------------- |
| Store will not launch         | `wsreset -i` and restart `UwpSvc`                    | 5              | Low    | No               |
| Cannot install or update apps | Re-register all AppX provisioned packages            | 4              | Medium | Yes              |
| Store missing completely      | Install Store package via DISM and AppX registration | 4              | Medium | Yes              |
| UWP permission sandbox broken | Reset CapabilityAccessManager registry policies      | 2              | Medium | Yes              |

## Task Scheduler and Automation (3 Issues)

| Issue                             | Fix                                                                     | Priority (1-5) | Risk   | Restart Required |
| --------------------------------- | ----------------------------------------------------------------------- | -------------- | ------ | ---------------- |
| Task Scheduler database corrupted | Rebuild `%SystemRoot%\\System32\\Tasks` metadata and re-import defaults | 4              | Medium | Yes              |
| Update tasks disabled             | Re-enable USO and Update tasks and re-register                          | 3              | Low    | No               |
| Background tasks not firing       | Reset triggers and restart Schedule service                             | 2              | Low    | No               |

## Time, Region, and NTP (3 Issues)

| Issue                           | Fix                                                                            | Priority (1-5) | Risk | Restart Required |
| ------------------------------- | ------------------------------------------------------------------------------ | -------------- | ---- | ---------------- |
| Wrong time zone breaks internet | `Set-TimeZone` to correct zone, then `w32tm /resync`                           | 3              | Low  | No               |
| Region mismatch crashes apps    | `Set-WinSystemLocale` and `Set-WinUserLanguageList` to desired region/language | 2              | Low  | Yes              |
| NTP sync stuck                  | Restart `w32time`, re-register service, and force sync                         | 2              | Low  | No               |

## File Explorer and Context Menu (4 Issues)

| Issue                                           | Fix                                                         | Priority (1-5) | Risk   | Restart Required |
| ----------------------------------------------- | ----------------------------------------------------------- | -------------- | ------ | ---------------- |
| Right-click menu slow or blank                  | Remove stale shell extensions (shell CLSID pruning)         | 4              | Low    | No               |
| File associations broken (.exe/.lnk)            | Restore default registry ProgIDs for exe and lnk            | 3              | Medium | Yes              |
| Missing default libraries (Documents, Pictures) | Re-add libraries via shell CLSID registration               | 2              | Low    | No               |
| Double-click not opening folders                | Reset mouse double-click and Explorer related registry keys | 2              | Low    | No               |

## Device Drivers and PnP (4 Issues)

| Issue                                | Fix                                                                     | Priority (1-5) | Risk   | Restart Required |
| ------------------------------------ | ----------------------------------------------------------------------- | -------------- | ------ | ---------------- |
| Unknown devices stuck                | `pnputil /scan-devices`                                                 | 4              | Low    | No               |
| Stale driver packages piling up      | Remove unused `oem*.inf` via `pnputil /delete-driver`                   | 3              | Medium | No               |
| Driver install service stuck         | Restart Plug and Play stack: `PlugPlay`, `DPS`, `Wudfsvc`, `DcomLaunch` | 3              | Medium | Yes              |
| USB selective suspend breaking ports | Disable power save on hubs and restart USB controllers                  | 2              | Low    | No               |


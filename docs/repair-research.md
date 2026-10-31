# Repair: orphaned Windows Installer records

Research and implementation decision, 31 October 2026. The old Repair page said "Repaired 111 leftovers", then Undo failed with
`Access to the registry key 'HKEY_CURRENT_USER\SOFTWARE\Microsoft\Installer\Features\E69F10F05963B914F8D585A3FA8314E7' is denied`,
and installs and uninstalls of the same programs still failed. This page records why, what Windows Installer keeps, and what the repair does now.

## Root cause of the three failures

1. **The repair said it worked and removed nothing in the user's hive.** Windows Installer creates the per-user product and feature keys
   (`HKCU\SOFTWARE\Microsoft\Installer\{Products,Features}\<packed code>`) from its service. Read from a real PC (read-only, `Get-Acl`):

   ```
   O:BA  G:SY  D:P (A;OICI;CCSWRPWPRC;;;WD) (A;OICI;KA;;;SY) (A;OICI;KA;;;BA)
   ```

   The owner is Administrators, the list is protected, Everyone gets read only, and the user's own account is not in it. The old code
   copied the key and called `RegistryKey.DeleteSubKeyTree(name, throwOnMissingSubKey: false)`. For a key this account may not open for
   writing, .NET returns **without an error and without deleting**. The Windows-wide keys went (the helper is elevated) and the user's half stayed, so each program was left
   *half registered*: Windows Installer's source list, features and `HKCU` product record still pointed at the cleared Package Cache. The Python 3.13.16
   installer then found the old 3.13.9 product through its upgrade code and asked for `...\Package Cache\{34E4C3B0-...}v3.13.9150.0\core.msi`.
   This is reproduced in `RegistryFidelityTests.ThePermissionsWindowsInstallerGivesPerUserProductKeysAreRemovedAndRestoredAndAPlainDeleteSilentlyLeavesThem`.
2. **Undo wrote into the key that was never removed.** Restore used `CreateSubKey(sub, writable: true)` and `SetValue`; on the surviving locked key that raises the exact access-denied
   message from the report. It also stopped at the first failure, so the keys already put back stayed, leaving yet another half-registered state. Reproduced in
   `RepairLadderTests.UndoWorksOnAKeyThatAnInstallerRecreatedWithPermissionsThatKeepThisAccountOut`.
3. **Uninstall still failed** because the list entry (the only handle to uninstall) was gone while the program's files and parts of its record were not, and because the old record covered
   only `Products`, `Features` and `UserData\...\Products`. It left `UserData\<sid>\Components`, `UpgradeCodes`, `Patches`, the managed-install keys and any Burn bundle
   registration and dependency records.

Also noted: the screenshots with the Python prompt are time stamped 11:11, before the repair at 11:16, so they show the broken state, not a failure after the repair. The friend's build may also differ from this branch (the rows are shown ticked while their text says the program is still installed).

## What Windows Installer keeps for one product

`P` is the product code packed into 32 characters (`InstallRepair.Pack`). `S` is a user SID, `S-1-5-18` for per-machine installs.

| Location | What is there | Removed with the product |
| --- | --- | --- |
| `HKLM\SOFTWARE\Classes\Installer\Products\P` (with `SourceList`, `Net`, `Media`) | name, package code, source list | whole key |
| `HKLM\SOFTWARE\Classes\Installer\Features\P` | feature to component map | whole key |
| `HKLM\...\CurrentVersion\Installer\UserData\S\Products\P` (`InstallProperties`, `Patches`, `Features`) | `LocalPackage`, install state | whole key |
| `HKLM\...\Installer\UserData\S\Components\C` | one value per client product, named `P` | only the value `P`; the key only when it is empty |
| `HKLM\...\Installer\UserData\S\Patches\X`, `Classes\Installer\Patches\X` | patch registrations | only when no other product wears the patch |
| `HKLM\SOFTWARE\Classes\Installer\UpgradeCodes\U` | one value per product with that upgrade code | only the value `P` |
| `HKLM\...\Installer\Managed\S\Installer\{Products,Features,UpgradeCodes}` | per-user-managed installs | whole key / value |
| `HKCU\SOFTWARE\Microsoft\Installer\{Products,Features,UpgradeCodes,Patches}` and `HKCU\SOFTWARE\Classes\Installer\...` | per-user installs (the key in the error) | whole key / value |
| `...\CurrentVersion\Uninstall\{code}` and `WOW6432Node` of it, in HKLM and HKCU | the entry in Installed apps | whole key, unless the product is registered another way |
| Burn bundle: `...\Uninstall\{bundle}` (`BundleProviderKey`, `BundleCachePath`), `HKLM|HKCU\SOFTWARE\Classes\Installer\Dependencies\{provider}` and `...\Dependencies\*\Dependents\{provider}` | the bundle and what it depends on | entry and records; the provider record stays while another bundle depends on it |

`HKLM\SOFTWARE\Classes\Installer` is a shared key under WOW64 (no redirection), so one path covers 32-bit and 64-bit installers. The `Uninstall` key has the usual `WOW6432Node` copy.
Not touched: `C:\Windows\Installer`, the Package Cache, and any files of the program. Only an empty Package Cache folder may be removed.

## What the established tools do

| Tool | What it does | What was taken from it |
| --- | --- | --- |
| `msizap` ([Msizap Remarks](https://learn.microsoft.com/en-us/previous-versions/windows/it-pro/windows-server-2003/cc736395(v=ws.10))) | deletes `Features`, `Products`, `UpgradeCodes` under `HKCU`/`HKLM`, `Managed\...`, `UserData\...\Components` and `...\Products` and the `Uninstall` key "if there are no longer any installations with the specified product code"; also `InProgress` and rollback data. Microsoft calls it "a last resort" and "a blunt instrument", and says to try `msiexec /fvomus package.msi` first. | the location list, the order of preference (repair first, remove last), the "no longer any installations" guard |
| `msiexec /fvomus` ([REINSTALLMODE](https://learn.microsoft.com/en-us/windows/win32/msi/reinstallmode)) | `v` runs from the source package and re-caches the local package; `o` older/missing files, `m` machine registry, `u` user registry, `s` shortcuts | recovery rung: put the saved copy back from the program's own installer file |
| `MsiSourceListAddSourceEx` ([docs](https://learn.microsoft.com/en-us/windows/win32/api/msi/nf-msi-msisourcelistaddsourceexw)) | adds a source folder; an administrator can only change per-machine products and their own per-user ones | not called directly: `/fvomus <file>` records the file's folder as the source itself |
| Burn ([registration.cpp](https://github.com/wixtoolset/wix/blob/main/src/burn/engine/registration.cpp), [deputil.cpp](https://github.com/wixtoolset/wix/blob/main/src/libs/dutil/WixToolset.DUtil/deputil.cpp)) | bundle entry under `Uninstall\{bundle}` in HKLM or HKCU; `Software\Classes\Installer\Dependencies\<id>` with a `Dependents` subkey | the bundle rule above |
| `winget uninstall --product-code` ([docs](https://learn.microsoft.com/windows/package-manager/winget/uninstall)) | winget can find an installed program by product code; `winget download` has no such option | no automatic download rung (see below) |
| Microsoft Program Install and Uninstall troubleshooter | repairs corrupted registry keys, removes broken entries; closed source, cannot be inspected | none beyond the same goals |

The error codes the recovery rung reads are from [Microsoft's list](https://learn.microsoft.com/en-us/windows/win32/msi/error-codes): 0 success, 3010 and 1641 restart needed, 1618 another install running, 1612 source not available, 1603 install failed.

## The ladder

Per finding, in `core/RepairService.cs`:

1. **Recover** (only when the program is still on the PC): look for the program's own installer file in its source list, the Package Cache (`{code}v*`) and `$PatchCache$`. A candidate is used only if the `ProductCode` in the file
   is the program's and, when Windows Installer registered one, the `PackageCode` is the same build. Then `msiexec /fvomus <file> /qn /norestart` (per-machine products through the helper). Success means exit 0, 3010 or 1641 **and** Windows Installer has a `LocalPackage` that exists afterwards. 3010/1641 are reported as *Fixed after restart*.
2. **Remove the whole record**, in every place above, as one step:
   plan first (read only, in the elevated helper, which can read `UserData`); a plan that could not read any key is never carried out ("removed nothing rather than leave a half-removed record");
   every key is copied in full (values as raw type and bytes, plus its permissions as SDDL) before it is removed; a key that cannot be copied is not removed; if any key fails the ones already removed are put back.
3. **Verify**: every planned key must be gone, every link must be gone, and `MsiQueryProductState` must say the product is unknown (skipped only when the product is also registered another way). If not, the removal is undone and the item is *Could not fix* with the reason.
4. **Report** each item as *Fixed*, *Fixed after restart* or *Could not fix*, with the list of steps tried.

**Finding the program's own installer when its saved copy is gone** (only for a program that is still installed, and only used when the file proves to be that exact build):

1. files already on the PC: the program's source list, `Package Cache\{code}v*`, `$PatchCache$`;
2. folders the person chose with *Locate installer folder* (an `.msi` is checked directly; a setup `.exe` is unpacked with its own `/layout`, but only when Windows reports a valid Authenticode signature and its version information names the program);
3. when allowed on the page (a tick box, on by default), WinGet's copy of the same version: `winget list --name <exact name> --exact` gives the package id and the version WinGet worked out for the installed program (Python's entry says `3.13.16150.0`, WinGet says `3.13.16`; checked on this PC),
   then `winget download --id <id> --version <v>` (WinGet verifies the file against the hash in its manifest) and the same signature check and `/layout`. Whatever comes back is still checked: its `ProductCode` must be the program's and its `PackageCode` the registered one, otherwise it is discarded and the record is removed as before.

**Programs with no Windows Installer record (Inno Setup, NSIS, Burn bundles whose setup program is gone)** have only a list entry whose uninstall command points at nothing:

1. if the program's folder (or the folder the command named) holds an uninstaller (`unins000.exe`, `uninst.exe`, `Uninstall.exe`), the entry's `UninstallString` and `QuietUninstallString` are pointed at it, keeping the switches (`/SILENT`); the previous command is saved in the undo copy;
2. when downloading is allowed and WinGet names the program: for a Burn bundle, the downloaded setup program is run with its own `/repair` (it puts back its Package Cache copy and registration and asks for administrator rights itself); for anything else, `winget install --force` of the same version, which makes Inno and NSIS installers write their uninstaller again;
3. otherwise the entry is removed as before, and the result says that the program's files stay where they are, and where.

After each of these the entry is read again and counts as fixed only when its uninstall command now names a file that exists.

Still not possible: an installer that is not on the PC, not in a folder the person points at and not in WinGet's list (for example a vendor download that WinGet does not carry). The record is then removed so the program can be installed again.

## Permissions, backup and undo

- A backup is a `KeyBackup`: every value as raw type and bytes (REG_NONE, unexpanded REG_EXPAND_SZ and the default value included), every subkey, and the permissions of every key (`GetSecurityDescriptorSddlForm`: owner, group, DACL). Old backups (typed values, no permissions) still restore.
- Removing a key whose permissions refuse: the helper (elevated, with the backup, restore and take-ownership privileges switched on) opens it with `REG_OPTION_BACKUP_RESTORE`, replaces the list for the removal and removes it. Without privileges the owner can still do this; a user hive key owned by Administrators is handed to the helper, which reaches the same hive as `HKU\<sid>`.
- Restoring: values first, children, then the permissions (the original list may forbid the writes), owner first on a best-effort basis (giving the owner back needs the restore privilege).
  A key that exists with permissions that keep this account out is taken over, written, and its saved permissions put back.
- Undo is all or nothing: every key is restored, and if any fails, the keys restored in that attempt are removed again (or their earlier state put back), the repair stays in the Undo tab, and the exact keys and reasons are shown.
- The helper only accepts paths on its allow-list (key and single-value rules), also for reading, and only: remove, restore, plan, repair a program from a file that matches its registered product and package, stop the `msiserver` service. It does not run anything else.

## Stuck installs

`InProgress` is removed only when no `_MSIExecute` mutex is held and no `msiexec` runs in the user's session (unchanged). A stuck **service** (`START_PENDING`/`STOP_PENDING` twice in a row, nothing installing) is a new finding; the fix is `sc stop msiserver`, then `taskkill /F /FI "SERVICES eq msiserver"` if it does not stop, nothing else.
A pending restart is reported (Info, nothing to fix); an item whose repair needs a restart is shown as *Fixed after restart* with a restart note on the page.

## Verified and not verified

Verified here: all of the above against a throwaway registry tree with real permissions (the Python 3.13 case, a machine-wide product with S-1-5-18 and both Uninstall views, shared components and upgrade code that must survive, Burn bundle, ghost, leftover links, locked keys, partial failures) and read-only checks of this PC with `TIDYWINDOW_LIVE_TESTS=1`.
Also verified on this PC, elevated, on throwaway keys: the privileged path (backup, restore and take-ownership privileges) for keys owned by Administrators and by SYSTEM, removed and restored with their owner and permissions; and, read-only, that WinGet matches the installed Python by name and version (`Python.Python.3.13 3.13.16`).
Not verified (needs the friend's laptop or a VM, see `docs/repair-validation.md`): `msiexec /fvomus` on a real Python 3.13.9 whose Package Cache was cleared, the download and `/layout` of a real setup program through the repair (the live test for it exists and is opt-in with `TIDYWINDOW_LIVE_DOWNLOAD=1`, but was not run here), `/repair` of a downloaded Burn setup program, `winget install --force` of an Inno or NSIS program, and that the Python 3.13.16 installer then runs without the `core.msi` prompt.

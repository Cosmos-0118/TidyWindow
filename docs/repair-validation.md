# Validating Repair on a real broken install

The unit tests prove the logic against a throwaway registry. These steps prove it against a real Windows Installer, in a **Windows Sandbox or a VM that can be thrown away**. Do not run them on a PC whose installs matter.

## 1. Build a broken Python the way the friend's laptop got one

Windows Sandbox (`Containers-DisposableClientVM`) or a VM snapshot, signed in as a standard-looking admin account (UAC on).

1. Install **Python 3.13.9** for the current user only (the default "Install for me"): `python-3.13.9-amd64.exe /quiet InstallAllUsers=0`.
2. Break it the way a cleaner or a crash does: delete the installers Windows Installer keeps and the Burn cache, without uninstalling.
   ```powershell
   Remove-Item "$env:LOCALAPPDATA\Package Cache" -Recurse -Force
   # the saved copy of each Python MSI, named by LocalPackage under your SID's UserData (needs an elevated window)
   $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
   Get-ChildItem "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData\$sid\Products" |
     ForEach-Object { Get-ItemProperty "$($_.PSPath)\InstallProperties" } |
     Where-Object DisplayName -like 'Python 3.13.9*' | ForEach-Object { Remove-Item $_.LocalPackage -Force }
   ```
3. Confirm it is broken: install **3.13.16** (`python-3.13.16-amd64.exe /passive`). Expect the Windows Installer dialog asking for `...\Package Cache\{...}v3.13.9150.0\core.msi`, or `0x80070643`.
   Also try **Settings > Installed apps > Python 3.13.9 > Uninstall**; it fails.

## 2. Run the new Repair

1. Start TidyWindow, open **Repair**. Expect findings for the Python 3.13.9 products (`Python 3.13.9 Core Interpreter (64-bit)`, Test Suite, ...) and its bundle.
   Programs still installed are *Review* and unticked: tick them.
2. Press **Fix**, approve the Windows prompt once.
3. Expect, per item, on the *Last repair* card:
   - *Fixed* with "Removed the whole record (N keys, M shared links)" and the steps `Looked for the program's own installer file ... none matches this exact build`;
   - nothing *Could not fix*. If something is, open "What was tried": it names the key and the reason.
4. Check with Windows itself (no TidyWindow):
   ```powershell
   $i = New-Object -ComObject WindowsInstaller.Installer
   $i.ProductsEx('', '', 7) | Where-Object { $_.ProductName -like '*Python 3.13.9*' }   # expect nothing
   Get-ChildItem 'HKCU:\SOFTWARE\Microsoft\Installer\Products' | ForEach-Object { (Get-ItemProperty $_.PSPath).ProductName } | Select-String Python
   ```
5. Run `python-3.13.16-amd64.exe /passive`. Expect **no** `core.msi` prompt and a finished install.
6. If the program's files were left, Uninstall of 3.13.16 must also work.

## 3. Undo

1. **Undo** tab, **Undo** on the repair. Expect "Undone ... N registry keys put back".
2. Compare: the Python 3.13.9 products are listed again (`$i.ProductsEx` above) and `Get-Acl` on `HKCU:\SOFTWARE\Microsoft\Installer\Features\<packed code>` shows owner Administrators, protected, Everyone read only (as before).
3. Reproduce the original report: repair, run the 3.13.16 installer so it recreates keys, then Undo. It must either restore everything or say which keys stopped it, and leave nothing half-restored (compare `reg export` of `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData` before and after a failed undo).

## 4. Recovery rung

1. Install Python 3.13.9 per user, delete only `C:\Windows\Installer\<its msi>` (find it with `$i.ProductsEx` > `InstallProperties` `LocalPackage`), keep `%LOCALAPPDATA%\Package Cache\{code}v...\core.msi`.
2. Repair. Expect *Fixed* with the steps `Found the program's own installer file at ...` and `Repaired it with Windows Installer; it returned 0`, the program still installed, no registry removed, nothing in the Undo tab.

## 4b. Download, locate and uninstaller repair

1. Python 3.13.9 per user as above, then delete the Package Cache **and** `C:\Windows\Installer\<its msi>`. Tick *Get a program's original installer from WinGet* on the page and press Fix. Expect steps `WinGet knows ... as Python.Python.3.13 3.13.9; downloaded that version`, `Found the program's own installer file at ...`, result *Fixed*, program still installed, `Get-WinEvent`/Installed apps show it repairable again.
2. Untick the box, download `python-3.13.9-amd64.exe` yourself into Downloads, use *Locate installer folder...* on Downloads, Fix. Expect `Found it in a folder the person chose`.
3. An Inno Setup program (any, such as Notepad++ portable installer or a game): rename its `unins000.exe` to `unins001.exe`; the Repair page lists it as *Review*; Fix. Expect *Fixed* with the entry pointing at `unins001.exe` and Installed apps > Uninstall working; Undo puts the old command back.
4. The same, with the uninstaller deleted: with the box ticked, expect WinGet to reinstall the same version and the uninstaller to come back; with it unticked, the entry is removed and the result names the folder that stays.

## 4c. Verified on the developer PC (31 Oct 2026)

`scripts/live-repair-check.ps1` installs Python 3.12.10 (Burn bundle with six MSIs) and WinSCP 6.7 (Inno Setup) per-user, breaks them the way a cleaner does, and the built app's Repair page was driven through its UI. The Installer registry was exported first; nothing else was touched, and both apps were uninstalled afterwards with their own uninstallers.

| Broken as | Repair did | Windows' own check afterwards |
|---|---|---|
| WinSCP: `unins000.*` deleted (no Windows Installer record) | no uninstaller in the folder, WinGet matched `WinSCP.WinSCP 6.7`, `winget install --force` of that version put it back | entry's uninstaller exists; `unins000.exe /VERYSILENT` exit 0, files and entry gone |
| Python 3.12.10: bundle cache and all six MSI cache folders deleted | WinGet matched `Python.Python.3.12 3.12.10` by search (the installed entry is not tied to a package by `winget list`), downloaded it, checked its signature and that it is `python-3.12.10-amd64.exe`, put it back in `Package Cache\{id}\` | `python-3.12.10-amd64.exe /uninstall /quiet` exit 0, all six products gone; the real Python 3.13.16 untouched |

Found by doing it, and fixed:

- A bundle whose products are still installed named no folder, so it counted as gone and its record was removed. It now counts as installed when products still list it as a dependent.
- Burn names the bundle by its own id in every `Dependents` list; the plan looked for the provider name, so child links stayed and the provider key was never removed.
- WinGet does not tie a python.org install to its package (`winget list` finds nothing for `Python 3.12.10 (64-bit)`). The package is now found by an exact-name search of the name with its version parts dropped, accepted only if that package has the installed version.
- The setup's `/repair`, run with its saved choices gone, installed the default parts (Documentation, Test Suite) and did not put the cached setup back. Replaced by copying the verified setup into the package cache.
- The signature check asked Windows PowerShell 5.1 with a `PSModulePath` inherited from PowerShell 7, which stops it loading its security module, so every setup looked unsigned. It now starts with a clean module path.
- Python 3.12.8's setup has a **revoked signing certificate**; Repair rightly refused to run it. That release cannot be used as the test app.

Undo of a repair that put a setup program back now takes that file out of the package cache again (listed in the Undo tab as "1 setup program"); a file that cannot be removed leaves the repair in the Undo tab with the reason. Covered by unit tests; not yet run through the app on a real restored setup.

An NSIS program, verified: Streamlink (`winget install Streamlink.Streamlink --scope user`) installs to `%LOCALAPPDATA%\Programs\Streamlink` with its list entry under HKLM, so repairing it goes through the administrator helper. `scripts/live-repair-check.ps1 -Phase Break` deletes its `uninstall.exe`. Repair found it ("Needs admin"), the helper started after the Windows prompt, WinGet reinstalled 8.6.3-1 over it and put `uninstall.exe` back, and `uninstall.exe /S` then removed the program (exit 0). That uninstaller, run without admin rights, left the HKLM list entry behind; Repair lists that as "the program's files are gone too, so this entry only blocks reinstalling".

The leftover Streamlink entry (HKLM, "files are gone too, so this entry only blocks reinstalling") was cleared through Repair's own Fix after the Windows prompt was approved, which verifies the administrator-helper path on a real machine entry. The prompt is on the secure desktop, so it cannot be seen or clicked from an automated session.

A missing `C:\Windows\Installer` copy, verified: Python 3.12.10's Core Interpreter MSI copy (`C:\Windows\Installer\1228769.msi`) was deleted with an elevated command. Repair found it, took the program's own `core.msi` from `Package Cache\{9D09C9EF-...}v3.12.10150.0`, repaired the product with Windows Installer (exit 0) without removing anything, and Windows Installer then had a new `LocalPackage` for it (`122876d.msi`). Python's own uninstaller then ran with exit 0 and removed everything.

Found by doing it, and fixed: the product's list entry names no install folder, so Repair judged the program gone and skipped the restore step, removing the Core product's record instead (the first attempt did exactly that). A product now counts as installed when any file or folder Windows Installer recorded for it is still on the PC.

## 5. Read-only checks on any PC

```powershell
$env:TIDYWINDOW_LIVE_TESTS = '1'
dotnet test tests/TidyWindow.Tests --filter "FullyQualifiedName~LiveRepair" --logger "console;verbosity=detailed"
```
`TIDYWINDOW_ELEVATED_TESTS=1` from an elevated window also runs the privileged path on throwaway keys under `HKCU\Software\TidyWindowTests` (fake product codes only). `TIDYWINDOW_LIVE_DOWNLOAD=1` additionally downloads a setup program into a temp folder and unpacks it; nothing is installed.
The live tests only read: the scan, the removal plan for every finding (never carried out), the capture of a real product key with its permissions, and that installer files are read by their own product code.

## Reporting what happened

Copy the *What was tried* text of any item that is not *Fixed*, plus the entry in the Logs page for the repair (it holds every step), and `reg export "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData\S-1-5-18\Products" before.reg` before and after.

# Breaks real, harmless per-user apps on this PC so Repair can be checked against a real Windows Installer.
#   -Phase Install   backs up the Installer registry, installs Python (Burn + MSI) and WinSCP 6.7 (Inno Setup)
#   -Phase Break     deletes that Python's Package Cache folders and WinSCP's unins* files (what a cleaner or a crash does)
#   -Phase Check     shows what Windows itself sees; run it before and after Repair
#   -Phase Cleanup   removes whatever is left of both apps
# Use a Python release whose setup is still validly signed (Repair refuses to run an unsigned or revoked one): 3.12.8's signing certificate has been revoked.
param([ValidateSet('Install', 'Break', 'Check', 'Cleanup')] [string] $Phase = 'Check',
      [string] $PythonVersion = '3.12.10',
      [string] $Backup = "$env:TEMP\tidywindow-live-backup")

$winscp = Join-Path $env:LOCALAPPDATA 'Programs\WinSCP'
$streamlink = Join-Path $env:LOCALAPPDATA 'Programs\Streamlink'   # NSIS; its list entry is in HKLM, so repairing it needs the administrator helper
$cache = Join-Path $env:LOCALAPPDATA 'Package Cache'
$sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$series = ($PythonVersion -split '\.')[0..1] -join '.'

switch ($Phase) {
    'Install' {
        New-Item -ItemType Directory -Force $Backup | Out-Null
        reg export "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData\$sid" "$Backup\userdata-me.reg" /y
        reg export "HKLM\SOFTWARE\Classes\Installer" "$Backup\hklm-classes-installer.reg" /y
        reg export "HKCU\SOFTWARE\Microsoft\Installer" "$Backup\hkcu-installer.reg" /y
        reg export "HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall" "$Backup\hkcu-uninstall.reg" /y
        winget install --id "Python.Python.$series" --exact --version $PythonVersion --scope user --silent --custom 'Include_launcher=0 Include_test=0 Include_doc=0 Shortcuts=0 PrependPath=0'
        winget install --id WinSCP.WinSCP --exact --version 6.7 --scope user --silent
        winget install --id Streamlink.Streamlink --exact --scope user --silent
        "Backups in $Backup"
    }
    'Break' {
        # only this test install's folders: the bundle and its MSIs ({guid}v<version>...), never other programs' caches
        Get-ChildItem $cache -Directory -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -like "{*}v$PythonVersion*" -or (Get-ChildItem $_.FullName -Filter "python-$PythonVersion-*.exe" -ErrorAction SilentlyContinue) } |
            ForEach-Object { $_.FullName; Remove-Item $_.FullName -Recurse -Force }
        Get-ChildItem $winscp -Filter 'unins*' -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName; Remove-Item $_.FullName -Force }
        Get-ChildItem $streamlink -Filter 'uninstall.exe' -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName; Remove-Item $_.FullName -Force }
    }
    'Check' {
        $i = New-Object -ComObject WindowsInstaller.Installer
        "-- Windows Installer products named Python ${series}:"
        $i.ProductsEx('', '', 7) | Where-Object { $_.InstallProperty('ProductName') -like "Python $series*" } | ForEach-Object { $_.InstallProperty('ProductName') }
        '-- Add/Remove Programs entries:'
        foreach ($root in 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall', 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall') {
            Get-ChildItem $root -ErrorAction SilentlyContinue | ForEach-Object { Get-ItemProperty $_.PSPath } |
                Where-Object { $_.DisplayName -like "Python $series*" -or $_.DisplayName -like 'WinSCP*' -or $_.DisplayName -like 'Streamlink*' } |
                ForEach-Object { '{0} | {1}' -f $_.DisplayName, $_.UninstallString }
        }
        '-- WinSCP uninstaller present: ' + [bool](Get-ChildItem $winscp -Filter 'unins*.exe' -ErrorAction SilentlyContinue)
        '-- Streamlink uninstaller present: ' + (Test-Path "$streamlink\uninstall.exe")
        '-- Python files present: '+ (Test-Path "$env:LOCALAPPDATA\Programs\Python\Python$($series -replace '\.','')\python.exe")
    }
    'Cleanup' {
        winget uninstall --id "Python.Python.$series" --exact --silent
        winget uninstall --id WinSCP.WinSCP --exact --silent
        winget uninstall --id Streamlink.Streamlink --exact --silent
        Remove-Item "$env:LOCALAPPDATA\Programs\Python\Python$($series -replace '\.','')" -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item $winscp -Recurse -Force -ErrorAction SilentlyContinue
    }
}

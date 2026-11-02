    # Appx registration is available in Windows PowerShell, not all PowerShell 7 hosts. Imported by path: with administrator rights nothing is loaded by name.
    Import-Module "$PSHOME\Modules\Appx\Appx.psd1"
    $package = Get-AppxPackage -Name Microsoft.DesktopAppInstaller -ErrorAction SilentlyContinue | Sort-Object { [version]$_.Version } -Descending | Select-Object -First 1
    if ($problem -eq 'Source') {
        # Runs with administrator rights, so WinGet is the one inside the App Installer package under Program Files, which only Windows writes:
        # never the alias in a profile or whatever the unelevated app found on a PATH, and resolved for the account that approved the prompt.
        $apps = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'WindowsApps'
        $winget = if ($package) { Join-Path $package.InstallLocation 'winget.exe' } else { '' }
        if (-not $winget -or !(Test-Path -LiteralPath $winget) -or -not $winget.StartsWith($apps + '\', 'OrdinalIgnoreCase')) { throw "WinGet's App Installer package was not found under $apps for this account, so its source was left alone." }
        & $winget source export --name winget | Set-Content -LiteralPath (Join-Path $job 'winget-source.json') -Encoding UTF8
        & $winget source update --name winget --accept-source-agreements --disable-interactivity
        if ($LASTEXITCODE -ne 0) {
            # Target only the built-in source. Custom and organizational sources are retained.
            & $winget source reset --name winget --force --disable-interactivity
            Assert-Native 'Refreshing the WinGet community source'
        }
    } else {
        if ($package) {
            $manifest = Join-Path $package.InstallLocation 'AppxManifest.xml'
            if (Test-Path -LiteralPath $manifest) {
                try { Add-AppxPackage -Register $manifest -DisableDevelopmentMode -ErrorAction Stop } catch { Write-Output 'Registration alone was insufficient; using Microsoft recovery.' }
            }
        }
        $module = Get-Module -ListAvailable -Name Microsoft.WinGet.Client | Sort-Object Version -Descending | Select-Object -First 1
        if (!$module) {
            Install-PackageProvider -Name NuGet -MinimumVersion 2.8.5.201 -Force -Scope CurrentUser | Out-Null
            Install-Module -Name Microsoft.WinGet.Client -Repository PSGallery -Scope CurrentUser -Force -AllowClobber -Confirm:$false
        }
        Import-Module Microsoft.WinGet.Client -ErrorAction Stop
        Repair-WinGetPackageManager -Latest -Force -ErrorAction Stop
    }

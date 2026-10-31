    if ($problem -eq 'Source' -and $executable) {
        & $executable source export --name winget | Set-Content -LiteralPath (Join-Path $job 'winget-source.json') -Encoding UTF8
        & $executable source update --name winget --accept-source-agreements --disable-interactivity
        if ($LASTEXITCODE -ne 0) {
            # Target only the built-in source. Custom and organizational sources are retained.
            & $executable source reset --name winget --force --disable-interactivity
            Assert-Native 'Refreshing the WinGet community source'
        }
    } else {
        # Appx registration is available in Windows PowerShell, not all PowerShell 7 hosts.
        $package = Get-AppxPackage -Name Microsoft.DesktopAppInstaller -ErrorAction SilentlyContinue
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

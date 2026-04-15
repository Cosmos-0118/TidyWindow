param(
    [switch] $SkipIconCacheRebuild,
    [switch] $SkipStartMenuRepair,
    [switch] $SkipTaskbarReset,
    [switch] $SkipUwpReregistration,
    [switch] $DryRun,
    [string] $ResultPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ── Module bootstrap ──────────────────────────────────────────────────
$callerModulePath = $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($callerModulePath)) { $callerModulePath = $PSCommandPath }
$scriptDirectory = Split-Path -Parent $callerModulePath
if ([string]::IsNullOrWhiteSpace($scriptDirectory)) { $scriptDirectory = (Get-Location).Path }
$modulePath = [System.IO.Path]::GetFullPath((Join-Path $scriptDirectory '..\modules\TidyWindow.Automation\TidyWindow.Automation.psm1'))
if (-not (Test-Path $modulePath)) { throw "Automation module not found at '$modulePath'." }
Import-Module $modulePath -Force

# ── Result tracking ───────────────────────────────────────────────────
$script:TidyOutputLines  = [System.Collections.Generic.List[string]]::new()
$script:TidyErrorLines   = [System.Collections.Generic.List[string]]::new()
$script:OperationSucceeded = $true
$script:UsingResultFile  = -not [string]::IsNullOrWhiteSpace($ResultPath)
$script:DryRunMode       = $DryRun.IsPresent
if ($script:UsingResultFile) { $ResultPath = [System.IO.Path]::GetFullPath($ResultPath) }

function Write-TidyOutput { param([Parameter(Mandatory)][object]$Message)
    $t = Convert-TidyLogMessage -InputObject $Message; if ([string]::IsNullOrWhiteSpace($t)) { return }
    [void]$script:TidyOutputLines.Add($t); Write-TidyLog -Level Information -Message $t
}
function Write-TidyError { param([Parameter(Mandatory)][object]$Message)
    $t = Convert-TidyLogMessage -InputObject $Message; if ([string]::IsNullOrWhiteSpace($t)) { return }
    [void]$script:TidyErrorLines.Add($t); TidyWindow.Automation\Write-TidyError -Message $t
}
function Save-TidyResult {
    if (-not $script:UsingResultFile) { return }
    $payload = [pscustomobject]@{
        Success = $script:OperationSucceeded -and ($script:TidyErrorLines.Count -eq 0)
        Output  = $script:TidyOutputLines
        Errors  = $script:TidyErrorLines
    }
    Set-Content -Path $ResultPath -Value ($payload | ConvertTo-Json -Depth 5) -Encoding UTF8
}
function Test-TidyAdmin {
    [bool](New-Object System.Security.Principal.WindowsPrincipal(
        [System.Security.Principal.WindowsIdentity]::GetCurrent()
    )).IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Invoke-Step {
    param(
        [Parameter(Mandatory)][string]      $Name,
        [Parameter(Mandatory)][scriptblock] $Action,
        [switch] $Critical
    )
    if ($script:DryRunMode) {
        Write-TidyOutput -Message "[DryRun] Would run: $Name"
        return
    }
    Write-TidyOutput -Message "-> $Name"
    try {
        & $Action
        Write-TidyOutput -Message "  OK: $Name succeeded."
    }
    catch {
        $msg = $_.Exception.Message
        if ($Critical) {
            Write-TidyError -Message "  FAIL: $Name (critical): $msg"
            $script:OperationSucceeded = $false
            throw
        }
        Write-TidyError -Message "  FAIL: $Name: $msg"
        $script:OperationSucceeded = $false
    }
}

# ══════════════════════════════════════════════════════════════════════
#  MAIN
# ══════════════════════════════════════════════════════════════════════
try {
    if (-not (Test-TidyAdmin)) {
        throw 'Shell and UI repair requires an elevated PowerShell session.'
    }

    $backupDir = Get-TidyBackupDirectory -FeatureName 'ShellRepair'
    Write-TidyOutput -Message "Backup directory: $backupDir"
    Write-TidyOutput -Message 'Starting shell and UI repair.'

    # ── 1. Rebuild icon cache ─────────────────────────────────────────
    if (-not $SkipIconCacheRebuild.IsPresent) {
        Invoke-Step -Name 'Rebuild icon cache' -Action {
            # Icon cache files live in %LOCALAPPDATA%\Microsoft\Windows\Explorer.
            $cacheDir = Join-Path $env:LOCALAPPDATA 'Microsoft\Windows\Explorer'
            if (-not (Test-Path -LiteralPath $cacheDir)) {
                Write-TidyOutput -Message '  Icon cache directory not found. Skipped.'
                return
            }

            $cacheFiles = @(Get-ChildItem -LiteralPath $cacheDir -Filter 'iconcache*' -File -Force -ErrorAction SilentlyContinue)
            $thumbFiles = @(Get-ChildItem -LiteralPath $cacheDir -Filter 'thumbcache*' -File -Force -ErrorAction SilentlyContinue)
            $allCaches = $cacheFiles + $thumbFiles

            if ($allCaches.Count -eq 0) {
                Write-TidyOutput -Message '  No icon/thumbnail cache files found.'
                return
            }

            # Stop Explorer to release locks on cache files.
            $explorerWasRunning = $null -ne (Get-Process -Name explorer -ErrorAction SilentlyContinue)

            # We must handle explorer restart in the finally block for this step.
            $explorerStopped = $false
            try {
                if ($explorerWasRunning) {
                    $procs = @(Get-Process -Name explorer -ErrorAction SilentlyContinue)
                    foreach ($p in $procs) {
                        try { $p.CloseMainWindow() | Out-Null; $p.WaitForExit(3000) | Out-Null } catch {}
                    }
                    # Force-close any stragglers.
                    $remaining = @(Get-Process -Name explorer -ErrorAction SilentlyContinue)
                    foreach ($p in $remaining) {
                        Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
                    }
                    $explorerStopped = $true
                    Start-Sleep -Milliseconds 500
                }

                $removed = 0
                foreach ($file in $allCaches) {
                    try {
                        Remove-Item -LiteralPath $file.FullName -Force -ErrorAction Stop
                        $removed++
                    }
                    catch {
                        Write-TidyLog -Level Warning -Message "  Could not delete '$($file.Name)': locked or protected."
                    }
                }
                Write-TidyOutput -Message "  Removed $removed / $($allCaches.Count) cache file(s)."
            }
            finally {
                # Always restart Explorer if we stopped it.
                if ($explorerStopped) {
                    Start-Process explorer.exe
                    $waited = 0
                    while ($waited -lt 8) {
                        Start-Sleep -Seconds 1; $waited++
                        if (Get-Process -Name explorer -ErrorAction SilentlyContinue) { break }
                    }
                    if (-not (Get-Process -Name explorer -ErrorAction SilentlyContinue)) {
                        Start-Process explorer.exe
                    }
                }
            }
        }
    }
    else { Write-TidyOutput -Message 'Icon cache rebuild skipped.' }

    # ── 2. Start menu repair ──────────────────────────────────────────
    if (-not $SkipStartMenuRepair.IsPresent) {
        Invoke-Step -Name 'Repair Start menu tile database' -Action {
            # Re-register the Start menu experience host.
            $startPkg = Get-AppxPackage -Name 'Microsoft.Windows.StartMenuExperienceHost' -ErrorAction SilentlyContinue
            if ($startPkg) {
                Add-AppxPackage -DisableDevelopmentMode -Register "$($startPkg.InstallLocation)\AppxManifest.xml" -ErrorAction Stop
                Write-TidyOutput -Message '  StartMenuExperienceHost re-registered.'
            }
            else {
                Write-TidyOutput -Message '  StartMenuExperienceHost package not found. Trying ShellExperienceHost.'
                $shellPkg = Get-AppxPackage -Name 'Microsoft.Windows.ShellExperienceHost' -ErrorAction SilentlyContinue
                if ($shellPkg) {
                    Add-AppxPackage -DisableDevelopmentMode -Register "$($shellPkg.InstallLocation)\AppxManifest.xml" -ErrorAction Stop
                    Write-TidyOutput -Message '  ShellExperienceHost re-registered.'
                }
            }
        }

        Invoke-Step -Name 'Restart Start menu process' -Action {
            # Graceful restart of StartMenuExperienceHost.
            $proc = Get-Process -Name 'StartMenuExperienceHost' -ErrorAction SilentlyContinue
            if ($proc) {
                try { $proc.CloseMainWindow() | Out-Null; $proc.WaitForExit(3000) | Out-Null } catch {}
                # Windows will auto-restart this process.
                Start-Sleep -Seconds 2
                if (-not (Get-Process -Name 'StartMenuExperienceHost' -ErrorAction SilentlyContinue)) {
                    # Force Windows to recreate it by triggering the Start button.
                    Start-Process 'explorer.exe' -ArgumentList 'shell:StartMenuFolder' -ErrorAction SilentlyContinue
                }
            }
        }
    }
    else { Write-TidyOutput -Message 'Start menu repair skipped.' }

    # ── 3. Taskbar reset ──────────────────────────────────────────────
    if (-not $SkipTaskbarReset.IsPresent) {
        Invoke-Step -Name 'Reset taskbar notification area' -Action {
            # Backup the notification area icon settings before clearing.
            $notifyKey = 'HKCU:\Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\TrayNotify'
            if (Test-Path -LiteralPath $notifyKey) {
                Backup-TidyRegistryKey -KeyPath $notifyKey -BackupDirectory $backupDir -Label 'TrayNotify'

                # Remove IconStreams and PastIconsStream to reset the notification area.
                Remove-ItemProperty -Path $notifyKey -Name 'IconStreams' -ErrorAction SilentlyContinue
                Remove-ItemProperty -Path $notifyKey -Name 'PastIconsStream' -ErrorAction SilentlyContinue
                Write-TidyOutput -Message '  Notification area stream caches cleared.'
            }
        }

        Invoke-Step -Name 'Repair taskbar auto-hide and alignment' -Action {
            # Verify taskbar settings registry keys are valid.
            $tbKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\StuckRects3'
            if (Test-Path -LiteralPath $tbKey) {
                Backup-TidyRegistryKey -KeyPath $tbKey -BackupDirectory $backupDir -Label 'StuckRects3'
                $settings = Get-ItemProperty -Path $tbKey -Name 'Settings' -ErrorAction SilentlyContinue
                if ($settings -and $settings.Settings -is [byte[]]) {
                    $bytes = $settings.Settings
                    if ($bytes.Length -ge 9) {
                        # Byte [8] controls auto-hide (0x03 = auto-hide on, 0x02 = off).
                        # We don't change user preference, just report it.
                        $autoHide = ($bytes[8] -band 0x01) -ne 0
                        Write-TidyOutput -Message "  Taskbar auto-hide: $($autoHide ? 'enabled' : 'disabled')"
                    }
                }
            }
        }
    }
    else { Write-TidyOutput -Message 'Taskbar reset skipped.' }

    # ── 4. UWP app reregistration ─────────────────────────────────────
    if (-not $SkipUwpReregistration.IsPresent) {
        Invoke-Step -Name 'Re-register broken UWP/MSIX packages' -Action {
            # Only re-register packages that are in a broken state, not all packages.
            $broken = @(Get-AppxPackage -ErrorAction SilentlyContinue |
                        Where-Object { $_.Status -ne 'Ok' -or -not (Test-Path -LiteralPath $_.InstallLocation -ErrorAction SilentlyContinue) })

            if ($broken.Count -eq 0) {
                Write-TidyOutput -Message '  All UWP packages are healthy.'
                return
            }

            Write-TidyOutput -Message "  Found $($broken.Count) broken package(s). Re-registering..."
            $fixed = 0
            foreach ($pkg in $broken) {
                $manifest = Join-Path $pkg.InstallLocation 'AppxManifest.xml'
                if (-not (Test-Path -LiteralPath $manifest)) {
                    Write-TidyLog -Level Warning -Message "  Manifest missing for '$($pkg.Name)'. Cannot re-register."
                    continue
                }
                try {
                    Add-AppxPackage -DisableDevelopmentMode -Register $manifest -ErrorAction Stop
                    $fixed++
                }
                catch {
                    Write-TidyLog -Level Warning -Message "  Re-register failed for '$($pkg.Name)': $($_.Exception.Message)"
                }
            }
            Write-TidyOutput -Message "  Re-registered $fixed / $($broken.Count) package(s)."
        }
    }
    else { Write-TidyOutput -Message 'UWP reregistration skipped.' }

    # ── 5. Restart Explorer (applies all shell changes) ───────────────
    Invoke-Step -Name 'Restart Explorer to apply changes' -Action {
        $procs = @(Get-Process -Name explorer -ErrorAction SilentlyContinue)
        if ($procs.Count -eq 0) {
            Start-Process explorer.exe
            return
        }

        foreach ($p in $procs) {
            try { $p.CloseMainWindow() | Out-Null; $p.WaitForExit(5000) | Out-Null } catch {}
        }
        $remaining = @(Get-Process -Name explorer -ErrorAction SilentlyContinue)
        foreach ($p in $remaining) {
            Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
        }

        Start-Sleep -Milliseconds 500
        Start-Process explorer.exe

        $waited = 0
        while ($waited -lt 10) {
            Start-Sleep -Seconds 1; $waited++
            if (Get-Process -Name explorer -ErrorAction SilentlyContinue) { break }
        }
        if (-not (Get-Process -Name explorer -ErrorAction SilentlyContinue)) {
            Start-Process explorer.exe
            Write-TidyError -Message '  Explorer did not restart within 10 seconds. Emergency start issued.'
        }
    }

    Write-TidyOutput -Message ''
    Write-TidyOutput -Message 'Shell and UI repair completed.'
    Write-TidyOutput -Message "Registry backups saved to: $backupDir"
}
catch {
    $script:OperationSucceeded = $false
    Write-TidyError -Message "Shell repair failed: $($_.Exception.Message)"
}
finally {
    Save-TidyResult
}

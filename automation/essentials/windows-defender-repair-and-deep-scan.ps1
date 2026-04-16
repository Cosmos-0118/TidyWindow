param(
    [switch] $SkipServiceRepair,
    [switch] $SkipDefinitionUpdate,
    [switch] $SkipScan,
    [ValidateSet('Quick', 'Full')]
    [string] $ScanType = 'Quick',
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
        Write-TidyError -Message "  FAIL: ${Name}: $msg"
        $script:OperationSucceeded = $false
    }
}

# ══════════════════════════════════════════════════════════════════════
#  MAIN
# ══════════════════════════════════════════════════════════════════════
try {
    if (-not (Test-TidyAdmin)) {
        throw 'Windows Defender repair requires an elevated PowerShell session.'
    }

    $backupDir = Get-TidyBackupDirectory -FeatureName 'DefenderRepair'
    Write-TidyOutput -Message "Backup directory: $backupDir"
    Write-TidyOutput -Message 'Starting Windows Defender repair and scan.'

    # ── Check if Defender is available ────────────────────────────────
    $defenderAvailable = $null -ne (Get-Command Get-MpComputerStatus -ErrorAction SilentlyContinue)
    if (-not $defenderAvailable) {
        Write-TidyError -Message 'Microsoft Defender cmdlets not available. Is a third-party AV installed?'
        throw 'Defender cmdlets unavailable.'
    }

    $status = Get-MpComputerStatus -ErrorAction SilentlyContinue
    if (-not $status) {
        Write-TidyError -Message 'Could not query Defender status.'
        throw 'Defender status unavailable.'
    }

    Write-TidyOutput -Message "  Antivirus enabled:   $($status.AntivirusEnabled)"
    Write-TidyOutput -Message "  Real-time protection: $($status.RealTimeProtectionEnabled)"
    Write-TidyOutput -Message "  Definitions age:     $($status.AntivirusSignatureAge) day(s)"
    Write-TidyOutput -Message "  Engine version:      $($status.AMEngineVersion)"

    # ── 1. Defender service repair ────────────────────────────────────
    if (-not $SkipServiceRepair.IsPresent) {
        Invoke-Step -Name 'Verify WinDefend service' -Action {
            $svc = Get-Service -Name 'WinDefend' -ErrorAction SilentlyContinue
            if (-not $svc) {
                Write-TidyError -Message '  WinDefend service not found.'
                return
            }

            if ($svc.Status -ne 'Running') {
                Write-TidyOutput -Message '  WinDefend is not running. Attempting start.'
                # WinDefend is a protected service; use sc.exe to start it.
                $r = Invoke-TidyNativeCommand -FilePath 'sc.exe' -Arguments 'start WinDefend' -TimeoutSeconds 30
                if (-not $r.Success) {
                    Write-TidyError -Message "  Could not start WinDefend: $($r.Error)"
                }
                else {
                    Wait-TidyServiceStatus -ServiceName 'WinDefend' -TargetStatus 'Running' -TimeoutSeconds 30
                    Write-TidyOutput -Message '  WinDefend started.'
                }
            }
            else {
                Write-TidyOutput -Message '  WinDefend is running.'
            }
        }

        Invoke-Step -Name 'Verify Defender supporting services' -Action {
            $supportServices = @('SecurityHealthService', 'Sense')
            foreach ($name in $supportServices) {
                $svc = Get-Service -Name $name -ErrorAction SilentlyContinue
                if (-not $svc) { continue }
                if ($svc.Status -ne 'Running' -and $svc.StartType -ne 'Disabled') {
                    try {
                        Start-Service -Name $name -ErrorAction Stop
                        Write-TidyOutput -Message "  Started $name."
                    }
                    catch {
                        Write-TidyLog -Level Warning -Message "  Could not start '$name': $($_.Exception.Message)"
                    }
                }
            }
        }

        Invoke-Step -Name 'Reset Defender preferences if Group Policy allows' -Action {
            # Check if Defender is GP-managed.
            $gpKey = 'Registry::HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows Defender'
            if (Test-TidyGroupPolicyManaged -RegistryPath $gpKey) {
                Write-TidyOutput -Message '  Defender is Group Policy managed. Skipping preference reset.'
                return
            }

            # Reset tampered exclusion paths (common malware tactic).
            $prefs = Get-MpPreference -ErrorAction SilentlyContinue
            if ($prefs -and $prefs.ExclusionPath -and $prefs.ExclusionPath.Count -gt 0) {
                Backup-TidyRegistryKey -KeyPath 'HKLM:\SOFTWARE\Microsoft\Windows Defender\Exclusions\Paths' -BackupDirectory $backupDir -Label 'DefenderExclusions'
                Write-TidyOutput -Message "  Current exclusion paths: $($prefs.ExclusionPath.Count)"
                # We report exclusions but do NOT remove them automatically.
                # Removing user-configured exclusions could break legitimate workflows.
                Write-TidyOutput -Message '  Review exclusions manually. Automated removal skipped for safety.'
            }
        }
    }
    else { Write-TidyOutput -Message 'Defender service repair skipped.' }

    # ── 2. Definition update ──────────────────────────────────────────
    if (-not $SkipDefinitionUpdate.IsPresent) {
        Invoke-Step -Name 'Update Defender definitions' -Action {
            $age = $status.AntivirusSignatureAge
            if ($age -le 1) {
                Write-TidyOutput -Message "  Definitions are current ($age day(s) old)."
                return
            }

            Write-TidyOutput -Message "  Definitions are $age day(s) old. Updating..."
            try {
                Update-MpSignature -ErrorAction Stop
                Write-TidyOutput -Message '  Definitions updated successfully.'
            }
            catch {
                # Fallback: try MpCmdRun.exe.
                $mpCmd = Join-Path $env:ProgramFiles 'Windows Defender\MpCmdRun.exe'
                if (-not (Test-Path -LiteralPath $mpCmd)) {
                    $mpCmd = Join-Path $env:ProgramW6432 'Windows Defender\MpCmdRun.exe'
                }

                if (Test-Path -LiteralPath $mpCmd) {
                    Write-TidyOutput -Message '  Falling back to MpCmdRun.exe for definition update.'
                    $r = Invoke-TidyNativeCommand -FilePath $mpCmd -Arguments '-SignatureUpdate' -TimeoutSeconds 120
                    if (-not $r.Success) {
                        throw "Definition update failed via MpCmdRun: exit code $($r.ExitCode)"
                    }
                }
                else {
                    throw "Definition update failed and MpCmdRun.exe not found: $($_.Exception.Message)"
                }
            }
        }
    }
    else { Write-TidyOutput -Message 'Definition update skipped.' }

    # ── 3. Scan ───────────────────────────────────────────────────────
    if (-not $SkipScan.IsPresent) {
        Invoke-Step -Name "Run $ScanType scan" -Action {
            $scanTypeVal = switch ($ScanType) {
                'Quick' { 1 }
                'Full'  { 2 }
            }

            Write-TidyOutput -Message "  Starting $ScanType scan (this may take a while)..."
            try {
                Start-MpScan -ScanType $ScanType -ErrorAction Stop
            }
            catch {
                # Fallback to MpCmdRun.exe.
                $mpCmd = Join-Path $env:ProgramFiles 'Windows Defender\MpCmdRun.exe'
                if (-not (Test-Path -LiteralPath $mpCmd)) {
                    $mpCmd = Join-Path $env:ProgramW6432 'Windows Defender\MpCmdRun.exe'
                }

                if (Test-Path -LiteralPath $mpCmd) {
                    Write-TidyOutput -Message '  Falling back to MpCmdRun.exe for scan.'
                    $r = Invoke-TidyNativeCommand -FilePath $mpCmd -Arguments "-Scan -ScanType $scanTypeVal" -TimeoutSeconds 600
                    if (-not $r.Success -and -not $r.TimedOut) {
                        throw "Scan failed via MpCmdRun: exit code $($r.ExitCode)"
                    }
                    if ($r.TimedOut) {
                        Write-TidyOutput -Message '  Scan is running in background (timed out waiting for completion).'
                    }
                }
                else {
                    throw "Scan failed: $($_.Exception.Message)"
                }
            }

            # Report threat detection results.
            try {
                $threats = Get-MpThreatDetection -ErrorAction SilentlyContinue
                if ($threats) {
                    $recentThreats = @($threats | Where-Object { $_.InitialDetectionTime -gt (Get-Date).AddHours(-1) })
                    if ($recentThreats.Count -gt 0) {
                        Write-TidyOutput -Message "  Threats detected in last hour: $($recentThreats.Count)"
                        foreach ($t in $recentThreats | Select-Object -First 10) {
                            Write-TidyOutput -Message "    - $($t.ThreatName) [$($t.ActionSuccess ? 'remediated' : 'action required')]"
                        }
                    }
                    else {
                        Write-TidyOutput -Message '  No threats detected.'
                    }
                }
            }
            catch {
                Write-TidyLog -Level Warning -Message "Could not query threat detections: $($_.Exception.Message)"
            }
        }
    }
    else { Write-TidyOutput -Message 'Scan skipped.' }

    # ── 4. Re-register Defender WMI provider ──────────────────────────
    Invoke-Step -Name 'Re-register Defender WMI provider' -Action {
        $mofPath = Join-Path $env:ProgramFiles 'Windows Defender\ProtectionManagement.mof'
        if (-not (Test-Path -LiteralPath $mofPath)) {
            $mofPath = Join-Path $env:ProgramW6432 'Windows Defender\ProtectionManagement.mof'
        }
        if (Test-Path -LiteralPath $mofPath) {
            $r = Invoke-TidyNativeCommand -FilePath 'mofcomp.exe' -Arguments "`"$mofPath`"" -TimeoutSeconds 30
            if ($r.Success) {
                Write-TidyOutput -Message '  WMI provider re-registered.'
            }
            else {
                Write-TidyLog -Level Warning -Message "  mofcomp returned exit code $($r.ExitCode)."
            }
        }
        else {
            Write-TidyOutput -Message '  ProtectionManagement.mof not found. Skipped.'
        }
    }

    Write-TidyOutput -Message ''
    Write-TidyOutput -Message 'Windows Defender repair and scan completed.'
    Write-TidyOutput -Message "Registry backups saved to: $backupDir"
}
catch {
    $script:OperationSucceeded = $false
    Write-TidyError -Message "Defender repair failed: $($_.Exception.Message)"
}
finally {
    Save-TidyResult
}

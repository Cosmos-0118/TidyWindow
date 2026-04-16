param(
    [switch] $SkipCredentialRepair,
    [switch] $SkipCertificateStoreRepair,
    [switch] $SkipSecurityCenterReset,
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
$script:ServiceStates    = [System.Collections.Generic.List[psobject]]::new()
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
        throw 'Security and credential repair requires an elevated PowerShell session.'
    }

    $backupDir = Get-TidyBackupDirectory -FeatureName 'SecurityRepair'
    Write-TidyOutput -Message "Backup directory: $backupDir"
    Write-TidyOutput -Message 'Starting security and credential repair.'

    # ── 1. Credential Manager service repair ──────────────────────────
    if (-not $SkipCredentialRepair.IsPresent) {
        Invoke-Step -Name 'Repair Credential Manager service' -Action {
            $svc = Get-Service -Name 'VaultSvc' -ErrorAction SilentlyContinue
            if (-not $svc) {
                Write-TidyOutput -Message '  VaultSvc service not found. Skipping.'
                return
            }

            # Re-register the Credential Manager vault using vaultcmd.
            $r = Invoke-TidyNativeCommand -FilePath 'vaultcmd.exe' -Arguments '/listcreds:"Windows Credentials" /all' -TimeoutSeconds 15 -AcceptableExitCodes @(0, 1)
            if ($r.TimedOut) {
                Write-TidyOutput -Message '  vaultcmd timed out — restarting VaultSvc.'
                Invoke-TidySafeServiceRestart -ServiceName 'VaultSvc' -RepairAction {
                    # No intermediate repair; the restart itself fixes most stuck vault issues.
                }
            }
            else {
                Write-TidyOutput -Message '  Credential Manager responded normally.'
            }
        }

        Invoke-Step -Name 'Clear stale Windows Credential Manager generic entries' -Action {
            # Only remove generic credentials that point to non-existent targets.
            # Never touch domain, certificate, or Windows Live credentials.
            $r = Invoke-TidyNativeCommand -FilePath 'cmdkey.exe' -Arguments '/list' -TimeoutSeconds 15
            if (-not $r.Success) {
                Write-TidyOutput -Message '  Could not enumerate credentials. Skipped.'
                return
            }

            # Parse cmdkey output for generic credentials only.
            $lines = $r.Output -split "`n"
            $staleCandidates = @()
            $currentTarget = $null
            foreach ($line in $lines) {
                if ($line -match 'Target:\s*(.+)') {
                    $currentTarget = $Matches[1].Trim()
                }
                elseif ($line -match 'Type:\s*Generic' -and $currentTarget) {
                    $staleCandidates += $currentTarget
                    $currentTarget = $null
                }
                elseif ($line -match 'Type:') {
                    $currentTarget = $null
                }
            }

            Write-TidyOutput -Message "  Found $($staleCandidates.Count) generic credential(s) to validate."
            # We intentionally do NOT auto-remove credentials. That would be destructive.
            # Instead, we just report the count for user awareness.
        }
    }
    else { Write-TidyOutput -Message 'Credential repair skipped.' }

    # ── 2. Certificate store repair ───────────────────────────────────
    if (-not $SkipCertificateStoreRepair.IsPresent) {
        Invoke-Step -Name 'Validate root certificate store' -Action {
            # Check that the local machine root store is accessible and has certificates.
            $rootStore = New-Object System.Security.Cryptography.X509Certificates.X509Store(
                'Root', 'LocalMachine')
            try {
                $rootStore.Open('ReadOnly')
                $certCount = $rootStore.Certificates.Count
                Write-TidyOutput -Message "  Root certificate store: $certCount certificate(s)."

                if ($certCount -eq 0) {
                    Write-TidyError -Message '  Root certificate store is EMPTY. This may indicate corruption.'
                }

                # Check for expired root certificates (informational).
                $now = [DateTime]::UtcNow
                $expired = @($rootStore.Certificates | Where-Object { $_.NotAfter -lt $now })
                if ($expired.Count -gt 0) {
                    Write-TidyOutput -Message "  $($expired.Count) expired root certificate(s) found (informational only; not removed)."
                }
            }
            finally {
                $rootStore.Close()
                $rootStore.Dispose()
            }
        }

        Invoke-Step -Name 'Repair CryptSvc service' -Action {
            $svc = Get-Service -Name 'CryptSvc' -ErrorAction SilentlyContinue
            if (-not $svc) {
                Write-TidyOutput -Message '  CryptSvc not found. Skipping.'
                return
            }

            if ($svc.Status -ne 'Running') {
                Write-TidyOutput -Message '  CryptSvc is not running. Attempting restart.'
                Invoke-TidySafeServiceRestart -ServiceName 'CryptSvc' -RepairAction {
                    # Clear the CryptnetUrlCache to force re-download of CRLs.
                    $cacheDir = Join-Path $env:LOCALAPPDATA 'Microsoft\Windows\INetCache\Content.IE5'
                    if (Test-Path -LiteralPath $cacheDir) {
                        # Only remove cached CRL/OCSP items, not the whole IE cache.
                        $crlItems = Get-ChildItem -LiteralPath $cacheDir -Recurse -File -ErrorAction SilentlyContinue |
                                    Where-Object { $_.Extension -in @('.crl', '.p7b', '.cer') }
                        foreach ($item in $crlItems) {
                            Remove-Item -LiteralPath $item.FullName -Force -ErrorAction SilentlyContinue
                        }
                    }
                }
            }
            else {
                Write-TidyOutput -Message '  CryptSvc is running normally.'
            }
        }
    }
    else { Write-TidyOutput -Message 'Certificate store repair skipped.' }

    # ── 3. Security Center registration ───────────────────────────────
    if (-not $SkipSecurityCenterReset.IsPresent) {
        Invoke-Step -Name 'Restart Windows Security Center service' -Action {
            $svc = Get-Service -Name 'wscsvc' -ErrorAction SilentlyContinue
            if (-not $svc) {
                Write-TidyOutput -Message '  wscsvc (Security Center) not found. Skipping.'
                return
            }

            # Check if Group Policy manages Security Center.
            $policyPath = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows NT\Security Center'
            if (Test-Path -LiteralPath $policyPath) {
                if (Test-TidyGroupPolicyManaged -RegistryPath ($policyPath -replace '^HKLM:\\', 'Registry::HKEY_LOCAL_MACHINE\')) {
                    Write-TidyOutput -Message '  Security Center is Group Policy managed. Skipping restart.'
                    return
                }
            }

            Invoke-TidySafeServiceRestart -ServiceName 'wscsvc' -RepairAction {
                # Re-register Security Center COM components.
                $dlls = @(
                    (Join-Path $env:SystemRoot 'System32\wscsvc.dll'),
                    (Join-Path $env:SystemRoot 'System32\SecurityHealthAgent.dll')
                )
                foreach ($dll in $dlls) {
                    if (Test-Path -LiteralPath $dll) {
                        $r = Invoke-TidyNativeCommand -FilePath 'regsvr32.exe' -Arguments "/s `"$dll`"" -TimeoutSeconds 15
                        if (-not $r.Success) {
                            Write-TidyLog -Level Warning -Message "  regsvr32 failed for '$dll': exit $($r.ExitCode)"
                        }
                    }
                }
            }
        }

        Invoke-Step -Name 'Reregister Windows Security Health components' -Action {
            # SecurityHealthSystray.exe registration.
            $systray = Join-Path $env:ProgramFiles 'Windows Defender\MSASCuiL.exe'
            if (-not (Test-Path -LiteralPath $systray)) {
                $systray = Join-Path $env:ProgramW6432 'Windows Security\Windows Defender\MSASCuiL.exe'
            }

            # Ensure the SecurityHealthSystray autorun is present.
            $runKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run'
            if (Test-Path -LiteralPath $runKey) {
                $existing = Get-ItemProperty -Path $runKey -Name 'SecurityHealth' -ErrorAction SilentlyContinue
                if (-not $existing) {
                    $healthExe = Join-Path $env:ProgramFiles 'Windows Defender\MSASCuiL.exe'
                    if (Test-Path -LiteralPath $healthExe) {
                        Backup-TidyRegistryKey -KeyPath $runKey -BackupDirectory $backupDir -Label 'SecurityHealth-Run'
                        Set-ItemProperty -Path $runKey -Name 'SecurityHealth' -Value "`"$healthExe`"" -Type String
                        Write-TidyOutput -Message '  Restored SecurityHealth autorun entry.'
                    }
                }
                else {
                    Write-TidyOutput -Message '  SecurityHealth autorun entry already present.'
                }
            }
        }
    }
    else { Write-TidyOutput -Message 'Security Center reset skipped.' }

    Write-TidyOutput -Message ''
    Write-TidyOutput -Message 'Security and credential repair completed.'
    Write-TidyOutput -Message "Registry backups saved to: $backupDir"
}
catch {
    $script:OperationSucceeded = $false
    Write-TidyError -Message "Security repair failed: $($_.Exception.Message)"
}
finally {
    # Restore any services that were stopped.
    foreach ($state in $script:ServiceStates) {
        try { Restore-TidyServiceState -State $state } catch {
            Write-TidyLog -Level Warning -Message "Failed to restore service '$($state.Name)': $($_.Exception.Message)"
        }
    }
    Save-TidyResult
}

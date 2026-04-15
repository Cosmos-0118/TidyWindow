param(
    [switch] $IncludeAdapterRefresh,
    [switch] $IncludeDhcpRenew,
    [switch] $SkipWinsockReset,
    [switch] $SkipIpReset,
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
$script:RebootRecommended = $false
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

# ── Step runner with tracking ─────────────────────────────────────────
function Invoke-Step {
    param(
        [Parameter(Mandatory)][string]      $Name,
        [Parameter(Mandatory)][scriptblock] $Action,
        [switch] $Critical
    )
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
        throw 'Network reset requires an elevated PowerShell session.'
    }

    Write-TidyOutput -Message 'Starting network reset and cache flush.'

    # ── 1. DNS cache flush ────────────────────────────────────────────
    Invoke-Step -Name 'Flush DNS cache' -Action {
        $r = Invoke-TidyNativeCommand -FilePath 'ipconfig.exe' -Arguments '/flushdns' -TimeoutSeconds 30
        if (-not $r.Success) { throw "ipconfig /flushdns exited with code $($r.ExitCode)" }
    }

    # ── 2. ARP / neighbor cache ───────────────────────────────────────
    Invoke-Step -Name 'Clear neighbor cache' -Action {
        # Use modern Remove-NetNeighbor if available; fall back to netsh.
        $cmd = Get-Command -Name 'Remove-NetNeighbor' -ErrorAction SilentlyContinue
        if ($cmd) {
            Get-NetNeighbor -ErrorAction SilentlyContinue |
                Where-Object { $_.State -ne 'Permanent' } |
                Remove-NetNeighbor -Confirm:$false -ErrorAction SilentlyContinue
        }
        else {
            $r = Invoke-TidyNativeCommand -FilePath 'netsh.exe' -Arguments 'interface ip delete arpcache' -TimeoutSeconds 30
            if (-not $r.Success) { Write-TidyLog -Level Warning -Message "ARP cache clear returned exit code $($r.ExitCode)." }
        }
    }

    # ── 3. TCP auto-tuning normalization ──────────────────────────────
    Invoke-Step -Name 'Normalize TCP stack' -Action {
        # These may be blocked by Group Policy on enterprise systems; treat failures as non-fatal.
        $r1 = Invoke-TidyNativeCommand -FilePath 'netsh.exe' -Arguments 'interface tcp set heuristics disabled' -TimeoutSeconds 15 -AcceptableExitCodes @(0,1)
        $r2 = Invoke-TidyNativeCommand -FilePath 'netsh.exe' -Arguments 'interface tcp set global autotuninglevel=normal' -TimeoutSeconds 15 -AcceptableExitCodes @(0,1)
        if (-not $r1.Success -and -not $r2.Success) {
            Write-TidyLog -Level Warning -Message 'TCP normalization may be blocked by Group Policy.'
        }
    }

    # ── 4. Winsock reset (opt-out) ────────────────────────────────────
    if ($SkipWinsockReset.IsPresent) {
        Write-TidyOutput -Message 'Winsock reset skipped (SkipWinsockReset flag).'
    }
    else {
        Invoke-Step -Name 'Winsock catalog reset' -Action {
            $r = Invoke-TidyNativeCommand -FilePath 'netsh.exe' -Arguments 'winsock reset' -TimeoutSeconds 60
            if (-not $r.Success) { throw "Winsock reset failed with exit code $($r.ExitCode)" }
            $script:RebootRecommended = $true
            Write-TidyOutput -Message '  NOTE: Winsock reset requires a system reboot to fully apply.'
        }
    }

    # ── 5. IP stack reset (opt-out) ───────────────────────────────────
    if ($SkipIpReset.IsPresent) {
        Write-TidyOutput -Message 'IP stack reset skipped (SkipIpReset flag).'
    }
    else {
        Invoke-Step -Name 'IP stack reset' -Action {
            $logPath = Join-Path $env:TEMP 'tidy-ip-reset.log'
            $r = Invoke-TidyNativeCommand -FilePath 'netsh.exe' -Arguments "int ip reset `"$logPath`"" -TimeoutSeconds 60 -AcceptableExitCodes @(0, 1)
            if ($r.ExitCode -eq 1) {
                $script:RebootRecommended = $true
                Write-TidyOutput -Message '  NOTE: IP reset returned code 1 (registry entries in use). Reboot recommended.'
            }
            elseif (-not $r.Success) {
                throw "IP stack reset failed with exit code $($r.ExitCode)"
            }
        }
    }

    # ── 6. Adapter refresh (opt-in) ───────────────────────────────────
    if ($IncludeAdapterRefresh.IsPresent) {
        Invoke-Step -Name 'Refresh network adapters' -Action {
            $getAdapter = Get-Command -Name 'Get-NetAdapter' -ErrorAction SilentlyContinue
            if (-not $getAdapter) {
                Write-TidyOutput -Message '  Skipped: Get-NetAdapter not available on this system.'
                return
            }

            $adapters = @(Get-NetAdapter -Physical | Where-Object { $_.Status -eq 'Up' })
            if ($adapters.Count -eq 0) {
                Write-TidyOutput -Message '  No active physical adapters found.'
                return
            }

            foreach ($adapter in $adapters) {
                $name = $adapter.Name
                Write-TidyOutput -Message "  Restarting adapter '$name'..."
                try {
                    Disable-NetAdapter -Name $name -Confirm:$false -ErrorAction Stop
                    # Wait up to 10 seconds for the adapter to fully disable.
                    $waited = 0
                    while ($waited -lt 10) {
                        Start-Sleep -Seconds 1; $waited++
                        $state = (Get-NetAdapter -Name $name -ErrorAction SilentlyContinue).Status
                        if ($state -eq 'Disabled' -or $state -eq 'Not Present') { break }
                    }
                    Enable-NetAdapter -Name $name -Confirm:$false -ErrorAction Stop
                    # Wait for the adapter to come back up.
                    $waited = 0
                    while ($waited -lt 15) {
                        Start-Sleep -Seconds 1; $waited++
                        $state = (Get-NetAdapter -Name $name -ErrorAction SilentlyContinue).Status
                        if ($state -eq 'Up') { break }
                    }
                    $finalState = (Get-NetAdapter -Name $name -ErrorAction SilentlyContinue).Status
                    if ($finalState -ne 'Up') {
                        Write-TidyError -Message "  Adapter '$name' did not return to Up state (current: $finalState)."
                    }
                    else {
                        Write-TidyOutput -Message "  Adapter '$name' restarted successfully."
                    }
                }
                catch {
                    # Critical: if disable succeeded but enable failed, force-enable the adapter.
                    try { Enable-NetAdapter -Name $name -Confirm:$false -ErrorAction SilentlyContinue } catch {}
                    Write-TidyError -Message "  Adapter '$name' restart failed: $($_.Exception.Message)"
                }
            }
        }
    }

    # ── 7. DHCP renew (opt-in) ────────────────────────────────────────
    if ($IncludeDhcpRenew.IsPresent) {
        Invoke-Step -Name 'DHCP release and renew' -Action {
            # Only run on interfaces actually configured for DHCP.
            $hasDhcp = $false
            try {
                $dhcpAdapters = Get-NetIPConfiguration -ErrorAction SilentlyContinue |
                    Where-Object { $_.NetAdapter.Status -eq 'Up' -and $_.IPv4Address }
                foreach ($cfg in $dhcpAdapters) {
                    $iface = Get-NetIPInterface -InterfaceIndex $cfg.InterfaceIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue
                    if ($iface.Dhcp -eq 'Enabled') { $hasDhcp = $true; break }
                }
            }
            catch { $hasDhcp = $true } # If detection fails, proceed cautiously.

            if (-not $hasDhcp) {
                Write-TidyOutput -Message '  No DHCP-enabled adapters detected. Skipping release/renew.'
                return
            }

            $null = Invoke-TidyNativeCommand -FilePath 'ipconfig.exe' -Arguments '/release' -TimeoutSeconds 30 -AcceptableExitCodes @(0, 1, 2)
            $renew = Invoke-TidyNativeCommand -FilePath 'ipconfig.exe' -Arguments '/renew'   -TimeoutSeconds 60 -AcceptableExitCodes @(0, 1)
            if (-not $renew.Success) {
                throw "DHCP renew failed (exit code $($renew.ExitCode)). Network may be temporarily unavailable."
            }
        }
    }

    # ── Summary ───────────────────────────────────────────────────────
    Write-TidyOutput -Message ''
    Write-TidyOutput -Message '--- Network reset summary ---'
    if ($script:RebootRecommended) {
        Write-TidyOutput -Message 'NOTE: A system reboot is recommended to complete the reset.'
    }
    Write-TidyOutput -Message 'Network reset and cache flush completed.'
}
catch {
    $script:OperationSucceeded = $false
    Write-TidyError -Message "Network reset failed: $($_.Exception.Message)"
}
finally {
    Save-TidyResult
}

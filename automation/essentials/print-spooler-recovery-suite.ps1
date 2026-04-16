param(
    [switch] $SkipDriverCleanup,
    [switch] $SkipDllReregister,
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
$savedServiceStates = [System.Collections.Generic.List[psobject]]::new()

try {
    if (-not (Test-TidyAdmin)) {
        throw 'Print spooler recovery requires an elevated PowerShell session.'
    }

    Write-TidyOutput -Message 'Starting print spooler recovery suite.'

    # ── 1. Save and stop spooler services ─────────────────────────────
    $spoolerServices = @('Spooler', 'PrintNotify')
    foreach ($svcName in $spoolerServices) {
        Invoke-Step -Name "Stop service '$svcName'" -Action {
            $state = Invoke-TidySafeServiceStop -ServiceName $svcName -TimeoutSeconds 45 -Force
            if ($state) { $savedServiceStates.Add($state) }
        }
    }

    # ── 2. Clear spool queue ──────────────────────────────────────────
    Invoke-Step -Name 'Clear spool queue' -Action {
        $spoolDir = Join-Path $env:SystemRoot 'System32\spool\PRINTERS'
        if (Test-Path -LiteralPath $spoolDir) {
            $files = @(Get-ChildItem -LiteralPath $spoolDir -File -Force -ErrorAction SilentlyContinue)
            $cleared = 0
            foreach ($file in $files) {
                try {
                    Remove-Item -LiteralPath $file.FullName -Force -ErrorAction Stop
                    $cleared++
                }
                catch {
                    Write-TidyLog -Level Warning -Message "Could not remove spool file '$($file.Name)': $($_.Exception.Message)"
                }
            }
            Write-TidyOutput -Message "  Cleared $cleared spool file(s)."
        }
        else {
            Write-TidyOutput -Message '  Spool directory not found (clean state).'
        }
    }

    # ── 3. Remove stale printer drivers (opt-out) ─────────────────────
    if ($SkipDriverCleanup.IsPresent) {
        Write-TidyOutput -Message 'Driver cleanup skipped (SkipDriverCleanup flag).'
    }
    else {
        Invoke-Step -Name 'Remove offline printer drivers' -Action {
            $driverCmd = Get-Command -Name 'Get-PrinterDriver' -ErrorAction SilentlyContinue
            if (-not $driverCmd) {
                Write-TidyOutput -Message '  Get-PrinterDriver not available. Skipping.'
                return
            }

            # Build set of drivers currently in use by installed printers.
            $printerCmd = Get-Command -Name 'Get-Printer' -ErrorAction SilentlyContinue
            $inUseDrivers = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
            if ($printerCmd) {
                try {
                    $printers = @(Get-Printer -ErrorAction Stop)
                    foreach ($p in $printers) {
                        if (-not [string]::IsNullOrWhiteSpace($p.DriverName)) {
                            [void]$inUseDrivers.Add($p.DriverName)
                        }
                    }
                }
                catch {
                    Write-TidyLog -Level Warning -Message "Printer enumeration failed: $($_.Exception.Message)"
                }
            }

            $drivers = @(Get-PrinterDriver -ErrorAction SilentlyContinue)
            $removed = 0; $skipped = 0
            foreach ($drv in $drivers) {
                if ($inUseDrivers.Contains($drv.Name)) {
                    $skipped++
                    continue
                }
                try {
                    Remove-PrinterDriver -Name $drv.Name -ErrorAction Stop
                    $removed++
                }
                catch {
                    Write-TidyLog -Level Warning -Message "Could not remove driver '$($drv.Name)': $($_.Exception.Message)"
                }
            }
            Write-TidyOutput -Message "  Removed $removed unused driver(s), skipped $skipped in-use."
        }
    }

    # ── 4. Re-register spooler DLLs (opt-out) ────────────────────────
    if ($SkipDllReregister.IsPresent) {
        Write-TidyOutput -Message 'DLL re-registration skipped (SkipDllReregister flag).'
    }
    else {
        Invoke-Step -Name 'Re-register spooler DLLs' -Action {
            $dlls = @('spoolss.dll', 'spoolsv.exe', 'localspl.dll', 'win32spl.dll')
            foreach ($dll in $dlls) {
                $dllPath = Join-Path $env:SystemRoot "System32\$dll"
                if (-not (Test-Path -LiteralPath $dllPath)) {
                    Write-TidyLog -Level Warning -Message "DLL not found: $dllPath"
                    continue
                }
                # Use regsvr32 for DLLs; for EXEs, use /regserver if applicable.
                if ($dllPath -like '*.dll') {
                    $r = Invoke-TidyNativeCommand -FilePath 'regsvr32.exe' -Arguments "/s `"$dllPath`"" -TimeoutSeconds 30 -AcceptableExitCodes @(0, 3, 5)
                    if (-not $r.Success) {
                        Write-TidyLog -Level Warning -Message "regsvr32 for '$dll' returned exit code $($r.ExitCode)."
                    }
                }
            }
        }
    }

    # ── 5. Reset print isolation policy ───────────────────────────────
    Invoke-Step -Name 'Reset print isolation policy' -Action {
        $regPath = 'HKLM:\SYSTEM\CurrentControlSet\Control\Print'
        if (Test-Path -LiteralPath $regPath) {
            $current = Get-ItemProperty -LiteralPath $regPath -Name 'PrintDriverIsolationOverride' -ErrorAction SilentlyContinue
            if ($null -ne $current) {
                Remove-ItemProperty -LiteralPath $regPath -Name 'PrintDriverIsolationOverride' -ErrorAction SilentlyContinue
                Write-TidyOutput -Message '  Removed PrintDriverIsolationOverride registry value.'
            }
            else {
                Write-TidyOutput -Message '  No isolation override found (already default).'
            }
        }
    }

    Write-TidyOutput -Message 'Print spooler recovery steps completed.'
}
catch {
    $script:OperationSucceeded = $false
    Write-TidyError -Message "Print spooler recovery failed: $($_.Exception.Message)"
}
finally {
    # ── ALWAYS restart services that were stopped ─────────────────────
    if (-not $script:DryRunMode) {
        foreach ($state in $savedServiceStates) {
            try {
                Restore-TidyServiceState -State $state -TimeoutSeconds 45
                Write-TidyOutput -Message "Service '$($state.Name)' restored."
            }
            catch {
                Write-TidyError -Message "CRITICAL: Failed to restore service '$($state.Name)'. Manual restart required."
            }
        }
    }

    # Verify printing works by checking spooler is running.
    $spoolerCheck = Get-Service -Name 'Spooler' -ErrorAction SilentlyContinue
    if ($spoolerCheck -and $spoolerCheck.Status -ne 'Running') {
        Write-TidyError -Message 'WARNING: Print Spooler is not running after recovery. Attempting emergency start...'
        try {
            Start-Service -Name 'Spooler' -ErrorAction Stop
            Write-TidyOutput -Message 'Print Spooler emergency start succeeded.'
        }
        catch {
            Write-TidyError -Message "CRITICAL: Print Spooler could not be started: $($_.Exception.Message)"
        }
    }

    Save-TidyResult
}

param(
    [switch] $SkipSafeModeExit,
    [switch] $SkipBootrecFixes,
    [switch] $SkipDismGuidance,
    [switch] $SkipTestSigningToggle,
    [switch] $SkipTimeSyncRepair,
    [switch] $SkipWmiRepair,
    [switch] $SkipDumpAndDriverScan,
    [string] $ResultPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$callerModulePath = $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($callerModulePath) -and (Get-Variable -Name PSCommandPath -Scope Script -ErrorAction SilentlyContinue)) {
    $callerModulePath = $PSCommandPath
}

$scriptDirectory = Split-Path -Parent $callerModulePath
if ([string]::IsNullOrWhiteSpace($scriptDirectory)) {
    $scriptDirectory = (Get-Location).Path
}

$modulePath = Join-Path -Path $scriptDirectory -ChildPath '..\modules\TidyWindow.Automation\TidyWindow.Automation.psm1'
$modulePath = [System.IO.Path]::GetFullPath($modulePath)
if (-not (Test-Path -Path $modulePath)) {
    throw "Automation module not found at path '$modulePath'."
}

Import-Module $modulePath -Force

$script:TidyOutputLines = [System.Collections.Generic.List[string]]::new()
$script:TidyErrorLines = [System.Collections.Generic.List[string]]::new()
$script:OperationSucceeded = $true
$script:UsingResultFile = -not [string]::IsNullOrWhiteSpace($ResultPath)

if ($script:UsingResultFile) {
    $ResultPath = [System.IO.Path]::GetFullPath($ResultPath)
}

function Write-TidyOutput {
    param(
        [Parameter(Mandatory = $true)]
        [object] $Message
    )

    $text = Convert-TidyLogMessage -InputObject $Message
    if ([string]::IsNullOrWhiteSpace($text)) {
        return
    }

    [void]$script:TidyOutputLines.Add($text)
    Write-Output $text
}

function Write-TidyError {
    param(
        [Parameter(Mandatory = $true)]
        [object] $Message
    )

    $text = Convert-TidyLogMessage -InputObject $Message
    if ([string]::IsNullOrWhiteSpace($text)) {
        return
    }

    [void]$script:TidyErrorLines.Add($text)
    Write-Error -Message $text
}

function Save-TidyResult {
    if (-not $script:UsingResultFile) {
        return
    }

    $payload = [pscustomobject]@{
        Success = $script:OperationSucceeded -and ($script:TidyErrorLines.Count -eq 0)
        Output  = $script:TidyOutputLines
        Errors  = $script:TidyErrorLines
    }

    $json = $payload | ConvertTo-Json -Depth 5
    Set-Content -Path $ResultPath -Value $json -Encoding UTF8
}

function Invoke-TidyCommand {
    param(
        [Parameter(Mandatory = $true)]
        [scriptblock] $Command,
        [string] $Description = 'Running command.',
        [object[]] $Arguments = @(),
        [switch] $RequireSuccess,
        [int[]] $AcceptableExitCodes = @()
    )

    Write-TidyLog -Level Information -Message $Description

    if (Test-Path -Path 'variable:LASTEXITCODE') {
        $global:LASTEXITCODE = 0
    }

    $output = & $Command @Arguments 2>&1
    $exitCode = if (Test-Path -Path 'variable:LASTEXITCODE') { $LASTEXITCODE } else { 0 }

    if ($exitCode -eq 0 -and $output) {
        $lastItem = ($output | Select-Object -Last 1)
        if ($lastItem -is [int] -or $lastItem -is [long]) {
            $exitCode = [int]$lastItem
        }
    }

    foreach ($entry in @($output)) {
        if ($null -eq $entry) { continue }

        if ($entry -is [System.Management.Automation.ErrorRecord]) {
            Write-TidyError -Message $entry
        }
        else {
            Write-TidyOutput -Message $entry
        }
    }

    if ($RequireSuccess -and $exitCode -ne 0) {
        $acceptsExitCode = $false
        if ($AcceptableExitCodes -and ($AcceptableExitCodes -contains $exitCode)) {
            $acceptsExitCode = $true
        }

        if (-not $acceptsExitCode) {
            throw "$Description failed with exit code $exitCode."
        }
    }

    return $exitCode
}

function Test-TidyAdmin {
    return [bool](New-Object System.Security.Principal.WindowsPrincipal([System.Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Exit-SafeMode {
    try {
        Write-TidyOutput -Message 'Removing SafeBoot configuration from current BCD entry (if present).'
        Invoke-TidyCommand -Command { bcdedit /deletevalue {current} safeboot } -Description 'Clearing safeboot flag.' -AcceptableExitCodes @(0, 1)
    }
    catch {
        $script:OperationSucceeded = $false
        Write-TidyError -Message ("Safe mode exit failed: {0}" -f $_.Exception.Message)
    }
}

function Run-BootrecFixes {
    try {
        Write-TidyOutput -Message 'Running bootrec /fixmbr.'
        Invoke-TidyCommand -Command { bootrec /fixmbr } -Description 'bootrec /fixmbr' -RequireSuccess

        Write-TidyOutput -Message 'Running bootrec /fixboot.'
        Invoke-TidyCommand -Command { bootrec /fixboot } -Description 'bootrec /fixboot' -AcceptableExitCodes @(0,1,2)

        Write-TidyOutput -Message 'Running bootrec /rebuildbcd.'
        Invoke-TidyCommand -Command { bootrec /rebuildbcd } -Description 'bootrec /rebuildbcd' -AcceptableExitCodes @(0,1)
    }
    catch {
        $script:OperationSucceeded = $false
        Write-TidyError -Message ("Bootrec sequence failed: {0}" -f $_.Exception.Message)
    }
}

function Show-DismRecoveryGuidance {
    $lines = @(
        'If offline repair is required from WinRE, run:',
        '  dism /Image:C:\ /Cleanup-Image /RestoreHealth',
        'If the OS is mounted differently, adjust the /Image path accordingly.'
    )
    foreach ($line in $lines) { Write-TidyOutput -Message $line }
}

function Toggle-TestSigning {
    try {
        Write-TidyOutput -Message 'Disabling testsigning (driver signature enforcement) to restore normal boot.'
        Invoke-TidyCommand -Command { bcdedit /set testsigning off } -Description 'Disabling testsigning.' -AcceptableExitCodes @(0,1)
    }
    catch {
        $script:OperationSucceeded = $false
        Write-TidyError -Message ("Testsigning toggle failed: {0}" -f $_.Exception.Message)
    }
}

function Repair-TimeSync {
    try {
        Write-TidyOutput -Message 'Repairing time sync service (w32time) and forcing resync.'
        Invoke-TidyCommand -Command { sc.exe triggerinfo w32time start/networkon stop/networkoff } -Description 'Resetting w32time triggers.' -AcceptableExitCodes @(0,1060)
        Invoke-TidyCommand -Command { net stop w32time } -Description 'Stopping w32time.' -AcceptableExitCodes @(0,2,1060)
        Invoke-TidyCommand -Command { net start w32time } -Description 'Starting w32time.' -RequireSuccess
        Invoke-TidyCommand -Command { w32tm /config /manualpeerlist:"time.windows.com,0x9" /syncfromflags:manual /update } -Description 'Configuring time peers.' -AcceptableExitCodes @(0)
        Invoke-TidyCommand -Command { w32tm /resync /force } -Description 'Forcing time resync.' -AcceptableExitCodes @(0,0x800705B4)
    }
    catch {
        $script:OperationSucceeded = $false
        Write-TidyError -Message ("Time synchronization repair failed: {0}" -f $_.Exception.Message)
    }
}

function Repair-WmiRepository {
    try {
        Write-TidyOutput -Message 'Salvaging WMI repository.'
        Invoke-TidyCommand -Command { winmgmt /salvagerepository } -Description 'WMI salvage.' -AcceptableExitCodes @(0, 0x1)

        Write-TidyOutput -Message 'Resetting WMI repository.'
        Invoke-TidyCommand -Command { winmgmt /resetrepository } -Description 'WMI reset.' -AcceptableExitCodes @(0, 0x1)

        Write-TidyOutput -Message 'Restarting Winmgmt service.'
        Invoke-TidyCommand -Command { Restart-Service -Name Winmgmt -Force -ErrorAction Stop } -Description 'Restarting Winmgmt.' -AcceptableExitCodes @(0)
    }
    catch {
        $script:OperationSucceeded = $false
        Write-TidyError -Message ("WMI repair failed: {0}" -f $_.Exception.Message)
    }
}

function Collect-DumpsAndDrivers {
    try {
        $minidumpPath = Join-Path -Path $env:SystemRoot -ChildPath 'Minidump'
        if (Test-Path -LiteralPath $minidumpPath) {
            $dumps = Get-ChildItem -LiteralPath $minidumpPath -File -ErrorAction SilentlyContinue | Sort-Object -Property LastWriteTime -Descending | Select-Object -First 5
            if ($dumps) {
                Write-TidyOutput -Message 'Recent minidumps:'
                foreach ($d in $dumps) {
                    Write-TidyOutput -Message ("  {0} ({1})" -f $d.Name, $d.LastWriteTime)
                }
            }
            else {
                Write-TidyOutput -Message 'No minidumps found.'
            }
        }
        else {
            Write-TidyOutput -Message 'Minidump folder not found.'
        }

        Write-TidyOutput -Message 'Running driver inventory (driverquery /v).' 
        Invoke-TidyCommand -Command { driverquery /v } -Description 'Driver inventory.' -AcceptableExitCodes @(0)
    }
    catch {
        $script:OperationSucceeded = $false
        Write-TidyError -Message ("Dump/driver inventory helper failed: {0}" -f $_.Exception.Message)
    }
}

try {
    if (-not (Test-TidyAdmin)) {
        throw 'Recovery and boot repair requires an elevated PowerShell session. Restart as administrator.'
    }

    Write-TidyLog -Level Information -Message 'Starting recovery and boot repair pack.'

    if (-not $SkipSafeModeExit.IsPresent) {
        Exit-SafeMode
    }
    else {
        Write-TidyOutput -Message 'Skipping safe mode exit per operator request.'
    }

    if (-not $SkipBootrecFixes.IsPresent) {
        Run-BootrecFixes
    }
    else {
        Write-TidyOutput -Message 'Skipping bootrec fixes per operator request.'
    }

    if (-not $SkipDismGuidance.IsPresent) {
        Show-DismRecoveryGuidance
    }
    else {
        Write-TidyOutput -Message 'Skipping DISM recovery guidance per operator request.'
    }

    if (-not $SkipTestSigningToggle.IsPresent) {
        Toggle-TestSigning
    }
    else {
        Write-TidyOutput -Message 'Skipping testsigning toggle per operator request.'
    }

    if (-not $SkipTimeSyncRepair.IsPresent) {
        Repair-TimeSync
    }
    else {
        Write-TidyOutput -Message 'Skipping time sync repair per operator request.'
    }

    if (-not $SkipWmiRepair.IsPresent) {
        Repair-WmiRepository
    }
    else {
        Write-TidyOutput -Message 'Skipping WMI repair per operator request.'
    }

    if (-not $SkipDumpAndDriverScan.IsPresent) {
        Collect-DumpsAndDrivers
    }
    else {
        Write-TidyOutput -Message 'Skipping dump and driver inventory helper per operator request.'
    }

    Write-TidyOutput -Message 'Recovery and boot repair pack completed.'
}
catch {
    $script:OperationSucceeded = $false
    $message = $_.Exception.Message
    if ([string]::IsNullOrWhiteSpace($message)) {
        $message = $_ | Out-String
    }

    Write-TidyLog -Level Error -Message $message
    Write-TidyError -Message $message
    if (-not $script:UsingResultFile) {
        throw
    }
}
finally {
    Save-TidyResult
    Write-TidyLog -Level Information -Message 'Recovery and boot repair script finished.'
}

if ($script:UsingResultFile) {
    $wasSuccessful = $script:OperationSucceeded -and ($script:TidyErrorLines.Count -eq 0)
    if ($wasSuccessful) {
        exit 0
    }

    exit 1
}

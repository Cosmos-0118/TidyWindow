param(
    [switch] $SkipSfc,
    [switch] $SkipDism,
    [switch] $RunRestoreHealth,
    [switch] $SkipRestoreHealth,
    [switch] $ComponentCleanup,
    [switch] $AnalyzeComponentStore,
    [string] $ResultPath,

    # New safety/automation options
    [switch] $DryRun,
    [switch] $CreateSystemRestorePoint,
    [string] $LogPath,
    [switch] $NoElevate
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

$modulePath = Join-Path -Path $scriptDirectory -ChildPath '..\modules\TidyWindow.Automation.psm1'
$modulePath = [System.IO.Path]::GetFullPath($modulePath)
if (-not (Test-Path -Path $modulePath)) {
    throw "Automation module not found at path '$modulePath'."
}

Import-Module $modulePath -Force

$script:TidyOutputLines = [System.Collections.Generic.List[string]]::new()
$script:TidyErrorLines = [System.Collections.Generic.List[string]]::new()
$script:OperationSucceeded = $true
$script:UsingResultFile = -not [string]::IsNullOrWhiteSpace($ResultPath)

# Default log path if not supplied
if ([string]::IsNullOrWhiteSpace($LogPath)) {
    $time = (Get-Date).ToString('yyyyMMdd_HHmmss')
    $LogPath = Join-Path -Path $env:TEMP -ChildPath "TidyWindow_SystemHealth_$time.log"
}

# Transcript file (human-friendly) - put next to log
$transcriptPath = [System.IO.Path]::ChangeExtension($LogPath, '.transcript.txt')

# Track dry-run mode
$script:DryRunMode = $DryRun.IsPresent

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

    if ($script:DryRunMode) {
        Write-TidyOutput -Message "[DryRun] Would run: $Description"
        Write-TidyOutput -Message "[DryRun] Command: $Command $($Arguments -join ' ')"
        return 0
    }

    $output = & $Command @Arguments 2>&1
    $exitCode = if (Test-Path -Path 'variable:LASTEXITCODE') { $LASTEXITCODE } else { 0 }

    foreach ($entry in @($output)) {
        if ($null -eq $entry) {
            continue
        }

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

function Ensure-Elevation {
    param(
        [switch] $AllowNoElevate
    )

    if (Test-TidyAdmin) { return $true }
    if ($AllowNoElevate -or $NoElevate.IsPresent) { return $false }

    # Relaunch elevated with same bound parameters
    try {
        $scriptPath = $PSCommandPath
        if ([string]::IsNullOrWhiteSpace($scriptPath)) { $scriptPath = $MyInvocation.MyCommand.Path }

        $argList = @('-NoProfile','-ExecutionPolicy','Bypass','-File',"$scriptPath")
        foreach ($k in $PSBoundParameters.Keys) {
            $val = $PSBoundParameters[$k]
            if ($val -is [switch]) {
                if ($val.IsPresent) { $argList += "-$k" }
            }
            else {
                # Quote string-ish values
                $argList += "-$k"; $argList += "`"$val`""
            }
        }

        Start-Process -FilePath (Get-Command powershell).Source -ArgumentList $argList -Verb RunAs -WindowStyle Hidden
        Write-TidyOutput -Message 'Elevating and re-launching as administrator. Exiting current process.'
        exit 0
    }
    catch {
        Write-TidyError -Message "Failed to elevate: $($_.Exception.Message)"
        throw
    }
}

function New-SystemRestorePointSafe {
    param(
        [string] $Description = 'TidyWindow system-health snapshot (automatic)'
    )

    if ($script:DryRunMode) {
        Write-TidyOutput -Message "[DryRun] Would create system restore point: $Description"
        return $true
    }

    try {
        if (-not (Get-Command -Name Checkpoint-Computer -ErrorAction SilentlyContinue)) {
            Write-TidyLog -Level Warning -Message 'Checkpoint-Computer not available on this system. Skipping restore point creation.'
            return $false
        }

        Write-TidyOutput -Message "Creating system restore point: $Description"
        Checkpoint-Computer -Description $Description -RestorePointType "APPLICATION_INSTALL" -ErrorAction Stop | Out-Null
        Write-TidyOutput -Message 'Restore point created (if supported by OS and enabled).'
        return $true
    }
    catch {
        Write-TidyLog -Level Warning -Message "Failed to create restore point: $($_.Exception.Message)"
        return $false
    }
}

$shouldRunRestoreHealth = $true
if ($PSBoundParameters.ContainsKey('RunRestoreHealth')) {
    $shouldRunRestoreHealth = $RunRestoreHealth.IsPresent
}
elseif ($SkipRestoreHealth.IsPresent) {
    $shouldRunRestoreHealth = $false
}

try {
    # Start transcript and logging
    try {
        Start-Transcript -Path $transcriptPath -Force -ErrorAction SilentlyContinue | Out-Null
    }
    catch {
        # non-fatal
    }

    Write-TidyLog -Level Information -Message 'Starting Windows system health scanner.'

    # Auto-elevate if necessary (unless explicitly disabled)
    if (-not (Test-TidyAdmin)) {
        $elevated = Ensure-Elevation -AllowNoElevate:$false
        if (-not $elevated) {
            throw 'System health scan requires elevated privileges and elevation was disabled.'
        }
    }

    if (-not $SkipSfc.IsPresent) {
        Write-TidyOutput -Message 'Running System File Checker (this can take 5-15 minutes).'
        $sfcExit = Invoke-TidyCommand -Command { sfc /scannow } -Description 'Running SFC /scannow.' -RequireSuccess -AcceptableExitCodes @(1)

        switch ($sfcExit) {
            0 { Write-TidyOutput -Message 'SFC completed without finding integrity violations.' }
            1 { Write-TidyOutput -Message 'SFC found and repaired integrity violations.' }
        }
    }
    else {
        Write-TidyOutput -Message 'Skipping SFC scan per operator request.'
    }

    if (-not $SkipDism.IsPresent) {
        Write-TidyOutput -Message 'Checking Windows component store health.'
        Invoke-TidyCommand -Command { DISM /Online /Cleanup-Image /CheckHealth } -Description 'DISM CheckHealth' -RequireSuccess | Out-Null

        Write-TidyOutput -Message 'Scanning Windows component store for corruption.'
        Invoke-TidyCommand -Command { DISM /Online /Cleanup-Image /ScanHealth } -Description 'DISM ScanHealth' -RequireSuccess | Out-Null

        if ($shouldRunRestoreHealth) {
            Write-TidyOutput -Message 'Repairing Windows component store corruption (RestoreHealth).'
            Invoke-TidyCommand -Command { DISM /Online /Cleanup-Image /RestoreHealth } -Description 'DISM RestoreHealth' -RequireSuccess | Out-Null
        }
        else {
            Write-TidyOutput -Message 'Skipping RestoreHealth per operator request.'
        }

        if ($ComponentCleanup.IsPresent) {
            Write-TidyOutput -Message 'Cleaning up superseded components.'
            Invoke-TidyCommand -Command { DISM /Online /Cleanup-Image /StartComponentCleanup } -Description 'DISM StartComponentCleanup' -RequireSuccess | Out-Null
        }

        if ($AnalyzeComponentStore.IsPresent) {
            Write-TidyOutput -Message 'Analyzing component store (provides size and reclaim recommendations).'
            Invoke-TidyCommand -Command { DISM /Online /Cleanup-Image /AnalyzeComponentStore } -Description 'DISM AnalyzeComponentStore' | Out-Null
        }
    }
    else {
        Write-TidyOutput -Message 'Skipping DISM checks per operator request.'
    }

    Write-TidyOutput -Message 'System health scan completed.'

    # Optionally create a restore point after successful repairs
    if ($CreateSystemRestorePoint.IsPresent -and -not $script:DryRunMode) {
        New-SystemRestorePointSafe -Description 'TidyWindow post-scan snapshot'
    }
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
    Write-TidyLog -Level Information -Message 'System health scanner finished.'
    try { Stop-Transcript -ErrorAction SilentlyContinue } catch {}
    # Write a short run summary to the log path
    try {
        $summary = [pscustomobject]@{
            Time = (Get-Date).ToString('o')
            Success = $script:OperationSucceeded -and ($script:TidyErrorLines.Count -eq 0)
            OutputLines = $script:TidyOutputLines.Count
            ErrorLines = $script:TidyErrorLines.Count
            TranscriptPath = $transcriptPath
        }
        $summary | ConvertTo-Json -Depth 3 | Out-File -FilePath $LogPath -Encoding UTF8
    }
    catch {
        # non-fatal
    }
}

if ($script:UsingResultFile) {
    $wasSuccessful = $script:OperationSucceeded -and ($script:TidyErrorLines.Count -eq 0)
    if ($wasSuccessful) {
        exit 0
    }

    exit 1
}

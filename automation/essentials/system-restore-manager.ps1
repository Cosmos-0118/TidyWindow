param(
    [switch] $Create,
    [string] $RestorePointName,
    [ValidateSet('APPLICATION_INSTALL', 'APPLICATION_UNINSTALL', 'DEVICE_DRIVER_INSTALL', 'MODIFY_SETTINGS', 'CANCELLED_OPERATION')]
    [string] $RestorePointType = 'MODIFY_SETTINGS',
    [switch] $List,
    [int] $KeepLatest = 0,
    [int] $PurgeOlderThanDays = 0,
    [switch] $EnableRestore,
    [switch] $DisableRestore,
    [string[]] $Drives = @('C:'),
    [switch] $Force,
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
        [switch] $RequireSuccess
    )

    Write-TidyLog -Level Information -Message $Description

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
        throw "$Description failed with exit code $exitCode."
    }

    return $exitCode
}

function Test-TidyAdmin {
    return [bool](New-Object System.Security.Principal.WindowsPrincipal([System.Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Normalize-TidyDrive {
    param([string] $Drive)

    if ([string]::IsNullOrWhiteSpace($Drive)) {
        throw 'Drive value cannot be empty.'
    }

    $trimmed = $Drive.Trim()
    if ($trimmed.Length -eq 1) {
        $trimmed = "${trimmed}:"
    }

    if ($trimmed.Length -lt 2 -or $trimmed[1] -ne ':') {
        throw "Drive value '$Drive' is not valid."
    }

    $normalized = $trimmed.Substring(0, 2).ToUpperInvariant() + '\\'
    return $normalized
}

function Get-TidyRestorePoints {
    $points = Get-CimInstance -Namespace 'root/default' -ClassName 'SystemRestore' -ErrorAction SilentlyContinue
    $list = [System.Collections.Generic.List[pscustomobject]]::new()

    foreach ($point in @($points)) {
        try {
            $created = [System.Management.ManagementDateTimeConverter]::ToDateTime($point.CreationTime)
        }
        catch {
            $created = Get-Date
        }

        $list.Add([pscustomobject]@{
            SequenceNumber = [uint32]$point.SequenceNumber
            Description    = $point.Description
            CreationTime   = $created
        }) | Out-Null
    }

    $sorted = $list | Sort-Object -Property CreationTime -Descending
    if ($sorted) {
        return @($sorted)
    }

    return @()
}

function Get-TidyRestoreCreationFrequencyMinutes {
    try {
        $settings = Get-ItemProperty -Path 'Registry::HKLM\\Software\\Microsoft\\Windows NT\\CurrentVersion\\SystemRestore' -ErrorAction Stop
        $rawValue = $settings.SystemRestorePointCreationFrequency
        if ($null -ne $rawValue) {
            $value = [int]$rawValue
            if ($value -le 0) {
                return 0
            }

            return $value
        }
    }
    catch {
        # Ignore and fall back to default frequency.
    }

    return 1440
}

function Remove-TidyRestorePoint {
    param([uint32] $SequenceNumber)

    Invoke-CimMethod -Namespace 'root/default' -ClassName 'SystemRestore' -MethodName 'RemoveRestorePoint' -Arguments @{ SequenceNumber = $SequenceNumber } -ErrorAction Stop | Out-Null
}

$shouldCreate = $Create.IsPresent
$shouldList = $List.IsPresent
$shouldEnable = $EnableRestore.IsPresent
$shouldDisable = $DisableRestore.IsPresent
$keepCount = [Math]::Max(0, $KeepLatest)
$purgeThreshold = if ($PurgeOlderThanDays -gt 0) { (Get-Date).AddDays(-1 * $PurgeOlderThanDays) } else { $null }

if (-not ($shouldCreate -or $shouldList -or $shouldEnable -or $shouldDisable -or $keepCount -gt 0 -or $purgeThreshold)) {
    $shouldCreate = $true
    $shouldList = $true
}

try {
    if (-not (Test-TidyAdmin)) {
        throw 'System Restore manager requires an elevated PowerShell session. Restart as administrator.'
    }

    if ($shouldEnable -and $shouldDisable) {
        throw 'EnableRestore and DisableRestore cannot be combined in one run.'
    }

    $normalizedDrives = $Drives | ForEach-Object { Normalize-TidyDrive -Drive $_ } | Sort-Object -Unique

    if ($shouldEnable) {
        foreach ($drive in $normalizedDrives) {
            Write-TidyOutput -Message ("Enabling System Restore on {0}" -f $drive)
            Invoke-TidyCommand -Command { param($d) Enable-ComputerRestore -Drive $d } -Arguments @($drive) -Description ("Enable System Restore {0}" -f $drive)
        }
    }

    if ($shouldDisable) {
        foreach ($drive in $normalizedDrives) {
            if (-not $Force.IsPresent) {
                Write-TidyOutput -Message ("Skipping disable for {0} without -Force." -f $drive)
                continue
            }

            Write-TidyOutput -Message ("Disabling System Restore on {0}" -f $drive)
            Invoke-TidyCommand -Command { param($d) Disable-ComputerRestore -Drive $d } -Arguments @($drive) -Description ("Disable System Restore {0}" -f $drive)
        }
    }

    $restorePoints = @()
    if ($shouldList -or $keepCount -gt 0 -or $purgeThreshold) {
        $restorePoints = Get-TidyRestorePoints
    }

    if ($keepCount -gt 0 -and $restorePoints.Count -gt $keepCount) {
        $toRemove = $restorePoints | Select-Object -Skip $keepCount
        foreach ($point in $toRemove) {
            Write-TidyOutput -Message ("Removing restore point #{0} from {1:g}" -f $point.SequenceNumber, $point.CreationTime)
            try {
                Remove-TidyRestorePoint -SequenceNumber $point.SequenceNumber
            }
            catch {
                Write-TidyError -Message ("Failed to remove restore point #{0}. {1}" -f $point.SequenceNumber, $_.Exception.Message)
            }
        }

        $restorePoints = Get-TidyRestorePoints
    }

    if ($purgeThreshold) {
        $toRemove = $restorePoints | Where-Object { $_.CreationTime -lt $purgeThreshold }
        foreach ($point in $toRemove) {
            Write-TidyOutput -Message ("Purging restore point #{0} from {1:g}" -f $point.SequenceNumber, $point.CreationTime)
            try {
                Remove-TidyRestorePoint -SequenceNumber $point.SequenceNumber
            }
            catch {
                Write-TidyError -Message ("Failed to purge restore point #{0}. {1}" -f $point.SequenceNumber, $_.Exception.Message)
            }
        }

        $restorePoints = Get-TidyRestorePoints
    }

    if ($shouldCreate) {
        if (-not $restorePoints) {
            $restorePoints = Get-TidyRestorePoints
        }

        $frequencyMinutes = Get-TidyRestoreCreationFrequencyMinutes
        $latestPoint = if ($restorePoints.Count -gt 0) { $restorePoints[0] } else { $null }
        $timeSinceLast = if ($latestPoint) { (New-TimeSpan -Start $latestPoint.CreationTime -End (Get-Date)).TotalMinutes } else { [double]::PositiveInfinity }

        if ($frequencyMinutes -gt 0 -and $timeSinceLast -lt $frequencyMinutes) {
            Write-TidyOutput -Message (
                "Skipping restore point creation; last restore point ({0:G}) is within the configured frequency window ({1} minutes)." -f 
                $latestPoint.CreationTime,
                $frequencyMinutes
            )
        }
        else {
        $name = if ([string]::IsNullOrWhiteSpace($RestorePointName)) { "TidyWindow snapshot {0}" -f (Get-Date).ToString('yyyy-MM-dd HH:mm') } else { $RestorePointName }
        Write-TidyOutput -Message ("Creating restore point '{0}' ({1})." -f $name, $RestorePointType)
            $creationSucceeded = $false

            try {
                Invoke-TidyCommand -Command {
                    param($description, $type)
                    Checkpoint-Computer -Description $description -RestorePointType $type -ErrorAction Stop
                } -Arguments @($name, $RestorePointType) -Description 'Creating System Restore snapshot.' | Out-Null
                $creationSucceeded = $true
            }
            catch {
                $message = $_.Exception.Message
                if ($message -and $message -like '*already been created within the past*') {
                    Write-TidyOutput -Message 'System Restore rejected the request because a recent restore point already exists. Skipping new creation.'
                }
                else {
                    throw
                }
            }

            if ($creationSucceeded) {
                $restorePoints = Get-TidyRestorePoints
            }
        }
    }

    if ($shouldList) {
        if ($restorePoints.Count -eq 0) {
            Write-TidyOutput -Message 'No restore points are currently registered.'
        }
        else {
            Write-TidyOutput -Message 'Current restore points (newest first):'
            foreach ($point in $restorePoints) {
                Write-TidyOutput -Message ("  #{0} — {1:G} — {2}" -f $point.SequenceNumber, $point.CreationTime, $point.Description)
            }
        }
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
    Write-TidyLog -Level Information -Message 'System Restore manager finished.'
}

if ($script:UsingResultFile) {
    $wasSuccessful = $script:OperationSucceeded -and ($script:TidyErrorLines.Count -eq 0)
    if ($wasSuccessful) {
        exit 0
    }

    exit 1
}

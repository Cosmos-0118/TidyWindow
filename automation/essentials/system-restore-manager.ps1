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

function Set-TidyServiceStartupMode {
    param(
        [Parameter(Mandatory = $true)]
        [string] $ServiceName,
        [ValidateSet('Automatic', 'AutomaticDelayedStart', 'Manual')]
        [string] $StartupType = 'Manual',
        [switch] $EnsureRunning
    )

    try {
        $service = Get-CimInstance -ClassName Win32_Service -Filter "Name='$ServiceName'" -ErrorAction Stop
    }
    catch {
        return $false
    }

    if ($null -eq $service -or [string]::IsNullOrWhiteSpace($service.StartMode)) {
        return $false
    }

    $changesMade = $false
    $needsStartupUpdate = $service.StartMode -eq 'Disabled'
    if (-not $needsStartupUpdate -and $StartupType -ne 'Manual') {
        $needsStartupUpdate = -not [string]::Equals($service.StartMode, $StartupType, [System.StringComparison]::OrdinalIgnoreCase)
    }

    if ($needsStartupUpdate) {
        Write-TidyOutput -Message ("Setting service {0} startup type to {1}." -f $ServiceName, $StartupType)
        try {
            Invoke-TidyCommand -Command {
                param($name, $type)
                Set-Service -Name $name -StartupType $type -ErrorAction Stop
            } -Arguments @($ServiceName, $StartupType) -Description ("Configure service {0}." -f $ServiceName) | Out-Null
            $changesMade = $true
        }
        catch {
            Write-TidyError -Message ("Failed to update service {0}. {1}" -f $ServiceName, $_.Exception.Message)
        }
    }

    if ($EnsureRunning.IsPresent) {
        try {
            $status = Get-Service -Name $ServiceName -ErrorAction Stop
            if ($status.Status -ne 'Running') {
                Write-TidyOutput -Message ("Starting service {0}." -f $ServiceName)
                Invoke-TidyCommand -Command {
                    param($name)
                    Start-Service -Name $name -ErrorAction Stop
                } -Arguments @($ServiceName) -Description ("Start service {0}." -f $ServiceName) | Out-Null
                $changesMade = $true
            }
        }
        catch {
            Write-TidyError -Message ("Failed to start service {0}. {1}" -f $ServiceName, $_.Exception.Message)
        }
    }

    return $changesMade
}

function Enable-TidySystemRestoreRegistry {
    $registryTargets = @(
        @{ Path = 'Registry::HKLM\Software\Microsoft\Windows NT\CurrentVersion\SystemRestore'; Names = @('DisableSR', 'DisableConfig') },
        @{ Path = 'Registry::HKLM\Software\Policies\Microsoft\Windows NT\SystemRestore'; Names = @('DisableSR', 'DisableConfig') }
    )

    $changesApplied = $false

    foreach ($target in $registryTargets) {
        $path = $target.Path
        $names = $target.Names

        try {
            if (-not (Test-Path -LiteralPath $path)) {
                continue
            }

            $current = Get-ItemProperty -LiteralPath $path -ErrorAction Stop
            if ($null -eq $current) {
                continue
            }

            foreach ($name in $names) {
                if (-not ($current.PSObject.Properties.Name -contains $name)) {
                    continue
                }

                $value = $current.$name
                $numeric = 0
                $shouldReset = $false

                if ($null -eq $value) {
                    $shouldReset = $true
                }
                elseif ([int]::TryParse($value.ToString(), [ref]$numeric)) {
                    if ($numeric -ne 0) {
                        $shouldReset = $true
                    }
                }
                else {
                    $shouldReset = $true
                }

                if ($shouldReset) {
                    Write-TidyOutput -Message ("Resetting System Restore flag {0} at {1}." -f $name, $path)
                    Set-ItemProperty -LiteralPath $path -Name $name -Value 0 -ErrorAction Stop
                    $changesApplied = $true
                }
            }
        }
        catch {
            Write-TidyError -Message ("Failed to adjust System Restore registry at {0}. {1}" -f $path, $_.Exception.Message)
        }
    }

    return $changesApplied
}

function Enable-TidyRestoreSupport {
    param(
        [Parameter(Mandatory = $true)]
        [string[]] $DriveLetters,
        [switch] $RepairServices
    )

    $anyChanges = $false

    $driveSet = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    $normalizedDrives = [System.Collections.Generic.List[string]]::new()

    foreach ($entry in @($DriveLetters)) {
        if ([string]::IsNullOrWhiteSpace($entry)) {
            continue
        }

        try {
            $normalized = Normalize-TidyDrive -Drive $entry
        }
        catch {
            Write-TidyError -Message ("Skipping invalid drive value '{0}'. {1}" -f $entry, $_.Exception.Message)
            continue
        }

        if ($driveSet.Add($normalized)) {
            $normalizedDrives.Add($normalized) | Out-Null
        }
    }

    $systemDriveValue = $env:SystemDrive
    if ([string]::IsNullOrWhiteSpace($systemDriveValue)) {
        $systemRoot = $env:SystemRoot
        if (-not [string]::IsNullOrWhiteSpace($systemRoot)) {
            try {
                $systemDriveValue = Split-Path -Path $systemRoot -Qualifier
            }
            catch {
                $systemDriveValue = $null
            }
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($systemDriveValue)) {
        try {
            $systemDriveNormalized = Normalize-TidyDrive -Drive $systemDriveValue
            $addedSystemDrive = $driveSet.Add($systemDriveNormalized)
            if ($addedSystemDrive) {
                $normalizedDrives.Insert(0, $systemDriveNormalized)
                Write-TidyOutput -Message ("Including system drive {0} in enable list." -f $systemDriveNormalized)
            }
            else {
                $currentIndex = $normalizedDrives.IndexOf($systemDriveNormalized)
                if ($currentIndex -gt 0) {
                    $normalizedDrives.RemoveAt($currentIndex)
                    $normalizedDrives.Insert(0, $systemDriveNormalized)
                    Write-TidyOutput -Message ("Prioritizing system drive {0} for System Restore enablement." -f $systemDriveNormalized)
                }
            }
        }
        catch {
            Write-TidyError -Message ("Unable to normalize system drive '{0}'. {1}" -f $systemDriveValue, $_.Exception.Message)
        }
    }

    if ($normalizedDrives.Count -eq 0) {
        Write-TidyError -Message 'No valid drive letters were supplied for System Restore enablement.'
        return $false
    }

    if ($RepairServices) {
        if (Enable-TidySystemRestoreRegistry) {
            $anyChanges = $true
        }

        foreach ($serviceName in @('srservice', 'VSS')) {
            if (Set-TidyServiceStartupMode -ServiceName $serviceName -EnsureRunning) {
                $anyChanges = $true
            }
        }
    }

    for ($i = 0; $i -lt $normalizedDrives.Count; $i++) {
        $drive = $normalizedDrives[$i]
        $targets = if ($i -eq 0) { @($drive) } else { @($normalizedDrives[0], $drive) }
        $displayTargets = [string]::Join(', ', $targets)
        Write-TidyOutput -Message ("Enabling System Restore on {0}" -f $displayTargets)
        try {
            Invoke-TidyCommand -Command {
                param($targetDrives)
                Enable-ComputerRestore -Drive $targetDrives -ErrorAction Stop
            } -Arguments @([object]$targets) -Description ("Enable System Restore {0}" -f $displayTargets) | Out-Null
            $anyChanges = $true
        }
        catch {
            $errorText = $_.Exception.Message
            if ($errorText -and $errorText -match 'Include System Drive') {
                Write-TidyError -Message ("System Restore reported that the system drive was missing while enabling protection on {0}. Verify that {1} remains included and retry." -f $displayTargets, $normalizedDrives[0])
            }
            else {
                Write-TidyError -Message ("Failed to enable System Restore on {0}. {1}" -f $displayTargets, $errorText)
            }
        }
    }

    return $anyChanges
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
        Enable-TidyRestoreSupport -DriveLetters $normalizedDrives -RepairServices
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
        $restorePoints = @(Get-TidyRestorePoints)
    }

    $restorePointCount = @($restorePoints).Count
    if ($keepCount -gt 0 -and $restorePointCount -gt $keepCount) {
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

        $restorePoints = @(Get-TidyRestorePoints)
        $restorePointCount = @($restorePoints).Count
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

        $restorePoints = @(Get-TidyRestorePoints)
        $restorePointCount = @($restorePoints).Count
    }

    if ($shouldCreate) {
        if (@($restorePoints).Count -eq 0) {
            $restorePoints = @(Get-TidyRestorePoints)
        }

        $frequencyMinutes = Get-TidyRestoreCreationFrequencyMinutes
        $restorePointCount = @($restorePoints).Count
        $latestPoint = if ($restorePointCount -gt 0) { $restorePoints[0] } else { $null }
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
            $creationAttempt = 0
            $autoRepairAttempted = $false

            while ($creationAttempt -lt 2 -and -not $creationSucceeded) {
                $creationAttempt++

                try {
                    Invoke-TidyCommand -Command {
                        param($description, $type)
                        Checkpoint-Computer -Description $description -RestorePointType $type -ErrorAction Stop
                    } -Arguments @($name, $RestorePointType) -Description 'Creating System Restore snapshot.' | Out-Null
                    $creationSucceeded = $true
                }
                catch {
                    $message = $_.Exception.Message
                    $normalized = if ($message) { $message.ToLowerInvariant() } else { '' }

                    if ($normalized -and $normalized -match 'already been created within the past') {
                        Write-TidyOutput -Message 'System Restore rejected the request because a recent restore point already exists. Skipping new creation.'
                        $creationSucceeded = $false
                        break
                    }

                    $requiresRepair = $normalized -and (
                        $normalized -match 'service cannot be started because it is disabled' -or
                        $normalized -match 'does not have enabled devices associated'
                    )

                    if ($requiresRepair -and -not $autoRepairAttempted) {
                        $autoRepairAttempted = $true
                        Write-TidyOutput -Message 'System Restore appears disabled. Attempting to enable required services and drive protection.'
                        Enable-TidyRestoreSupport -DriveLetters $normalizedDrives -RepairServices | Out-Null
                        continue
                    }

                    throw
                }
            }

            if ($creationSucceeded) {
                $restorePoints = @(Get-TidyRestorePoints)
                $restorePointCount = @($restorePoints).Count
            }
        }
    }

    if ($shouldList) {
        $restorePointCount = @($restorePoints).Count
        if ($restorePointCount -eq 0) {
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

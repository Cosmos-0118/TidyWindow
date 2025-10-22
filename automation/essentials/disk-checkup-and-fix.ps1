param(
    [string] $Volume = 'C:',
    [switch] $ScanOnly,
    [switch] $PerformRepair,
    [switch] $IncludeSurfaceScan,
    [switch] $ScheduleIfBusy,
    [switch] $SkipSmart,
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

    return [pscustomobject]@{
        ExitCode = $exitCode
        Output   = @($output)
    }
}

function Test-TidyAdmin {
    return [bool](New-Object System.Security.Principal.WindowsPrincipal([System.Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Resolve-VolumePath {
    param([string] $Value)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        throw 'Volume parameter cannot be empty.'
    }

    $trimmed = $Value.Trim()
    if ($trimmed.Length -eq 1) {
        $trimmed = "${trimmed}:"
    }

    if ($trimmed.Length -eq 2 -and $trimmed[1] -eq ':') {
        return $trimmed.ToUpperInvariant()
    }

    try {
        $resolved = (Get-Item -LiteralPath $trimmed -ErrorAction Stop).FullName
        if ($resolved.Length -ge 2 -and $resolved[1] -eq ':') {
            return $resolved.Substring(0, 2).ToUpperInvariant()
        }
    }
    catch {
        # Fall through to manual parsing.
    }

    if ($trimmed.StartsWith('\\?\', [System.StringComparison]::OrdinalIgnoreCase)) {
        $trimmed = $trimmed.Substring(4)
    }

    if ($trimmed.Length -ge 2 -and $trimmed[1] -eq ':') {
        return $trimmed.Substring(0, 2).ToUpperInvariant()
    }

    throw "Unable to resolve volume from input '$Value'."
}

function Get-SmartStatus {
    $results = @()

    $statusProvider = $null
    if (Get-Command -Name Get-CimInstance -ErrorAction SilentlyContinue) {
        $statusProvider = { Get-CimInstance -Namespace 'root/wmi' -ClassName 'MSStorageDriver_FailurePredictStatus' -ErrorAction Stop }
    }
    elseif (Get-Command -Name Get-WmiObject -ErrorAction SilentlyContinue) {
        $statusProvider = { Get-WmiObject -Namespace 'root/wmi' -Class 'MSStorageDriver_FailurePredictStatus' -ErrorAction Stop }
    }

    if ($null -ne $statusProvider) {
        try {
            $statusEntries = & $statusProvider
            foreach ($entry in @($statusEntries)) {
                $results += [pscustomobject]@{
                    DevicePath    = $entry.InstanceName
                    PredictFailure = [bool]$entry.PredictFailure
                    Reason         = $entry.Reason
                }
            }
        }
        catch {
            Write-TidyOutput -Message ("SMART predictive status provider not available ({0})." -f $_.Exception.Message)
        }
    }
    else {
        Write-TidyOutput -Message 'SMART predictive status provider APIs are not available on this platform.'
    }

    $detailProvider = $null
    if (Get-Command -Name Get-CimInstance -ErrorAction SilentlyContinue) {
        $detailProvider = { Get-CimInstance -Namespace 'root/microsoft/windows/storage' -ClassName 'MSFT_PhysicalDisk' -ErrorAction Stop }
    }
    elseif (Get-Command -Name Get-WmiObject -ErrorAction SilentlyContinue) {
        $detailProvider = { Get-WmiObject -Namespace 'root/microsoft/windows/storage' -Class 'MSFT_PhysicalDisk' -ErrorAction Stop }
    }

    if ($null -ne $detailProvider) {
        try {
            $detailEntries = & $detailProvider
            foreach ($detail in @($detailEntries)) {
                $results += [pscustomobject]@{
                    DevicePath     = if ($detail.PSObject.Properties['FriendlyName']) { $detail.FriendlyName } else { $detail.DeviceId }
                    PredictFailure = ($detail.PSObject.Properties['HealthStatus'] -and $detail.HealthStatus -ne 0)
                    Reason         = if ($detail.PSObject.Properties['HealthStatus']) { "HealthStatus=$($detail.HealthStatus); OperationalStatus=$($detail.OperationalStatus -join ',')" } else { 'Health telemetry not exposed.' }
                }
            }
        }
        catch {
            Write-TidyOutput -Message ("Physical disk health telemetry unavailable ({0})." -f $_.Exception.Message)
        }
    }

    return $results
}

try {
    if (-not (Test-TidyAdmin)) {
        throw 'Disk checkup requires an elevated PowerShell session. Restart as administrator.'
    }

    $targetVolume = Resolve-VolumePath -Value $Volume
    Write-TidyOutput -Message ("Target volume: {0}" -f $targetVolume)

    Write-TidyLog -Level Information -Message ("Starting disk check for volume {0}." -f $targetVolume)

    $arguments = @($targetVolume)
    $modeDescription = 'online scan'

    if ($IncludeSurfaceScan.IsPresent -and -not $PerformRepair.IsPresent) {
        throw 'Surface scan requires -PerformRepair (it implies an offline /r pass).'
    }

    if ($IncludeSurfaceScan.IsPresent) {
        $PerformRepair = $true
    }

    if ($PerformRepair.IsPresent) {
        $arguments += '/f'
        $modeDescription = 'repair'
        if ($IncludeSurfaceScan.IsPresent) {
            $arguments += '/r'
            $modeDescription = 'repair with surface scan'
        }
    }
    elseif (-not $ScanOnly.IsPresent) {
        $arguments += '/scan'
        $modeDescription = 'online scan'
    }

    Write-TidyOutput -Message ("Running CHKDSK in {0} mode." -f $modeDescription)
    $chkdskResult = Invoke-TidyCommand -Command { param($args) & chkdsk @args } -Arguments @($arguments) -Description ("CHKDSK {0}" -f ($arguments -join ' ')) | Select-Object -Last 1

    $scheduleRequired = $false
    foreach ($line in $chkdskResult.Output) {
        $text = [string]$line
        if ($text -match 'cannot lock current drive' -or $text -match 'schedule this volume to be checked') {
            $scheduleRequired = $true
            break
        }
    }

    if ($scheduleRequired -and $PerformRepair.IsPresent) {
        if ($ScheduleIfBusy.IsPresent) {
            Write-TidyOutput -Message 'Volume is busy. Scheduling repair for next reboot.'
            $confirmArgs = @($targetVolume, '/f')
            if ($IncludeSurfaceScan.IsPresent) {
                $confirmArgs += '/r'
            }

            $scheduleResult = Invoke-TidyCommand -Command { param($drive, $params) cmd.exe /c ("echo Y|chkdsk {0} {1}" -f $drive, ($params -join ' ')) } -Arguments @($targetVolume, $confirmArgs) -Description 'Scheduling CHKDSK at next reboot.' | Select-Object -Last 1
            foreach ($entry in $scheduleResult.Output) {
                if ([string]::IsNullOrWhiteSpace($entry)) {
                    continue
                }

                if ($entry -match 'will be checked the next time the system restarts') {
                    Write-TidyOutput -Message 'Repair successfully scheduled. Reboot to run the offline pass.'
                    break
                }
            }
        }
        else {
            Write-TidyOutput -Message 'Volume is busy. Re-run with -ScheduleIfBusy or manually confirm the prompt to repair at next boot.'
        }
    }

    if (-not $SkipSmart.IsPresent) {
        Write-TidyOutput -Message 'Collecting SMART health indicators.'
        $smartData = Get-SmartStatus
        if ($smartData.Count -eq 0) {
            Write-TidyOutput -Message 'SMART data unavailable on this platform or storage bus.'
        }
        else {
            foreach ($item in $smartData) {
                $status = if ($item.PredictFailure) { 'At Risk' } else { 'Healthy' }
                Write-TidyOutput -Message ("[{0}] {1}" -f $status, $item.DevicePath)
                if (-not [string]::IsNullOrWhiteSpace($item.Reason)) {
                    Write-TidyOutput -Message ("  ↳ Details: {0}" -f $item.Reason)
                }
            }
        }
    }

    Write-TidyOutput -Message 'Disk checkup completed.'
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
    Write-TidyLog -Level Information -Message 'Disk checkup script finished.'
}

if ($script:UsingResultFile) {
    $wasSuccessful = $script:OperationSucceeded -and ($script:TidyErrorLines.Count -eq 0)
    if ($wasSuccessful) {
        exit 0
    }

    exit 1
}

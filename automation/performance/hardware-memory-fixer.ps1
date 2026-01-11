[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [switch] $Detect,
    [switch] $ApplyFix,
    [switch] $RestoreCompression,
    [switch] $PassThru
)

set-strictmode -version latest
$ErrorActionPreference = 'Stop'

function Assert-Elevation {
    $isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if (-not $isAdmin) {
        throw 'Elevation required: run as administrator to modify boot settings or memory compression.'
    }
}

function Get-MemoryMetrics {
    $physicalBytes = 0
    try {
        $physicalBytes = (Get-CimInstance -ClassName Win32_PhysicalMemory | Measure-Object -Property Capacity -Sum).Sum
    }
    catch {
        Write-Warning "Failed to read DIMM capacity: $($_.Exception.Message)"
    }

    $reportedBytes = 0
    try {
        $reportedBytes = (Get-CimInstance -ClassName Win32_ComputerSystem).TotalPhysicalMemory
    }
    catch {
        Write-Warning "Failed to read reported memory: $($_.Exception.Message)"
    }

    $reservedBytes = [math]::Max(0, $physicalBytes - $reportedBytes)
    $compression = $null
    try {
        $mma = Get-MMAgent -ErrorAction Stop
        $compression = $mma.MemoryCompression
    }
    catch {
        Write-Warning "Unable to query memory compression: $($_.Exception.Message)"
    }

    return [pscustomobject]@{
        physicalGB        = [math]::Round($physicalBytes / 1GB, 2)
        reportedGB        = [math]::Round($reportedBytes / 1GB, 2)
        hardwareReservedMB = [math]::Round($reservedBytes / 1MB, 0)
        memoryCompression = $compression
    }
}

function Clear-BcdMemoryCaps {
    $targets = @('truncatememory', 'removememory', 'maxmem')
    foreach ($setting in $targets) {
        if ($PSCmdlet.ShouldProcess("BCD {current} $setting", 'Delete value')) {
            $output = & bcdedit /deletevalue {current} $setting 2>&1
            if ($LASTEXITCODE -ne 0) {
                if ($LASTEXITCODE -eq 1 -and $output -like '*The parameter is incorrect*') {
                    Write-Verbose "bcdedit deletevalue $setting not set (no change)."
                }
                else {
                    Write-Warning "bcdedit deletevalue $setting returned ${LASTEXITCODE}: $output"
                }
            }
        }
    }
}

function Disable-MemoryCompression {
    if ($PSCmdlet.ShouldProcess('Memory compression', 'Disable')) {
        Disable-MMAgent -MemoryCompression -ErrorAction SilentlyContinue
    }
}

function Enable-MemoryCompression {
    if ($PSCmdlet.ShouldProcess('Memory compression', 'Enable')) {
        Enable-MMAgent -MemoryCompression -ErrorAction SilentlyContinue
    }
}

$mode = 'Detect'
if ($ApplyFix) { $mode = 'ApplyFix' }
if ($RestoreCompression) { $mode = 'RestoreCompression' }

if (-not $ApplyFix -and -not $RestoreCompression -and -not $Detect) {
    $Detect = $true
}

if ($ApplyFix -or $RestoreCompression) {
    Assert-Elevation
}

if ($ApplyFix) {
    Clear-BcdMemoryCaps
    Disable-MemoryCompression
    Write-Host 'Cleared BCD memory caps (truncatememory/removememory/maxmem).' -ForegroundColor Cyan
    Write-Host 'Disabled memory compression via MMAgent.' -ForegroundColor Cyan
    Write-Host 'Reminder: open System Configuration (msconfig) > Boot > Advanced options and uncheck "Maximum memory" if set.' -ForegroundColor Yellow
}
elseif ($RestoreCompression) {
    Enable-MemoryCompression
    Write-Host 'Memory compression restored to enabled.' -ForegroundColor Cyan
}

$metrics = Get-MemoryMetrics

if ($PassThru) {
    [pscustomobject]@{
        mode              = $mode
        physicalGB        = $metrics.physicalGB
        reportedGB        = $metrics.reportedGB
        hardwareReservedMB = $metrics.hardwareReservedMB
        memoryCompression = $metrics.memoryCompression
    }
}

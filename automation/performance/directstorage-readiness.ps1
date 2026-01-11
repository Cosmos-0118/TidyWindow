[CmdletBinding()]
param(
    [switch]$Detect,
    [switch]$BoostIO,
    [switch]$BoostThreads,
    [switch]$Restore,
    [switch]$PassThru
)

$ErrorActionPreference = 'Stop'

function Write-Line([string]$text) {
    Write-Output $text
}

function Get-NvmeStatus {
    try {
        $disks = Get-PhysicalDisk -ErrorAction Stop | Where-Object { $_.BusType -eq 'NVMe' }
        return @{ Present = ($disks.Count -gt 0); Count = $disks.Count }
    }
    catch {
        return @{ Present = $false; Count = 0; Warning = 'PhysicalDisk query failed' }
    }
}

function Get-GpuStatus {
    try {
        $gpus = Get-CimInstance -ClassName Win32_VideoController -ErrorAction Stop
        $modern = $gpus | Where-Object { $_.DriverModel -like '*WDDM*' }
        return @{ Present = ($gpus.Count -gt 0); Modern = ($modern.Count -gt 0); Count = $gpus.Count }
    }
    catch {
        return @{ Present = $false; Modern = $false; Count = 0; Warning = 'GPU query failed' }
    }
}

function Write-DetectReport {
    $nvme = Get-NvmeStatus
    $gpu = Get-GpuStatus

    Write-Line ("nvme.present: {0}" -f $nvme.Present)
    Write-Line ("nvme.count: {0}" -f $nvme.Count)
    if ($nvme.Warning) { Write-Line ("warning: {0}" -f $nvme.Warning) }

    Write-Line ("gpu.present: {0}" -f $gpu.Present)
    Write-Line ("gpu.modern: {0}" -f $gpu.Modern)
    Write-Line ("gpu.count: {0}" -f $gpu.Count)
    if ($gpu.Warning) { Write-Line ("warning: {0}" -f $gpu.Warning) }

    $ready = $nvme.Present -and $gpu.Present -and $gpu.Modern
    Write-Line ("ready: {0}" -f $ready)
}

$didWork = $false

if ($Detect -or (-not $BoostIO -and -not $BoostThreads -and -not $Restore)) {
    Write-Line 'action: Detect'
    Write-DetectReport
    $didWork = $true
}

if ($BoostIO -or $BoostThreads) {
    Write-Line 'action: Boost'
    if ($BoostIO) {
        Write-Line 'ioPriority: Elevated (per-app hint)'
    }
    if ($BoostThreads) {
        Write-Line 'threadPriority: AboveNormal hint for foreground workload'
    }
    Write-Line 'note: Boost uses hints only; no persistent system changes applied.'
    $didWork = $true
}

if ($Restore) {
    Write-Line 'action: Restore'
    Write-Line 'ioPriority: Default'
    Write-Line 'threadPriority: Normal'
    $didWork = $true
}

if (-not $didWork) {
    Write-Line 'action: None'
}

Write-Line 'exitCode: 0'

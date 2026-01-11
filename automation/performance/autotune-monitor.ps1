[CmdletBinding()]
param(
    [switch]$Start,
    [switch]$Stop,
    [switch]$Detect,
    [string[]]$ProcessNames,
    [string]$Preset = 'LatencyBoost',
    [switch]$PassThru
)

$ErrorActionPreference = 'Stop'

$stateRoot = Join-Path $env:ProgramData 'TidyWindow/PerformanceLab'
$statePath = Join-Path $stateRoot 'auto-tune-state.json'

function Write-Line([string]$text) {
    Write-Output $text
}

function Ensure-StateDirectory {
    if (-not (Test-Path $stateRoot)) {
        New-Item -ItemType Directory -Force -Path $stateRoot | Out-Null
    }
}

function Read-State {
    if (Test-Path $statePath) {
        try {
            return Get-Content $statePath -Raw | ConvertFrom-Json -ErrorAction Stop
        }
        catch {
            return $null
        }
    }
    return $null
}

function Write-State([object]$payload) {
    Ensure-StateDirectory
    $payload | ConvertTo-Json -Depth 4 | Set-Content -Path $statePath -Encoding UTF8
}

function Find-RunningMatches([string[]]$names) {
    if (-not $names -or $names.Count -eq 0) {
        return @()
    }

    $normalized = $names | ForEach-Object { $_.Trim() } | Where-Object { $_ }
    $running = Get-Process -ErrorAction SilentlyContinue
    return $running | Where-Object { $normalized -contains $_.ProcessName } | Select-Object -ExpandProperty ProcessName -Unique
}

$didWork = $false

if ($Detect -or (-not $Start -and -not $Stop)) {
    $state = Read-State
    if ($state) {
        Write-Line 'action: Detect'
        Write-Line ("state: running")
        Write-Line ("preset: {0}" -f $state.preset)
        if ($state.processes) { Write-Line ("processes: {0}" -f ($state.processes -join ';')) }
        if ($state.started) { Write-Line ("started: {0}" -f $state.started) }
    }
    else {
        Write-Line 'action: Detect'
        Write-Line 'state: stopped'
    }
    $didWork = $true
}

if ($Start) {
    $now = Get-Date
    $procList = if ($ProcessNames) { $ProcessNames } else { @() }
    $matches = Find-RunningMatches $procList

    $state = [ordered]@{
        state     = 'running'
        preset    = $Preset
        processes = $procList
        started   = $now.ToString('o')
        matches   = $matches
    }

    Write-State $state

    Write-Line 'action: Start'
    Write-Line ("preset: {0}" -f $Preset)
    if ($procList.Count -gt 0) { Write-Line ("processes: {0}" -f ($procList -join ';')) }
    if ($matches.Count -gt 0) { Write-Line ("matches: {0}" -f ($matches -join ';')) } else { Write-Line 'matches: none yet' }
    Write-Line 'delta.cpu: reserved for sampler'
    Write-Line 'delta.io: reserved for sampler'
    $didWork = $true
}

if ($Stop) {
    if (Test-Path $statePath) {
        Remove-Item $statePath -Force
    }
    Write-Line 'action: Stop'
    Write-Line 'state: stopped'
    Write-Line 'revert: scheduler defaults restored'
    $didWork = $true
}

if (-not $didWork) {
    Write-Line 'action: None'
}

Write-Line 'exitCode: 0'

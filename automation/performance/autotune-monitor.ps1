[CmdletBinding()]
param(
    [switch]$Start,
    [switch]$Stop,
    [switch]$Detect,
    [Parameter(Position = 0)] [string[]]$ProcessNames,
    [string]$Preset = 'LatencyBoost',
    [switch]$PassThru,
    [Parameter(ValueFromRemainingArguments = $true)] [string[]]$ExtraArgs
)

# Merge any extra unnamed tokens as process names to avoid positional binding errors from UI callers.
$ProcessNames = @($ProcessNames) + @($ExtraArgs)
$ProcessNames = $ProcessNames | ForEach-Object { $_ -split '[;,]' } | ForEach-Object { $_.Trim() } | Where-Object { $_ } | Select-Object -Unique

$ErrorActionPreference = 'Stop'

$defaultRoot = Join-Path $env:ProgramData 'TidyWindow/PerformanceLab'
$fallbackRoot = Join-Path $env:LOCALAPPDATA 'TidyWindow/PerformanceLab'

function Set-StateRoot([string]$root) {
    $script:stateRoot = $root
    $script:statePath = Join-Path $root 'auto-tune-state.json'
    $script:logPath = Join-Path $root 'auto-tune-log.txt'
}

Set-StateRoot -root $defaultRoot

$fallbackStatePath = Join-Path $fallbackRoot 'auto-tune-state.json'
if (-not (Test-Path $statePath) -and (Test-Path $fallbackStatePath)) {
    # Prefer existing fallback state so detector reads the same location.
    Set-StateRoot -root $fallbackRoot
}
$scriptDir = Split-Path -Path $PSCommandPath -Parent
$schedulerScript = Join-Path $scriptDir 'scheduler-affinity.ps1'

function Write-Line([string]$text) {
    Write-Output $text
}

function Append-Log([string]$message) {
    Ensure-StateDirectory
    $stamp = [DateTimeOffset]::Now.ToString('yyyy-MM-dd HH:mm:ss zzz')
    Add-Content -Path $logPath -Value "[$stamp] $message" -Encoding UTF8
}

function Apply-Tuning([string[]]$names, [string]$preset) {
    $actions = @()
    $warnings = @()

    if (-not (Test-Path $schedulerScript)) {
        $warnings += 'scheduler-affinity.ps1 not found; tuning skipped.'
        return [pscustomobject]@{ actions = $actions; warnings = $warnings }
    }

    try {
        $result = & $schedulerScript -Preset $preset -ProcessNames $names -PassThru
        if ($result) {
            if ($result.actions) { $actions += $result.actions }
            if ($result.warnings) { $warnings += $result.warnings }
        }
    }
    catch {
        $warnings += "tuning failed: $_"
    }

    return [pscustomobject]@{ actions = $actions; warnings = $warnings }
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
    try {
        $payload | ConvertTo-Json -Depth 4 | Set-Content -Path $statePath -Encoding UTF8
    }
    catch [System.UnauthorizedAccessException] {
        if (-not (Test-Path $fallbackRoot)) {
            New-Item -ItemType Directory -Force -Path $fallbackRoot | Out-Null
        }

        $fallbackPath = Join-Path $fallbackRoot 'auto-tune-state.json'
        $payload | ConvertTo-Json -Depth 4 | Set-Content -Path $fallbackPath -Encoding UTF8

        Set-StateRoot -root $fallbackRoot
    }
}

function Find-RunningMatches([string[]]$names) {
    if (-not $names -or $names.Count -eq 0) {
        return @()
    }

    $normalized = $names | ForEach-Object {
        $trimmed = $_.Trim()
        if (-not $trimmed) { return }
        [System.IO.Path]::GetFileNameWithoutExtension($trimmed).ToLowerInvariant()
    } | Where-Object { $_ }

    $running = Get-Process -ErrorAction SilentlyContinue
    return $running | Where-Object { $normalized -contains $_.ProcessName.ToLowerInvariant() } | Select-Object -ExpandProperty ProcessName -Unique
}

$didWork = $false

if ($Detect -or (-not $Start -and -not $Stop)) {
    $state = Read-State
    Write-Line 'action: Detect'
    if ($state) {
        Write-Line 'state: armed'
        Write-Line ("preset: {0}" -f $state.preset)
        if ($state.processes) { Write-Line ("processes: {0}" -f ($state.processes -join ';')) }
        if ($state.lastDetected) { Write-Line ("lastDetected: {0}" -f $state.lastDetected) }
        if ($state.lastApplied) { Write-Line ("lastApplied: {0}" -f $state.lastApplied) }
        if ($state.actions) { Write-Line ("actions: {0}" -f ($state.actions -join '; ')) }
        if ($state.warnings) { Write-Line ("warnings: {0}" -f ($state.warnings -join '; ')) }
        if ($state.matches) { Write-Line ("matches: {0}" -f ($state.matches -join ';')) }
    }
    else {
        Write-Line 'state: stopped'
    }
    $didWork = $true
}

if ($Start) {
    $now = Get-Date
    $procList = if ($ProcessNames) { $ProcessNames } else { @() }
    $matches = Find-RunningMatches $procList

    $state = [ordered]@{
        state        = 'armed'
        preset       = $Preset
        processes    = $procList
        lastDetected = $now.ToString('o')
        matches      = $matches
    }

    if ($matches.Count -gt 0) {
        $applyResult = Apply-Tuning -names $matches -preset $Preset
        $state.lastApplied = (Get-Date).ToString('o')
        $state.actions = $applyResult.actions
        $state.warnings = $applyResult.warnings
        Append-Log ("applied {0} to {1}; actions: {2}; warnings: {3}" -f $Preset, ($matches -join ','), ($applyResult.actions -join ' | '), ($applyResult.warnings -join ' | '))
    }

    Write-State $state

    Write-Line 'action: Start'
    Write-Line ("preset: {0}" -f $Preset)
    if ($procList.Count -gt 0) { Write-Line ("processes: {0}" -f ($procList -join ';')) } else { Write-Line 'processes: none' }
    if ($matches.Count -gt 0) { Write-Line ("matches: {0}" -f ($matches -join ';')) } else { Write-Line 'matches: none yet' }
    if ($state.actions) { Write-Line ("actions: {0}" -f ($state.actions -join '; ')) }
    if ($state.warnings) { Write-Line ("warnings: {0}" -f ($state.warnings -join '; ')) }
    $didWork = $true
}

if ($Stop) {
    if (Test-Path $statePath) {
        Remove-Item $statePath -Force
    }

    Write-Line 'action: Stop'
    Write-Line 'state: stopped'
    $didWork = $true
}

if (-not $didWork) {
    Write-Line 'action: None'
}

Write-Line 'exitCode: 0'

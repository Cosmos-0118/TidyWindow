[CmdletBinding()]
param(
    [switch]$Start,
    [switch]$Stop,
    [switch]$Detect,
    [Parameter(Position = 0)] [string[]]$ProcessNames,
    [string]$Preset = 'LatencyBoost',
    [switch]$PassThru,
    [Parameter(ValueFromRemainingArguments = $true)] [string[]]$ExtraArgs,
    [switch]$WatchLoop,
    [int]$PollSeconds = 5,
    [int]$MaxMinutes = 180
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
    $script:watchPidPath = Join-Path $root 'auto-tune-watch.pid'
    $script:logPath = Join-Path $root 'auto-tune-log.txt'
}

Set-StateRoot -root $defaultRoot

$fallbackStatePath = Join-Path $fallbackRoot 'auto-tune-state.json'
if (-not (Test-Path $statePath) -and (Test-Path $fallbackStatePath)) {
    # Prefer existing fallback state so watcher/detector share the same path
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

function Resolve-PwshPath {
    $candidates = @(
        (Join-Path $PSHOME 'pwsh.exe'),
        (Join-Path $PSHOME 'powershell.exe'),
        'pwsh',
        'powershell'
    )

    foreach ($path in $candidates) {
        try {
            $cmd = Get-Command $path -ErrorAction SilentlyContinue
            if ($cmd) { return $cmd.Source }
        }
        catch {
            continue
        }
    }

    throw 'No PowerShell host found to launch watcher.'
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

        # Persist the fallback so subsequent reads use the same path (including watcher pid/log)
        Set-StateRoot -root $fallbackRoot
    }
}

function Write-WatcherId([int]$processId) {
    Ensure-StateDirectory
    Set-Content -Path $watchPidPath -Value $processId -Encoding ASCII
}

function Read-WatchPid {
    if (Test-Path $watchPidPath) {
        $txt = Get-Content $watchPidPath -Raw -ErrorAction SilentlyContinue
        if ($txt -as [int]) { return [int]$txt }
    }
    return $null
}

function Write-WatchPid([int]$watchProcessId) { Write-WatcherId -processId $watchProcessId }

function Clear-WatchPid {
    if (Test-Path $watchPidPath) { Remove-Item $watchPidPath -Force -ErrorAction SilentlyContinue }
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

function Start-WatchLoop([string[]]$names, [string]$preset, [int]$intervalSeconds, [int]$maxMinutes) {
    $deadline = (Get-Date).AddMinutes($maxMinutes)

    while ($true) {
        if (-not (Test-Path $statePath)) { break }
        if ((Get-Date) -gt $deadline) { break }

        $matches = Find-RunningMatches $names
        if ($matches.Count -gt 0) {
            $applyResult = Apply-Tuning -names $matches -preset $preset
            $state = Read-State
            if (-not $state) { $state = [ordered]@{} }
            $state.state = 'running'
            $state.preset = $preset
            $state.processes = $names
            $state.lastDetected = (Get-Date).ToString('o')
            $state.lastApplied = (Get-Date).ToString('o')
            $state.matches = $matches
            $state.actions = $applyResult.actions
            $state.warnings = $applyResult.warnings
            Write-State $state
            Append-Log ("watcher applied {0} to {1}; actions: {2}; warnings: {3}" -f $preset, ($matches -join ','), ($applyResult.actions -join ' | '), ($applyResult.warnings -join ' | '))
        }

        Start-Sleep -Seconds $intervalSeconds
    }
}

$intervalSeconds = if ($PollSeconds -lt 1) { 1 } else { $PollSeconds }
$maxMinutes = if ($MaxMinutes -lt 1) { 1 } else { $MaxMinutes }

# If invoked as the watcher, run the poller and exit early.
if ($WatchLoop) {
    Start-WatchLoop -names $ProcessNames -preset $Preset -intervalSeconds $intervalSeconds -maxMinutes $maxMinutes
    exit 0
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
        if ($state.lastDetected) { Write-Line ("lastDetected: {0}" -f $state.lastDetected) }
        if ($state.lastApplied) { Write-Line ("lastApplied: {0}" -f $state.lastApplied) }
        if ($state.actions) { Write-Line ("actions: {0}" -f ($state.actions -join '; ')) }
        if ($state.warnings) { Write-Line ("warnings: {0}" -f ($state.warnings -join '; ')) }
        $watchPid = Read-WatchPid
        if ($watchPid) { Write-Line ("watcherPid: {0}" -f $watchPid) }
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

    # Stop any previous watcher tied to an older session.
    $existing = Read-WatchPid
    if ($existing) {
        try {
            $oldProc = Get-Process -Id $existing -ErrorAction SilentlyContinue
            if ($oldProc) { $oldProc | Stop-Process -Force -ErrorAction SilentlyContinue }
        }
        catch { }
        Clear-WatchPid
    }

    $state = [ordered]@{
        state     = 'running'
        preset    = $Preset
        processes = $procList
        started   = $now.ToString('o')
        matches   = $matches
    }

    if ($matches.Count -gt 0) {
        $applyResult = Apply-Tuning -names $matches -preset $Preset
        $state.lastApplied = (Get-Date).ToString('o')
        $state.actions = $applyResult.actions
        $state.warnings = $applyResult.warnings
        Append-Log ("startup applied {0} to {1}; actions: {2}; warnings: {3}" -f $Preset, ($matches -join ','), ($applyResult.actions -join ' | '), ($applyResult.warnings -join ' | '))
    }

    Write-State $state

    # Launch a lightweight watcher as a detached process so new matches after start are captured.
    $watcherStarted = $false
    try {
        $pwshPath = Resolve-PwshPath
        $argsList = @('-NoLogo', '-NoProfile', '-File', $PSCommandPath, '-WatchLoop', '-Preset', $Preset, '-PollSeconds', $intervalSeconds, '-MaxMinutes', $maxMinutes)
        foreach ($p in $procList) { $argsList += @('-ProcessNames', $p) }
        $proc = Start-Process -FilePath $pwshPath -ArgumentList $argsList -WindowStyle Hidden -PassThru -WorkingDirectory $scriptDir
        if ($proc -and $proc.Id) {
            Write-WatchPid $proc.Id
            Append-Log ("watcher started pid={0} preset={1} processes={2}" -f $proc.Id, $Preset, ($procList -join ','))
            $watcherStarted = $true
        }
    }
    catch {
        Write-Line ("warning: failed to start watcher: {0}" -f $_)
        Append-Log ("watcher failed to start: {0}" -f $_)
    }

    if (-not $watcherStarted) {
        Append-Log "watcher missing after launch attempt; running inline watch loop"
        Start-WatchLoop -names $procList -preset $Preset -intervalSeconds $intervalSeconds -maxMinutes $maxMinutes
    }

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
    $watchPid = Read-WatchPid
    if ($watchPid) {
        try {
            $proc = Get-Process -Id $watchPid -ErrorAction SilentlyContinue
            if ($proc) { $proc | Stop-Process -Force -ErrorAction SilentlyContinue }
        }
        catch { }
        Clear-WatchPid
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

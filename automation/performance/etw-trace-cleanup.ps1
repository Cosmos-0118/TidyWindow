[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [switch] $Detect,
    [ValidateSet('Minimal','Aggressive')]
    [string] $StopTier,
    [switch] $RestoreDefaults,
    [switch] $PassThru
)

set-strictmode -version latest
$ErrorActionPreference = 'Stop'

function Assert-Elevation {
    $isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if (-not $isAdmin) {
        throw 'Elevation required: ETW cleanup needs administrator rights.'
    }
}

function Get-ActiveSessions {
    $output = & logman query -ets 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "logman query failed: $output"
    }

    $sessions = @()
    foreach ($line in $output) {
        $name = $line.Trim()
        if ([string]::IsNullOrWhiteSpace($name)) { continue }
        if ($name -like 'Data Collector*' -or $name -like '----------------*') { continue }
        # Skip headers like "Type" rows
        if ($name -match '^Type' -or $name -match '^===') { continue }
        $sessions += $name
    }

    return $sessions
}

function Save-Baseline {
    param([string] $Path,[string[]] $Sessions)
    if (-not (Test-Path (Split-Path $Path))) {
        New-Item -ItemType Directory -Path (Split-Path $Path) -Force | Out-Null
    }
    if (-not (Test-Path $Path)) {
        $Sessions | Set-Content -Path $Path -Encoding ASCII
    }
}

function Stop-Sessions {
    param([string[]] $Targets,[string[]] $AllowList,[bool] $IsAggressive)
    $stopped = @()
    $failures = @()
    $warnings = @()

    foreach ($session in $Targets) {
        if ($AllowList -contains $session) { continue }
        $args = @('stop', $session, '-ets')
        if ($PSCmdlet.ShouldProcess($session, 'logman stop')) {
            $output = & logman @args 2>&1
            if ($LASTEXITCODE -ne 0) {
                if ($output -like '*Data Collector Set was not found*') {
                    $warnings += "skip $session (not found): $output"
                }
                else {
                    $failures += "stop $session failed: $output"
                }
            }
            else {
                $stopped += $session
            }
        }
    }

    return [pscustomobject]@{ Stopped = $stopped; Failures = $failures; Warnings = $warnings }
}

function Start-Sessions {
    param([string[]] $Targets)
    $started = @()
    $failures = @()
    foreach ($session in $Targets) {
        $args = @('start', $session, '-ets')
        if ($PSCmdlet.ShouldProcess($session, 'logman start')) {
            $output = & logman @args 2>&1
            if ($LASTEXITCODE -ne 0) {
                $failures += "start $session failed: $output"
            }
            else {
                $started += $session
            }
        }
    }

    return [pscustomobject]@{ Started = $started; Failures = $failures }
}

Assert-Elevation

$intent = 'Detect'
if ($StopTier) { $intent = "Stop:$StopTier" }
elseif ($RestoreDefaults) { $intent = 'RestoreDefaults' }

$allowList = @(
    'NT Kernel Logger',
    'Circular Kernel Session',
    'EventLog-Application',
    'EventLog-System',
    'EventLog-Security',
    'DiagLog',
    'ReadyBoot'
)

$minimalTargets = @(
    'Diagtrack-Listener',
    'Diagtrack Session',
    'DiagLog',
    'NegoLog',
    'P2PLog'
)

$storageRoot = Join-Path $env:ProgramData 'TidyWindow\PerformanceLab'
$baselinePath = Join-Path $storageRoot 'etw-baseline.txt'

$sessions = Get-ActiveSessions
$failures = @()
$warnings = @()
$stopped = @()
$started = @()

switch ($intent) {
    'Detect' {
        # no-op besides reporting
    }
    'Stop:Minimal' {
        Save-Baseline -Path $baselinePath -Sessions $sessions
        $targets = $sessions | Where-Object { $minimalTargets -contains $_ }
        $result = Stop-Sessions -Targets $targets -AllowList $allowList -IsAggressive:$false
        $stopped = $result.Stopped
        $failures += $result.Failures
        $warnings = @($warnings + $result.Warnings)
    }
    'Stop:Aggressive' {
        Save-Baseline -Path $baselinePath -Sessions $sessions
        $targets = $sessions | Where-Object { $allowList -notcontains $_ }
        $result = Stop-Sessions -Targets $targets -AllowList $allowList -IsAggressive:$true
        $stopped = $result.Stopped
        $failures += $result.Failures
        $warnings = @($warnings + $result.Warnings)
    }
    'RestoreDefaults' {
        if (Test-Path $baselinePath) {
            $baseline = Get-Content -Path $baselinePath | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
            $result = Start-Sessions -Targets $baseline
            $started = $result.Started
            $failures += $result.Failures
        }
        else {
            # If we have no baseline, at least ensure allowlist is running
            $result = Start-Sessions -Targets $allowList
            $started = $result.Started
            $failures += $result.Failures
        }
    }
}

$payload = [pscustomobject]@{
    action = $intent
    activeSessions = $sessions
    stopped = $stopped
    started = $started
    baseline = (Test-Path $baselinePath)
}

if ($failures.Count -gt 0) {
    $payload | Add-Member -NotePropertyName failures -NotePropertyValue $failures
}

if ($warnings.Count -gt 0) {
    $payload | Add-Member -NotePropertyName warnings -NotePropertyValue $warnings
}

if ($PassThru) {
    $payload
}

if ($warnings.Count -gt 0) {
    foreach ($w in $warnings) { Write-Warning $w }
}

if ($failures.Count -gt 0) {
    foreach ($f in $failures) { Write-Warning $f }
    throw 'One or more ETW operations failed.'
}

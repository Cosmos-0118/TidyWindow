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
        if ($name -match '^Type' -or $name -match '^===') { continue }
        if ($name -like 'The command completed successfully*') { continue }
        if ($name -like 'There are no trace sessions*') { continue }

        $match = [regex]::Match($name, '^(?<session>.+?)\s{2,}')
        if ($match.Success) {
            $sessionName = $match.Groups['session'].Value.Trim()
        }
        else {
            $sessionName = $name
        }

        if (-not [string]::IsNullOrWhiteSpace($sessionName)) {
            $sessions += $sessionName
        }
    }

    return $sessions
}

function Save-Baseline {
    param([string] $Path,[string[]] $Sessions)
    if (-not (Test-Path (Split-Path $Path))) {
        New-Item -ItemType Directory -Path (Split-Path $Path) -Force | Out-Null
    }

    $Sessions | Set-Content -Path $Path -Encoding ASCII
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
                elseif ($output -like '*Access is denied*') {
                    $warnings += "skip $session (access denied): $output"
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
    $warnings = @()
    foreach ($session in $Targets) {
        $args = @('start', $session, '-ets')
        if ($PSCmdlet.ShouldProcess($session, 'logman start')) {
            $output = & logman @args 2>&1
            if ($LASTEXITCODE -ne 0) {
                if ($output -like '*already exists*') {
                    $warnings += "skip $session (already running): $output"
                }
                elseif ($output -like '*Access is denied*') {
                    $warnings += "skip $session (access denied): $output"
                }
                elseif ($output -like '*Data Collector Set was not found*') {
                    $warnings += "skip $session (not found): $output"
                }
                else {
                    $failures += "start $session failed: $output"
                }
            }
            else {
                $started += $session
            }
        }
    }

    return [pscustomobject]@{ Started = $started; Failures = $failures; Warnings = $warnings }
}

Assert-Elevation

$intent = 'Detect'
if ($StopTier) { $intent = "Stop:$StopTier" }
elseif ($RestoreDefaults) { $intent = 'RestoreDefaults' }

$allowList = @(
    'NT Kernel Logger',
    'Circular Kernel Context Logger',
    'EventLog-Application',
    'EventLog-System',
    'EventLog-Security',
    'DiagLog',
    'ReadyBoot',
    'UBPM'
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
 $note = $null

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
        if ($stopped.Count -eq 0 -and $failures.Count -eq 0) {
            $note = 'No eligible sessions to stop.'
        }
    }
    'Stop:Aggressive' {
        Save-Baseline -Path $baselinePath -Sessions $sessions
        $targets = $sessions | Where-Object { $allowList -notcontains $_ }
        $result = Stop-Sessions -Targets $targets -AllowList $allowList -IsAggressive:$true
        $stopped = $result.Stopped
        $failures += $result.Failures
        $warnings = @($warnings + $result.Warnings)
        if ($stopped.Count -eq 0 -and $failures.Count -eq 0) {
            $note = 'No eligible sessions to stop.'
        }
    }
    'RestoreDefaults' {
        if (Test-Path $baselinePath) {
            $baseline = Get-Content -Path $baselinePath | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
            $current = $sessions
            $targets = $baseline | Where-Object { $current -notcontains $_ }
            $result = Start-Sessions -Targets $targets
            $started = $result.Started
            $failures += $result.Failures
            $warnings = @($warnings + $result.Warnings)
            if ($started.Count -eq 0 -and $failures.Count -eq 0) {
                $note = 'All baseline sessions already running.'
            }
        }
        else {
            # If we have no baseline, at least ensure allowlist is running
            $current = $sessions
            $targets = $allowList | Where-Object { $current -notcontains $_ }
            $result = Start-Sessions -Targets $targets
            $started = $result.Started
            $failures += $result.Failures
            $warnings = @($warnings + $result.Warnings)
            if ($started.Count -eq 0 -and $failures.Count -eq 0) {
                $note = 'Allowlist sessions already running.'
            }
        }
    }
}

$stoppedCount = $stopped.Count
$startedCount = $started.Count
$warningCount = $warnings.Count
$failureCount = $failures.Count

$payload = [pscustomobject]@{
    action = $intent
    activeSessions = $sessions
    stopped = $stopped
    started = $started
    baseline = (Test-Path $baselinePath)
    stoppedCount = $stoppedCount
    startedCount = $startedCount
    warningCount = $warningCount
    failureCount = $failureCount
}

if ($note) {
    $payload | Add-Member -NotePropertyName note -NotePropertyValue $note
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

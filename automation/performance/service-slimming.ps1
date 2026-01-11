[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [ValidateSet('Balanced', 'Minimal')]
    [string] $Template = 'Balanced',
    [switch] $Restore,
    [string] $StatePath,
    [switch] $PassThru
)

set-strictmode -version latest
$ErrorActionPreference = 'Stop'

$defaultDir = Join-Path -Path $Env:ProgramData -ChildPath 'TidyWindow/PerformanceLab'
$fallbackDir = Join-Path -Path $Env:LocalAppData -ChildPath 'TidyWindow/PerformanceLab'
$statePath = if ([string]::IsNullOrWhiteSpace($StatePath)) {
    Join-Path -Path $defaultDir -ChildPath ('service-backup-{0:yyyyMMddHHmmss}.json' -f (Get-Date))
}
else {
    $StatePath
}

$templates = @{
    Balanced = @('DiagTrack', 'dmwappushservice', 'RetailDemo', 'XblAuthManager', 'XblGameSave', 'XboxNetApiSvc')
    Minimal  = @('DiagTrack', 'dmwappushservice', 'RetailDemo', 'XblAuthManager', 'XblGameSave', 'XboxNetApiSvc', 'OneSyncSvc', 'WalletService', 'MapsBroker', 'CDPSvc')
}

if (-not $templates.ContainsKey($Template)) {
    throw "Unknown template: $Template"
}

function Ensure-Directory($path) {
    $dir = Split-Path -Parent $path
    if (-not (Test-Path -LiteralPath $dir)) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
    }
}

function Snapshot-Services([string[]] $names) {
    $snap = @()
    foreach ($name in $names) {
        $svc = Get-Service -Name $name -ErrorAction SilentlyContinue
        if ($null -eq $svc) {
            $snap += [pscustomobject]@{ Name = $name; Exists = $false; Status = 'Unknown'; StartType = 'Unknown' }
            continue
        }

        $startType = try { (Get-CimInstance -ClassName Win32_Service -Filter "Name='$name'").StartMode } catch { $null }
        $snap += [pscustomobject]@{
            Name      = $svc.Name
            Display   = $svc.DisplayName
            Status    = $svc.Status.ToString()
            StartType = $startType
            Exists    = $true
        }
    }
    return , $snap
}

if ($Restore) {
    if (-not (Test-Path -LiteralPath $statePath)) {
        $fallbackPath = if ([string]::IsNullOrWhiteSpace($StatePath)) {
            Join-Path -Path $fallbackDir -ChildPath (Split-Path -Leaf $statePath)
        }

        if ($fallbackPath -and (Test-Path -LiteralPath $fallbackPath)) {
            $statePath = $fallbackPath
        }
        else {
            throw "Restore requested but state file not found: $statePath"
        }
    }

    $payload = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json -ErrorAction Stop
    foreach ($entry in $payload) {
        if (-not $entry.Exists) { continue }
        $svc = Get-Service -Name $entry.Name -ErrorAction SilentlyContinue
        if ($null -eq $svc) { continue }

        if ($PSCmdlet.ShouldProcess($entry.Name, "Set start type to $($entry.StartType)")) {
            if ($entry.StartType -match 'Disabled|Manual|Automatic|Auto') {
                Set-Service -Name $entry.Name -StartupType $entry.StartType -ErrorAction SilentlyContinue
            }
        }

        if ($PSCmdlet.ShouldProcess($entry.Name, "Restore status $($entry.Status)")) {
            if ($entry.Status -eq 'Running' -and $svc.Status -ne 'Running') {
                Start-Service -Name $entry.Name -ErrorAction SilentlyContinue
            }
            elseif ($entry.Status -eq 'Stopped' -and $svc.Status -eq 'Running') {
                Stop-Service -Name $entry.Name -Force -ErrorAction SilentlyContinue
            }
        }
    }

    if ($PassThru) {
        [pscustomobject]@{
            mode      = 'Restored'
            statePath = $statePath
            restored  = @($payload | Where-Object { $_.Exists }).Count
            missing   = @($payload | Where-Object { -not $_.Exists }).Count
        }
    }
    return
}

$targets = $templates[$Template]
$baseline = Snapshot-Services -names $targets
try {
    Ensure-Directory -path $statePath
}
catch [System.UnauthorizedAccessException] {
    if ([string]::IsNullOrWhiteSpace($StatePath)) {
        $statePath = Join-Path -Path $fallbackDir -ChildPath (Split-Path -Leaf $statePath)
        Ensure-Directory -path $statePath
        Write-Warning "State path was not writable; using $statePath instead."
    }
    else {
        throw
    }
}

try {
    $baseline | ConvertTo-Json -Depth 5 | Out-File -LiteralPath $statePath -Encoding UTF8 -Force
}
catch [System.UnauthorizedAccessException] {
    if ([string]::IsNullOrWhiteSpace($StatePath)) {
        $statePath = Join-Path -Path $fallbackDir -ChildPath (Split-Path -Leaf $statePath)
        Ensure-Directory -path $statePath
        $baseline | ConvertTo-Json -Depth 5 | Out-File -LiteralPath $statePath -Encoding UTF8 -Force
        Write-Warning "State path was not writable; using $statePath instead."
    }
    else {
        throw
    }
}

foreach ($svc in $baseline | Where-Object { $_.Exists }) {
    $desiredStart = if ($Template -eq 'Minimal') { 'Disabled' } else { 'Manual' }

    if ($PSCmdlet.ShouldProcess($svc.Name, "Set start type to $desiredStart")) {
        Set-Service -Name $svc.Name -StartupType $desiredStart -ErrorAction SilentlyContinue
    }

    if ($PSCmdlet.ShouldProcess($svc.Name, 'Stop service')) {
        if ((Get-Service -Name $svc.Name).Status -eq 'Running') {
            Stop-Service -Name $svc.Name -Force -ErrorAction SilentlyContinue
        }
    }
}

if ($PassThru) {
    [pscustomobject]@{
        mode      = 'Applied'
        template  = $Template
        statePath = $statePath
        targeted  = $targets.Count
        missing   = @($baseline | Where-Object { -not $_.Exists }).Count
    }
}

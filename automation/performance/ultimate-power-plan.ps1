[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [switch] $Enable,
    [switch] $Restore,
    [switch] $PassThru,
    [string] $StatePath
)

set-strictmode -version latest
$ErrorActionPreference = 'Stop'

function Get-ActiveScheme {
    $output = & powercfg /getactivescheme 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to query active scheme: $output"
    }

    $match = [regex]::Match($output, 'GUID:\s*([0-9a-fA-F-]{36})\s*\((.+)\)')
    if (-not $match.Success) {
        return @{ Guid = $null; Name = $output.Trim() }
    }

    return @{ Guid = $match.Groups[1].Value; Name = $match.Groups[2].Value }
}

function Ensure-Directory($path) {
    $dir = Split-Path -Parent $path
    if (-not (Test-Path -LiteralPath $dir)) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
    }
}

function Test-WritablePath([string] $path) {
    try {
        Ensure-Directory -path $path
        $probe = Join-Path -Path (Split-Path -Parent $path) -ChildPath ("$([guid]::NewGuid().ToString()).tmp")
        'probe' | Out-File -LiteralPath $probe -Encoding UTF8 -Force -ErrorAction Stop
        Remove-Item -LiteralPath $probe -Force -ErrorAction SilentlyContinue
        return $true
    }
    catch {
        return $false
    }
}

function Write-StateFile([string] $path, [psobject] $stateObject) {
    try {
        Ensure-Directory -path $path
        $stateObject | ConvertTo-Json -Depth 4 -ErrorAction Stop | Out-File -LiteralPath $path -Encoding UTF8 -Force -ErrorAction Stop
        return $true
    }
    catch {
        Write-Verbose "Write-StateFile failed for $($path): $($_.Exception.Message)"
        return $false
    }
}

function Get-UltimateSchemeGuids {
    $list = & powercfg /list 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Verbose "powercfg /list failed: $list"
        return @()
    }

    $regex = [regex]'Power Scheme GUID:\s*([0-9a-fA-F-]{36})\s*\((.+)\)'
    $guids = @()
    foreach ($line in $list) {
        $match = $regex.Match($line)
        if (-not $match.Success) { continue }
        $name = $match.Groups[2].Value.Trim()
        if ($name -like 'Ultimate Performance') {
            $guids += $match.Groups[1].Value
        }
    }

    return $guids
}

function Get-UltimateSchemeGuid {
    return (Get-UltimateSchemeGuids | Select-Object -First 1)
}

function Cleanup-UltimateDuplicates([string] $keepGuid) {
    $all = Get-UltimateSchemeGuids
    if (-not $all -or -not $keepGuid) { return }

    $duplicates = $all | Where-Object { $_ -and ($_ -ne $keepGuid) }
    foreach ($guid in $duplicates) {
        if ($PSCmdlet.ShouldProcess("Ultimate Performance $guid", 'Delete duplicate')) {
            try {
                & powercfg -delete $guid | Out-Null
            }
            catch {
                Write-Warning "Failed to delete duplicate Ultimate plan $($guid): $($_)"
            }
        }
    }
}

$ultimateGuid = 'e9a42b02-d5df-448d-aa00-03f14749eb61'
$balancedGuid = '381b4222-f694-41f0-9685-ff5bb260df2e'

if (-not $Enable -and -not $Restore) {
    $Enable = $true
}

$primaryStatePath = Join-Path -Path $Env:ProgramData -ChildPath 'TidyWindow/PerformanceLab/powerplan-state.json'
$fallbackStatePath = Join-Path -Path $Env:LocalAppData -ChildPath 'TidyWindow/PerformanceLab/powerplan-state.json'

$statePathParam = $StatePath
$resolvedStatePath = if ([string]::IsNullOrWhiteSpace($statePathParam)) { $primaryStatePath } else { $statePathParam }

$isElevated = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isElevated -and [string]::IsNullOrWhiteSpace($statePathParam)) {
    $resolvedStatePath = $fallbackStatePath
}

if ([string]::IsNullOrWhiteSpace($statePathParam)) {
    if (-not (Test-WritablePath -path $resolvedStatePath) -and (Test-WritablePath -path $fallbackStatePath)) {
        $resolvedStatePath = $fallbackStatePath
        Write-Warning "State path was not writable; using $resolvedStatePath instead."
    }
}

Write-Verbose "StatePath parameter raw: '$statePathParam'"

$state = $null
if (Test-Path -LiteralPath $resolvedStatePath) {
    try {
        $state = Get-Content -LiteralPath $resolvedStatePath -Raw | ConvertFrom-Json -ErrorAction Stop
    }
    catch {
        Write-Warning "Existing state file is unreadable: $resolvedStatePath"
    }
}
elseif (-not [string]::IsNullOrWhiteSpace($statePathParam) -and (Test-Path -LiteralPath $fallbackStatePath)) {
    try {
        $state = Get-Content -LiteralPath $fallbackStatePath -Raw | ConvertFrom-Json -ErrorAction Stop
        $resolvedStatePath = $fallbackStatePath
    }
    catch {
        Write-Warning "Fallback state file is unreadable: $fallbackStatePath"
    }
}

if ($Enable) {
    $current = Get-ActiveScheme
    $stateToPersist = [pscustomobject]@{
        capturedAtUtc = (Get-Date).ToUniversalTime()
        activeGuid    = $current.Guid
        activeName    = $current.Name
    }

    Write-Verbose "State target before write: $resolvedStatePath"
    $stateWritten = Write-StateFile -path $resolvedStatePath -stateObject $stateToPersist
    if (-not $stateWritten -and [string]::IsNullOrWhiteSpace($statePathParam)) {
        Write-Verbose "Primary state write failed; attempting fallback $fallbackStatePath"
        $stateWritten = Write-StateFile -path $fallbackStatePath -stateObject $stateToPersist
        if ($stateWritten) {
            $resolvedStatePath = $fallbackStatePath
            Write-Verbose "Fallback state write succeeded at $resolvedStatePath"
            Write-Warning "State path was not writable; using $resolvedStatePath instead."
        }
    }

    if (-not $stateWritten) {
        throw "Unable to persist state to $resolvedStatePath."
    }

    $targetGuid = Get-UltimateSchemeGuid
    if (-not $targetGuid) {
        if ($PSCmdlet.ShouldProcess('Ultimate Performance power plan', 'Create')) {
            $dupOutput = & powercfg -duplicatescheme $ultimateGuid 2>&1
            $guidMatch = [regex]::Match($dupOutput, 'GUID:\s*([0-9a-fA-F-]{36})')
            $targetGuid = if ($guidMatch.Success) { $guidMatch.Groups[1].Value } else { Get-UltimateSchemeGuid }
        }
    }

    if (-not $targetGuid) {
        throw 'Unable to locate Ultimate Performance scheme after creation.'
    }

    if ($PSCmdlet.ShouldProcess("Ultimate Performance $targetGuid", 'Activate')) {
        & powercfg -setactive $targetGuid | Out-Null
        $active = Get-ActiveScheme
        if (-not $active.Guid -or $active.Guid -ne $targetGuid) {
            throw "Failed to activate Ultimate Performance (expected $targetGuid, got $($active.Guid))."
        }
    }

    Cleanup-UltimateDuplicates -keepGuid $targetGuid

    if ($PassThru) {
        [pscustomobject]@{
            mode         = 'Enabled'
            ultimateGuid = $targetGuid
            previousGuid = $current.Guid
            previousName = $current.Name
            statePath    = $resolvedStatePath
        }
    }
    return
}

# Restore path
$targetGuid = if ($state -and $state.activeGuid) { $state.activeGuid } else { $null }
$targetName = if ($state -and $state.activeName) { $state.activeName } else { $null }
if ([string]::IsNullOrWhiteSpace($targetGuid)) {
    $targetGuid = $balancedGuid
    $targetName = 'Balanced (fallback)'
}

if ($PSCmdlet.ShouldProcess("Power plan $targetGuid", 'Restore')) {
    & powercfg -setactive $targetGuid | Out-Null
}

if ($PassThru) {
    [pscustomobject]@{
        mode         = 'Restored'
        restoredGuid = $targetGuid
        restoredName = $targetName
        statePath    = $resolvedStatePath
    }
}

param(
    [switch] $IncludeSystemComponents,
    [switch] $IncludeUpdates,
    [switch] $IncludeWinget,
    [switch] $IncludeUserEntries,
    [switch] $PlanOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$callerPath = $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($callerPath)) {
    $callerPath = $PSCommandPath
}

$scriptDirectory = Split-Path -Parent $callerPath
if ([string]::IsNullOrWhiteSpace($scriptDirectory)) {
    $scriptDirectory = (Get-Location).Path
}

$modulePath = Join-Path -Path $scriptDirectory -ChildPath '..\modules\TidyWindow.Automation\TidyWindow.Automation.psm1'
$modulePath = [System.IO.Path]::GetFullPath($modulePath)
if (-not (Test-Path -LiteralPath $modulePath)) {
    throw "Automation module not found at path '$modulePath'."
}

Import-Module $modulePath -Force

$includeWingetFlag = $true
if ($PSBoundParameters.ContainsKey('IncludeWinget')) {
    $includeWingetFlag = $IncludeWinget.IsPresent
}

$includeUserFlag = $true
if ($PSBoundParameters.ContainsKey('IncludeUserEntries')) {
    $includeUserFlag = $IncludeUserEntries.IsPresent
}

$plan = @(
    'Enumerate HKLM uninstall registry (64-bit view)',
    'Enumerate HKLM WOW6432Node uninstall registry (32-bit view)'
)

if ($includeUserFlag) {
    $plan += 'Enumerate HKCU uninstall registry for per-user installers'
}

if ($includeWingetFlag) {
    $plan += 'Merge registry payload with winget list metadata'
}

if ($PlanOnly.IsPresent) {
    $result = [pscustomobject]@{
        generatedAt = [DateTimeOffset]::UtcNow.ToString('o')
        isDryRun = $true
        durationMs = 0
        plan = $plan
        apps = @()
        warnings = @()
    }

    $result | ConvertTo-Json -Depth 6 -Compress
    return
}

$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
$inventory = Get-TidyInstalledAppInventory -IncludeSystemComponents:$IncludeSystemComponents.IsPresent -IncludeUpdates:$IncludeUpdates.IsPresent -IncludeWinget:$includeWingetFlag -IncludeUserHives:$includeUserFlag
$stopwatch.Stop()

$result = [pscustomobject]@{
    generatedAt = [DateTimeOffset]::UtcNow.ToString('o')
    durationMs = [int][Math]::Max(0, $stopwatch.ElapsedMilliseconds)
    isDryRun = $false
    plan = $plan
    apps = $inventory.Apps
    warnings = $inventory.Warnings
}

$result | ConvertTo-Json -Depth 6 -Compress

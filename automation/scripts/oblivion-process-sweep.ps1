[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $AppId,
    [string] $InventoryPath,
    [int] $MaxPasses = 3,
    [int] $WaitSeconds = 3,
    [switch] $DryRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Resolve-ScriptPath {
    param([string] $Relative)

    $root = if ($PSScriptRoot) { $PSScriptRoot } elseif ($PSCommandPath) { Split-Path -Parent $PSCommandPath } else { (Get-Location).Path }
    return [System.IO.Path]::GetFullPath((Join-Path -Path $root -ChildPath $Relative))
}

function Import-TidyModule {
    $modulePath = Resolve-ScriptPath -Relative '..\modules\TidyWindow.Automation\TidyWindow.Automation.psm1'
    if (-not (Test-Path -LiteralPath $modulePath)) {
        throw "Automation module not found at '$modulePath'."
    }

    Import-Module $modulePath -Force
}

function Resolve-DefaultInventoryPath {
    $path = Resolve-ScriptPath -Relative '..\..\data\catalog\oblivion-inventory.json'
    if (Test-Path -LiteralPath $path) {
        return $path
    }

    throw 'Inventory file path must be provided.'
}

function ConvertTo-JsonObject {
    param([string] $Json, [string] $Context)

    if ([string]::IsNullOrWhiteSpace($Json)) {
        throw "No JSON content for $Context."
    }

    try {
        return $Json | ConvertFrom-Json -Depth 8 -ErrorAction Stop
    }
    catch {
        throw "Failed to parse $Context JSON: $($_.Exception.Message)"
    }
}

function Load-Inventory {
    param([string] $Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "Inventory file '$Path' not found."
    }

    $json = Get-Content -LiteralPath $Path -Raw -ErrorAction Stop
    return ConvertTo-JsonObject -Json $json -Context 'inventory'
}

function Get-OblivionApp {
    param([psobject] $Inventory, [string] $Identifier)

    $app = $Inventory.apps | Where-Object { $_.appId -eq $Identifier } | Select-Object -First 1
    if (-not $app) {
        throw "Application '$Identifier' not found in inventory."
    }

    return $app
}

Import-TidyModule
if (-not $InventoryPath) {
    $InventoryPath = Resolve-DefaultInventoryPath
}

$inventory = Load-Inventory -Path $InventoryPath
$app = Get-OblivionApp -Inventory $inventory -Identifier $AppId

Write-TidyStructuredEvent -Type 'kickoff' -Payload @{ appId = $AppId; name = $app.name; version = $app.version; stage = 'ProcessSweep' }
Write-TidyStructuredEvent -Type 'stage' -Payload @{ stage = 'ProcessSweep'; status = 'started' }

$processSweep = Invoke-OblivionProcessSweep -App $app -DryRun:$DryRun -MaxPasses $MaxPasses -WaitSeconds $WaitSeconds
$attemptLog = if ($processSweep.AttemptLog) { @($processSweep.AttemptLog) } else { @() }
foreach ($attempt in $attemptLog) {
    Write-TidyStructuredEvent -Type 'processSweepAttempt' -Payload $attempt
}
$remainingCount = if ($processSweep.Remaining -is [System.Collections.ICollection]) { $processSweep.Remaining.Count } elseif ($processSweep.Remaining) { (@($processSweep.Remaining)).Count } else { 0 }

Write-TidyStructuredEvent -Type 'stage' -Payload @{
    stage     = 'ProcessSweep'
    status    = 'completed'
    detected  = $processSweep.Detected
    stopped   = $processSweep.Stopped
    attempts  = $processSweep.Attempts
    remaining = $remainingCount
}

Write-TidyStructuredEvent -Type 'summary' -Payload @{
    stage     = 'ProcessSweep'
    appId     = $AppId
    detected  = $processSweep.Detected
    stopped   = $processSweep.Stopped
    remaining = $remainingCount
    dryRun    = [bool]$DryRun
}


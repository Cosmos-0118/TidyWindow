[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $AppId,
    [string] $InventoryPath,
    [string] $SelectionPath,
    [switch] $AutoSelectAll,
    [switch] $WaitForSelection,
    [int] $SelectionTimeoutSeconds = 600,
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
    $modulePath = Resolve-ScriptPath -Relative '..\modules\TidyWindow.Automation.psm1'
    if (-not (Test-Path -LiteralPath $modulePath)) {
        throw "Automation module not found at '$modulePath'."
    }

    Import-Module $modulePath -Force
}

Import-TidyModule

function Resolve-DefaultInventoryPath {
    $path = Resolve-ScriptPath -Relative '..\..\data\catalog\oblivion-inventory.json'
    if (Test-Path -LiteralPath $path) {
        return $path
    }

    throw 'Inventory file path must be provided.'
}

if (-not $InventoryPath) {
    $InventoryPath = Resolve-DefaultInventoryPath
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

$inventory = Load-Inventory -Path $InventoryPath
$app = $inventory.apps | Where-Object { $_.appId -eq $AppId } | Select-Object -First 1
if (-not $app) {
    throw "Application '$AppId' not found in inventory."
}

$runDirectory = New-TidyFeatureRunDirectory -FeatureName 'Oblivion' -AppIdentifier $AppId
$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$logPath = Join-Path -Path $runDirectory -ChildPath "oblivion-run-$timestamp.json"

Write-TidyStructuredEvent -Type 'kickoff' -Payload @{ appId = $AppId; name = $app.name; version = $app.version; uninstallCommand = $app.uninstallCommand }

# Stage: Default uninstall
Write-TidyStructuredEvent -Type 'stage' -Payload @{ stage = 'DefaultUninstall'; status = 'started' }
$uninstallCommand = if ($app.quietUninstallCommand) { $app.quietUninstallCommand } elseif ($app.uninstallCommand) { $app.uninstallCommand } else { $null }
$uninstallResult = $null
if ($uninstallCommand) {
    if ($DryRun) {
        $uninstallResult = @{ exitCode = 0; output = 'dry-run'; errors = ''; durationMs = 0 }
    }
    else {
        $uninstallResult = Invoke-TidyCommandLine -CommandLine $uninstallCommand
    }
}
else {
    $uninstallResult = @{ exitCode = 0; output = 'No uninstall command found'; errors = ''; durationMs = 0 }
}
Write-TidyStructuredEvent -Type 'stage' -Payload @{ stage = 'DefaultUninstall'; status = 'completed'; exitCode = $uninstallResult.exitCode }

# Stage: Process sweep
Write-TidyStructuredEvent -Type 'stage' -Payload @{ stage = 'ProcessSweep'; status = 'started' }
$processSnapshot = Get-TidyProcessSnapshot
$relatedProcesses = Find-TidyRelatedProcesses -App $app -Snapshot $processSnapshot -MaxMatches 50
$stoppedCount = Stop-TidyProcesses -Processes $relatedProcesses -DryRun:$DryRun -Force
Write-TidyStructuredEvent -Type 'stage' -Payload @{ stage = 'ProcessSweep'; status = 'completed'; detected = $relatedProcesses.Count; stopped = $stoppedCount }

# Stage: Artifact discovery
Write-TidyStructuredEvent -Type 'stage' -Payload @{ stage = 'ArtifactDiscovery'; status = 'started' }
$artifacts = Get-TidyArtifacts -App $app
Write-TidyStructuredEvent -Type 'artifacts' -Payload @{ count = $artifacts.Count; items = $artifacts }
Write-TidyStructuredEvent -Type 'stage' -Payload @{ stage = 'ArtifactDiscovery'; status = 'completed'; count = $artifacts.Count }

$selectedIds = $null
if ($AutoSelectAll) {
    $selectedIds = $artifacts | ForEach-Object { $_.id }
}
elseif ($SelectionPath) {
    if (-not (Test-Path -LiteralPath $SelectionPath) -and $WaitForSelection) {
        $deadline = (Get-Date).AddSeconds($SelectionTimeoutSeconds)
        Write-TidyStructuredEvent -Type 'awaitingSelection' -Payload @{ selectionPath = $SelectionPath; timeoutSeconds = $SelectionTimeoutSeconds }
        while ((Get-Date) -lt $deadline) {
            if (Test-Path -LiteralPath $SelectionPath) { break }
            Start-Sleep -Milliseconds 500
        }
    }

    if (Test-Path -LiteralPath $SelectionPath) {
        $selectionJson = Get-Content -LiteralPath $SelectionPath -Raw -ErrorAction Stop
        $selectionPayload = ConvertTo-JsonObject -Json $selectionJson -Context 'selection'
        if ($selectionPayload -is [System.Collections.IEnumerable] -and -not ($selectionPayload -is [string])) {
            $selectedIds = @($selectionPayload)
        }
        elseif ($selectionPayload -and $selectionPayload.PSObject.Properties['selectedIds']) {
            $selectedIds = @($selectionPayload.selectedIds)
        }
        elseif ($selectionPayload -and $selectionPayload.PSObject.Properties['removeAll'] -and $selectionPayload.removeAll) {
            $selectedIds = $artifacts | ForEach-Object { $_.id }
        }
        elseif ($selectionPayload -and $selectionPayload.PSObject.Properties['removeNone'] -and $selectionPayload.removeNone) {
            $selectedIds = @()
        }

        if ($selectionPayload -and $selectionPayload.PSObject.Properties['deselectIds'] -and $selectedIds) {
            $deselect = @($selectionPayload.deselectIds)
            $selectedIds = @($selectedIds | Where-Object { $deselect -notcontains $_ })
        }
    }
}

if (-not $selectedIds -or $selectedIds.Count -eq 0) {
    $selectedIds = $artifacts | ForEach-Object { $_.id }
}

$selectedArtifacts = $artifacts | Where-Object { $selectedIds -contains $_.id }
Write-TidyStructuredEvent -Type 'selection' -Payload @{ selected = $selectedArtifacts.Count; total = $artifacts.Count }

# Stage: Cleanup execution
Write-TidyStructuredEvent -Type 'stage' -Payload @{ stage = 'Cleanup'; status = 'started' }
$removalResult = Remove-TidyArtifacts -Artifacts $selectedArtifacts -DryRun:$DryRun
foreach ($entry in $removalResult.Results) {
    Write-TidyStructuredEvent -Type 'artifactResult' -Payload $entry
}

Write-TidyStructuredEvent -Type 'stage' -Payload @{ stage = 'Cleanup'; status = 'completed'; removed = $removalResult.RemovedCount; failures = $removalResult.FailureCount }

$failures = $removalResult.Results | Where-Object { -not $_.success }
$summary = @{
    appId      = $AppId
    removed    = $removalResult.RemovedCount
    skipped    = $artifacts.Count - $removalResult.RemovedCount
    failures   = $failures
    freedBytes = $removalResult.FreedBytes
    timestamp  = [DateTimeOffset]::UtcNow.ToString('o')
}

Write-TidyRunLog -Path $logPath -Payload $summary
Write-TidyStructuredEvent -Type 'summary' -Payload $summary

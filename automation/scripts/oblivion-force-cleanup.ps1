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

function Resolve-ArtifactSelection {
    param(
        [psobject[]] $Artifacts,
        [string] $SelectionPath,
        [switch] $AutoSelectAll,
        [switch] $WaitForSelection,
        [int] $SelectionTimeoutSeconds
    )

    $selectedIds = $null
    if ($AutoSelectAll) {
        $selectedIds = $Artifacts | ForEach-Object { $_.id }
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
                $selectedIds = $Artifacts | ForEach-Object { $_.id }
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

    $selectedIds = @($selectedIds | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) })

    if ($selectedIds.Count -eq 0) {
        $selectedIds = @($Artifacts | ForEach-Object { $_.id })
    }

    return $selectedIds
}

$script:OblivionVerificationKnownTypes = @('Registry', 'File', 'Directory', 'Service')
function Test-LocalArtifactRemoved {
    param([psobject] $Artifact)

    if (-not $Artifact -or -not $Artifact.path) { return $null }

    switch ($Artifact.type) {
        'Registry' {
            try { return -not (Test-Path -LiteralPath $Artifact.path) } catch { return $null }
        }
        'File' { try { return -not (Test-Path -LiteralPath $Artifact.path) } catch { return $null } }
        'Directory' { try { return -not (Test-Path -LiteralPath $Artifact.path) } catch { return $null } }
        'Service' {
            try {
                $service = Get-Service -Name $Artifact.path -ErrorAction SilentlyContinue
                return -not $service
            }
            catch { return $null }
        }
        Default { return $null }
    }
}

Import-TidyModule
if (-not $InventoryPath) {
    $InventoryPath = Resolve-DefaultInventoryPath
}

$inventory = Load-Inventory -Path $InventoryPath
$app = Get-OblivionApp -Inventory $inventory -Identifier $AppId

Write-TidyStructuredEvent -Type 'kickoff' -Payload @{ appId = $AppId; name = $app.name; version = $app.version; stage = 'Cleanup' }
Write-TidyStructuredEvent -Type 'stage' -Payload @{ stage = 'ArtifactDiscovery'; status = 'started' }

$discovery = Invoke-OblivionArtifactDiscovery -App $app
$artifacts = $discovery.Artifacts

Write-TidyStructuredEvent -Type 'artifacts' -Payload @{ count = $artifacts.Count; items = $artifacts }
Write-TidyStructuredEvent -Type 'stage' -Payload @{ stage = 'ArtifactDiscovery'; status = 'completed'; count = $artifacts.Count; added = $discovery.AddedCount }

$selectedIds = Resolve-ArtifactSelection -Artifacts $artifacts -SelectionPath $SelectionPath -AutoSelectAll:$AutoSelectAll -WaitForSelection:$WaitForSelection -SelectionTimeoutSeconds $SelectionTimeoutSeconds
$selectedArtifacts = @($artifacts | Where-Object { $selectedIds -contains $_.id })
Write-TidyStructuredEvent -Type 'selection' -Payload @{ selected = $selectedArtifacts.Count; total = $artifacts.Count }

Write-TidyStructuredEvent -Type 'stage' -Payload @{ stage = 'Cleanup'; status = 'started' }
$removalResult = Invoke-OblivionForceRemoval -Artifacts $selectedArtifacts -DryRun:$DryRun
$verificationMismatches = 0
$verificationErrors = 0
$verificationUnknown = 0
$verifiedRemovedCount = 0
$finalFailureCount = 0
$artifactFinalStatus = @{}
foreach ($entry in $removalResult.Results) {
    if ($entry.PSObject.Properties['retryStrategy'] -and $entry.retryStrategy) {
        Write-TidyStructuredEvent -Type 'cleanupRetry' -Payload @{
            artifactId = $entry.artifactId
            strategy   = $entry.retryStrategy
            success    = $entry.success
            error      = $entry.error
        }
    }
    Write-TidyStructuredEvent -Type 'artifactResult' -Payload $entry

    $artifactContext = $selectedArtifacts | Where-Object { $_.id -eq $entry.artifactId } | Select-Object -First 1
    $verificationPassed = $null
    $verificationError = $null
    if ($artifactContext) {
        try {
            $verificationPassed = Test-LocalArtifactRemoved -Artifact $artifactContext
            if ($entry.success -and -not $verificationPassed) {
                $verificationMismatches++
            }
            elseif ($verificationPassed -eq $false) {
                $verificationMismatches++
            }
        }
        catch {
            $verificationError = $_.Exception.Message
            $verificationErrors++
        }
    }
    else {
        $verificationError = 'ArtifactNotFoundInSelection'
        $verificationErrors++
    }

    $finalStatus = 'unknown'
    if ($verificationError) {
        $finalStatus = 'error'
        $finalFailureCount++
    }
    elseif ($verificationPassed -eq $true -and $entry.success) {
        $finalStatus = 'verifiedRemoved'
        $verifiedRemovedCount++
    }
    elseif ($verificationPassed -eq $false) {
        $finalStatus = 'stillPresent'
        $finalFailureCount++
    }
    elseif (-not $entry.success) {
        $finalStatus = 'failed'
        $finalFailureCount++
    }
    else {
        $verificationUnknown++
    }

    $artifactFinalStatus[$entry.artifactId] = $finalStatus

    Write-TidyStructuredEvent -Type 'artifactVerification' -Payload @{
        artifactId        = $entry.artifactId
        reportedSuccess   = [bool]$entry.success
        verifiedRemoved   = if ($verificationPassed -ne $null) { [bool]$verificationPassed } else { $null }
        verificationError = $verificationError
        finalStatus       = $finalStatus
    }
}
Write-TidyStructuredEvent -Type 'verificationSummary' -Payload @{
    totalResults = $removalResult.Results.Count
    mismatches   = $verificationMismatches
    errors       = $verificationErrors
    unknown      = $verificationUnknown
    verifiedRemoved = $verifiedRemovedCount
    finalFailures = $finalFailureCount
}
Write-TidyStructuredEvent -Type 'stage' -Payload @{
    stage    = 'Cleanup'
    status   = 'completed'
    removed  = $verifiedRemovedCount
    failures = $finalFailureCount
    unknown  = $verificationUnknown
}

Write-TidyStructuredEvent -Type 'summary' -Payload @{
    stage      = 'Cleanup'
    appId      = $AppId
    removed    = $verifiedRemovedCount
    reportedRemoved = $removalResult.RemovedCount
    failures   = $finalFailureCount
    reportedFailures = $removalResult.FailureCount
    verificationUnknown = $verificationUnknown
    freedBytes = $removalResult.FreedBytes
    dryRun     = [bool]$DryRun
}

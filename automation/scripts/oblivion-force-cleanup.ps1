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

function Resolve-ArtifactSelection {
    param(
        [psobject[]] $Artifacts,
        [string] $SelectionPath,
        [switch] $AutoSelectAll,
        [switch] $WaitForSelection,
        [int] $SelectionTimeoutSeconds
    )

    $autoSelectAllEnabled = [bool]$AutoSelectAll
    $waitForSelectionEnabled = [bool]$WaitForSelection

    if ($autoSelectAllEnabled) {
        return @($Artifacts | ForEach-Object { $_.id })
    }

    $payload = Read-OblivionSelectionPayload -SelectionPath $SelectionPath -RequireSelection -WaitForSelection:$waitForSelectionEnabled -SelectionTimeoutSeconds $SelectionTimeoutSeconds
    if (-not $payload) {
        throw 'Selection data missing. Provide a selection file or pass -AutoSelectAll.'
    }

    $selectedIds = @($payload.selectedIds)
    $deselectedIds = @()
    if ($payload.PSObject.Properties['deselectedIds'] -and $payload.deselectedIds) {
        $deselectedIds = @($payload.deselectedIds)
    }

    if ($deselectedIds.Count -gt 0) {
        $selectedIds = @($selectedIds | Where-Object { $deselectedIds -notcontains $_ })
    }

    return @($selectedIds | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
}

function Read-OblivionSelectionPayload {
    param(
        [string] $SelectionPath,
        [switch] $RequireSelection,
        [switch] $WaitForSelection,
        [int] $SelectionTimeoutSeconds
    )

    $waitForSelectionEnabled = [bool]$WaitForSelection

    if ([string]::IsNullOrWhiteSpace($SelectionPath)) {
        if ($RequireSelection) {
            throw 'SelectionPath is required when AutoSelectAll is disabled.'
        }

        return $null
    }

    $timeout = [math]::Max(1, $SelectionTimeoutSeconds)
    $deadline = (Get-Date).AddSeconds($timeout)
    $shouldWait = -not (Test-Path -LiteralPath $SelectionPath)
    if ($shouldWait -and $waitForSelectionEnabled) {
        $awaitingEvent = Write-TidyStructuredEvent -Type 'awaitingSelection' -Payload @{ selectionPath = $SelectionPath; timeoutSeconds = $SelectionTimeoutSeconds }
        if ($awaitingEvent) {
            [Console]::Out.WriteLine($awaitingEvent)
        }
    }

    while (-not (Test-Path -LiteralPath $SelectionPath)) {
        if (-not $waitForSelectionEnabled) { break }
        if ((Get-Date) -ge $deadline) {
            break
        }
        Start-Sleep -Milliseconds 250
    }

    if (-not (Test-Path -LiteralPath $SelectionPath)) {
        if ($RequireSelection) {
            throw 'Selection file not provided before timeout.'
        }

        return $null
    }

    $signaturePath = "$SelectionPath.sha256"
    if (-not (Test-Path -LiteralPath $signaturePath)) {
        throw "Selection signature '$signaturePath' is missing."
    }

    $expectedSignature = (Get-Content -LiteralPath $signaturePath -Raw -ErrorAction Stop).Trim()
    if ([string]::IsNullOrWhiteSpace($expectedSignature)) {
        throw 'Selection signature is invalid.'
    }

    $actualHash = (Get-FileHash -LiteralPath $SelectionPath -Algorithm SHA256 -ErrorAction Stop).Hash
    if (-not [string]::Equals($actualHash, $expectedSignature, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'Selection file failed signature validation.'
    }

    $selectionJson = Get-Content -LiteralPath $SelectionPath -Raw -ErrorAction Stop
    $selectionPayload = ConvertTo-JsonObject -Json $selectionJson -Context 'selection'
    if (-not $selectionPayload -or -not $selectionPayload.PSObject) {
        throw 'Selection payload is empty.'
    }

    $allowedProps = @('selectedIds', 'deselectedIds')
    foreach ($prop in $selectionPayload.PSObject.Properties.Name) {
        if ($allowedProps -notcontains $prop) {
            throw "Selection payload contains unsupported property '$prop'."
        }
    }

    if (-not $selectionPayload.PSObject.Properties['selectedIds']) {
        throw "Selection payload must include 'selectedIds'."
    }

    $selectedIds = ConvertTo-SelectionIdArray -Value $selectionPayload.selectedIds -PropertyName 'selectedIds'
    $deselectedIds = @()
    if ($selectionPayload.PSObject.Properties['deselectedIds']) {
        $deselectedIds = ConvertTo-SelectionIdArray -Value $selectionPayload.deselectedIds -PropertyName 'deselectedIds'
    }

    return [pscustomobject]@{
        selectedIds   = $selectedIds
        deselectedIds = $deselectedIds
    }
}

function ConvertTo-SelectionIdArray {
    param(
        [object] $Value,
        [string] $PropertyName
    )

    if ($null -eq $Value) {
        return @()
    }

    if ($Value -is [System.Collections.IEnumerable] -and -not ($Value -is [string])) {
        return @($Value | ForEach-Object {
            $entry = [string]$_
            if (-not [string]::IsNullOrWhiteSpace($entry)) { $entry.Trim() }
        } | Where-Object { $_ })
    }

    throw "Selection property '$PropertyName' must be an array of artifact identifiers."
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


param(
    [switch] $IncludeOptional,
    [switch] $Silent,
    [string] $ResultPath,
    [string[]] $IncludeDriverClasses,
    [string[]] $ExcludeDriverClasses,
    [string[]] $AllowVendors,
    [string[]] $BlockVendors
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$callerModulePath = $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($callerModulePath) -and (Get-Variable -Name PSCommandPath -Scope Script -ErrorAction SilentlyContinue)) {
    $callerModulePath = $PSCommandPath
}

$scriptDirectory = Split-Path -Parent $callerModulePath
if ([string]::IsNullOrWhiteSpace($scriptDirectory)) {
    $scriptDirectory = (Get-Location).Path
}

$modulePath = Join-Path -Path $scriptDirectory -ChildPath '..\modules\TidyWindow.Automation.psm1'
$modulePath = [System.IO.Path]::GetFullPath($modulePath)
if (-not (Test-Path -LiteralPath $modulePath)) {
    throw "Automation module not found at path '$modulePath'."
}

Import-Module $modulePath -Force

$script:TidyOutputLines = [System.Collections.Generic.List[string]]::new()
$script:TidyErrorLines = [System.Collections.Generic.List[string]]::new()
$script:OperationSucceeded = $false
$script:UsingResultFile = -not [string]::IsNullOrWhiteSpace($ResultPath)
if ($script:UsingResultFile) {
    $ResultPath = [System.IO.Path]::GetFullPath($ResultPath)
}

$normalizedIncludeDriverClasses = @()
if ($IncludeDriverClasses) {
    $normalizedIncludeDriverClasses = $IncludeDriverClasses | ForEach-Object { $_.Trim().ToLowerInvariant() } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
}

$normalizedExcludeDriverClasses = @()
if ($ExcludeDriverClasses) {
    $normalizedExcludeDriverClasses = $ExcludeDriverClasses | ForEach-Object { $_.Trim().ToLowerInvariant() } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
}

$normalizedAllowVendors = @()
if ($AllowVendors) {
    $normalizedAllowVendors = $AllowVendors | ForEach-Object { $_.Trim().ToLowerInvariant() } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
}

$normalizedBlockVendors = @()
if ($BlockVendors) {
    $normalizedBlockVendors = $BlockVendors | ForEach-Object { $_.Trim().ToLowerInvariant() } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
}

function Write-TidyOutput {
    param(
        [Parameter(Mandatory = $true)]
        [object] $Message
    )

    $text = Convert-TidyLogMessage -InputObject $Message
    if ([string]::IsNullOrWhiteSpace($text)) {
        return
    }

    [void]$script:TidyOutputLines.Add($text)
    if (-not $Silent.IsPresent) {
        Write-Output $text
    }
}

function Write-TidyError {
    param(
        [Parameter(Mandatory = $true)]
        [object] $Message
    )

    $text = Convert-TidyLogMessage -InputObject $Message
    if ([string]::IsNullOrWhiteSpace($text)) {
        return
    }

    [void]$script:TidyErrorLines.Add($text)
    Write-Error -Message $text
}

function Save-TidyResult {
    if (-not $script:UsingResultFile) {
        return
    }

    $payload = [pscustomobject]@{
        Success = $script:OperationSucceeded -and ($script:TidyErrorLines.Count -eq 0)
        Output  = $script:TidyOutputLines
        Errors  = $script:TidyErrorLines
    }

    $json = $payload | ConvertTo-Json -Depth 5
    Set-Content -LiteralPath $ResultPath -Value $json -Encoding UTF8
}

function Convert-TidyDateString {
    param([object] $Value)

    if ($null -eq $Value) {
        return $null
    }

    try {
        if ($Value -is [datetime]) {
            return ([datetime]$Value).ToUniversalTime().ToString('o')
        }

        $text = $Value.ToString()
        if ([string]::IsNullOrWhiteSpace($text)) {
            return $null
        }

        $parsed = [datetime]::Parse($text, [System.Globalization.CultureInfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::AssumeUniversal)
        return $parsed.ToUniversalTime().ToString('o')
    }
    catch {
        return $null
    }
}

function Normalize-HardwareIds {
    param([object] $Input)

    $results = [System.Collections.Generic.List[string]]::new()
    foreach ($candidate in @($Input)) {
        if ($null -eq $candidate) { continue }
        $text = $candidate.ToString().Trim()
        if (-not [string]::IsNullOrWhiteSpace($text)) {
            [void]$results.Add($text)
        }
    }

    return $results.ToArray()
}

function Compare-VersionStrings {
    param(
        [string] $CurrentVersion,
        [string] $AvailableVersion
    )

    if ([string]::IsNullOrWhiteSpace($CurrentVersion) -or [string]::IsNullOrWhiteSpace($AvailableVersion)) {
        return [pscustomobject]@{
            status = 'Unknown'
            details = 'At least one version string is missing.'
        }
    }

    $normalize = {
        param([string] $input)
        $trimmed = $input.Trim()
        if ([string]::IsNullOrWhiteSpace($trimmed)) { return $null }
        $normalized = $trimmed -replace '[^0-9A-Za-z\._-]', '.'
        return $normalized.Trim('. ')
    }

    $currentNormalized = & $normalize $CurrentVersion
    $availableNormalized = & $normalize $AvailableVersion

    if (-not $currentNormalized -or -not $availableNormalized) {
        return [pscustomobject]@{
            status = 'Unknown'
            details = 'Unable to normalize version strings.'
        }
    }

    $parsedCurrent = $null
    $parsedAvailable = $null

    if ([System.Version]::TryParse($currentNormalized, [ref]$parsedCurrent) -and [System.Version]::TryParse($availableNormalized, [ref]$parsedAvailable)) {
        if ($parsedAvailable -gt $parsedCurrent) {
            return [pscustomobject]@{ status = 'UpdateAvailable'; details = 'Available version is newer.' }
        }
        elseif ($parsedAvailable -lt $parsedCurrent) {
            return [pscustomobject]@{ status = 'PotentialDowngrade'; details = 'Offered version appears older.' }
        }
        else {
            return [pscustomobject]@{ status = 'Equal'; details = 'Versions appear identical.' }
        }
    }

    if ($availableNormalized.Equals($currentNormalized, [System.StringComparison]::OrdinalIgnoreCase)) {
        return [pscustomobject]@{ status = 'Equal'; details = 'Versions match after normalization.' }
    }

    return [pscustomobject]@{
        status = 'Unknown'
        details = 'Could not confidently compare version strings.'
    }
}

function Resolve-DriverClass {
    param([object] $Update, [object] $DriverInfo)

    $candidates = @()
    if ($DriverInfo) {
        foreach ($property in 'DriverClass','Class','ClassName') {
            if ($DriverInfo.PSObject.Properties.Name -contains $property) {
                $value = $DriverInfo.$property
                if (-not [string]::IsNullOrWhiteSpace($value)) {
                    $candidates += $value
                }
            }
        }
    }

    if (-not $candidates -and $Update) {
        if ($Update.Categories) {
            foreach ($category in @($Update.Categories)) {
                if ($category -and -not [string]::IsNullOrWhiteSpace($category.Name)) {
                    $candidates += $category.Name
                }
            }
        }
    }

    foreach ($candidate in $candidates) {
        $normalized = $candidate.ToString().Trim()
        if (-not [string]::IsNullOrWhiteSpace($normalized)) {
            return $normalized
        }
    }

    return $null
}

function Normalize-VendorName {
    param([string] $Vendor)
    if ([string]::IsNullOrWhiteSpace($Vendor)) {
        return $null
    }

    return $Vendor.Trim().ToLowerInvariant()
}

function Normalize-DriverClassName {
    param([string] $DriverClass)

    if ([string]::IsNullOrWhiteSpace($DriverClass)) {
        return $null
    }

    return $DriverClass.Trim().ToLowerInvariant()
}

function New-SkipSummaryEntry {
    param(
        [string] $Title,
        [string] $DeviceName,
        [string] $Manufacturer,
        [string] $DriverClass,
        [bool] $IsOptional,
        [string] $Reason,
        [string] $ReasonCode,
        [string] $UpdateId
    )

    $normalizedVendor = Normalize-VendorName -Vendor $Manufacturer
    $normalizedDriverClass = Normalize-DriverClassName -DriverClass $DriverClass

    return [pscustomobject]@{
        title               = if ([string]::IsNullOrWhiteSpace($Title)) { $DeviceName } else { $Title }
        deviceName          = if ([string]::IsNullOrWhiteSpace($DeviceName)) { 'Unknown device' } else { $DeviceName }
        manufacturer        = if ([string]::IsNullOrWhiteSpace($Manufacturer)) { $null } else { $Manufacturer }
        normalizedVendor    = $normalizedVendor
        driverClass         = if ([string]::IsNullOrWhiteSpace($DriverClass)) { $null } else { $DriverClass }
        normalizedDriverClass = $normalizedDriverClass
        isOptional          = [bool]$IsOptional
        reason              = $Reason
        reasonCode          = $ReasonCode
        updateId            = if ([string]::IsNullOrWhiteSpace($UpdateId)) { $null } else { $UpdateId }
    }
}

function New-BadgeHints {
    param(
        [pscustomobject] $VersionComparison,
        [bool] $IsOptional,
        [string] $DriverClass,
        [string] $Manufacturer
    )

    $comparisonStatus = $null
    $comparisonDetails = $null
    if ($VersionComparison -and $VersionComparison.PSObject.Properties.Name -contains 'status') {
        $comparisonStatus = $VersionComparison.status
        $comparisonDetails = $VersionComparison.details
    }

    $normalizedVendor = Normalize-VendorName -Vendor $Manufacturer
    $normalizedClass = Normalize-DriverClassName -DriverClass $DriverClass

    return [pscustomobject]@{
        availability = [pscustomobject]@{
            state  = if ([string]::IsNullOrWhiteSpace($comparisonStatus)) { 'Unknown' } else { $comparisonStatus }
            detail = if ([string]::IsNullOrWhiteSpace($comparisonDetails)) { $null } else { $comparisonDetails }
        }
        downgradeRisk = [pscustomobject]@{
            isRisk = ($comparisonStatus -eq 'PotentialDowngrade')
            detail = if ($comparisonStatus -eq 'PotentialDowngrade') { $comparisonDetails } else { $null }
        }
        vendor = [pscustomobject]@{
            name       = if ([string]::IsNullOrWhiteSpace($Manufacturer)) { $null } else { $Manufacturer }
            normalized = $normalizedVendor
        }
        driverClass = [pscustomobject]@{
            name       = if ([string]::IsNullOrWhiteSpace($DriverClass)) { $null } else { $DriverClass }
            normalized = $normalizedClass
        }
        optional = [pscustomobject]@{
            isOptional = [bool]$IsOptional
            label      = if ($IsOptional) { 'Optional' } else { 'Recommended' }
        }
    }
}

function Should-SkipUpdateByFilters {
    param(
        [string] $DriverClass,
        [string] $VendorName
    )

    $normalizedClass = Normalize-DriverClassName -DriverClass $DriverClass
    $normalizedVendor = Normalize-VendorName -Vendor $VendorName

    if ($normalizedIncludeDriverClasses.Count -gt 0 -and ($null -eq $normalizedClass -or -not $normalizedIncludeDriverClasses.Contains($normalizedClass))) {
        return 'Driver class not in include list.'
    }

    if ($normalizedExcludeDriverClasses.Count -gt 0 -and $normalizedClass -and $normalizedExcludeDriverClasses.Contains($normalizedClass)) {
        return 'Driver class blocked by exclude list.'
    }

    if ($normalizedAllowVendors.Count -gt 0 -and ($null -eq $normalizedVendor -or -not $normalizedAllowVendors.Contains($normalizedVendor))) {
        return 'Vendor not in allow list.'
    }

    if ($normalizedBlockVendors.Count -gt 0 -and $normalizedVendor -and $normalizedBlockVendors.Contains($normalizedVendor)) {
        return 'Vendor blocked by policy.'
    }

    return $null
}

function Get-InstalledDriverInventory {
    $lookup = [System.Collections.Generic.Dictionary[string, psobject]]::new([System.StringComparer]::OrdinalIgnoreCase)
    $inventory = [System.Collections.Generic.List[psobject]]::new()

    try {
        $installed = Get-CimInstance -ClassName Win32_PnPSignedDriver -ErrorAction Stop
    }
    catch {
        Write-TidyOutput -Message ('Unable to inventory installed drivers: {0}' -f $_.Exception.Message)
        return [pscustomobject]@{ Lookup = $lookup; Inventory = $inventory }
    }

    foreach ($entry in @($installed)) {
        if ($null -eq $entry) { continue }

        $hardwareIds = Normalize-HardwareIds -Input $entry.HardwareID
        $driverDate = Convert-TidyDateString -Value $entry.DriverDate
        $installDate = $null
        if ($entry.PSObject.Properties['DriverDate']) {
            $installDate = $driverDate
        }
        if ($entry.PSObject.Properties['Date']) {
            $possible = Convert-TidyDateString -Value $entry.Date
            if ($possible) { $installDate = $possible }
        }

        $status = 'Unknown'
        $problemCode = $null
        if ($entry.PSObject.Properties['DeviceProblemCode']) {
            $problemCode = $entry.DeviceProblemCode
        }
        if ($null -ne $problemCode) {
            if ($problemCode -eq 0) {
                $status = 'Working'
            }
            else {
                $status = "ProblemCode $problemCode"
            }
        }

        $detail = [pscustomobject]@{
            DeviceName    = $entry.DeviceName
            FriendlyName  = $entry.FriendlyName
            Manufacturer  = $entry.Manufacturer
            DriverVersion = $entry.DriverVersion
            DriverDate    = $entry.DriverDate
            InfName       = $entry.InfName
        }

        foreach ($hardwareId in $hardwareIds) {
            if (-not $lookup.ContainsKey($hardwareId)) {
                $lookup[$hardwareId] = $detail
            }
        }

        $inventory.Add([pscustomobject]@{
                deviceName        = if ([string]::IsNullOrWhiteSpace($entry.FriendlyName)) { $entry.DeviceName } else { $entry.FriendlyName }
                manufacturer      = $entry.Manufacturer
                provider          = $entry.DriverProviderName
                driverVersion     = $entry.DriverVersion
                driverDate        = $driverDate
                installDate       = $installDate
                classGuid         = if ($entry.ClassGuid) { $entry.ClassGuid.ToString() } else { $null }
                driverDescription = $entry.Description
                hardwareIds       = $hardwareIds
                signed            = if ($entry.PSObject.Properties['IsSigned']) { [bool]$entry.IsSigned } else { $null }
                infName           = $entry.InfName
                deviceId          = $entry.DeviceID
                problemCode       = $problemCode
                status            = $status
            }) | Out-Null
    }

    return [pscustomobject]@{
        Lookup    = $lookup
        Inventory = $inventory
    }
}

function Resolve-VersionString {
    param(
        [object] $Primary,
        [object[]] $FallbackSources
    )

    if ($Primary -and -not [string]::IsNullOrWhiteSpace($Primary.ToString())) {
        return $Primary.ToString().Trim()
    }

    foreach ($source in @($FallbackSources)) {
        if ($null -eq $source) { continue }
        $text = $source.ToString().Trim()
        if (-not [string]::IsNullOrWhiteSpace($text)) {
            return $text
        }
    }

    return $null
}

function Extract-VersionFromDescription {
    param([string] $Description)

    if ([string]::IsNullOrWhiteSpace($Description)) {
        return $null
    }

    $match = [System.Text.RegularExpressions.Regex]::Match($Description, 'Version\s*[:=]\s*(?<ver>[0-9][0-9A-Za-z\._-]*)')
    if ($match.Success) {
        return $match.Groups['ver'].Value.Trim()
    }

    return $null
}

try {
    Write-TidyLog -Level Information -Message 'Scanning for Windows driver updates.'
    Write-TidyOutput -Message 'Gathering pending driver updates. This can take a moment...'

    $session = $null
    $searcher = $null
    $result = $null

    try {
        $session = New-Object -ComObject 'Microsoft.Update.Session'
        $searcher = $session.CreateUpdateSearcher()
        $searcher.Online = $true
        $criteria = "IsInstalled=0 and Type='Driver'"
        $result = $searcher.Search($criteria)
    }
    catch {
        throw "Driver update search failed: $($_.Exception.Message)"
    }
    finally {
        if ($null -ne $searcher) {
            [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($searcher)
        }
        if ($null -ne $session) {
            [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($session)
        }
    }

    $inventoryData = Get-InstalledDriverInventory
    $lookup = $inventoryData.Lookup
    $installedDrivers = $inventoryData.Inventory
    if ($installedDrivers -and $installedDrivers.Count -gt 0) {
        Write-TidyOutput -Message ("Cataloged {0} installed driver record(s)." -f $installedDrivers.Count)
    }

    if ($null -eq $result -or $null -eq $result.Updates -or $result.Updates.Count -eq 0) {
        Write-TidyOutput -Message 'No driver updates are currently offered by Windows Update.'
        $payload = [pscustomobject]@{
            schemaVersion    = '1.2.0'
            generatedAtUtc   = (Get-Date).ToUniversalTime().ToString('o')
            includeOptional  = [bool]$IncludeOptional
            updates          = @()
            skippedOptional  = 0
            skippedByFilters = 0
            skipDetails      = @()
            appliedFilters   = [pscustomobject]@{
                includeDriverClasses = $normalizedIncludeDriverClasses
                excludeDriverClasses = $normalizedExcludeDriverClasses
                allowVendors         = $normalizedAllowVendors
                blockVendors         = $normalizedBlockVendors
            }
            installedDrivers = $installedDrivers.ToArray()
        }
        $jsonPayload = $payload | ConvertTo-Json -Depth 6 -Compress
        Write-Output $jsonPayload
        $script:OperationSucceeded = $true
        return
    }
    $updates = [System.Collections.Generic.List[psobject]]::new()
    $skippedOptional = 0
    $skippedByFilters = 0
    $filterSkipReasons = [System.Collections.Generic.List[string]]::new()
    $skipSummaries = [System.Collections.Generic.List[psobject]]::new()

    foreach ($update in @($result.Updates)) {
        if ($null -eq $update) { continue }

        $isOptional = $false
        try {
            if ($update.AutoSelectOnWebSites -eq $false) {
                $isOptional = $true
            }
        }
        catch {
            $isOptional = $false
        }

        $driverInfo = $null
        try {
            $driverInfo = $update.Driver
        }
        catch {
            $driverInfo = $null
        }

        $hardwareIds = @()
        if ($driverInfo -and $driverInfo.HardwareIDs) {
            $hardwareIds = Normalize-HardwareIds -Input $driverInfo.HardwareIDs
        }
        elseif ($update.Categories) {
            $hardwareIds = Normalize-HardwareIds -Input $update.Categories
        }

        $driverClass = Resolve-DriverClass -Update $update -DriverInfo $driverInfo

        $matched = $null
        foreach ($hardwareId in $hardwareIds) {
            if ($lookup.ContainsKey($hardwareId)) {
                $matched = $lookup[$hardwareId]
                break
            }
        }

        if (-not $matched -and $hardwareIds.Count -gt 0) {
            foreach ($entry in $lookup.GetEnumerator()) {
                foreach ($hardwareId in $hardwareIds) {
                    if ($entry.Key.StartsWith($hardwareId, [System.StringComparison]::OrdinalIgnoreCase)) {
                        $matched = $entry.Value
                        break
                    }
                }

                if ($matched) { break }
            }
        }

        $currentVersion = $null
        $currentDate = $null
        $currentManufacturer = $null
        $deviceName = $null

        if ($matched) {
            $currentVersion = Resolve-VersionString -Primary $matched.DriverVersion -FallbackSources @()
            $currentDate = Convert-TidyDateString -Value $matched.DriverDate
            $currentManufacturer = Resolve-VersionString -Primary $matched.Manufacturer -FallbackSources @()
            $deviceName = Resolve-VersionString -Primary $matched.FriendlyName -FallbackSources @($matched.DeviceName)
        }

        if ([string]::IsNullOrWhiteSpace($deviceName)) {
            if ($driverInfo -and -not [string]::IsNullOrWhiteSpace($driverInfo.Description)) {
                $deviceName = $driverInfo.Description.Trim()
            }
            elseif (-not [string]::IsNullOrWhiteSpace($update.Title)) {
                $deviceName = $update.Title.Trim()
            }
            else {
                $deviceName = 'Unknown device'
            }
        }

        $availableVersion = $null
        if ($driverInfo -and $driverInfo.DriverVerVersion) {
            $availableVersion = Resolve-VersionString -Primary $driverInfo.DriverVerVersion -FallbackSources @()
        }

        if (-not $availableVersion) {
            $availableVersion = Resolve-VersionString -Primary (Extract-VersionFromDescription -Description $update.Description) -FallbackSources @()
        }

        $availableDate = $null
        if ($driverInfo -and $driverInfo.DriverVerDate) {
            $availableDate = Convert-TidyDateString -Value $driverInfo.DriverVerDate
        }
        elseif ($update.LastDeploymentChangeTime) {
            $availableDate = Convert-TidyDateString -Value $update.LastDeploymentChangeTime
        }

        $manufacturer = $currentManufacturer
        if ([string]::IsNullOrWhiteSpace($manufacturer) -and $driverInfo) {
            $manufacturer = Resolve-VersionString -Primary $driverInfo.Manufacturer -FallbackSources @($driverInfo.ProviderName)
        }
        if ([string]::IsNullOrWhiteSpace($manufacturer) -and $update.Publisher) {
            $manufacturer = $update.Publisher.Trim()
        }

        $normalizedVendor = Normalize-VendorName -Vendor $manufacturer
        $normalizedDriverClass = Normalize-DriverClassName -DriverClass $driverClass

        $updateId = $null
        $revisionNumber = $null
        if ($update.PSObject.Properties['Identity'] -and $update.Identity) {
            $updateId = $update.Identity.UpdateID
            $revisionNumber = $update.Identity.RevisionNumber
        }

        if ($isOptional -and -not $IncludeOptional.IsPresent) {
            $skippedOptional++
            $skipEntry = New-SkipSummaryEntry -Title $update.Title -DeviceName $deviceName -Manufacturer $manufacturer -DriverClass $driverClass -IsOptional $true -Reason 'Optional update excluded by policy.' -ReasonCode 'OptionalFilter' -UpdateId $updateId
            [void]$skipSummaries.Add($skipEntry)
            if (-not [string]::IsNullOrWhiteSpace($skipEntry.reason)) {
                [void]$filterSkipReasons.Add('{0}: {1}' -f ($skipEntry.deviceName), $skipEntry.reason)
            }
            continue
        }

        $filterReason = Should-SkipUpdateByFilters -DriverClass $driverClass -VendorName $manufacturer
        if ($filterReason) {
            $skippedByFilters++
            $skipContextTitle = if (-not [string]::IsNullOrWhiteSpace($update.Title)) { $update.Title.Trim() } else { 'Unknown update' }
            [void]$filterSkipReasons.Add('{0}: {1}' -f $skipContextTitle, $filterReason)
            $skipEntry = New-SkipSummaryEntry -Title $update.Title -DeviceName $deviceName -Manufacturer $manufacturer -DriverClass $driverClass -IsOptional $isOptional -Reason $filterReason -ReasonCode 'PolicyFilter' -UpdateId $updateId
            [void]$skipSummaries.Add($skipEntry)
            Write-TidyOutput -Message ("Skipping driver '{0}' due to filter: {1}" -f $deviceName, $filterReason)
            continue
        }

        $categoryNames = [System.Collections.Generic.List[string]]::new()
        foreach ($category in @($update.Categories)) {
            if ($null -eq $category) { continue }
            $name = $category.Name
            if ([string]::IsNullOrWhiteSpace($name)) { continue }
            [void]$categoryNames.Add($name.Trim())
        }

        $infoUrls = [System.Collections.Generic.List[string]]::new()
        foreach ($url in @($update.MoreInfoUrls)) {
            if ($null -eq $url) { continue }
            $text = $url.ToString().Trim()
            if (-not [string]::IsNullOrWhiteSpace($text)) {
                [void]$infoUrls.Add($text)
            }
        }

        $versionComparison = Compare-VersionStrings -CurrentVersion $currentVersion -AvailableVersion $availableVersion

        $severity = $null
        if ($update.PSObject.Properties['MsrcSeverity']) {
            $severity = $update.MsrcSeverity
        }

        $classification = $null
        if ($update.PSObject.Properties['DriverClassification']) {
            $classification = $update.DriverClassification
        }
        elseif ($driverInfo -and $driverInfo.PSObject.Properties['DriverClassification']) {
            $classification = $driverInfo.DriverClassification
        }

        $badgeHints = New-BadgeHints -VersionComparison $versionComparison -IsOptional $isOptional -DriverClass $driverClass -Manufacturer $manufacturer

        $updates.Add([pscustomobject]@{
                title                = $update.Title
                deviceName           = $deviceName
                manufacturer         = $manufacturer
                hardwareIds          = $hardwareIds
                isOptional           = [bool]$isOptional
                currentVersion       = if ($currentVersion) { $currentVersion } else { $null }
                currentVersionDate   = $currentDate
                availableVersion     = if ($availableVersion) { $availableVersion } else { $null }
                availableVersionDate = $availableDate
                description          = if ([string]::IsNullOrWhiteSpace($update.Description)) { $null } else { $update.Description.Trim() }
                categories           = $categoryNames.ToArray()
                informationUrls      = $infoUrls.ToArray()
                driverClass          = if ([string]::IsNullOrWhiteSpace($driverClass)) { $null } else { $driverClass }
                classification       = if ([string]::IsNullOrWhiteSpace($classification)) { $null } else { $classification }
                severity             = if ([string]::IsNullOrWhiteSpace($severity)) { $null } else { $severity }
                updateId             = $updateId
                revisionNumber       = $revisionNumber
                versionComparison    = $versionComparison
                installedInfPath     = if ($matched -and $matched.InfName) { $matched.InfName } else { $null }
                installedManufacturer = if ([string]::IsNullOrWhiteSpace($currentManufacturer)) { $null } else { $currentManufacturer }
                badgeHints           = $badgeHints
                normalizedVendor     = $normalizedVendor
                normalizedDriverClass = $normalizedDriverClass
            })
    }

    $count = $updates.Count
    if ($count -eq 0) {
        Write-TidyOutput -Message 'No driver updates matched the requested filters.'
        if ($skippedOptional -gt 0 -and -not $IncludeOptional.IsPresent) {
            Write-TidyOutput -Message ("Skipped {0} optional update(s). Enable IncludeOptional to surface them." -f $skippedOptional)
        }
        if ($skippedByFilters -gt 0) {
            Write-TidyOutput -Message ("Skipped {0} update(s) due to filter policies." -f $skippedByFilters)
        }
    }
    else {
        Write-TidyOutput -Message ("Detected {0} driver update(s)." -f $count)
        if ($skippedOptional -gt 0 -and -not $IncludeOptional.IsPresent) {
            Write-TidyOutput -Message ("Skipped {0} optional update(s). Enable IncludeOptional to include them." -f $skippedOptional)
        }
        if ($skippedByFilters -gt 0) {
            Write-TidyOutput -Message ("Filtered out {0} update(s) based on driver class or vendor settings." -f $skippedByFilters)
        }
    }

    $payload = [pscustomobject]@{
    schemaVersion   = '1.2.0'
        generatedAtUtc  = (Get-Date).ToUniversalTime().ToString('o')
        includeOptional = [bool]$IncludeOptional
        updates         = $updates.ToArray()
        skippedOptional = $skippedOptional
        skippedByFilters = $skippedByFilters
        skipDetails     = $filterSkipReasons.ToArray()
        skipSummaries   = $skipSummaries.ToArray()
        appliedFilters  = [pscustomobject]@{
            includeDriverClasses = $normalizedIncludeDriverClasses
            excludeDriverClasses = $normalizedExcludeDriverClasses
            allowVendors         = $normalizedAllowVendors
            blockVendors         = $normalizedBlockVendors
        }
        installedDrivers = $installedDrivers.ToArray()
    }

    $jsonPayload = $payload | ConvertTo-Json -Depth 8 -Compress
    Write-Output $jsonPayload
    $script:OperationSucceeded = $true
}
catch {
    $script:OperationSucceeded = $false
    Write-TidyError -Message $_
}
finally {
    try {
        Save-TidyResult
    }
    catch {
        # ignore persistence failures
    }
}

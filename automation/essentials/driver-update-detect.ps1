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

function ConvertTo-TidyArray {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $false, Position = 0, ValueFromPipeline = $true)]
        $InputObject
    )

    begin {
        $buffer = [System.Collections.Generic.List[object]]::new()
    }

    process {
        if ($null -eq $InputObject) {
            return
        }

        if ($InputObject -is [string]) {
            $buffer.Add($InputObject)
            return
        }

        if ($InputObject -is [System.Collections.IEnumerable]) {
            foreach ($item in $InputObject) {
                if ($null -ne $item) {
                    $buffer.Add($item)
                }
            }

            return
        }

        $buffer.Add($InputObject)
    }

    end {
        return $buffer.ToArray()
    }
}

function Get-UpdateCategories {
    param([object] $Update)

    if ($null -eq $Update) {
        return [object[]]@()
    }

    try {
        $categories = $Update.Categories
    }
    catch {
        return [object[]]@()
    }

    if (-not $categories) {
        return [object[]]@()
    }

    return [object[]]($categories | ConvertTo-TidyArray)
}

function Get-UpdatePropertyValue {
    param(
        [object] $Update,
        [string] $PropertyName
    )

    if ($null -eq $Update -or [string]::IsNullOrWhiteSpace($PropertyName)) {
        return $null
    }

    $property = $Update.PSObject.Properties[$PropertyName]
    if ($null -eq $property) {
        return $null
    }

    try {
        return $property.Value
    }
    catch {
        return $null
    }
}

function Normalize-HardwareIds {
    param([object] $Input)

    $results = [System.Collections.Generic.List[string]]::new()
    foreach ($candidate in ($Input | ConvertTo-TidyArray)) {
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
        foreach ($category in (Get-UpdateCategories -Update $Update)) {
            if ($category -and -not [string]::IsNullOrWhiteSpace($category.Name)) {
                $candidates += $category.Name
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

$script:DisplayAdapterClassGuid = '{4d36e968-e325-11ce-bfc1-08002be10318}'
$script:GpuVendorGuidanceCatalog = @{
    AMD = @{
        vendorLabel = 'AMD Radeon'
        message     = 'AMD releases Adrenalin packages more frequently than Windows Update. Use AMD Auto-Detect to pull the latest display and chipset drivers directly.'
        linkLabel   = 'Open AMD Auto-Detect'
        supportUri  = 'https://www.amd.com/en/support'
    }
    NVIDIA = @{
        vendorLabel = 'NVIDIA GeForce'
        message     = 'NVIDIA publishes Game Ready and Studio drivers via GeForce Experience. Launch their download center to stay ahead of Windows Update.'
        linkLabel   = 'Open NVIDIA Drivers'
        supportUri  = 'https://www.nvidia.com/Download/index.aspx'
    }
    Intel = @{
        vendorLabel = 'Intel Arc / Iris / UHD'
        message     = 'Intel Driver & Support Assistant keeps GPU and chipset packages current, including quarterly Arc releases.'
        linkLabel   = 'Open Intel DSA'
        supportUri  = 'https://www.intel.com/content/www/us/en/support/detect.html'
    }
}

function New-DriverHealthInsight {
    param(
        [string] $DeviceName,
        [string] $Issue,
        [string] $Detail,
        [string] $InfName,
        [string] $Severity
    )

    return [pscustomobject]@{
        deviceName = if ([string]::IsNullOrWhiteSpace($DeviceName)) { 'Unknown device' } else { $DeviceName }
        issue      = if ([string]::IsNullOrWhiteSpace($Issue)) { 'Unspecified issue' } else { $Issue }
        detail     = if ([string]::IsNullOrWhiteSpace($Detail)) { $null } else { $Detail }
        infName    = if ([string]::IsNullOrWhiteSpace($InfName)) { $null } else { $InfName }
        severity   = if ([string]::IsNullOrWhiteSpace($Severity)) { 'Advisory' } else { $Severity }
    }
}

function Get-DriverHealthInsights {
    param([object[]] $InstalledDrivers)

    $results = [System.Collections.Generic.List[psobject]]::new()
    if (-not $InstalledDrivers) {
        return $results.ToArray()
    }

    foreach ($driver in ($InstalledDrivers | ConvertTo-TidyArray)) {
        if ($null -eq $driver) { continue }

        $problemCode = $driver.problemCode
        if ($null -ne $problemCode -and [int]$problemCode -gt 0) {
            $issue = "Problem code $problemCode"
            $detail = if ([string]::IsNullOrWhiteSpace($driver.status)) { 'Device reported an issue.' } else { $driver.status }
            $severity = if ($problemCode -in @(43, 52)) { 'Critical' } else { 'Warning' }
            [void]$results.Add((New-DriverHealthInsight -DeviceName $driver.deviceName -Issue $issue -Detail $detail -InfName $driver.infName -Severity $severity))
            continue
        }

        if ($driver.signed -eq $false) {
            [void]$results.Add((New-DriverHealthInsight -DeviceName $driver.deviceName -Issue 'Driver signature missing' -Detail 'Unsigned drivers may fail to load or pass Smart App Control.' -InfName $driver.infName -Severity 'Advisory'))
        }
    }

    return $results.ToArray()
}

function Normalize-GpuVendorKey {
    param([string] $Text)

    if ([string]::IsNullOrWhiteSpace($Text)) {
        return $null
    }

    $value = $Text.Trim()
    if ($value -match 'AMD' -or $value -match 'RADEON' -or $value -match 'ADVANCED\s+MICRO\s+DEVICES') {
        return 'AMD'
    }

    if ($value -match 'NVIDIA' -or $value -match 'GEFORCE') {
        return 'NVIDIA'
    }

    if ($value -match 'INTEL') {
        return 'Intel'
    }

    return $null
}

function Should-TreatAsDisplayDriver {
    param(
        [string] $DriverClass,
        [string] $ClassGuid
    )

    if (-not [string]::IsNullOrWhiteSpace($DriverClass) -and $DriverClass.IndexOf('display', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
        return $true
    }

    if (-not [string]::IsNullOrWhiteSpace($ClassGuid) -and $ClassGuid.Equals($script:DisplayAdapterClassGuid, [System.StringComparison]::OrdinalIgnoreCase)) {
        return $true
    }

    return $false
}

function Get-GpuGuidanceItems {
    param(
        [object[]] $InstalledDrivers,
        [object[]] $UpdateCandidates
    )

    $vendors = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)

    foreach ($driver in ($InstalledDrivers | ConvertTo-TidyArray)) {
        if ($null -eq $driver) { continue }
        if (-not (Should-TreatAsDisplayDriver -DriverClass $null -ClassGuid $driver.classGuid)) { continue }
        $vendorKey = Normalize-GpuVendorKey -Text $driver.manufacturer
        if ($vendorKey) { [void]$vendors.Add($vendorKey) }
    }

    foreach ($update in ($UpdateCandidates | ConvertTo-TidyArray)) {
        if ($null -eq $update) { continue }
        if (-not (Should-TreatAsDisplayDriver -DriverClass $update.driverClass -ClassGuid $null)) { continue }
        $vendorSource = if (-not [string]::IsNullOrWhiteSpace($update.normalizedVendor)) { $update.normalizedVendor } else { $update.manufacturer }
        $vendorKey = Normalize-GpuVendorKey -Text $vendorSource
        if ($vendorKey) { [void]$vendors.Add($vendorKey) }
    }

    if ($vendors.Count -eq 0) {
        return @()
    }

    $results = [System.Collections.Generic.List[psobject]]::new()
    foreach ($vendor in $vendors) {
        if (-not $script:GpuVendorGuidanceCatalog.ContainsKey($vendor)) { continue }
        $descriptor = $script:GpuVendorGuidanceCatalog[$vendor]
        [void]$results.Add([pscustomobject]@{
            vendorKey   = $vendor
            vendorLabel = $descriptor.vendorLabel
            message     = $descriptor.message
            linkLabel   = $descriptor.linkLabel
            supportUri  = $descriptor.supportUri
        })
    }

    return $results.ToArray()
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

    foreach ($entry in ($installed | ConvertTo-TidyArray)) {
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

    foreach ($source in ($FallbackSources | ConvertTo-TidyArray)) {
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

    $healthInsights = Get-DriverHealthInsights -InstalledDrivers $installedDrivers

    [object[]]$availableUpdates = $result.Updates | ConvertTo-TidyArray
    if ($availableUpdates.Length -eq 0) {
        $gpuGuidance = Get-GpuGuidanceItems -InstalledDrivers $installedDrivers -UpdateCandidates @()
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
            healthInsights   = $healthInsights
            vendorGuidance   = $gpuGuidance
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

    foreach ($update in $availableUpdates) {
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
        else {
            $categoryFallback = Get-UpdateCategories -Update $update
            if ($categoryFallback) {
                $hardwareIds = Normalize-HardwareIds -Input $categoryFallback
            }
        }

        $hardwareIds = @($hardwareIds | ConvertTo-TidyArray)

        $updateTitle = Get-UpdatePropertyValue -Update $update -PropertyName 'Title'
        $updateDescription = Get-UpdatePropertyValue -Update $update -PropertyName 'Description'
        $updateLastDeploymentChange = Get-UpdatePropertyValue -Update $update -PropertyName 'LastDeploymentChangeTime'
        $updatePublisher = Get-UpdatePropertyValue -Update $update -PropertyName 'Publisher'
        $updateMoreInfoUrls = Get-UpdatePropertyValue -Update $update -PropertyName 'MoreInfoUrls'

        $driverClass = Resolve-DriverClass -Update $update -DriverInfo $driverInfo

        $matched = $null
        foreach ($hardwareId in $hardwareIds) {
            if ($lookup.ContainsKey($hardwareId)) {
                $matched = $lookup[$hardwareId]
                break
            }
        }

        if (-not $matched -and $hardwareIds.Length -gt 0) {
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
            elseif (-not [string]::IsNullOrWhiteSpace($updateTitle)) {
                $deviceName = $updateTitle.Trim()
            }
            else {
                $deviceName = 'Unknown device'
            }
        }

        $availableVersion = $null
        if ($driverInfo -and $driverInfo.DriverVerVersion) {
            $availableVersion = Resolve-VersionString -Primary $driverInfo.DriverVerVersion -FallbackSources @()
        }

        if (-not $availableVersion -and $updateDescription) {
            $availableVersion = Resolve-VersionString -Primary (Extract-VersionFromDescription -Description $updateDescription) -FallbackSources @()
        }

        $availableDate = $null
        if ($driverInfo -and $driverInfo.DriverVerDate) {
            $availableDate = Convert-TidyDateString -Value $driverInfo.DriverVerDate
        }
        elseif ($updateLastDeploymentChange) {
            $availableDate = Convert-TidyDateString -Value $updateLastDeploymentChange
        }

        $manufacturer = $currentManufacturer
        if ([string]::IsNullOrWhiteSpace($manufacturer) -and $driverInfo) {
            $manufacturer = Resolve-VersionString -Primary $driverInfo.Manufacturer -FallbackSources @($driverInfo.ProviderName)
        }
        if ([string]::IsNullOrWhiteSpace($manufacturer) -and $updatePublisher) {
            $manufacturer = $updatePublisher.Trim()
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
            $skipEntry = New-SkipSummaryEntry -Title $updateTitle -DeviceName $deviceName -Manufacturer $manufacturer -DriverClass $driverClass -IsOptional $true -Reason 'Optional update excluded by policy.' -ReasonCode 'OptionalFilter' -UpdateId $updateId
            [void]$skipSummaries.Add($skipEntry)
            if (-not [string]::IsNullOrWhiteSpace($skipEntry.reason)) {
                [void]$filterSkipReasons.Add(('{0}: {1}' -f $skipEntry.deviceName, $skipEntry.reason))
            }
            continue
        }

        $filterReason = Should-SkipUpdateByFilters -DriverClass $driverClass -VendorName $manufacturer
        if ($filterReason) {
            $skippedByFilters++
            $skipContextTitle = if (-not [string]::IsNullOrWhiteSpace($updateTitle)) { $updateTitle.Trim() } else { 'Unknown update' }
            [void]$filterSkipReasons.Add(('{0}: {1}' -f $skipContextTitle, $filterReason))
            $skipEntry = New-SkipSummaryEntry -Title $updateTitle -DeviceName $deviceName -Manufacturer $manufacturer -DriverClass $driverClass -IsOptional $isOptional -Reason $filterReason -ReasonCode 'PolicyFilter' -UpdateId $updateId
            [void]$skipSummaries.Add($skipEntry)
            Write-TidyOutput -Message ("Skipping driver '{0}' due to filter: {1}" -f $deviceName, $filterReason)
            continue
        }

        $categoryNames = [System.Collections.Generic.List[string]]::new()
        foreach ($category in (Get-UpdateCategories -Update $update)) {
            if ($null -eq $category) { continue }
            $name = $category.Name
            if ([string]::IsNullOrWhiteSpace($name)) { continue }
            [void]$categoryNames.Add($name.Trim())
        }

        $infoUrls = [System.Collections.Generic.List[string]]::new()
        foreach ($url in ($updateMoreInfoUrls | ConvertTo-TidyArray)) {
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
            title                = if ([string]::IsNullOrWhiteSpace($updateTitle)) { $null } else { $updateTitle.Trim() }
                deviceName           = $deviceName
                manufacturer         = $manufacturer
                hardwareIds          = $hardwareIds
                isOptional           = [bool]$isOptional
                currentVersion       = if ($currentVersion) { $currentVersion } else { $null }
                currentVersionDate   = $currentDate
                availableVersion     = if ($availableVersion) { $availableVersion } else { $null }
                availableVersionDate = $availableDate
                description          = if ([string]::IsNullOrWhiteSpace($updateDescription)) { $null } else { $updateDescription.Trim() }
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

    $updatesArray = $updates.ToArray()
    $gpuGuidance = Get-GpuGuidanceItems -InstalledDrivers $installedDrivers -UpdateCandidates $updatesArray
    $count = $updatesArray.Count
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
        schemaVersion    = '1.2.0'
        generatedAtUtc   = (Get-Date).ToUniversalTime().ToString('o')
        includeOptional  = [bool]$IncludeOptional
        updates          = $updatesArray
        skippedOptional  = $skippedOptional
        skippedByFilters = $skippedByFilters
        skipDetails      = $filterSkipReasons.ToArray()
        skipSummaries    = $skipSummaries.ToArray()
        appliedFilters   = [pscustomobject]@{
            includeDriverClasses = $normalizedIncludeDriverClasses
            excludeDriverClasses = $normalizedExcludeDriverClasses
            allowVendors         = $normalizedAllowVendors
            blockVendors         = $normalizedBlockVendors
        }
        installedDrivers = $installedDrivers.ToArray()
        healthInsights   = $healthInsights
        vendorGuidance   = $gpuGuidance
    }

    $jsonPayload = $payload | ConvertTo-Json -Depth 8 -Compress
    Write-Output $jsonPayload
    $script:OperationSucceeded = $true
}
catch {
    $script:OperationSucceeded = $false
    $lineNumber = $_.InvocationInfo?.ScriptLineNumber
    $scriptPosition = $_.InvocationInfo?.PositionMessage
    $message = if ($lineNumber) { "Driver scan failed at line ${lineNumber}: $($_.Exception.Message)" } else { $_.Exception.Message }
    Write-TidyError -Message $message
    if ($scriptPosition) {
        Write-TidyError -Message $scriptPosition
    }
    if ($_.ScriptStackTrace) {
        Write-TidyError -Message $_.ScriptStackTrace
    }
}
finally {
    try {
        Save-TidyResult
    }
    catch {
        # ignore persistence failures
    }
}

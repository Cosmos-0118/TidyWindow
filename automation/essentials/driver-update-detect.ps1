param(
    [switch] $IncludeOptional,
    [switch] $Silent,
    [string] $ResultPath
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

function Get-InstalledDriverLookup {
    $lookup = [System.Collections.Generic.Dictionary[string,psobject]]::new([System.StringComparer]::OrdinalIgnoreCase)

    try {
        $installed = Get-CimInstance -ClassName Win32_PnPSignedDriver -ErrorAction Stop
    }
    catch {
        Write-TidyOutput -Message ('Unable to inventory installed drivers: {0}' -f $_.Exception.Message)
        return $lookup
    }

    foreach ($entry in @($installed)) {
        if ($null -eq $entry) { continue }

        $detail = [pscustomobject]@{
            DeviceName  = $entry.DeviceName
            FriendlyName = $entry.FriendlyName
            Manufacturer = $entry.Manufacturer
            DriverVersion = $entry.DriverVersion
            DriverDate    = $entry.DriverDate
            InfName       = $entry.InfName
        }

        foreach ($hardwareId in @($entry.HardwareID)) {
            if ([string]::IsNullOrWhiteSpace($hardwareId)) { continue }
            if (-not $lookup.ContainsKey($hardwareId)) {
                $lookup[$hardwareId] = $detail
            }
        }
    }

    return $lookup
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

    if ($null -eq $result -or $result.Updates is $null -or $result.Updates.Count -eq 0) {
        Write-TidyOutput -Message 'No driver updates are currently offered by Windows Update.'
        $payload = [pscustomobject]@{
            generatedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
            includeOptional = [bool]$IncludeOptional
            updates = @()
        }
        $json = $payload | ConvertTo-Json -Depth 4 -Compress
        Write-Output $json
        $script:OperationSucceeded = $true
        return
    }

    $lookup = Get-InstalledDriverLookup
    $updates = [System.Collections.Generic.List[psobject]]::new()
    $skippedOptional = 0

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

        if ($isOptional -and -not $IncludeOptional.IsPresent) {
            $skippedOptional++
            continue
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

        $updates.Add([pscustomobject]@{
            title = $update.Title
            deviceName = $deviceName
            manufacturer = $manufacturer
            hardwareIds = $hardwareIds
            isOptional = [bool]$isOptional
            currentVersion = if ($currentVersion) { $currentVersion } else { $null }
            currentVersionDate = $currentDate
            availableVersion = if ($availableVersion) { $availableVersion } else { $null }
            availableVersionDate = $availableDate
            description = if ([string]::IsNullOrWhiteSpace($update.Description)) { $null } else { $update.Description.Trim() }
            categories = $categoryNames.ToArray()
            informationUrls = $infoUrls.ToArray()
        })
    }

    $count = $updates.Count
    if ($count -eq 0) {
        Write-TidyOutput -Message 'No driver updates matched the requested filters.'
        if ($skippedOptional -gt 0 -and -not $IncludeOptional.IsPresent) {
            Write-TidyOutput -Message ("Skipped {0} optional update(s). Enable IncludeOptional to surface them." -f $skippedOptional)
        }
    }
    else {
        Write-TidyOutput -Message ("Detected {0} driver update(s)." -f $count)
        if ($skippedOptional -gt 0 -and -not $IncludeOptional.IsPresent) {
            Write-TidyOutput -Message ("Skipped {0} optional update(s). Enable IncludeOptional to include them." -f $skippedOptional)
        }
    }

    $payload = [pscustomobject]@{
        generatedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
        includeOptional = [bool]$IncludeOptional
        updates = $updates.ToArray()
        skippedOptional = $skippedOptional
    }

    $jsonPayload = $payload | ConvertTo-Json -Depth 6 -Compress
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

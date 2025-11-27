$script:TidyInventoryScriptsRoot = $null

function Get-TidyInventoryScriptsRoot {
    if ($script:TidyInventoryScriptsRoot) {
        return $script:TidyInventoryScriptsRoot
    }

    $candidate = Join-Path -Path $PSScriptRoot -ChildPath '..\scripts'
    $script:TidyInventoryScriptsRoot = [System.IO.Path]::GetFullPath($candidate)
    return $script:TidyInventoryScriptsRoot
}

function ConvertTo-TidyUniqueArray {
    param([System.Collections.IEnumerable] $Source)

    if (-not $Source) {
        return @()
    }

    $set = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($item in $Source) {
        if ($null -eq $item) { continue }
        $text = $item.ToString().Trim()
        if ([string]::IsNullOrWhiteSpace($text)) { continue }
        [void]$set.Add($text)
    }

    $results = New-Object 'System.Collections.Generic.List[string]' ($set.Count)
    foreach ($value in $set) {
        $results.Add($value) | Out-Null
    }

    return $results.ToArray()
}

function Normalize-TidyAppName {
    param([string] $Value)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return $null
    }

    $clean = [System.Text.RegularExpressions.Regex]::Replace($Value.Trim(), '[^A-Za-z0-9]+', ' ')
    if ([string]::IsNullOrWhiteSpace($clean)) {
        return $null
    }

    return $clean.Trim().ToLowerInvariant()
}

function Get-TidyInventoryKey {
    param(
        [string] $Name,
        [string] $Version
    )

    $normalized = Normalize-TidyAppName -Value $Name
    if (-not $normalized) {
        return $null
    }

    $versionToken = ''
    if (-not [string]::IsNullOrWhiteSpace($Version)) {
        $versionToken = $Version.Trim().ToLowerInvariant()
    }
    return "$normalized|$versionToken"
}

function Get-TidyProductCode {
    param(
        [string] $KeyName,
        [string[]] $CommandLines
    )

    $candidates = New-Object System.Collections.Generic.List[string]
    if (-not [string]::IsNullOrWhiteSpace($KeyName)) {
        $candidates.Add($KeyName)
    }

    if ($CommandLines) {
        foreach ($line in $CommandLines) {
            if ([string]::IsNullOrWhiteSpace($line)) { continue }
            $candidates.Add($line)
        }
    }

    foreach ($candidate in $candidates) {
        if ($candidate -match '\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}') {
            return $matches[0]
        }
    }

    return $null
}

function Get-TidyInstallerHints {
    param([string[]] $CommandLines)

    if (-not $CommandLines) {
        return @()
    }

    $set = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($command in $CommandLines) {
        if ([string]::IsNullOrWhiteSpace($command)) { continue }
        $value = $command.Trim()

        if ($value -match 'msiexec(\.exe)?') {
            [void]$set.Add('MSI')
        }
        if ($value -match 'unins(\d+)?\.exe' -or $value -match 'InnoSetup') {
            [void]$set.Add('Inno Setup')
        }
        if ($value -match 'InstallShield' -or $value -match 'isscript') {
            [void]$set.Add('InstallShield')
        }
        if ($value -match 'nsuninst\.exe' -or $value -match 'nsis') {
            [void]$set.Add('NSIS')
        }
        if ($value -match 'setup\.exe' -and -not $set.Contains('SetupExe')) {
            [void]$set.Add('SetupExe')
        }
    }

    $results = New-Object 'System.Collections.Generic.List[string]' ($set.Count)
    foreach ($value in $set) {
        $results.Add($value) | Out-Null
    }

    return $results.ToArray()
}

function Get-TidyInstallerType {
    param(
        [bool] $IsWindowsInstaller,
        [string[]] $InstallerHints,
        [string] $UninstallString
    )

    if ($IsWindowsInstaller) {
        return 'MSI'
    }

    if ($InstallerHints -and ($InstallerHints -contains 'Inno Setup')) {
        return 'Inno Setup'
    }

    if ($InstallerHints -and ($InstallerHints -contains 'InstallShield')) {
        return 'InstallShield'
    }

    if ($InstallerHints -and ($InstallerHints -contains 'NSIS')) {
        return 'NSIS'
    }

    if (-not [string]::IsNullOrWhiteSpace($UninstallString) -and $UninstallString.IndexOf('winget', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
        return 'Winget'
    }

    if ($InstallerHints -and $InstallerHints.Length -gt 0) {
        return $InstallerHints[0]
    }

    return 'Unknown'
}

function Get-TidyEstimatedSizeBytes {
    param($EstimatedSize)

    if ($null -eq $EstimatedSize) {
        return $null
    }

    try {
        $value = [long]$EstimatedSize
        if ($value -le 0) { return $null }
        return $value * 1KB
    }
    catch {
        return $null
    }
}

function Get-TidyRegistryInstalledApps {
    param(
        [bool] $IncludeSystemComponents,
        [bool] $IncludeUpdates,
        [bool] $IncludeUserHives,
        [System.Collections.Generic.List[string]] $Warnings
    )

    $entries = New-Object System.Collections.Generic.List[psobject]
    $roots = @(
        @{ Path = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall'; Tag = 'Registry64'; Hive = 'HKLM' },
        @{ Path = 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall'; Tag = 'Registry32'; Hive = 'HKLM' },
        @{ Path = 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall'; Tag = 'CurrentUser'; Hive = 'HKCU' }
    )

    foreach ($root in $roots) {
        if ($root.Hive -eq 'HKCU' -and -not $IncludeUserHives) {
            continue
        }

        $path = $root.Path
        if (-not (Test-Path -LiteralPath $path)) {
            continue
        }

        try {
            $subKeys = Get-ChildItem -Path $path -ErrorAction Stop
        }
        catch {
            if ($Warnings) {
                $Warnings.Add("Failed to enumerate uninstall entries under '$path': $($_.Exception.Message)") | Out-Null
            }
            continue
        }

        foreach ($key in $subKeys) {
            $props = $null
            try {
                $props = Get-ItemProperty -LiteralPath $key.PSPath -ErrorAction Stop
            }
            catch {
                continue
            }

            $displayName = $props.PSObject.Properties['DisplayName']
            if ($null -eq $displayName -or [string]::IsNullOrWhiteSpace($displayName.Value)) {
                continue
            }

            $name = $displayName.Value.Trim()
            $systemComponent = $false
            $systemComponentProperty = $props.PSObject.Properties['SystemComponent']
            if ($systemComponentProperty -and $systemComponentProperty.Value) {
                $raw = $systemComponentProperty.Value
                $systemComponent = ($raw -is [bool] -and $raw) -or ($raw -is [int] -and $raw -eq 1)
            }

            if (-not $IncludeSystemComponents -and $systemComponent) {
                continue
            }

            $releaseType = $null
            $releaseTypeProperty = $props.PSObject.Properties['ReleaseType']
            if ($releaseTypeProperty -and -not [string]::IsNullOrWhiteSpace($releaseTypeProperty.Value)) {
                $releaseType = $releaseTypeProperty.Value.Trim()
            }

            if (-not $IncludeUpdates -and -not [string]::IsNullOrWhiteSpace($releaseType) -and $releaseType -match 'update|hotfix|security') {
                continue
            }

            $displayVersion = $props.PSObject.Properties['DisplayVersion']
            $publisherProperty = $props.PSObject.Properties['Publisher']
            $installLocationProperty = $props.PSObject.Properties['InstallLocation']
            $uninstallStringProperty = $props.PSObject.Properties['UninstallString']
            $quietUninstallStringProperty = $props.PSObject.Properties['QuietUninstallString']
            $windowsInstallerProperty = $props.PSObject.Properties['WindowsInstaller']

            $installDateProperty = $props.PSObject.Properties['InstallDate']
            $estimatedSizeProperty = $props.PSObject.Properties['EstimatedSize']
            $displayIconProperty = $props.PSObject.Properties['DisplayIcon']
            $languageProperty = $props.PSObject.Properties['Language']

            $displayVersionValue = $null
            if ($displayVersion) {
                $displayVersionValue = $displayVersion.Value
            }

            $publisherValue = $null
            if ($publisherProperty) {
                $publisherValue = $publisherProperty.Value
            }

            $installLocation = $null
            if ($installLocationProperty) {
                $installLocation = $installLocationProperty.Value
            }

            if ($installLocation -and -not [string]::IsNullOrWhiteSpace($installLocation)) {
                $installLocation = $installLocation.Trim()
            }
            else {
                $installLocation = $null
            }

            $windowsInstaller = $false
            if ($windowsInstallerProperty -and $windowsInstallerProperty.Value) {
                $rawInstaller = $windowsInstallerProperty.Value
                $windowsInstaller = ($rawInstaller -is [bool] -and $rawInstaller) -or ($rawInstaller -is [int] -and $rawInstaller -eq 1)
            }

            $uninstallString = $null
            if ($uninstallStringProperty) {
                $uninstallString = $uninstallStringProperty.Value
            }

            $quietUninstallString = $null
            if ($quietUninstallStringProperty) {
                $quietUninstallString = $quietUninstallStringProperty.Value
            }
            $commandLines = @($uninstallString, $quietUninstallString) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
            $productCode = Get-TidyProductCode -KeyName $key.PSChildName -CommandLines $commandLines
            $installerHints = Get-TidyInstallerHints -CommandLines $commandLines
            $installerType = Get-TidyInstallerType -IsWindowsInstaller $windowsInstaller -InstallerHints $installerHints -UninstallString $uninstallString

            $estimatedSizeValue = $null
            if ($estimatedSizeProperty) {
                $estimatedSizeValue = $estimatedSizeProperty.Value
            }

            $estimatedBytes = Get-TidyEstimatedSizeBytes -EstimatedSize $estimatedSizeValue

            $installDateValue = $null
            if ($installDateProperty) {
                $installDateValue = $installDateProperty.Value
            }

            $displayIconValue = $null
            if ($displayIconProperty) {
                $displayIconValue = $displayIconProperty.Value
            }

            $languageValue = $null
            if ($languageProperty) {
                $languageValue = $languageProperty.Value
            }

            $metadata = @{}
            $metadata['RegistryHive'] = $root.Hive
            $metadata['RegistryPath'] = $key.PSPath
            if ($root.Tag) { $metadata['RegistryScope'] = $root.Tag }

            $sourceTags = @('Registry', $root.Tag)
            if ($root.Hive -eq 'HKCU') {
                $sourceTags += 'User'
            }
            else {
                $sourceTags += 'Machine'
            }

            $entry = [pscustomobject]@{
                Name = $name
                Version = $displayVersionValue
                Publisher = $publisherValue
                InstallLocation = $installLocation
                UninstallString = $uninstallString
                QuietUninstallString = $quietUninstallString
                WindowsInstaller = $windowsInstaller
                ProductCode = $productCode
                InstallerType = $installerType
                InstallerHints = $installerHints
                SourceTags = ConvertTo-TidyUniqueArray -Source $sourceTags
                RegistryKey = $key.PSPath
                SystemComponent = $systemComponent
                ReleaseType = $releaseType
                EstimatedSizeBytes = $estimatedBytes
                InstallDate = $installDateValue
                DisplayIcon = $displayIconValue
                Language = $languageValue
                WingetId = $null
                WingetSource = $null
                WingetVersion = $null
                WingetAvailableVersion = $null
                Metadata = $metadata
            }

            $entries.Add($entry)
        }
    }

    return $entries
}

function Get-TidyWingetInventoryScriptPath {
    $root = Get-TidyInventoryScriptsRoot
    if (-not $root) {
        return $null
    }

    $scriptPath = Join-Path -Path $root -ChildPath 'get-package-inventory.ps1'
    return [System.IO.Path]::GetFullPath($scriptPath)
}

function Get-TidyWingetInstalledApps {
    param([System.Collections.Generic.List[string]] $Warnings)

    $scriptPath = Get-TidyWingetInventoryScriptPath
    if (-not $scriptPath -or -not (Test-Path -LiteralPath $scriptPath)) {
        if ($Warnings) {
            $Warnings.Add("winget inventory script not found at '$scriptPath'.") | Out-Null
        }
        return @()
    }

    try {
        $json = & $scriptPath -Managers @('winget')
        if ([string]::IsNullOrWhiteSpace($json)) {
            return @()
        }

        $payload = $json | ConvertFrom-Json -ErrorAction Stop
        if ($null -eq $payload) {
            return @()
        }

        if ($payload.warnings) {
            foreach ($warning in $payload.warnings) {
                if ([string]::IsNullOrWhiteSpace($warning)) { continue }
                $Warnings.Add($warning.Trim()) | Out-Null
            }
        }

        $results = New-Object System.Collections.Generic.List[psobject]
        foreach ($package in @($payload.packages)) {
            if (-not $package) { continue }
            if (-not $package.Manager -or $package.Manager -ne 'winget') { continue }
            $name = $package.Name
            $id = $package.Id
            if ([string]::IsNullOrWhiteSpace($name) -or [string]::IsNullOrWhiteSpace($id)) { continue }

            $sourceValue = 'winget'
            if ($package.Source) {
                $sourceValue = $package.Source
            }

            $results.Add([pscustomobject]@{
                Name = $name
                Version = $package.InstalledVersion
                Source = $sourceValue
                Id = $id
                AvailableVersion = $package.AvailableVersion
            })
        }

        return $results
    }
    catch {
        if ($Warnings) {
            $Warnings.Add("winget enumeration failed: $($_.Exception.Message)") | Out-Null
        }

        return @()
    }
}

function Merge-TidyWingetMetadata {
    param(
        [System.Collections.Generic.List[psobject]] $RegistryApps,
        [System.Collections.Generic.List[psobject]] $WingetApps
    )

    if (-not $RegistryApps -or $RegistryApps.Count -eq 0) {
        return $WingetApps
    }

    if (-not $WingetApps -or $WingetApps.Count -eq 0) {
        return $RegistryApps
    }

    $index = New-Object 'System.Collections.Generic.Dictionary[string,System.Collections.Generic.List[psobject]]' ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($app in $RegistryApps) {
        $key = Get-TidyInventoryKey -Name $app.Name -Version $app.Version
        if (-not $key) {
            continue
        }

        if (-not $index.ContainsKey($key)) {
            $index[$key] = New-Object System.Collections.Generic.List[psobject]
        }

        $index[$key].Add($app)
    }

    foreach ($winget in $WingetApps) {
        $key = Get-TidyInventoryKey -Name $winget.Name -Version $winget.Version
        $applied = $false
        if ($key -and $index.ContainsKey($key)) {
            foreach ($match in $index[$key]) {
                $match.WingetId = $winget.Id
                $wingetSource = 'winget'
                if ($winget.Source) {
                    $wingetSource = $winget.Source
                }

                $match.WingetSource = $wingetSource
                $match.WingetVersion = $winget.Version
                $match.WingetAvailableVersion = $winget.AvailableVersion
                $match.SourceTags = ConvertTo-TidyUniqueArray -Source (@($match.SourceTags) + @('Winget'))
            }

            $applied = $true
        }

        if (-not $applied) {
            $metadata = @{}
            $metadataSource = 'winget'
            if ($winget.Source) {
                $metadataSource = $winget.Source
            }

            $metadata['Source'] = $metadataSource
            $RegistryApps.Add([pscustomobject]@{
                Name = $winget.Name
                Version = $winget.Version
                Publisher = $null
                InstallLocation = $null
                UninstallString = $null
                QuietUninstallString = $null
                WindowsInstaller = $false
                ProductCode = $null
                InstallerType = 'Winget'
                InstallerHints = @('Winget')
                SourceTags = @('Winget')
                RegistryKey = $null
                SystemComponent = $false
                ReleaseType = $null
                EstimatedSizeBytes = $null
                InstallDate = $null
                DisplayIcon = $null
                Language = $null
                WingetId = $winget.Id
                WingetSource = $winget.Source
                WingetVersion = $winget.Version
                WingetAvailableVersion = $winget.AvailableVersion
                Metadata = $metadata
            })
        }
    }

    return $RegistryApps
}

function Get-TidyInstalledAppInventory {
    [CmdletBinding()]
    param(
        [switch] $IncludeSystemComponents,
        [switch] $IncludeUpdates,
        [switch] $IncludeWinget,
        [switch] $IncludeUserHives
    )

    $warnings = New-Object System.Collections.Generic.List[string]

    $includeWingetFlag = $true
    if ($PSBoundParameters.ContainsKey('IncludeWinget')) {
        $includeWingetFlag = $IncludeWinget.IsPresent
    }

    $includeUserHivesFlag = $true
    if ($PSBoundParameters.ContainsKey('IncludeUserHives')) {
        $includeUserHivesFlag = $IncludeUserHives.IsPresent
    }

    $registryApps = Get-TidyRegistryInstalledApps -IncludeSystemComponents $IncludeSystemComponents.IsPresent -IncludeUpdates $IncludeUpdates.IsPresent -IncludeUserHives $includeUserHivesFlag -Warnings $warnings
    $resultList = New-Object System.Collections.Generic.List[psobject]
    foreach ($entry in $registryApps) {
        $resultList.Add($entry)
    }

    if ($includeWingetFlag) {
        $wingetApps = Get-TidyWingetInstalledApps -Warnings $warnings
        if ($wingetApps) {
            $resultList = Merge-TidyWingetMetadata -RegistryApps $resultList -WingetApps $wingetApps
        }
    }

    return [pscustomobject]@{
        Apps = $resultList
        Warnings = $warnings
    }
}

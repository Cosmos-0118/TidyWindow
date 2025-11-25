param(
    [string[]]$Managers
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Remove-AnsiSequences {
    param([string]$Value)

    if ([string]::IsNullOrEmpty($Value)) {
        return $Value
    }

    return [System.Text.RegularExpressions.Regex]::Replace($Value, '\x1B\[[0-9;]*m', '')
}

function Split-TableColumns {
    param([string]$Line)

    if ([string]::IsNullOrWhiteSpace($Line)) {
        return @()
    }

    $clean = Remove-AnsiSequences -Value $Line
    return [System.Text.RegularExpressions.Regex]::Split($clean.Trim(), '\s{2,}') | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
}

function New-StringDictionary {
    return New-Object 'System.Collections.Generic.Dictionary[string,string]' ([System.StringComparer]::OrdinalIgnoreCase)
}

function New-ObjectDictionary {
    return New-Object 'System.Collections.Generic.Dictionary[string,psobject]' ([System.StringComparer]::OrdinalIgnoreCase)
}

function Normalize-NullableValue {
    param([string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return $null
    }

    $trimmed = $Value.Trim()
    if ([string]::IsNullOrWhiteSpace($trimmed) -or $trimmed -eq '-') {
        return $null
    }

    $repaired = Repair-TextEncodingArtifacts -Value $trimmed
    if ([string]::IsNullOrWhiteSpace($repaired)) {
        return $null
    }

    return $repaired
}

function Normalize-Identifier {
    param([string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return $null
    }

    $trimmed = $Value.Trim()
    if ([string]::IsNullOrEmpty($trimmed)) {
        return $null
    }

    $sanitized = [System.Text.RegularExpressions.Regex]::Replace($trimmed, '^[^A-Za-z0-9]+', '')
    return $sanitized
}

 $script:EncodingArtifactChars = @(
    [char]0x00C2,
    [char]0x00C3,
    [char]0x00C7,
    [char]0x00CA,
    [char]0x00E2,
    [char]0x00AA,
    [char]0x00BA,
    [char]0x00B0,
    [char]0x00B7,
    [char]0x00B4,
    [char]0x00AB,
    [char]0x00BB,
    [char]0x0393,
    [char]0x0394,
    [char]0x03A3,
    [char]0x03C0,
    [char]0x252C,
    [char]0x2534,
    [char]0x2561,
    [char]0x2591,
    [char]0x2592,
    [char]0x2593,
    [char]0xFFFD
)

function Repair-TextEncodingArtifacts {
    param([string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return $Value
    }

    $trimmed = $Value.Trim()
    $requiresRepair = $false
    foreach ($artifact in $script:EncodingArtifactChars) {
        if ($trimmed.IndexOf($artifact) -ge 0) {
            $requiresRepair = $true
            break
        }
    }

    if (-not $requiresRepair) {
        return $trimmed
    }

    foreach ($encodingName in @('windows-1252', 'ibm437')) {
        try {
            $encoding = [System.Text.Encoding]::GetEncoding(
                $encodingName,
                [System.Text.EncoderFallback]::ExceptionFallback,
                [System.Text.DecoderFallback]::ExceptionFallback)
            $bytes = $encoding.GetBytes($trimmed)
            $decoded = [System.Text.Encoding]::UTF8.GetString($bytes)
            if ($decoded.IndexOf([char]0xFFFD) -ge 0) {
                continue
            }

            if (-not [string]::IsNullOrWhiteSpace($decoded) -and $decoded -ne $trimmed) {
                return $decoded.Trim()
            }
        }
        catch {
            continue
        }
    }

    return $trimmed
}

function Normalize-DisplayText {
    param([string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return $null
    }

    $repaired = Repair-TextEncodingArtifacts -Value $Value
    if ([string]::IsNullOrWhiteSpace($repaired)) {
        return $null
    }

    $collapsed = [System.Text.RegularExpressions.Regex]::Replace($repaired, '\s+', ' ')
    return $collapsed.Trim()
}

function Normalize-WingetSourceName {
    param([string]$Value)

    $normalized = Normalize-DisplayText -Value $Value
    if ([string]::IsNullOrWhiteSpace($normalized)) {
        return $null
    }

    $normalized = Repair-WingetTruncatedToken -Value $normalized
    if ([string]::IsNullOrWhiteSpace($normalized)) {
        return $null
    }

    if ($normalized -match 'winget') {
        return 'winget'
    }

    if ($normalized -match '^mssto') {
        return 'msstore'
    }

    if ($normalized -match 'msstore|store') {
        return 'msstore'
    }

    return $normalized
}

$script:WingetExportLookup = $null
$script:WingetExportLookupFailed = $false

function Get-WingetExportLookup {
    param([System.Management.Automation.CommandInfo]$Command)

    if ($script:WingetExportLookupFailed) {
        return $null
    }

    if ($script:WingetExportLookup -is [System.Collections.IDictionary]) {
        return $script:WingetExportLookup
    }

    if (-not $Command) {
        $script:WingetExportLookupFailed = $true
        return $null
    }

    $tempPath = $null
    $lookup = New-Object 'System.Collections.Generic.Dictionary[string,psobject]' ([System.StringComparer]::OrdinalIgnoreCase)

    try {
        $tempPath = [System.IO.Path]::GetTempFileName()
        $args = @('export', '--accept-source-agreements', '--include-versions', '--disable-interactivity', '--output', $tempPath)
        & $Command.Source @args *> $null

        if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $tempPath)) {
            $script:WingetExportLookupFailed = $true
            return $null
        }

        $json = Get-Content -LiteralPath $tempPath -Raw -ErrorAction Stop
        if ([string]::IsNullOrWhiteSpace($json)) {
            $script:WingetExportLookup = $lookup
            return $lookup
        }

        $payload = ConvertFrom-Json -InputObject $json -ErrorAction Stop
        if ($null -eq $payload -or -not $payload.Sources) {
            $script:WingetExportLookup = $lookup
            return $lookup
        }

        foreach ($source in @($payload.Sources)) {
            if (-not $source) { continue }

            $sourceName = $null
            if ($source.PSObject.Properties.Match('SourceDetails')) {
                $details = $source.SourceDetails
                if ($details) {
                    $sourceName = $details.Name
                    if ([string]::IsNullOrWhiteSpace($sourceName)) {
                        $sourceName = $details.Identifier
                    }
                }
            }

            $sourceName = Normalize-WingetSourceName -Value $sourceName

            foreach ($package in @($source.Packages)) {
                if (-not $package) { continue }
                $identifier = $package.PackageIdentifier
                if ([string]::IsNullOrWhiteSpace($identifier)) { continue }

                $key = $identifier.Trim()
                if (-not $lookup.ContainsKey($key)) {
                    $lookup[$key] = [pscustomobject]@{
                        PackageIdentifier = $key
                        Version           = $package.Version
                        Source            = $sourceName
                    }
                }
                else {
                    $lookup[$key] = [pscustomobject]@{
                        PackageIdentifier = $key
                        Version           = $package.Version
                        Source            = if (-not [string]::IsNullOrWhiteSpace($sourceName)) { $sourceName } else { $lookup[$key].Source }
                    }
                }
            }
        }

        $script:WingetExportLookup = $lookup
        return $lookup
    }
    catch {
        $script:WingetExportLookupFailed = $true
        return $null
    }
    finally {
        if ($tempPath -and (Test-Path -LiteralPath $tempPath)) {
            Remove-Item -LiteralPath $tempPath -Force -ErrorAction SilentlyContinue
        }
    }
}

function Repair-WingetTruncatedToken {
    param([string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return $null
    }

    $clean = $Value.Trim()
    if ($clean.IndexOf('ΓÇª') -ge 0) {
        $clean = $clean -replace 'ΓÇª', '…'
    }

    if ($clean.EndsWith('…')) {
        $clean = $clean.Substring(0, $clean.Length - 1).TrimEnd()
    }

    if ($clean.EndsWith('...')) {
        $clean = $clean.Substring(0, $clean.Length - 3).TrimEnd()
    }

    if ($clean.Length -gt 0) {
        return $clean
    }

    return $Value.Trim()
}

function Resolve-WingetIdentifier {
    param(
        [string]$Candidate,
        [string]$InstalledVersion,
        [System.Collections.Generic.Dictionary[string,psobject]]$Lookup
    )

    if ([string]::IsNullOrWhiteSpace($Candidate)) {
        return $null
    }

    $trimmed = $Candidate.Trim()
    if ($Lookup -and $Lookup.ContainsKey($trimmed)) {
        return $trimmed
    }

    $normalized = Repair-WingetTruncatedToken -Value $trimmed
    if ([string]::IsNullOrWhiteSpace($normalized)) {
        $normalized = $trimmed
    }

    if ($Lookup -and $Lookup.ContainsKey($normalized)) {
        return $normalized
    }

    if (-not $Lookup) {
        return $normalized
    }

    $matches = New-Object 'System.Collections.Generic.List[string]'
    foreach ($key in $Lookup.Keys) {
        if ($key.StartsWith($normalized, [System.StringComparison]::OrdinalIgnoreCase)) {
            $matches.Add($key)
        }
    }

    if ($matches.Count -eq 1) {
        return $matches[0]
    }

    if ($matches.Count -gt 1 -and -not [string]::IsNullOrWhiteSpace($InstalledVersion)) {
        $candidateVersion = $InstalledVersion.Trim()
        foreach ($match in $matches) {
            $entry = $Lookup[$match]
            if ($entry -and -not [string]::IsNullOrWhiteSpace($entry.Version) -and [string]::Equals($entry.Version.Trim(), $candidateVersion, [System.StringComparison]::OrdinalIgnoreCase)) {
                return $match
            }
        }
    }

    return $normalized
}

function Get-ColumnMap {
    param([string[]]$Lines)

    if (-not $Lines) {
        return $null
    }

    for ($i = 0; $i -lt $Lines.Count; $i++) {
        $candidate = $Lines[$i]
        if ([string]::IsNullOrWhiteSpace($candidate)) { continue }
        if ($candidate -match '^\s*Name\s+Id\s+Version') {
            $idStart = $candidate.IndexOf('Id')
            $versionStart = $candidate.IndexOf('Version', [Math]::Max($idStart, 0))
            $availableStart = $candidate.IndexOf('Available', [Math]::Max($versionStart, 0))
            $sourceStart = $candidate.IndexOf('Source', [Math]::Max($availableStart, 0))

            return @{
                HeaderIndex   = $i
                IdStart       = $idStart
                VersionStart  = $versionStart
                AvailableStart = $availableStart
                SourceStart   = $sourceStart
            }
        }
    }

    return $null
}

function Get-ColumnValue {
    param(
        [string]$Line,
        [int]$Start,
        [int]$End
    )

    if ([string]::IsNullOrEmpty($Line)) {
        return ''
    }

    if ($Start -lt 0 -or $Start -ge $Line.Length) {
        return ''
    }

    $hasEnd = $End -ge 0 -and $End -gt $Start
    $effectiveEnd = if ($hasEnd) { [Math]::Min($Line.Length, $End) } else { $Line.Length }
    $length = $effectiveEnd - $Start

    if ($length -le 0) {
        return ''
    }

    return $Line.Substring($Start, $length).Trim()
}

$callerModulePath = $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($callerModulePath)) {
    $callerModulePath = $PSCommandPath
}

$scriptDirectory = Split-Path -Parent $callerModulePath
if ([string]::IsNullOrWhiteSpace($scriptDirectory)) {
    $scriptDirectory = (Get-Location).Path
}

$modulePath = Join-Path -Path $scriptDirectory -ChildPath '..\modules\TidyWindow.Automation\TidyWindow.Automation.psm1'
$modulePath = [System.IO.Path]::GetFullPath($modulePath)
if (-not (Test-Path -LiteralPath $modulePath)) {
    throw "Automation module not found at path '$modulePath'."
}

Import-Module $modulePath -Force

$packages = New-Object System.Collections.Generic.List[psobject]
$warnings = New-Object System.Collections.Generic.List[string]

if (-not $Managers -or $Managers.Count -eq 0) {
    $Managers = @('winget', 'choco', 'scoop')
}

$Managers = $Managers | ForEach-Object { $_.Trim().ToLowerInvariant() } | Where-Object { $_ }

$wingetCommand = Get-Command -Name 'winget' -ErrorAction SilentlyContinue
$chocoCommand = Get-Command -Name 'choco' -ErrorAction SilentlyContinue
$scoopCommand = Get-Command -Name 'scoop' -ErrorAction SilentlyContinue

function Collect-WingetInventory {
    param([System.Management.Automation.CommandInfo]$Command)

    $exportLookup = Get-WingetExportLookup -Command $Command
    $installed = New-ObjectDictionary
    $upgrades = New-ObjectDictionary

    $args = @('list', '--accept-source-agreements', '--disable-interactivity')
    $lines = & $Command.Source @args 2>$null

    if ($LASTEXITCODE -ne 0) {
        $script:warnings.Add("winget list failed with exit code $LASTEXITCODE.") | Out-Null
    }

    if ($LASTEXITCODE -eq 0 -and $lines) {
        $lines = @($lines)
        $map = Get-ColumnMap -Lines $lines

        if ($map) {
            $headerIndex = [int]$map.HeaderIndex
            $idStart = [int]$map.IdStart
            $versionStart = [int]$map.VersionStart
            $availableStart = [int]$map.AvailableStart
            $sourceStart = [int]$map.SourceStart
            $startIndex = [Math]::Min($lines.Count, $headerIndex + 2)

            for ($i = $startIndex; $i -lt $lines.Count; $i++) {
                $line = $lines[$i]
                if ([string]::IsNullOrWhiteSpace($line)) { continue }
                if ($line -match '^-{3,}') { continue }
                if ($line -like 'Installed apps*') { continue }

                $targetWidth = if ($sourceStart -ge 0) { $sourceStart + 20 } elseif ($availableStart -ge 0) { $availableStart + 20 } elseif ($versionStart -ge 0) { $versionStart + 20 } else { $line.Length }
                $padded = $line.PadRight([Math]::Max($line.Length, $targetWidth))

                $name = Get-ColumnValue -Line $padded -Start 0 -End $idStart
                $id = Get-ColumnValue -Line $padded -Start $idStart -End $versionStart
                $version = Get-ColumnValue -Line $padded -Start $versionStart -End $availableStart
                $source = Get-ColumnValue -Line $padded -Start $sourceStart -End -1

                $normalizedId = Normalize-Identifier -Value $id
                if (-not [string]::IsNullOrWhiteSpace($normalizedId)) {
                    $cleanSource = Normalize-WingetSourceName -Value $source
                    if ([string]::IsNullOrWhiteSpace($cleanSource)) {
                        $cleanSource = 'winget'
                    }

                    $cleanVersion = Normalize-NullableValue -Value $version
                    $canonicalId = Resolve-WingetIdentifier -Candidate $normalizedId -InstalledVersion $cleanVersion -Lookup $exportLookup
                    if ([string]::IsNullOrWhiteSpace($canonicalId)) {
                        $canonicalId = $normalizedId
                    }

                    $displayName = Normalize-DisplayText -Value $name
                    if ([string]::IsNullOrWhiteSpace($displayName)) {
                        $displayName = $canonicalId
                    }

                    $resolvedVersion = $cleanVersion
                    $resolvedSource = $cleanSource
                    if ($exportLookup -and $exportLookup.ContainsKey($canonicalId)) {
                        $exportEntry = $exportLookup[$canonicalId]
                        if (-not $resolvedVersion -and $exportEntry.Version) {
                            $resolvedVersion = Normalize-NullableValue -Value $exportEntry.Version
                        }

                        if ([string]::IsNullOrWhiteSpace($resolvedSource) -and $exportEntry.Source) {
                            $resolvedSource = Normalize-WingetSourceName -Value $exportEntry.Source
                        }
                    }

                    if ([string]::IsNullOrWhiteSpace($resolvedSource)) {
                        $resolvedSource = 'winget'
                    }

                    if (-not $installed.ContainsKey($canonicalId)) {
                        $installed[$canonicalId] = [pscustomobject]@{
                            Name = $displayName
                            Version = $resolvedVersion
                            Source = $resolvedSource
                        }
                    }
                    elseif ($resolvedSource -eq 'winget' -and $installed[$canonicalId].Source -ne 'winget') {
                        # Prefer winget-backed entries when duplicates exist.
                        $installed[$canonicalId] = [pscustomobject]@{
                            Name = $displayName
                            Version = $resolvedVersion
                            Source = $resolvedSource
                        }
                    }
                }
            }
        }
    }

    $upgradeArgs = @('upgrade', '--include-unknown', '--accept-source-agreements', '--disable-interactivity')
    $upgradeLines = & $Command.Source @upgradeArgs 2>$null

    if ($LASTEXITCODE -ne 0) {
        $script:warnings.Add("winget upgrade failed with exit code $LASTEXITCODE.") | Out-Null
    }

    if ($LASTEXITCODE -eq 0 -and $upgradeLines) {
        $upgradeLines = @($upgradeLines)
        $map = Get-ColumnMap -Lines $upgradeLines

        if ($map) {
            $headerIndex = [int]$map.HeaderIndex
            $idStart = [int]$map.IdStart
            $versionStart = [int]$map.VersionStart
            $availableStart = [int]$map.AvailableStart
            $sourceStart = [int]$map.SourceStart
            $startIndex = [Math]::Min($upgradeLines.Count, $headerIndex + 2)

            for ($i = $startIndex; $i -lt $upgradeLines.Count; $i++) {
                $line = $upgradeLines[$i]
                if ([string]::IsNullOrWhiteSpace($line)) { continue }
                if ($line -match '^-{3,}' -or $line -match 'upgrades? available') { continue }
                if ($line -like 'Installed apps*') { continue }

                $targetWidth = if ($sourceStart -ge 0) { $sourceStart + 20 } elseif ($availableStart -ge 0) { $availableStart + 20 } elseif ($versionStart -ge 0) { $versionStart + 20 } else { $line.Length }
                $padded = $line.PadRight([Math]::Max($line.Length, $targetWidth))

                $id = Get-ColumnValue -Line $padded -Start $idStart -End $versionStart
                $normalizedId = Normalize-Identifier -Value $id
                $available = Get-ColumnValue -Line $padded -Start $availableStart -End $sourceStart
                $upgradeSource = if ($sourceStart -ge 0) { Get-ColumnValue -Line $padded -Start $sourceStart -End -1 } else { $null }

                if (-not [string]::IsNullOrWhiteSpace($normalizedId) -and -not [string]::IsNullOrWhiteSpace($available)) {
                    $canonicalUpgradeId = Resolve-WingetIdentifier -Candidate $normalizedId -InstalledVersion $null -Lookup $exportLookup
                    if ([string]::IsNullOrWhiteSpace($canonicalUpgradeId)) {
                        $canonicalUpgradeId = $normalizedId
                    }

                    $cleanUpgradeSource = Normalize-WingetSourceName -Value $upgradeSource
                    if ([string]::IsNullOrWhiteSpace($cleanUpgradeSource)) {
                        $cleanUpgradeSource = 'winget'
                    }

                    if (-not $upgrades.ContainsKey($canonicalUpgradeId)) {
                        $upgrades[$canonicalUpgradeId] = [pscustomobject]@{
                            Sources = New-Object 'System.Collections.Generic.Dictionary[string,string]' ([System.StringComparer]::OrdinalIgnoreCase)
                        }
                    }

                    $sourceMap = $upgrades[$canonicalUpgradeId].Sources
                    $sourceMap[$cleanUpgradeSource] = Normalize-NullableValue -Value $available
                }
            }
        }
    }

    return ,@($installed, $upgrades)
}

function Collect-ChocoInventory {
    param([System.Management.Automation.CommandInfo]$Command)

    $installed = New-ObjectDictionary
    $upgrades = New-ObjectDictionary

    $installRoot = $env:ChocolateyInstall
    if ([string]::IsNullOrWhiteSpace($installRoot)) {
        $installRoot = 'C:\ProgramData\chocolatey'
    }

    $libRoot = Join-Path -Path $installRoot -ChildPath 'lib'
    if (-not (Test-Path -LiteralPath $libRoot)) {
        [void]$script:warnings.Add("Chocolatey library directory not found at '$libRoot'.")
    }
    else {
        try {
            $packageDirs = Get-ChildItem -Path $libRoot -Directory -ErrorAction Stop
        }
        catch {
            $packageDirs = @()
            [void]$script:warnings.Add("Failed to enumerate Chocolatey packages under '$libRoot': $_")
        }

        foreach ($dir in @($packageDirs)) {
            if (-not $dir) { continue }
            $packageId = $dir.Name
            if ([string]::IsNullOrWhiteSpace($packageId)) { continue }

            $version = $null
            try {
                $version = Get-TidyInstalledPackageVersion -Manager 'choco' -PackageId $packageId
            }
            catch {
                $version = $null
            }

            $displayName = Normalize-DisplayText -Value $packageId
            if ([string]::IsNullOrWhiteSpace($displayName)) {
                $displayName = $packageId
            }

            $installed[$packageId] = [pscustomobject]@{
                Name    = $displayName
                Version = Normalize-NullableValue -Value $version
                Source  = 'chocolatey'
            }
        }
    }

    $upgradeLines = & $Command.Source 'outdated' '--limit-output' '--no-color' 2>$null
    if ($LASTEXITCODE -eq 0 -and $upgradeLines) {
        foreach ($line in $upgradeLines) {
            if ($line -match '^(?<id>[^|]+)\|(?<installed>[^|]*)\|(?<available>[^|]*)') {
                $id = $matches['id'].Trim()
                $available = $matches['available'].Trim()
                if ($id) {
                    $upgrades[$id] = [pscustomobject]@{
                        Available = $available
                    }
                }
            }
        }
    }

    return ,@($installed, $upgrades)
}

function Collect-ScoopInventory {
    param([System.Management.Automation.CommandInfo]$Command)

    $installed = New-ObjectDictionary
    $upgrades = New-ObjectDictionary

    $lines = & $Command.Source 'list' 2>$null
    if ($LASTEXITCODE -eq 0 -and $lines) {
        $entries = @($lines)
        $handledAsObjects = $false

        foreach ($entry in $entries) {
            if ($entry -is [psobject] -and $entry.PSObject.Properties['Name']) {
                $handledAsObjects = $true
                $nameValue = $entry.PSObject.Properties['Name'].Value
                $versionProp = $entry.PSObject.Properties['Version']
                $sourceProp = $entry.PSObject.Properties['Source']
                if ([string]::IsNullOrWhiteSpace($nameValue)) { continue }

                $versionValue = if ($null -ne $versionProp) { $versionProp.Value } else { $null }
                $sourceValue = if ($null -ne $sourceProp) { $sourceProp.Value } else { $null }

                $name = $nameValue.ToString().Trim()
                if (-not [string]::IsNullOrWhiteSpace($name)) {
                    $displayName = Normalize-DisplayText -Value $name
                    if ([string]::IsNullOrWhiteSpace($displayName)) {
                        $displayName = $name
                    }

                    $installed[$name] = [pscustomobject]@{
                        Name = $displayName
                        Version = if ($null -ne $versionValue) { Normalize-NullableValue -Value ($versionValue.ToString()) } else { $null }
                        Source = if ($null -ne $sourceValue) { Normalize-NullableValue -Value ($sourceValue.ToString()) } else { $null }
                    }
                }
            }
        }

        if (-not $handledAsObjects) {
            $started = $false
            foreach ($line in $entries) {
                $cleanLine = Remove-AnsiSequences -Value ([string]$line)
                if ([string]::IsNullOrWhiteSpace($cleanLine)) { continue }
                if (-not $started) {
                    if ($cleanLine -match '^----' -or $cleanLine -match '^\s*Name\s+Version') { $started = $true }
                    continue
                }

                $parts = @(Split-TableColumns -Line $cleanLine)
                if ($parts.Count -lt 2) { continue }

                $name = $parts[0].Trim()
                $version = $parts[1].Trim()
                $bucket = if ($parts.Length -ge 3) { $parts[2].Trim() } else { '' }

                if (-not [string]::IsNullOrWhiteSpace($name)) {
                    $displayName = Normalize-DisplayText -Value $name
                    if ([string]::IsNullOrWhiteSpace($displayName)) {
                        $displayName = $name
                    }

                    $installed[$name] = [pscustomobject]@{
                        Name = $displayName
                        Version = Normalize-NullableValue -Value $version
                        Source = Normalize-NullableValue -Value $bucket
                    }
                }
            }
        }
    }

    $statusLines = & $Command.Source 'status' 2>$null
    if ($LASTEXITCODE -eq 0 -and $statusLines) {
        $entries = @($statusLines)
        $handledStatusObjects = $false

        foreach ($entry in $entries) {
            if ($entry -is [psobject] -and $entry.PSObject.Properties['Name']) {
                $handledStatusObjects = $true
                $nameValue = $entry.PSObject.Properties['Name'].Value
                $latestProp = $entry.PSObject.Properties['Latest Version']
                $latestValue = if ($null -ne $latestProp) { $latestProp.Value } else { $null }

                if ([string]::IsNullOrWhiteSpace($nameValue) -or [string]::IsNullOrWhiteSpace($latestValue)) {
                    continue
                }

                $name = $nameValue.ToString().Trim()
                $available = $latestValue.ToString().Trim()

                if (-not [string]::IsNullOrWhiteSpace($name) -and -not [string]::IsNullOrWhiteSpace($available)) {
                    $upgrades[$name] = [pscustomobject]@{
                        Available = Normalize-NullableValue -Value $available
                    }
                }
            }
        }

        if (-not $handledStatusObjects) {
            $started = $false
            foreach ($line in $entries) {
                $cleanLine = Remove-AnsiSequences -Value ([string]$line)
                if ([string]::IsNullOrWhiteSpace($cleanLine)) { continue }
                if ($cleanLine -like 'WARN*') { continue }
                if (-not $started) {
                    if ($cleanLine -match '^----' -or $cleanLine -match '^\s*Name\s+Installed\s+Available') { $started = $true }
                    continue
                }

                $parts = @(Split-TableColumns -Line $cleanLine)
                if ($parts.Count -lt 3) { continue }

                $name = $parts[0].Trim()
                $available = $parts[2].Trim()

                if (-not [string]::IsNullOrWhiteSpace($name) -and -not [string]::IsNullOrWhiteSpace($available)) {
                    $upgrades[$name] = [pscustomobject]@{
                        Available = Normalize-NullableValue -Value $available
                    }
                }
            }
        }
    }

    return ,@($installed, $upgrades)
}

if ($Managers -contains 'winget') {
    if (-not $wingetCommand) {
        $warnings.Add('winget command not found.') | Out-Null
    }
    else {
        try {
            $result = Collect-WingetInventory -Command $wingetCommand
            $installed = $result[0]
            $upgrades = $result[1]

            foreach ($entry in $installed.GetEnumerator()) {
                $id = $entry.Key
                $meta = $entry.Value
                $available = $null
                $displayName = Normalize-DisplayText -Value $meta.Name
                if ([string]::IsNullOrWhiteSpace($displayName)) {
                    $displayName = $id
                }

                $resolvedSource = Normalize-WingetSourceName -Value $meta.Source
                if ([string]::IsNullOrWhiteSpace($resolvedSource)) {
                    $resolvedSource = 'winget'
                }

                if ($upgrades.ContainsKey($id)) {
                    $upgradeEntry = $upgrades[$id]
                    $installedSource = Normalize-WingetSourceName -Value $meta.Source
                    if ([string]::IsNullOrWhiteSpace($installedSource)) {
                        $installedSource = 'winget'
                    }
                    $sourceProperty = $upgradeEntry.PSObject.Properties['Sources']
                    $sourceMap = if ($null -ne $sourceProperty) { $sourceProperty.Value } else { $null }

                    if ($null -ne $sourceMap -and $sourceMap.Count -gt 0) {
                        if (-not [string]::IsNullOrWhiteSpace($installedSource) -and $sourceMap.ContainsKey($installedSource)) {
                            $available = Normalize-NullableValue -Value $sourceMap[$installedSource]
                        }

                        if (-not $available -and $sourceMap.ContainsKey('winget')) {
                            $available = Normalize-NullableValue -Value $sourceMap['winget']
                        }

                        if (-not $available) {
                            $fallbackKey = ($sourceMap.Keys | Select-Object -First 1)
                            if ($fallbackKey) {
                                $available = Normalize-NullableValue -Value $sourceMap[$fallbackKey]
                            }
                        }
                    }
                }

                $packages.Add([pscustomobject]@{
                    Manager = 'winget'
                    Id = $id
                    Name = $displayName
                    InstalledVersion = $meta.Version
                    AvailableVersion = $available
                    Source = $resolvedSource
                }) | Out-Null
            }
        }
        catch {
            $warnings.Add("winget inventory failed: $($_.Exception.Message)") | Out-Null
        }
    }
}

if ($Managers -contains 'choco' -or $Managers -contains 'chocolatey') {
    $targetName = 'choco'
    if (-not $chocoCommand) {
        $warnings.Add('choco command not found.') | Out-Null
    }
    else {
        try {
            $result = Collect-ChocoInventory -Command $chocoCommand
            $installed = $result[0]
            $upgrades = $result[1]

            foreach ($entry in $installed.GetEnumerator()) {
                $id = $entry.Key
                $meta = $entry.Value
                $available = $null
                if ($upgrades.ContainsKey($id)) {
                    $available = Normalize-NullableValue -Value $upgrades[$id].Available
                }

                $packages.Add([pscustomobject]@{
                    Manager = $targetName
                    Id = $id
                    Name = $meta.Name
                    InstalledVersion = $meta.Version
                    AvailableVersion = $available
                    Source = 'chocolatey'
                }) | Out-Null
            }
        }
        catch {
            $warnings.Add("choco inventory failed: $($_.Exception.Message)") | Out-Null
        }
    }
}

if ($Managers -contains 'scoop') {
    if (-not $scoopCommand) {
        $warnings.Add('scoop command not found.') | Out-Null
    }
    else {
        try {
            $result = Collect-ScoopInventory -Command $scoopCommand
            $installed = $result[0]
            $upgrades = $result[1]

            foreach ($entry in $installed.GetEnumerator()) {
                $id = $entry.Key
                $meta = $entry.Value
                $available = $null
                if ($upgrades.ContainsKey($id)) {
                    $available = Normalize-NullableValue -Value $upgrades[$id].Available
                }

                $packages.Add([pscustomobject]@{
                    Manager = 'scoop'
                    Id = $id
                    Name = $meta.Name
                    InstalledVersion = $meta.Version
                    AvailableVersion = $available
                    Source = Normalize-NullableValue -Value $meta.Source
                }) | Out-Null
            }
        }
        catch {
            $warnings.Add("scoop inventory failed: $($_.Exception.Message)") | Out-Null
        }
    }
}

$result = [pscustomobject]@{
    generatedAt = [DateTimeOffset]::UtcNow.ToString('o')
    packages = $packages
    warnings = $warnings
}

$result | ConvertTo-Json -Depth 6 -Compress


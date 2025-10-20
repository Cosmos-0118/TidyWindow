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

    return $trimmed
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

    $installed = New-ObjectDictionary
    $upgrades = New-ObjectDictionary

    $args = @('list', '--accept-source-agreements', '--disable-interactivity')
    $lines = & $Command.Source @args 2>$null

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
                    $cleanSource = Normalize-NullableValue -Value $source
                    if ([string]::IsNullOrWhiteSpace($cleanSource) -or -not $cleanSource.Equals('winget', [System.StringComparison]::OrdinalIgnoreCase)) {
                        continue
                    }

                    $installed[$normalizedId] = [pscustomobject]@{
                        Name = $name
                        Version = Normalize-NullableValue -Value $version
                        Source = $cleanSource
                    }
                }
            }
        }
    }

    $upgradeArgs = @('upgrade', '--include-unknown', '--accept-source-agreements', '--disable-interactivity')
    $upgradeLines = & $Command.Source @upgradeArgs 2>$null

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

                if (-not [string]::IsNullOrWhiteSpace($normalizedId) -and -not [string]::IsNullOrWhiteSpace($available)) {
                    $upgrades[$normalizedId] = [pscustomobject]@{
                        Available = $available
                    }
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

    $installedLines = & $Command.Source 'list' '--local-only' '--limit-output' '--no-color' 2>$null
    if ($LASTEXITCODE -eq 0 -and $installedLines) {
        foreach ($line in $installedLines) {
            if ($line -match '^(?<id>[^|]+)\|(?<version>[^|]+)') {
                $installed[$matches['id'].Trim()] = [pscustomobject]@{
                    Name = $matches['id'].Trim()
                    Version = $matches['version'].Trim()
                    Source = 'chocolatey'
                }
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
                    $installed[$name] = [pscustomobject]@{
                        Name = $name
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
                    $installed[$name] = [pscustomobject]@{
                        Name = $name
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
                if ($upgrades.ContainsKey($id)) {
                    $available = Normalize-NullableValue -Value $upgrades[$id].Available
                }

                $packages.Add([pscustomobject]@{
                    Manager = 'winget'
                    Id = $id
                    Name = $meta.Name
                    InstalledVersion = $meta.Version
                    AvailableVersion = $available
                    Source = Normalize-NullableValue -Value $meta.Source
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

$result | ConvertTo-Json -Depth 6

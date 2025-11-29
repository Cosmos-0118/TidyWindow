function Select-TidyBestVersion {
    # Picks the highest semantic-looking version value from a set of candidates.
    [CmdletBinding()]
    param(
        [Parameter()]
        [System.Collections.IEnumerable] $Values
    )

    if (-not $Values) {
        return $null
    }

    $unique = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    $best = $null
    $bestVersion = $null

    foreach ($value in $Values) {
        if ($null -eq $value) {
            continue
        }

        $text = $value.ToString()
        if ([string]::IsNullOrWhiteSpace($text)) {
            continue
        }

        $trimmed = $text.Trim()
        if ([string]::IsNullOrWhiteSpace($trimmed)) {
            continue
        }

        if (-not $unique.Add($trimmed)) {
            continue
        }

        $match = [System.Text.RegularExpressions.Regex]::Match($trimmed, '([0-9A-Za-z]+(?:[\._\-+][0-9A-Za-z]+)*)')
        $candidateValue = $trimmed
        if ($match.Success) {
            $candidateValue = $match.Groups[1].Value.Trim()
        }

        if (-not [string]::IsNullOrWhiteSpace($candidateValue)) {
            $normalized = $candidateValue.Replace('_', '.').Replace('-', '.')
            while ($normalized.Contains('..')) {
                $normalized = $normalized.Replace('..', '.')
            }

            $parsed = $null
            if ([version]::TryParse($normalized, [ref]$parsed)) {
                if (($bestVersion -eq $null) -or ($parsed -gt $bestVersion)) {
                    $bestVersion = $parsed
                    $best = $candidateValue
                }

                continue
            }
        }

        if (-not $best) {
            $best = $candidateValue
            if (-not $best) {
                $best = $trimmed
            }
        }
    }

    if (-not $best) {
        foreach ($value in $Values) {
            if ($null -eq $value) { continue }
            $text = $value.ToString()
            if ([string]::IsNullOrWhiteSpace($text)) { continue }
            $trimmed = $text.Trim()
            if ([string]::IsNullOrWhiteSpace($trimmed)) { continue }
            return $trimmed
        }
    }

    return $best
}

function Get-TidyCommandPath {
    # Resolves the absolute path to a CLI tool when available.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string] $CommandName
    )

    $command = Get-Command -Name $CommandName -ErrorAction SilentlyContinue
    if (-not $command) {
        return $null
    }

    if (-not [string]::IsNullOrWhiteSpace($command.Source)) {
        return $command.Source
    }

    if (-not [string]::IsNullOrWhiteSpace($command.Path)) {
        return $command.Path
    }

    return $command.Name
}

function Get-TidyWingetMsixCandidates {
    # Returns MSIX/Appx package identifiers that appear to match a winget package id.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string] $PackageId
    )

    $results = [System.Collections.Generic.List[pscustomobject]]::new()

    if ([string]::IsNullOrWhiteSpace($PackageId)) {
        return $results
    }

    $appxCommand = Get-Command -Name 'Get-AppxPackage' -ErrorAction SilentlyContinue
    if (-not $appxCommand) {
        return $results
    }

    $patterns = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    [void]$patterns.Add($PackageId)
    [void]$patterns.Add("*$PackageId")

    $segments = $PackageId.Split('.', [System.StringSplitOptions]::RemoveEmptyEntries)
    if ($segments.Length -gt 1) {
        $suffix = '.' + ([string]::Join('.', $segments[1..($segments.Length - 1)]))
        [void]$patterns.Add("*$suffix")
        if ($segments.Length -gt 2) {
            $tail = [string]::Join('.', $segments[($segments.Length - 2)..($segments.Length - 1)])
            if (-not [string]::IsNullOrWhiteSpace($tail)) {
                [void]$patterns.Add("*.$tail")
            }
        }
    }

    $collected = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)

    foreach ($pattern in $patterns) {
        foreach ($scope in @($false, $true)) {
            try {
                $arguments = @{ Name = $pattern; ErrorAction = 'Stop' }
                if ($scope) {
                    $arguments['AllUsers'] = $true
                }

                $packages = Get-AppxPackage @arguments
            }
            catch {
                continue
            }

            foreach ($pkg in @($packages)) {
                if ($null -eq $pkg) { continue }
                $fullName = $pkg.PackageFullName
                if ([string]::IsNullOrWhiteSpace($fullName)) { continue }
                if (-not $collected.Add($fullName)) { continue }

                $versionString = $null
                try {
                    if ($pkg.Version) {
                        $versionString = $pkg.Version.ToString()
                    }
                }
                catch {
                    $versionString = $null
                }

                $versionValue = $null
                if (-not [string]::IsNullOrWhiteSpace($versionString)) {
                    $versionValue = $versionString
                }

                $results.Add([pscustomobject]@{
                    Identifier = "MSIX\$fullName"
                    Version     = $versionValue
                })
            }
        }
    }

    return $results
}

function Get-TidyWingetInstalledVersion {
    # Detects the installed version of a winget package if present.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string] $PackageId
    )

    $exe = Get-TidyCommandPath -CommandName 'winget'
    if (-not $exe) {
        return $null
    }

    $candidates = [System.Collections.Generic.List[string]]::new()

    try {
        $jsonOutput = & $exe 'list' '--id' $PackageId '-e' '--disable-interactivity' '--accept-source-agreements' '--output' 'json' 2>$null
        if ($LASTEXITCODE -eq 0 -and $jsonOutput) {
            $payload = [string]::Join([Environment]::NewLine, $jsonOutput)
            if (-not [string]::IsNullOrWhiteSpace($payload)) {
                $data = ConvertFrom-Json -InputObject $payload -ErrorAction Stop
                if ($data -is [System.Collections.IEnumerable]) {
                    foreach ($entry in $data) {
                        if ($null -eq $entry) { continue }
                        $identifier = $entry.PackageIdentifier
                        if (-not $identifier) { $identifier = $entry.Id }
                        if ($identifier -and -not [string]::Equals($identifier.ToString(), $PackageId, [System.StringComparison]::OrdinalIgnoreCase)) {
                            continue
                        }

                        $installed = $entry.InstalledVersion
                        if (-not $installed) { $installed = $entry.Version }
                        if ($installed) {
                            $value = $installed.ToString()
                            if (-not [string]::IsNullOrWhiteSpace($value)) {
                                [void]$candidates.Add($value.Trim())
                            }
                        }
                    }
                }
                elseif ($data) {
                    $installed = $data.InstalledVersion
                    if (-not $installed) { $installed = $data.Version }
                    if ($installed) {
                        $value = $installed.ToString()
                        if (-not [string]::IsNullOrWhiteSpace($value)) {
                            [void]$candidates.Add($value.Trim())
                        }
                    }
                }
            }
        }
    }
    catch {
        # Continue with text parsing fallback if JSON output is not available.
    }

    try {
        $fallback = & $exe 'list' '--id' $PackageId '-e' '--disable-interactivity' '--accept-source-agreements' 2>$null
        $columnMap = $null
        foreach ($line in @($fallback)) {
            if ([string]::IsNullOrWhiteSpace($line)) {
                continue
            }

            $text = [string]$line
            $clean = [System.Text.RegularExpressions.Regex]::Replace($text, '\x1B\[[0-9;]*[A-Za-z]', '')
            $clean = $clean.Replace("`r", '')
            if ([string]::IsNullOrWhiteSpace($clean)) {
                continue
            }

            if ($clean -match '^(?i)\s*Name\s+Id\s+Version') {
                $idStart = $clean.IndexOf('Id')
                $versionStart = $clean.IndexOf('Version', [Math]::Max($idStart, 0))
                $availableStart = $clean.IndexOf('Available', [Math]::Max($versionStart, 0))
                $columnMap = @{
                    IdStart        = $idStart
                    VersionStart   = $versionStart
                    AvailableStart = $availableStart
                }
                continue
            }
            if ($clean -match '^-{3,}') { continue }
            if ($clean -match '^(?i).*no installed package.*') { return $null }
            if ($clean -match '^(?i).*no installed packages found.*') { return $null }

            $trimmed = $clean.Trim()
            if ([string]::IsNullOrWhiteSpace($trimmed)) { continue }

            if ($columnMap) {
                $nameColumn = $null
                $idColumn = $null
                $versionColumn = $null

                if ($columnMap.IdStart -gt 0 -and $columnMap.IdStart -le $clean.Length) {
                    $nameLength = [Math]::Min($clean.Length, $columnMap.IdStart)
                    if ($nameLength -gt 0) {
                        $nameColumn = $clean.Substring(0, $nameLength).Trim()
                    }
                }

                if ($columnMap.IdStart -ge 0 -and $columnMap.IdStart -lt $clean.Length) {
                    $idEnd = if ($columnMap.VersionStart -gt $columnMap.IdStart) { [Math]::Min($clean.Length, $columnMap.VersionStart) } else { $clean.Length }
                    $idLength = $idEnd - $columnMap.IdStart
                    if ($idLength -gt 0) {
                        $idColumn = $clean.Substring($columnMap.IdStart, $idLength).Trim()
                    }
                }

                if ($columnMap.VersionStart -ge 0 -and $columnMap.VersionStart -lt $clean.Length) {
                    $versionEnd = if ($columnMap.AvailableStart -gt $columnMap.VersionStart) { [Math]::Min($clean.Length, $columnMap.AvailableStart) } else { $clean.Length }
                    $versionLength = $versionEnd - $columnMap.VersionStart
                    if ($versionLength -gt 0) {
                        $versionColumn = $clean.Substring($columnMap.VersionStart, $versionLength).Trim()
                    }
                }

                if (-not [string]::IsNullOrWhiteSpace($idColumn) -and -not [string]::IsNullOrWhiteSpace($versionColumn)) {
                    if ([string]::Equals($idColumn, $PackageId, [System.StringComparison]::OrdinalIgnoreCase)) {
                        $versionCandidate = $versionColumn
                        if ((-not ($versionCandidate -match '\d')) -or $versionCandidate.TrimStart().StartsWith('<') -or $versionCandidate.TrimStart().StartsWith('>')) {
                            if (-not [string]::IsNullOrWhiteSpace($nameColumn)) {
                                $nameMatch = [System.Text.RegularExpressions.Regex]::Match($nameColumn, '([0-9]+(?:[\._\-][0-9A-Za-z]+)+)')
                                if ($nameMatch.Success) {
                                    $versionCandidate = $nameMatch.Groups[1].Value
                                }
                            }
                        }

                        if (-not [string]::IsNullOrWhiteSpace($versionCandidate)) {
                            [void]$candidates.Add($versionCandidate.Trim())
                            continue
                        }
                    }
                }
            }

            $pattern = '^(?<name>.+?)\s+' + [System.Text.RegularExpressions.Regex]::Escape($PackageId) + '\s+(?<version>[^\s]+)'
            $match = [System.Text.RegularExpressions.Regex]::Match($trimmed, $pattern, [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
            if ($match.Success) {
                $candidate = $match.Groups['version'].Value.Trim()
                if (-not [string]::IsNullOrWhiteSpace($candidate)) {
                    [void]$candidates.Add($candidate)
                }
            }
        }
    }
    catch {
        # No further fallback available.
    }

    if ($candidates.Count -eq 0) {
        try {
            $msixCandidates = Get-TidyWingetMsixCandidates -PackageId $PackageId
            foreach ($entry in @($msixCandidates)) {
                if ($null -eq $entry) { continue }
                $value = $entry.Version
                if ([string]::IsNullOrWhiteSpace($value) -and $entry.Identifier) {
                    $match = [System.Text.RegularExpressions.Regex]::Match($entry.Identifier, '_(?<ver>[0-9]+(?:\.[0-9A-Za-z]+)+)_', [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
                    if ($match.Success) {
                        $value = $match.Groups['ver'].Value
                    }
                }

                if (-not [string]::IsNullOrWhiteSpace($value)) {
                    [void]$candidates.Add($value.Trim())
                }
            }
        }
        catch {
            # Ignore MSIX probing failures and fall through to the default logic.
        }
    }

    if ($candidates.Count -eq 0) {
        return $null
    }

    return (Select-TidyBestVersion -Values $candidates)
}

function Get-TidyChocoInstalledVersion {
    # Detects the installed version of a Chocolatey package.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string] $PackageId
    )

    $installRoot = $env:ChocolateyInstall
    if ([string]::IsNullOrWhiteSpace($installRoot)) {
        $installRoot = 'C:\ProgramData\chocolatey'
    }

    if (-not (Test-Path -LiteralPath $installRoot)) {
        return $null
    }

    $libRoot = Join-Path -Path $installRoot -ChildPath 'lib'
    if (-not (Test-Path -LiteralPath $libRoot)) {
        return $null
    }

    $candidateDirs = @()
    $directPath = Join-Path -Path $libRoot -ChildPath $PackageId
    if (Test-Path -LiteralPath $directPath) {
        $candidateDirs += (Get-Item -LiteralPath $directPath)
    }

    if ($candidateDirs.Count -eq 0) {
        try {
            $candidateDirs = Get-ChildItem -Path $libRoot -Directory -ErrorAction Stop | Where-Object {
                [string]::Equals($_.Name, $PackageId, [System.StringComparison]::OrdinalIgnoreCase)
            }
        }
        catch {
            $candidateDirs = @()
        }
    }

    if ($candidateDirs.Count -eq 0) {
        try {
            $candidateDirs = Get-ChildItem -Path $libRoot -Directory -ErrorAction Stop | Where-Object {
                $nuspec = Get-ChildItem -Path $_.FullName -Filter '*.nuspec' -File -ErrorAction SilentlyContinue | Select-Object -First 1
                if (-not $nuspec) {
                    $false
                }
                else {
                    $matchesId = $false
                    try {
                        $xml = [xml](Get-Content -LiteralPath $nuspec.FullName -Raw -ErrorAction Stop)
                        $metadata = $xml.package.metadata
                        if ($metadata) {
                            $idValue = $metadata.id
                            if (-not [string]::IsNullOrWhiteSpace($idValue) -and [string]::Equals($idValue, $PackageId, [System.StringComparison]::OrdinalIgnoreCase)) {
                                $matchesId = $true
                            }
                        }
                    }
                    catch {
                        $matchesId = $false
                    }

                    $matchesId
                }
            }
        }
        catch {
            $candidateDirs = @()
        }
    }

    foreach ($dir in $candidateDirs) {
        try {
            $nuspec = Get-ChildItem -Path $dir.FullName -Filter '*.nuspec' -File -ErrorAction Stop | Sort-Object LastWriteTime -Descending | Select-Object -First 1
            if (-not $nuspec) { continue }

            $xml = [xml](Get-Content -LiteralPath $nuspec.FullName -Raw -ErrorAction Stop)
            $metadata = $xml.package.metadata
            if ($metadata -and $metadata.version) {
                $candidate = $metadata.version.ToString().Trim()
                if (-not [string]::IsNullOrWhiteSpace($candidate)) {
                    return $candidate
                }
            }
        }
        catch {
            continue
        }
    }

    return $null
}

function Get-TidyScoopInstalledVersion {
    # Detects the installed version of a Scoop package.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string] $PackageId
    )

    $exe = Get-TidyCommandPath -CommandName 'scoop'
    if (-not $exe) {
        return $null
    }

    $installedCandidates = [System.Collections.Generic.List[string]]::new()
    $otherCandidates = [System.Collections.Generic.List[string]]::new()

    $addCandidate = ({
        param(
            [string] $Value,
            [bool] $IsInstalledHint
        )

        if ([string]::IsNullOrWhiteSpace($Value)) {
            return
        }

        $trimmed = $Value.Trim()
        if ([string]::IsNullOrWhiteSpace($trimmed)) {
            return
        }

        if ($trimmed -in @('-', 'n/a', 'Not installed', 'not installed', 'Not Installed')) {
            return
        }


        $target = $null
        if ($IsInstalledHint) {
            $target = $installedCandidates
        }
        else {
            $target = $otherCandidates
        }

        if ($null -eq $target) {
            return
        }

        [void]$target.Add($trimmed)
    }).GetNewClosure()

    try {
        $output = & $exe 'info' $PackageId 2>$null
        if ($LASTEXITCODE -eq 0 -and $output) {
            foreach ($entry in $output) {
                if ($null -eq $entry) {
                    continue
                }

                if ($entry -is [pscustomobject]) {
                    $candidate = $null
                    $isInstalled = $false
                    if ($entry.PSObject.Properties.Match('Installed')) { $candidate = $entry.Installed; $isInstalled = $true }
                    elseif ($entry.PSObject.Properties.Match('installed')) { $candidate = $entry.installed; $isInstalled = $true }
                    elseif ($entry.PSObject.Properties.Match('Version')) { $candidate = $entry.Version }
                    elseif ($entry.PSObject.Properties.Match('version')) { $candidate = $entry.version }

                    if ($null -ne $candidate) {
                        & $addCandidate -Value ($candidate.ToString()) -IsInstalledHint:$isInstalled
                    }

                    continue
                }

                $text = $entry.ToString()
                if ([string]::IsNullOrWhiteSpace($text)) {
                    continue
                }

                if ($text -match '^Installed\s*:\s*(?<ver>.+)$') {
                    & $addCandidate -Value $matches['ver'] -IsInstalledHint:$true
                    continue
                }

                if ($text -match '^Version\s*:\s*(?<ver>.+)$') {
                    & $addCandidate -Value $matches['ver'] -IsInstalledHint:$false
                    continue
                }

                if ($text -match '^Latest Version\s*:\s*(?<ver>.+)$') {
                    & $addCandidate -Value $matches['ver'] -IsInstalledHint:$false
                    continue
                }

                if ($text -match '^\s*(?<ver>[0-9][0-9A-Za-z\.\-_+]*)\s*$') {
                    & $addCandidate -Value $matches['ver'] -IsInstalledHint:$false
                }
            }
        }
    }
    catch {
        # Continue to list parsing.
    }

    try {
        $output = & $exe 'list' $PackageId 2>$null
        if ($LASTEXITCODE -eq 0 -and $output) {
            foreach ($entry in $output) {
                if ($null -eq $entry) {
                    continue
                }

                if ($entry -is [pscustomobject]) {
                    $name = $entry.Name
                    if (-not $name) { $name = $entry.name }
                    if (-not $name) { continue }

                    if ([string]::Equals($name, $PackageId, [System.StringComparison]::OrdinalIgnoreCase)) {
                        $candidate = $entry.Version
                        if (-not $candidate) { $candidate = $entry.version }
                        if ($candidate) {
                            & $addCandidate -Value ($candidate.ToString()) -IsInstalledHint:$true
                        }
                    }

                    continue
                }

                $text = $entry.ToString()
                if ([string]::IsNullOrWhiteSpace($text)) {
                    continue
                }

                if ($text -like 'Installed apps matching*') { continue }
                if ($text -match '^[\s-]+$') { continue }
                if ($text -match '^(Name|----)\s') { continue }

                $match = [System.Text.RegularExpressions.Regex]::Match($text, '^\s*(?<name>\S+)\s+(?<ver>\S+)')
                if ($match.Success -and [string]::Equals($match.Groups['name'].Value, $PackageId, [System.StringComparison]::OrdinalIgnoreCase)) {
                    & $addCandidate -Value $match.Groups['ver'].Value -IsInstalledHint:$true
                }
            }
        }
    }
    catch {
        # No further fallback available.
    }

    $selectionPool = $otherCandidates
    if ($installedCandidates.Count -gt 0) {
        $selectionPool = $installedCandidates
    }

    if ($selectionPool.Count -eq 0) {
        return $null
    }

    return (Select-TidyBestVersion -Values $selectionPool)
}

function Get-TidyInstalledPackageVersion {
    # Normalizes package manager hints and retrieves installed versions when available.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string] $Manager,
        [Parameter(Mandatory = $true)]
        [string] $PackageId
    )

    if ([string]::IsNullOrWhiteSpace($Manager) -or [string]::IsNullOrWhiteSpace($PackageId)) {
        return $null
    }

    $normalized = $Manager.Trim().ToLowerInvariant()
    switch ($normalized) {
        'winget' { return Get-TidyWingetInstalledVersion -PackageId $PackageId }
        'choco' { return Get-TidyChocoInstalledVersion -PackageId $PackageId }
        'chocolatey' { return Get-TidyChocoInstalledVersion -PackageId $PackageId }
        'scoop' { return Get-TidyScoopInstalledVersion -PackageId $PackageId }
        default { return $null }
    }
}


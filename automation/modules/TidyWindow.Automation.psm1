function Remove-TidyAnsiSequences {
    # Strips ANSI escape codes so UI output remains readable.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $false)]
        [string] $Text
    )

    if ([string]::IsNullOrWhiteSpace($Text)) {
        return $Text
    }

    $clean = [System.Text.RegularExpressions.Regex]::Replace($Text, '\x1B\[[0-9;?]*[ -/]*[@-~]', '')
    $clean = [System.Text.RegularExpressions.Regex]::Replace($clean, '\x1B', '')
    $clean = [System.Text.RegularExpressions.Regex]::Replace($clean, '\[[0-9;]{1,5}[A-Za-z]', '')
    return $clean
}

function Convert-TidyLogMessage {
    # Normalizes log payloads into printable strings to avoid binding errors.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [AllowNull()]
        [object] $InputObject
    )

    if ($null -eq $InputObject) {
        return '<null>'
    }

    if ($InputObject -is [string]) {
        return Remove-TidyAnsiSequences -Text $InputObject
    }

    if ($InputObject -is [pscustomobject]) {
        $pairs = foreach ($prop in $InputObject.PSObject.Properties) {
            $key = if ([string]::IsNullOrEmpty($prop.Name)) { '<unnamed>' } else { $prop.Name }
            $value = Convert-TidyLogMessage -InputObject $prop.Value
            "$key=$value"
        }

        return $pairs -join '; '
    }

    if ($InputObject -is [System.Collections.IDictionary]) {
        $pairs = @()
        foreach ($entry in $InputObject.GetEnumerator()) {
            $key = if ($null -eq $entry.Key) { '<null>' } else { $entry.Key.ToString() }
            $value = Convert-TidyLogMessage -InputObject $entry.Value
            $pairs += "$key=$value"
        }
        return $pairs -join '; '
    }

    if ($InputObject -is [System.Collections.IEnumerable] -and -not ($InputObject -is [string])) {
        $items = foreach ($item in $InputObject) { Convert-TidyLogMessage -InputObject $item }
        return $items -join [Environment]::NewLine
    }

    try {
        $converted = [System.Management.Automation.LanguagePrimitives]::ConvertTo($InputObject, [string])
        return Remove-TidyAnsiSequences -Text $converted
    }
    catch {
        $fallback = ($InputObject | Out-String).TrimEnd()
        return Remove-TidyAnsiSequences -Text $fallback
    }
}

function Write-TidyLog {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [ValidateSet('Information', 'Warning', 'Error')]
        [string] $Level,
        [Parameter(Mandatory = $true, ValueFromPipeline = $true, ValueFromRemainingArguments = $true)]
        [object[]] $Message
    )

    process {
        $timestamp = [DateTimeOffset]::Now.ToString('yyyy-MM-dd HH:mm:ss zzz', [System.Globalization.CultureInfo]::InvariantCulture)
        $parts = foreach ($segment in $Message) { Convert-TidyLogMessage -InputObject $segment }
        $text = ($parts -join ' ').Trim()
        if ([string]::IsNullOrWhiteSpace($text)) {
            $text = '<empty>'
        }

        Write-Host "[$timestamp][$Level] $text"
    }
}

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
        $candidateValue = if ($match.Success) { $match.Groups[1].Value.Trim() } else { $trimmed }

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

                $results.Add([pscustomobject]@{
                    Identifier = "MSIX\$fullName"
                    Version     = if ([string]::IsNullOrWhiteSpace($versionString)) { $null } else { $versionString }
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

            if ($clean -match '^(?i)\s*Name\s+Id\s+Version') { continue }
            if ($clean -match '^-{3,}') { continue }
            if ($clean -match '^(?i).*no installed package.*') { return $null }
            if ($clean -match '^(?i).*no installed packages found.*') { return $null }

            $trimmed = $clean.Trim()
            if ([string]::IsNullOrWhiteSpace($trimmed)) { continue }

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

    $selectionPool = if ($installedCandidates.Count -gt 0) { $installedCandidates } else { $otherCandidates }

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

function Assert-TidyAdmin {
    if (-not ([bool](New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator))) {
        throw 'Administrator privileges are required for this operation.'
    }
}

function Invoke-TidyRegistryScript {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string] $ScriptName,
        [hashtable] $Parameters
    )

    $registryRoot = Join-Path -Path $PSScriptRoot -ChildPath '..\registry'
    $scriptPath = Join-Path -Path $registryRoot -ChildPath $ScriptName
    $scriptPath = [System.IO.Path]::GetFullPath($scriptPath)

    if (-not (Test-Path -LiteralPath $scriptPath)) {
        throw "Registry automation script not found at path '$scriptPath'."
    }

    if (-not $Parameters) {
        $Parameters = @{}
    }

    & $scriptPath @Parameters
}

function Add-TidyShouldProcessParameters {
    param(
        [hashtable] $Parameters,
        [System.Collections.IDictionary] $BoundParameters
    )

    if (-not $Parameters) {
        $Parameters = @{}
    }

    if ($BoundParameters -and ($BoundParameters.PSObject.Methods.Name -contains 'ContainsKey') -and $BoundParameters.ContainsKey('WhatIf')) {
        $Parameters['WhatIf'] = $BoundParameters['WhatIf']
    }
    elseif ($BoundParameters -and ($BoundParameters.PSObject.Methods.Name -contains 'Contains') -and $BoundParameters.Contains('WhatIf')) {
        $Parameters['WhatIf'] = $BoundParameters['WhatIf']
    }

    if ($BoundParameters -and ($BoundParameters.PSObject.Methods.Name -contains 'ContainsKey') -and $BoundParameters.ContainsKey('Confirm')) {
        $Parameters['Confirm'] = $BoundParameters['Confirm']
    }
    elseif ($BoundParameters -and ($BoundParameters.PSObject.Methods.Name -contains 'Contains') -and $BoundParameters.Contains('Confirm')) {
        $Parameters['Confirm'] = $BoundParameters['Confirm']
    }

    return $Parameters
}

function Set-TidyMenuShowDelay {
    [CmdletBinding(SupportsShouldProcess = $true)]
    param(
        [ValidateRange(0, 2000)]
        [int] $DelayMilliseconds = 120,
        [switch] $RevertToDefault,
        [string] $ResultPath
    )

    $parameters = @{}
    if ($PSBoundParameters.ContainsKey('DelayMilliseconds')) {
        $parameters['DelayMilliseconds'] = $DelayMilliseconds
    }
    if ($RevertToDefault.IsPresent) {
        $parameters['RevertToDefault'] = $true
    }
    if ($PSBoundParameters.ContainsKey('ResultPath') -and -not [string]::IsNullOrWhiteSpace($ResultPath)) {
        $parameters['ResultPath'] = $ResultPath
    }

    $parameters = Add-TidyShouldProcessParameters -Parameters $parameters -BoundParameters $PSBoundParameters
    Invoke-TidyRegistryScript -ScriptName 'set-menu-show-delay.ps1' -Parameters $parameters
}

function Set-TidyWindowAnimation {
    [CmdletBinding(SupportsShouldProcess = $true)]
    param(
        [ValidateSet('Disable', 'Enable')]
        [string] $AnimationState = 'Disable',
        [string] $ResultPath
    )

    $parameters = @{}
    if ($PSBoundParameters.ContainsKey('AnimationState')) {
        $parameters['AnimationState'] = $AnimationState
    }
    if ($PSBoundParameters.ContainsKey('ResultPath') -and -not [string]::IsNullOrWhiteSpace($ResultPath)) {
        $parameters['ResultPath'] = $ResultPath
    }

    $parameters = Add-TidyShouldProcessParameters -Parameters $parameters -BoundParameters $PSBoundParameters
    Invoke-TidyRegistryScript -ScriptName 'set-window-animation.ps1' -Parameters $parameters
}

function Set-TidyVisualEffectsProfile {
    [CmdletBinding(SupportsShouldProcess = $true)]
    param(
        [ValidateSet('Balanced', 'Performance', 'Appearance', 'Default')]
        [string] $Profile = 'Balanced',
        [string] $ResultPath
    )

    $parameters = @{}
    if ($PSBoundParameters.ContainsKey('Profile')) {
        $parameters['Profile'] = $Profile
    }
    if ($PSBoundParameters.ContainsKey('ResultPath') -and -not [string]::IsNullOrWhiteSpace($ResultPath)) {
        $parameters['ResultPath'] = $ResultPath
    }

    $parameters = Add-TidyShouldProcessParameters -Parameters $parameters -BoundParameters $PSBoundParameters
    Invoke-TidyRegistryScript -ScriptName 'set-visual-effects.ps1' -Parameters $parameters
}

function Set-TidyPrefetchingMode {
    [CmdletBinding(SupportsShouldProcess = $true)]
    param(
        [ValidateSet('SsdRecommended', 'Default', 'Disabled')]
        [string] $Mode = 'SsdRecommended',
        [string] $ResultPath
    )

    $parameters = @{}
    if ($PSBoundParameters.ContainsKey('Mode')) {
        $parameters['Mode'] = $Mode
    }
    if ($PSBoundParameters.ContainsKey('ResultPath') -and -not [string]::IsNullOrWhiteSpace($ResultPath)) {
        $parameters['ResultPath'] = $ResultPath
    }

    $parameters = Add-TidyShouldProcessParameters -Parameters $parameters -BoundParameters $PSBoundParameters
    Invoke-TidyRegistryScript -ScriptName 'manage-prefetching.ps1' -Parameters $parameters
}

function Set-TidyTelemetryLevel {
    [CmdletBinding(SupportsShouldProcess = $true)]
    param(
        [ValidateSet('Security', 'Basic', 'Enhanced', 'Full')]
        [string] $Level = 'Security',
        [string] $ResultPath
    )

    $parameters = @{}
    if ($PSBoundParameters.ContainsKey('Level')) {
        $parameters['Level'] = $Level
    }
    if ($PSBoundParameters.ContainsKey('ResultPath') -and -not [string]::IsNullOrWhiteSpace($ResultPath)) {
        $parameters['ResultPath'] = $ResultPath
    }

    $parameters = Add-TidyShouldProcessParameters -Parameters $parameters -BoundParameters $PSBoundParameters
    Invoke-TidyRegistryScript -ScriptName 'set-telemetry-level.ps1' -Parameters $parameters
}

function Set-TidyCortanaPolicy {
    [CmdletBinding(SupportsShouldProcess = $true)]
    param(
        [switch] $Enable,
        [switch] $Disable,
        [string] $ResultPath
    )

    $parameters = @{}
    if ($Enable.IsPresent) {
        $parameters['Enable'] = $true
    }
    if ($Disable.IsPresent) {
        $parameters['Disable'] = $true
    }
    if ($PSBoundParameters.ContainsKey('ResultPath') -and -not [string]::IsNullOrWhiteSpace($ResultPath)) {
        $parameters['ResultPath'] = $ResultPath
    }

    $parameters = Add-TidyShouldProcessParameters -Parameters $parameters -BoundParameters $PSBoundParameters
    Invoke-TidyRegistryScript -ScriptName 'set-cortana-policy.ps1' -Parameters $parameters
}

function Set-TidyNetworkLatencyProfile {
    [CmdletBinding(SupportsShouldProcess = $true)]
    param(
        [switch] $Revert,
        [string] $ResultPath
    )

    $parameters = @{}
    if ($Revert.IsPresent) {
        $parameters['Revert'] = $true
    }
    if ($PSBoundParameters.ContainsKey('ResultPath') -and -not [string]::IsNullOrWhiteSpace($ResultPath)) {
        $parameters['ResultPath'] = $ResultPath
    }

    $parameters = Add-TidyShouldProcessParameters -Parameters $parameters -BoundParameters $PSBoundParameters
    Invoke-TidyRegistryScript -ScriptName 'tune-network-latency.ps1' -Parameters $parameters
}

function Set-TidySysMainState {
    [CmdletBinding(SupportsShouldProcess = $true)]
    param(
        [switch] $Enable,
        [switch] $Disable,
        [string] $ResultPath
    )

    $parameters = @{}
    if ($Enable.IsPresent) {
        $parameters['Enable'] = $true
    }
    if ($Disable.IsPresent) {
        $parameters['Disable'] = $true
    }
    if ($PSBoundParameters.ContainsKey('ResultPath') -and -not [string]::IsNullOrWhiteSpace($ResultPath)) {
        $parameters['ResultPath'] = $ResultPath
    }

    $parameters = Add-TidyShouldProcessParameters -Parameters $parameters -BoundParameters $PSBoundParameters
    Invoke-TidyRegistryScript -ScriptName 'stop-sysmain.ps1' -Parameters $parameters
}

function Set-TidyLowDiskAlertPolicy {
    [CmdletBinding(SupportsShouldProcess = $true)]
    param(
        [switch] $DisableAlerts,
        [switch] $EnableAlerts,
        [string] $ResultPath
    )

    $parameters = @{}
    if ($DisableAlerts.IsPresent) {
        $parameters['DisableAlerts'] = $true
    }
    if ($EnableAlerts.IsPresent) {
        $parameters['EnableAlerts'] = $true
    }
    if ($PSBoundParameters.ContainsKey('ResultPath') -and -not [string]::IsNullOrWhiteSpace($ResultPath)) {
        $parameters['ResultPath'] = $ResultPath
    }

    $parameters = Add-TidyShouldProcessParameters -Parameters $parameters -BoundParameters $PSBoundParameters
    Invoke-TidyRegistryScript -ScriptName 'hide-low-disk-alerts.ps1' -Parameters $parameters
}

function Set-TidyAutoRestartSignOn {
    [CmdletBinding(SupportsShouldProcess = $true)]
    param(
        [switch] $Enable,
        [switch] $Disable,
        [string] $ResultPath
    )

    $parameters = @{}
    if ($Enable.IsPresent) {
        $parameters['Enable'] = $true
    }
    if ($Disable.IsPresent) {
        $parameters['Disable'] = $true
    }
    if ($PSBoundParameters.ContainsKey('ResultPath') -and -not [string]::IsNullOrWhiteSpace($ResultPath)) {
        $parameters['ResultPath'] = $ResultPath
    }

    $parameters = Add-TidyShouldProcessParameters -Parameters $parameters -BoundParameters $PSBoundParameters
    Invoke-TidyRegistryScript -ScriptName 'configure-auto-restart-sign-on.ps1' -Parameters $parameters
}

function Set-TidyAutoEndTasks {
    [CmdletBinding(SupportsShouldProcess = $true)]
    param(
        [switch] $Enable,
        [switch] $Disable,
        [string] $ResultPath
    )

    $parameters = @{}
    if ($Enable.IsPresent) {
        $parameters['Enable'] = $true
    }
    if ($Disable.IsPresent) {
        $parameters['Disable'] = $true
    }
    if ($PSBoundParameters.ContainsKey('ResultPath') -and -not [string]::IsNullOrWhiteSpace($ResultPath)) {
        $parameters['ResultPath'] = $ResultPath
    }

    $parameters = Add-TidyShouldProcessParameters -Parameters $parameters -BoundParameters $PSBoundParameters
    Invoke-TidyRegistryScript -ScriptName 'auto-end-tasks.ps1' -Parameters $parameters
}

function Set-TidyHungAppTimeouts {
    [CmdletBinding(SupportsShouldProcess = $true)]
    param(
        [ValidateRange(1000, 20000)]
        [int] $HungAppTimeout = 5000,
        [ValidateRange(1000, 20000)]
        [int] $WaitToKillAppTimeout = 5000,
        [switch] $Revert,
        [string] $ResultPath
    )

    $parameters = @{}
    if ($PSBoundParameters.ContainsKey('HungAppTimeout')) {
        $parameters['HungAppTimeout'] = $HungAppTimeout
    }
    if ($PSBoundParameters.ContainsKey('WaitToKillAppTimeout')) {
        $parameters['WaitToKillAppTimeout'] = $WaitToKillAppTimeout
    }
    if ($Revert.IsPresent) {
        $parameters['Revert'] = $true
    }
    if ($PSBoundParameters.ContainsKey('ResultPath') -and -not [string]::IsNullOrWhiteSpace($ResultPath)) {
        $parameters['ResultPath'] = $ResultPath
    }

    $parameters = Add-TidyShouldProcessParameters -Parameters $parameters -BoundParameters $PSBoundParameters
    Invoke-TidyRegistryScript -ScriptName 'adjust-hung-app-timeouts.ps1' -Parameters $parameters
}

function Set-TidyLockWorkstationPolicy {
    [CmdletBinding(SupportsShouldProcess = $true)]
    param(
        [switch] $Enable,
        [switch] $Disable,
        [string] $ResultPath
    )

    $parameters = @{}
    if ($Enable.IsPresent) {
        $parameters['Enable'] = $true
    }
    if ($Disable.IsPresent) {
        $parameters['Disable'] = $true
    }
    if ($PSBoundParameters.ContainsKey('ResultPath') -and -not [string]::IsNullOrWhiteSpace($ResultPath)) {
        $parameters['ResultPath'] = $ResultPath
    }

    $parameters = Add-TidyShouldProcessParameters -Parameters $parameters -BoundParameters $PSBoundParameters
    Invoke-TidyRegistryScript -ScriptName 'toggle-lock-workstation.ps1' -Parameters $parameters
}

function Resolve-TidyPath {
    [CmdletBinding()]
    param([string] $Path)

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return $null
    }

    $expanded = [Environment]::ExpandEnvironmentVariables($Path.Trim().Trim('"'))
    if ($expanded.StartsWith('~')) {
        $home = $env:USERPROFILE
        if (-not [string]::IsNullOrWhiteSpace($home)) {
            $expanded = $home + $expanded.Substring(1)
        }
    }

    try {
        return [System.IO.Path]::GetFullPath($expanded)
    }
    catch {
        return $expanded
    }
}

function ConvertTo-TidyNameKey {
    [CmdletBinding()]
    param([string] $Value)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return $null
    }

    $clean = $Value.ToLowerInvariant()
    $clean = [System.Text.RegularExpressions.Regex]::Replace($clean, '[^a-z0-9]', '')
    if ([string]::IsNullOrWhiteSpace($clean)) {
        return $null
    }

    return $clean
}

function Get-TidyProgramDataDirectory {
    [CmdletBinding()]
    param()

    $programData = $env:ProgramData
    if ([string]::IsNullOrWhiteSpace($programData)) {
        $programData = 'C:\ProgramData'
    }

    $root = Join-Path -Path $programData -ChildPath 'TidyWindow'
    if (-not (Test-Path -LiteralPath $root)) {
        [void](New-Item -Path $root -ItemType Directory -Force)
    }

    return $root
}

function New-TidyFeatureRunDirectory {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string] $FeatureName,
        [Parameter(Mandatory = $true)]
        [string] $AppIdentifier
    )

    $root = Get-TidyProgramDataDirectory
    $featureRoot = Join-Path -Path $root -ChildPath $FeatureName
    if (-not (Test-Path -LiteralPath $featureRoot)) {
        [void](New-Item -Path $featureRoot -ItemType Directory -Force)
    }

    $safeId = [System.Text.RegularExpressions.Regex]::Replace($AppIdentifier, '[^A-Za-z0-9_-]', '_')
    if ([string]::IsNullOrWhiteSpace($safeId)) {
        $safeId = 'app'
    }

    $target = Join-Path -Path $featureRoot -ChildPath $safeId
    if (-not (Test-Path -LiteralPath $target)) {
        [void](New-Item -Path $target -ItemType Directory -Force)
    }

    return $target
}

function Write-TidyStructuredEvent {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string] $Type,
        [object] $Payload
    )

    $envelope = [ordered]@{
        type      = $Type
        timestamp = [DateTimeOffset]::UtcNow.ToString('o')
    }

    if ($Payload) {
        if ($Payload -is [System.Collections.IDictionary]) {
            foreach ($key in $Payload.Keys) {
                $envelope[$key] = $Payload[$key]
            }
        }
        elseif ($Payload -is [pscustomobject]) {
            foreach ($prop in $Payload.PSObject.Properties) {
                if (-not [string]::IsNullOrWhiteSpace($prop.Name)) {
                    $envelope[$prop.Name] = $prop.Value
                }
            }
        }
        else {
            $envelope['payload'] = $Payload
        }
    }

    $json = $envelope | ConvertTo-Json -Depth 6 -Compress
    Write-Output $json
}

function Write-TidyRunLog {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path,
        [Parameter(Mandatory = $true)]
        [hashtable] $Payload
    )

    $directory = Split-Path -Parent $Path
    if (-not [string]::IsNullOrWhiteSpace($directory) -and -not (Test-Path -LiteralPath $directory)) {
        [void](New-Item -Path $directory -ItemType Directory -Force)
    }

    $Payload | ConvertTo-Json -Depth 6 | Out-File -FilePath $Path -Encoding utf8 -Force
}

function Invoke-TidyCommandLine {
    [CmdletBinding()]
    param([string] $CommandLine)

    if ([string]::IsNullOrWhiteSpace($CommandLine)) {
        return [pscustomobject]@{ exitCode = 0; output = ''; errors = ''; durationMs = 0 }
    }

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = 'cmd.exe'
    $psi.Arguments = "/d /s /c ""$CommandLine"""
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true

    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $psi

    $start = [DateTimeOffset]::UtcNow
    $null = $process.Start()
    $stdout = $process.StandardOutput.ReadToEnd()
    $stderr = $process.StandardError.ReadToEnd()
    $process.WaitForExit()
    $duration = ([DateTimeOffset]::UtcNow - $start).TotalMilliseconds

    return [pscustomobject]@{
        exitCode  = $process.ExitCode
        output    = $stdout
        errors    = $stderr
        durationMs = [math]::Round($duration, 0)
    }
}

function Get-TidyProcessSnapshot {
    [CmdletBinding()]
    param()

    $list = New-Object 'System.Collections.Generic.List[psobject]'
    try {
        Get-Process | ForEach-Object {
            $path = $null
            try { $path = $_.Path } catch { $path = $null }
            $list.Add([pscustomobject]@{ id = $_.Id; name = $_.ProcessName; path = $path }) | Out-Null
        }
    }
    catch {
        # Ignore snapshot failures.
    }

    return $list
}

function Get-TidyServiceSnapshot {
    [CmdletBinding()]
    param()

    $list = New-Object 'System.Collections.Generic.List[psobject]'
    try {
        Get-CimInstance -ClassName Win32_Service | ForEach-Object {
            $list.Add([pscustomobject]@{
                name        = $_.Name
                displayName = $_.DisplayName
                path        = $_.PathName
                state       = $_.State
            }) | Out-Null
        }
    }
    catch {
        # Ignore snapshot failures.
    }

    return $list
}

function Find-TidyRelatedProcesses {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [psobject] $App,
        [psobject[]] $Snapshot,
        [int] $MaxMatches = 25
    )

    $results = New-Object 'System.Collections.Generic.List[psobject]'
    if (-not $Snapshot) {
        return $results
    }

    $installRoot = Resolve-TidyPath -Path $App.installRoot
    $processHints = @($App.processHints) | Where-Object { $_ }
    $nameKey = ConvertTo-TidyNameKey -Value $App.name

    foreach ($proc in $Snapshot) {
        if ($results.Count -ge $MaxMatches) { break }
        $match = $false

        if ($installRoot -and $proc.path -and $proc.path.StartsWith($installRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
            $match = $true
        }
        elseif ($processHints.Count -gt 0) {
            foreach ($hint in $processHints) {
                if ([string]::IsNullOrWhiteSpace($hint) -or -not $proc.name) { continue }
                if ($proc.name.IndexOf($hint, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
                    $match = $true
                    break
                }
            }
        }
        elseif ($nameKey) {
            $procKey = ConvertTo-TidyNameKey -Value $proc.name
            if ($procKey -and $procKey.Contains($nameKey)) {
                $match = $true
            }
        }

        if ($match) {
            $results.Add($proc) | Out-Null
        }
    }

    return $results
}

function Stop-TidyProcesses {
    [CmdletBinding()]
    param(
        [psobject[]] $Processes,
        [switch] $DryRun,
        [switch] $Force
    )

    if (-not $Processes -or $Processes.Count -eq 0) {
        return 0
    }

    $stopped = 0
    foreach ($proc in $Processes) {
        if ($DryRun) {
            $stopped++
            continue
        }

        try {
            Stop-Process -Id $proc.id -Force:$Force -ErrorAction Stop
            $stopped++
        }
        catch {
            Write-TidyLog -Level 'Warning' -Message "Failed to stop process $($proc.name) ($($proc.id)): $($_.Exception.Message)"
        }
    }

    return $stopped
}

function ConvertTo-TidyRegistryPath {
    [CmdletBinding()]
    param([string] $KeyPath)

    if ([string]::IsNullOrWhiteSpace($KeyPath)) { return $null }

    switch -Regex ($KeyPath) {
        '^(HKEY_LOCAL_MACHINE|HKLM)\\(.+)$' { return "Registry::HKEY_LOCAL_MACHINE\\$($matches[2])" }
        '^(HKEY_CURRENT_USER|HKCU)\\(.+)$'  { return "Registry::HKEY_CURRENT_USER\\$($matches[2])" }
        '^(HKEY_CLASSES_ROOT|HKCR)\\(.+)$'  { return "Registry::HKEY_CLASSES_ROOT\\$($matches[2])" }
        '^(HKEY_USERS|HKU)\\(.+)$'          { return "Registry::HKEY_USERS\\$($matches[2])" }
        '^(HKEY_CURRENT_CONFIG|HKCC)\\(.+)$'{ return "Registry::HKEY_CURRENT_CONFIG\\$($matches[2])" }
        Default { return $KeyPath }
    }
}

function Measure-TidyDirectoryBytes {
    [CmdletBinding()]
    param([string] $Path)

    if (-not (Test-Path -LiteralPath $Path)) { return 0 }

    try {
        $items = Get-ChildItem -LiteralPath $Path -Recurse -File -ErrorAction SilentlyContinue
        return ($items | Measure-Object -Property Length -Sum).Sum
    }
    catch {
        return 0
    }
}

function New-TidyArtifactId {
    [CmdletBinding()]
    param()

    return [Guid]::NewGuid().ToString('n')
}

function New-TidyFileArtifact {
    [CmdletBinding()]
    param(
        [string] $Path,
        [string] $Reason
    )

    $resolved = Resolve-TidyPath -Path $Path
    if ([string]::IsNullOrWhiteSpace($resolved) -or -not (Test-Path -LiteralPath $resolved)) {
        return $null
    }

    $item = Get-Item -LiteralPath $resolved -ErrorAction SilentlyContinue
    if (-not $item) { return $null }

    $isDirectory = $item.PSIsContainer
    $size = if ($isDirectory) { Measure-TidyDirectoryBytes -Path $resolved } else { $item.Length }

    $programFiles = $env:ProgramFiles
    $programFilesX86 = [Environment]::GetEnvironmentVariable('ProgramFiles(x86)')
    $requiresElevation = $false
    if ($programFiles -and $resolved.StartsWith($programFiles, [System.StringComparison]::OrdinalIgnoreCase)) {
        $requiresElevation = $true
    }
    elseif ($programFilesX86 -and $resolved.StartsWith($programFilesX86, [System.StringComparison]::OrdinalIgnoreCase)) {
        $requiresElevation = $true
    }

    return [pscustomobject]@{
        id                = New-TidyArtifactId
        type              = $isDirectory ? 'Directory' : 'File'
        group             = 'Files'
        path              = $resolved
        displayName       = Split-Path -Path $resolved -Leaf
        sizeBytes         = $size
        defaultSelected   = $true
        requiresElevation = $requiresElevation
        metadata          = @{ reason = $Reason }
    }
}

function New-TidyRegistryArtifact {
    [CmdletBinding()]
    param([string] $KeyPath)

    $providerPath = ConvertTo-TidyRegistryPath -KeyPath $KeyPath
    if (-not $providerPath) { return $null }

    return [pscustomobject]@{
        id                = New-TidyArtifactId
        type              = 'Registry'
        group             = 'Registry'
        path              = $providerPath
        displayName       = $KeyPath
        sizeBytes         = 0
        defaultSelected   = $true
        requiresElevation = $providerPath -like 'Registry::HKEY_LOCAL_MACHINE*'
        metadata          = @{ reason = 'UninstallKey' }
    }
}

function New-TidyServiceArtifact {
    [CmdletBinding()]
    param([string] $ServiceName)

    if ([string]::IsNullOrWhiteSpace($ServiceName)) { return $null }

    return [pscustomobject]@{
        id                = New-TidyArtifactId
        type              = 'Service'
        group             = 'Services'
        path              = $ServiceName
        displayName       = $ServiceName
        sizeBytes         = 0
        defaultSelected   = $true
        requiresElevation = $true
        metadata          = @{}
    }
}

function Get-TidyCandidateDataFolders {
    [CmdletBinding()]
    param([psobject] $App)

    $results = @()
    $name = $App.name
    if ([string]::IsNullOrWhiteSpace($name)) { return $results }

    $safe = [System.Text.RegularExpressions.Regex]::Replace($name, '[^A-Za-z0-9]', '')
    if ([string]::IsNullOrWhiteSpace($safe)) { return $results }

    $roots = @()
    if ($env:ProgramData) { $roots += $env:ProgramData }
    else { $roots += 'C:\ProgramData' }
    if ($env:LOCALAPPDATA) { $roots += $env:LOCALAPPDATA }
    if ($env:APPDATA) { $roots += $env:APPDATA }

    foreach ($root in $roots) {
        if ([string]::IsNullOrWhiteSpace($root)) { continue }
        $candidate = Join-Path -Path $root -ChildPath $safe
        if (Test-Path -LiteralPath $candidate) {
            $results += $candidate
        }
    }

    return $results
}

function Get-TidyArtifacts {
    [CmdletBinding()]
    param([psobject] $App)

    $artifacts = New-Object 'System.Collections.Generic.List[psobject]'

    foreach ($root in @($App.installRoot)) {
        $artifact = New-TidyFileArtifact -Path $root -Reason 'InstallRoot'
        if ($artifact) { $artifacts.Add($artifact) | Out-Null }
    }

    foreach ($hint in @($App.artifactHints)) {
        if ([string]::IsNullOrWhiteSpace($hint)) { continue }
        if ($App.installRoot -and [string]::Equals($hint, $App.installRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
            continue
        }
        $artifact = New-TidyFileArtifact -Path $hint -Reason 'Hint'
        if ($artifact) { $artifacts.Add($artifact) | Out-Null }
    }

    foreach ($folder in Get-TidyCandidateDataFolders -App $App) {
        $artifact = New-TidyFileArtifact -Path $folder -Reason 'DataFolder'
        if ($artifact) { $artifacts.Add($artifact) | Out-Null }
    }

    if ($App.registry -and $App.registry.keyPath) {
        $artifact = New-TidyRegistryArtifact -KeyPath $App.registry.keyPath
        if ($artifact) { $artifacts.Add($artifact) | Out-Null }
    }

    foreach ($svc in @($App.serviceHints)) {
        $artifact = New-TidyServiceArtifact -ServiceName $svc
        if ($artifact) { $artifacts.Add($artifact) | Out-Null }
    }

    return $artifacts
}

function Remove-TidyArtifacts {
    [CmdletBinding()]
    param(
        [psobject[]] $Artifacts,
        [switch] $DryRun
    )

    $results = New-Object 'System.Collections.Generic.List[psobject]'
    if (-not $Artifacts -or $Artifacts.Count -eq 0) {
        return [pscustomobject]@{ Results = $results; RemovedCount = 0; FailureCount = 0; FreedBytes = 0 }
    }

    $order = @{ Directory = 0; File = 1; Registry = 2; Service = 3 }
    $ordered = $Artifacts | Sort-Object -Property @{ Expression = { if ($order.ContainsKey($_.type)) { $order[$_.type] } else { 99 } } }, path

    $removed = 0
    $freed = 0

    foreach ($artifact in $ordered) {
        $entry = [ordered]@{
            artifactId = $artifact.id
            type       = $artifact.type
            path       = $artifact.path
            success    = $false
            error      = $null
        }

        if ($DryRun) {
            $entry.success = $true
        }
        else {
            try {
                switch ($artifact.type) {
                    'Directory' {
                        if (Test-Path -LiteralPath $artifact.path) {
                            Remove-Item -LiteralPath $artifact.path -Recurse -Force -ErrorAction Stop
                        }
                        $entry.success = $true
                    }
                    'File' {
                        if (Test-Path -LiteralPath $artifact.path) {
                            Remove-Item -LiteralPath $artifact.path -Force -ErrorAction Stop
                        }
                        $entry.success = $true
                    }
                    'Registry' {
                        if (Test-Path -Path $artifact.path) {
                            Remove-Item -Path $artifact.path -Recurse -Force -ErrorAction Stop
                        }
                        $entry.success = $true
                    }
                    'Service' {
                        try { Stop-Service -Name $artifact.path -Force -ErrorAction SilentlyContinue } catch { }
                        & sc.exe 'delete' $artifact.path *> $null
                        if ($LASTEXITCODE -ne 0) {
                            throw "sc.exe delete failed with exit code $LASTEXITCODE"
                        }
                        $entry.success = $true
                    }
                    Default {
                        $entry.error = 'Unsupported artifact type.'
                    }
                }
            }
            catch {
                $entry.error = $_.Exception.Message
            }
        }

        if ($entry.success) {
            $removed++
            if ($artifact.sizeBytes) {
                $freed += [long]$artifact.sizeBytes
            }
        }

        $results.Add([pscustomobject]$entry) | Out-Null
    }

    $failures = $results | Where-Object { -not $_.success }
    return [pscustomobject]@{
        Results      = $results
        RemovedCount = $removed
        FailureCount = $failures.Count
        FreedBytes   = $freed
    }
}

Export-ModuleMember -Function Convert-TidyLogMessage, Write-TidyLog, Get-TidyCommandPath, Get-TidyWingetMsixCandidates, Get-TidyWingetInstalledVersion, Get-TidyChocoInstalledVersion, Get-TidyScoopInstalledVersion, Get-TidyInstalledPackageVersion, Assert-TidyAdmin, Set-TidyMenuShowDelay, Set-TidyWindowAnimation, Set-TidyVisualEffectsProfile, Set-TidyPrefetchingMode, Set-TidyTelemetryLevel, Set-TidyCortanaPolicy, Set-TidyNetworkLatencyProfile, Set-TidySysMainState, Set-TidyLowDiskAlertPolicy, Set-TidyAutoRestartSignOn, Set-TidyAutoEndTasks, Set-TidyHungAppTimeouts, Set-TidyLockWorkstationPolicy, Resolve-TidyPath, ConvertTo-TidyNameKey, Get-TidyProgramDataDirectory, New-TidyFeatureRunDirectory, Write-TidyStructuredEvent, Write-TidyRunLog, Invoke-TidyCommandLine, Get-TidyProcessSnapshot, Get-TidyServiceSnapshot, Find-TidyRelatedProcesses, Stop-TidyProcesses, ConvertTo-TidyRegistryPath, Measure-TidyDirectoryBytes, New-TidyArtifactId, New-TidyFileArtifact, New-TidyRegistryArtifact, New-TidyServiceArtifact, Get-TidyCandidateDataFolders, Get-TidyArtifacts, Remove-TidyArtifacts
